using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Motif d'un 429 — classification du quota violé, parsée du corps
    /// d'erreur Google (<c>error.details</c> : <c>RetryInfo.retryDelay</c> +
    /// <c>QuotaFailure.violations[].quotaMetric</c>), ou inconnue (Ollama
    /// cloud ne sert ni motif ni fenêtre).
    /// </summary>
    internal enum LlmRateLimitMotif
    {
        /// <summary>Non classifié (corps illisible, provider sans détail).</summary>
        Unknown = 0,
        /// <summary>Tokens par minute (entrée ou sortie) — fenêtre calendaire
        /// courte : attente puis reprise même backend.</summary>
        Tpm,
        /// <summary>Requêtes par minute — attente courte puis reprise.</summary>
        Rpm,
        /// <summary>Quota quotidien (requêtes ou tokens par jour) — la fenêtre
        /// se compte en heures : aucune attente n'est sensée, bascule
        /// immédiate au repli.</summary>
        Rpd
    }

    /// <summary>
    /// Exception typée d'un HTTP 429 d'un backend LLM. Porte la
    /// classification du motif + la fenêtre de reprise recommandée par le
    /// serveur (Google <c>RetryInfo.retryDelay</c>, en-tête Retry-After).
    /// Le gate <see cref="LlmRateLimitGate"/> est marqué AVANT le lancement ;
    /// c'est l'appelant (boucle agent, appel one-shot) qui décide attente +
    /// reprise même backend vs bascule, selon son plafond de surface
    /// (tâches de fond ≈ 60-90 s, chat interactif ≈ 20 s).
    /// </summary>
    internal sealed class LlmRateLimitException : Exception
    {
        /// <summary>Motif classifié du quota violé.</summary>
        public LlmRateLimitMotif Motif { get; }

        /// <summary>Fenêtre de reprise recommandée par le serveur ; null =
        /// non servie (le gate applique alors sa fenêtre calendaire par
        /// défaut).</summary>
        public TimeSpan? RetryDelay { get; }

        /// <summary>Metric brute du quota violé (diagnostic — ex.
        /// generativelanguage…/generate_content_paid_tier_2_input_token_count).</summary>
        public string QuotaMetric { get; }

        /// <summary>Limite nommée dans le message d'erreur (ex. 16000
        /// tokens/min).</summary>
        public long? Limit { get; }

        public LlmRateLimitException(LlmRateLimitMotif motif, TimeSpan? retryDelay,
            string quotaMetric, long? limit, string message)
            : base(message)
        {
            Motif = motif;
            RetryDelay = retryDelay;
            QuotaMetric = quotaMetric;
            Limit = limit;
        }

        /// <summary>Libellé humain du motif (logs greppables, miroir de la
        /// table des quotas — TPM-entrée / TPM-sortie / RPM / RPD).</summary>
        public string MotifLabel => LlmRateLimitGate.Label(Motif, QuotaMetric);
    }

    /// <summary>
    /// Gate 429 in-memory : état appris par clé « provider|URL|modèle » —
    /// fenêtre de bridage jusqu'à, motif classifié, compteur de 429
    /// consécutifs et volume d'entrée réel observé (tokens prompt).
    /// Réactif uniquement — PAS de pacemaker proactif : on n'attend jamais
    /// avant un premier 429 (le gate ne coûte rien tant que le quota tient).
    /// <para>Doctrine 2026-11-10 (terrain, run EPG gemma-4-31b-it free tier :
    /// 3ᵉ appel dans la fenêtre TPM 16k tokens/min → 429 → bascule
    /// immédiate au suivant → phase perdue en dérive) : une reprise MÊME
    /// backend après la fenêtre servie par Google (ou 60 s par défaut) sauve
    /// le run ; RPD (fenêtre en heures) et le 2ᵉ 429 consécutif basculent
    /// immédiatement. Apprentissage EN MÉMOIRE, jamais un fichier d'état :
    /// tout disparaît au restart d'Emby, un 429 suffit à ré-apprendre.</para>
    /// </summary>
    internal static class LlmRateLimitGate
    {
        /// <summary>Plafond d'attente par défaut (tâches de fond : reco EPG,
        /// Tonight, audit, one-shot) — enveloppe « attente utile » : une
        /// fenêtre TPM calendaire se dégonfle en ≤ 60 s.</summary>
        internal static readonly TimeSpan DefaultWaitCeiling = TimeSpan.FromSeconds(90);

        /// <summary>Plafond chat interactif (l'usager patiente au-delà) :
        /// chat agent (LlmAgentService construit avec
        /// <c>rateLimitWaitCeiling</c>) et bouton « Tester » de la page de
        /// config. Trop court pour une fenêtre complète → bascule au
        /// suivant.</summary>
        internal static readonly TimeSpan InteractiveWaitCeiling = TimeSpan.FromSeconds(20);

        /// <summary>Fenêtre appliquée quand le serveur ne sert rien
        /// (Ollama cloud) : une fenêtre TPM calendaire. Les requêtes rejetées
        /// en 429 ne consomment pas de quota — au bout de la fenêtre l'appel
        /// repasse sans dette.</summary>
        private static readonly TimeSpan DefaultThrottle = TimeSpan.FromSeconds(60);

        private sealed class Entry
        {
            public LlmRateLimitMotif Motif = LlmRateLimitMotif.Unknown;
            public DateTime ThrottledUntilUtc = DateTime.MinValue;
            public int Consecutive;
            public int LastInputTokens;
            public string LastMetric;
            public long? LastLimit;
            public DateTime LastSuccessUtc = DateTime.MinValue;
        }

        private static readonly ConcurrentDictionary<string, Entry> _entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly object _lock = new object();

        /// <summary>Clé de gate d'un backend : « provider|URL résolue|modèle »
        /// — résolution d'URL identique au dispatch <see cref="LlmClient"/>
        /// (DefaultUrl si vide). DOIT rester en phase avec LlmClient.ChatAsync.</summary>
        internal static string KeyOf(LlmProvider provider, string url, string model)
        {
            var u = string.IsNullOrWhiteSpace(url)
                ? LlmProviderHelper.DefaultUrl(provider)
                : url.TrimEnd('/');
            return provider + "|" + u + "|" + model;
        }

        /// <summary>
        /// Marque le backend bridé à la suite d'un 429. <paramref name="retryDelay"/>
        /// = fenêtre servie (Google RetryInfo / Retry-After) ; null → fenêtre
        /// calendaire par défaut. Metric + limite sont mémorisées pour les
        /// lignes de bascule du gate (diagnostic). Le compteur de 429
        /// consécutifs ne vit que DANS la fenêtre : expirée, le prochain 429
        /// repart à 1.
        /// </summary>
        internal static void MarkThrottled(string key, LlmRateLimitMotif motif, TimeSpan? retryDelay,
            string quotaMetric = null, long? limit = null)
        {
            var window = retryDelay.HasValue && retryDelay.Value > TimeSpan.Zero
                ? retryDelay.Value : DefaultThrottle;
            lock (_lock)
            {
                var e = _entries.GetOrAdd(key, _ => new Entry());
                var now = DateTime.UtcNow;
                e.Consecutive = now < e.ThrottledUntilUtc ? e.Consecutive + 1 : 1;
                e.Motif = motif;
                e.LastMetric = quotaMetric;
                e.LastLimit = limit;
                e.ThrottledUntilUtc = now.Add(window);
            }
        }

        /// <summary>
        /// Enregistre un appel réussi : lève le bridage appris, remet le
        /// compteur de 429 consécutifs à zéro et apprend le volume d'entrée
        /// réel (tokens prompt) de ce (backend, modèle) — télémétrie du gate
        /// TPM. Sans effet sur un provider qui ne passe pas par le gate.
        /// </summary>
        internal static void RecordSuccess(string key, int inputTokens)
        {
            lock (_lock)
            {
                var e = _entries.GetOrAdd(key, _ => new Entry());
                if (inputTokens > 0) e.LastInputTokens = inputTokens;
                e.Consecutive = 0;
                e.ThrottledUntilUtc = DateTime.MinValue;
                e.Motif = LlmRateLimitMotif.Unknown;
                e.LastSuccessUtc = DateTime.UtcNow;
            }
        }

        /// <summary>Temps restant avant reprise tentable (Zero = libre —
        /// jamais bridé, ou fenêtre expirée).</summary>
        internal static TimeSpan TimeUntilRetry(string key)
        {
            lock (_lock)
            {
                return _entries.TryGetValue(key, out var e) && e.ThrottledUntilUtc > DateTime.UtcNow
                    ? e.ThrottledUntilUtc - DateTime.UtcNow
                    : TimeSpan.Zero;
            }
        }

        /// <summary>Nombre de 429 consécutifs dans la fenêtre courante
        /// (0 = pas de séquence vivante ; la fenêtre expirée remet à zéro).</summary>
        internal static int ConsecutiveThrottles(string key)
        {
            lock (_lock)
            {
                return _entries.TryGetValue(key, out var e) && e.ThrottledUntilUtc > DateTime.UtcNow
                    ? e.Consecutive : 0;
            }
        }

        /// <summary>
        /// true = une reprise MÊME backend après ce 429 est admissible :
        /// motif pas quotidien (RPD = fenêtre en heures, aucune attente
        /// sensée), premier 429 de la séquence (le 2ᵉ consécutif cède,
        /// fail-loud), et fenêtre tenable dans le plafond de surface.
        /// L'attente effective a lieu dans le pré-contrôle de
        /// <see cref="LlmClient.ChatAsync"/> au moment de la relance —
        /// les boucles DÉCIDENT, le client DORT.
        /// </summary>
        internal static bool CanResume(LlmRateLimitException ex, string key, TimeSpan waitCeiling)
        {
            if (ex == null || ex.Motif == LlmRateLimitMotif.Rpd) return false;
            if (ConsecutiveThrottles(key) > 1) return false;
            var remaining = TimeUntilRetry(key);
            return remaining > TimeSpan.Zero && remaining <= waitCeiling;
        }

        /// <summary>
        /// Pré-contrôle AVANT tout HTTP (providers à quotas : Gemini,
        /// ollama_cloud). Backend encore bridé par un 429 récent :
        /// <list type="bullet">
        /// <item>RPD, 2ᵉ 429 consécutif, ou fenêtre restante &gt; plafond →
        /// throw immédiat (le caller bascule sans roundtrip ni attente) ;</item>
        /// <item>fenêtre restante ≤ plafond → attente ct-aware puis retour —
        /// l'appel part normalement, la fenêtre s'est dégonflée.</item>
        /// </list>
        /// Rien à faire → retour immédiat (cas nominal, zéro coût).
        /// </summary>
        internal static async Task PrecheckAsync(string key, string model, TimeSpan waitCeiling,
            ILogger logger, CancellationToken ct)
        {
            TimeSpan remaining;
            LlmRateLimitMotif motif;
            int consecutive;
            string metric;
            long? limit;
            lock (_lock)
            {
                if (_entries.TryGetValue(key, out var e) && e.ThrottledUntilUtc > DateTime.UtcNow)
                {
                    remaining = e.ThrottledUntilUtc - DateTime.UtcNow;
                    motif = e.Motif;
                    consecutive = e.Consecutive;
                    metric = e.LastMetric;
                    limit = e.LastLimit;
                }
                else
                {
                    return; // cas nominal : aucun 429 récent sur ce (backend, modèle)
                }
            }

            var s = (int)Math.Ceiling(remaining.TotalSeconds);
            if (motif == LlmRateLimitMotif.Rpd)
                throw new LlmRateLimitException(motif, remaining, metric, limit,
                    model + " : 429 quota quotidien (RPD) déjà atteint — bascule sans attente (gate).");
            if (consecutive >= 2)
                throw new LlmRateLimitException(motif, remaining, metric, limit,
                    model + " : 2ᵉ 429 consécutif — bascule sans attente (gate).");
            if (remaining > waitCeiling)
                throw new LlmRateLimitException(motif, remaining, metric, limit,
                    model + " : 429 motif=" + Label(motif, metric) +
                    ", fenêtre restante " + s + " s > plafond " + (int)waitCeiling.TotalSeconds +
                    " s — bascule sans attente (gate).");

            logger?.Info("[LLM_AI] Gate 429 : {0} encore bridé (motif={1}, fenêtre restante {2} s ≤ plafond {3} s) — attente puis appel.",
                model, Label(motif, metric), s, (int)waitCeiling.TotalSeconds);
            await Task.Delay(remaining, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Parse le corps 429 de Gemini : motif depuis
        /// <c>violations[].quotaMetric/quotaId</c> (ou le message — « metric:
        /// …_input_token_count, limit: 16000 »), fenêtre depuis
        /// <c>RetryInfo.retryDelay</c> (« 26s », « 1.5s »). Best-effort : un
        /// corps illisible produit motif Unknown + fenêtre par défaut.
        /// </summary>
        internal static LlmRateLimitException ParseGemini429(string body)
        {
            var motif = LlmRateLimitMotif.Unknown;
            string metric = null;
            long? limit = null;
            TimeSpan? delay = null;
            string message = null;

            try
            {
                using (var doc = JsonDocument.Parse(body ?? string.Empty))
                {
                    if (doc.RootElement.TryGetProperty("error", out var err))
                    {
                        if (err.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                            message = msg.GetString();
                        if (err.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var d in details.EnumerateArray())
                            {
                                if (!delay.HasValue && d.TryGetProperty("retryDelay", out var rd) &&
                                    rd.ValueKind == JsonValueKind.String)
                                    delay = ParseDelay(rd.GetString());
                                if (motif == LlmRateLimitMotif.Unknown &&
                                    d.TryGetProperty("violations", out var violations) &&
                                    violations.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var v in violations.EnumerateArray())
                                    {
                                        var qm = GetString(v, "quotaMetric");
                                        var qi = GetString(v, "quotaId");
                                        var m2 = Classify(qm, qi);
                                        if (m2 != LlmRateLimitMotif.Unknown)
                                        {
                                            motif = m2;
                                            metric = qm ?? qi;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // corps non JSON — best-effort, fallback message ci-dessous
            }

            // Fallback sur le texte du message (proven live : « * Quota
            // exceeded for metric: …input_token_count, limit: 16000… »).
            if (!string.IsNullOrEmpty(message))
            {
                if (motif == LlmRateLimitMotif.Unknown)
                {
                    var m2 = Classify(message, null);
                    if (m2 != LlmRateLimitMotif.Unknown)
                    {
                        motif = m2;
                        var mm = Regex.Match(message, @"metric:\s*([^\s,]+)");
                        metric = mm.Success ? mm.Groups[1].Value : null;
                    }
                }
                if (!limit.HasValue)
                {
                    var lm = Regex.Match(message, @"limit:\s*(\d+)");
                    if (lm.Success)
                        limit = long.Parse(lm.Groups[1].Value, CultureInfo.InvariantCulture);
                }
            }

            var detail = limit.HasValue ? ", limite=" + limit.Value : string.Empty;
            detail += delay.HasValue
                ? ", fenêtre servie " + (int)Math.Ceiling(delay.Value.TotalSeconds) + " s"
                : string.Empty;
            return new LlmRateLimitException(motif, delay, metric, limit,
                "Gemini a répondu HTTP 429 (motif=" + Label(motif, metric) + detail + ") — corps : " +
                (string.IsNullOrEmpty(message) ? Truncate(body, 300) : Truncate(message, 300)));
        }

        /// <summary>
        /// 429 Ollama cloud : ni motif ni fenêtre servis de façon fiable →
        /// motif Unknown ; le gate applique sa fenêtre calendaire par défaut
        /// (60 s), recadrable par l'en-tête Retry-After (relu par le caller).
        /// </summary>
        internal static LlmRateLimitException ParseOllama429(string body)
        {
            string message = null;
            try
            {
                using (var doc = JsonDocument.Parse(body ?? string.Empty))
                {
                    if (doc.RootElement.TryGetProperty("error", out var err) &&
                        err.ValueKind == JsonValueKind.String)
                        message = err.GetString();
                }
            }
            catch
            {
                // corps non JSON — best-effort
            }

            return new LlmRateLimitException(LlmRateLimitMotif.Unknown, null, null, null,
                "Ollama a répondu HTTP 429 (aucune fenêtre servie — gate fenêtre par défaut)" +
                (message != null ? " : " + Truncate(message, 200) : string.Empty));
        }

        /// <summary>Libellé humain du motif — TPM-entrée / TPM-sortie selon
        /// la metric, RPM, RPD, ou inconnu.</summary>
        internal static string Label(LlmRateLimitMotif motif, string metric)
        {
            switch (motif)
            {
                case LlmRateLimitMotif.Tpm:
                    return metric != null && metric.IndexOf("output", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "TPM-sortie" : "TPM-entrée";
                case LlmRateLimitMotif.Rpm: return "RPM";
                case LlmRateLimitMotif.Rpd: return "RPD (quota quotidien)";
                default: return "motif inconnu";
            }
        }

        private static LlmRateLimitMotif Classify(string metric, string quotaId)
        {
            var s = ((metric ?? string.Empty) + " " + (quotaId ?? string.Empty)).ToLowerInvariant();
            if (s.Length == 0) return LlmRateLimitMotif.Unknown;
            if (s.Contains("per_day")) return LlmRateLimitMotif.Rpd;       // fenêtre jour → bascule
            if (s.Contains("token_count")) return LlmRateLimitMotif.Tpm;   // tokens/min (entrée ou sortie)
            if (s.Contains("request_count")) return LlmRateLimitMotif.Rpm; // requêtes/min
            return LlmRateLimitMotif.Unknown;
        }

        /// <summary>« 26s » / « 1.5s » / « 2m » / « 1h » → TimeSpan ; null sinon.</summary>
        private static TimeSpan? ParseDelay(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var m = Regex.Match(s.Trim(), @"^(\d+(?:\.\d+)?)\s*(s|m|h)$", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            switch (m.Groups[2].Value.ToLowerInvariant())
            {
                case "h": return TimeSpan.FromHours(v);
                case "m": return TimeSpan.FromMinutes(v);
                default: return TimeSpan.FromSeconds(v);
            }
        }

        private static string GetString(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");
    }
}
