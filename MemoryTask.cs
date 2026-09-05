using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace LLM_AI
{
    /// <summary>
    /// Tâche planifiée hebdomadaire de la <b>mémoire réflexive</b> (opt-in
    /// <see cref="PluginConfiguration.MemoryCardEnabled"/>, défaut dimanche
    /// 4 h 30 — après l'analyse hebdo classique de 4 h). C'est la Phase C du
    /// concept : le LLM gère et met à jour lui-même sa <b>fiche mémoire</b>
    /// persistante (~250 mots, Markdown) — ses réussites, ses échecs, ses
    /// stratégies pour les prochains runs et chats.
    /// <para>
    /// Pour UN foyer (la fiche est globale) :
    /// <list type="bullet">
    /// <item>construit en C# (déterministe, zéro LLM) le tableau d'événements
    ///   bruts de la semaine : jointure des stores de la Phase A/B —
    ///   <see cref="DecisionStore"/> (décisions avec leur RAISON et la version
    ///   de fiche en vigueur) × <see cref="DecisionStore"/> télémétrie
    ///   (fraction lue : rejet immédiat &lt; 5 %, abandon 5–50 %, validé
    ///   &gt; 80 %) × pools de candidats (ce qui avait été écarté — distingue
    ///   une mauvaise reco d'une erreur de classement) ×
    ///   <see cref="EpgSnapshotStore"/> (durée de diffusion → % du direct) ;</item>
    /// <item>fait produire au LLM (un seul appel sans outils,
    ///   <see cref="LlmRunner.RunSynthesisAsync"/>) la NOUVELLE fiche, en
    ///   partant de l'actuelle ;</item>
    /// <item>persiste (version++, historisation immuable,
    ///   <see cref="MemoryCard.Save"/>) — elle est réinjectée dans les prompts
    ///   des runs suivants par <see cref="MemoryCard.BuildInjectionBlock"/> ;
    ///   quand la fiche est active, elle REMPLACE le bloc de directives de la
    ///   boucle de rétroaction classique dans les injections.</item>
    /// </list></para>
    /// <para><b>Fail-open</b> : pas de signal → pas de run ; un échec LLM
    /// conserve la fiche précédente ; aucune erreur ne casse les runs aval.</para>
    /// <para>Découverte par scanning d'assembly (comme
    /// <see cref="RecoAnalysisTask"/>) — services Emby injectés par DI.</para>
    /// </summary>
    public class MemoryTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _library;
        private readonly IServerApplicationHost _host;

        // Orchestration LLM partagée (résolution backends + appel sans
        // outils avec repli). Construite une fois à l'instanciation.
        private readonly LlmRunner _runner;

        public MemoryTask(
            ILogger logger,
            IJsonSerializer jsonSerializer,
            ILibraryManager library,
            IUserManager users,
            ILiveTvManager liveTv,
            IServerApplicationHost host)
        {
            _logger = logger;
            _library = library;
            _host = host;
            _runner = new LlmRunner(logger, jsonSerializer, library, users, liveTv, host);
        }

        public string Name => I18n.S("task.memory.name", I18n.ResolveDisplayLangKey(_host));

        /// <summary>Identifiant stable de la tâche.</summary>
        public string Key => "a9b4c7d1-6e52-4f83-9a10-3c7d5e8b2f41";

        public string Description => I18n.S("task.memory.desc", I18n.ResolveDisplayLangKey(_host));

        public string Category => I18n.S("task.category", I18n.ResolveDisplayLangKey(_host));

        public bool IsHidden => false;

        public bool IsEnabled => Plugin.Instance?.Configuration?.MemoryCardEnabled ?? false;

        public bool IsLogged => true;

        // ------------------------------------------------------------------
        //  Prompts (system)
        // ------------------------------------------------------------------

        internal const string MEMORY_ROLE =
            "Tu es l'assistant de recommandations TV/cinéma d'un serveur Emby domestique. " +
            "Une fois par semaine, tu réécris TA PROPRE FICHE MÉMOIRE : un résumé de ce que " +
            "tu sais de l'usager, de tes réussites et de tes échecs, et des stratégies " +
            "concrètes pour tes futures recommandations et conversations. On te fournit " +
            "ta fiche actuelle et les événements bruts de la semaine (décisions journalisées " +
            "avec leur résultat de visionnage, calibration de tes versions de fiche, " +
            "candidats écartés, visionnages sans recommandation).";

        internal const string MEMORY_RULES =
            "### TA TÂCHE\n" +
            "Produis la NOUVELLE fiche mémoire, en Markdown, qui remplacera l'actuelle.\n" +
            "### RÈGLES\n" +
            "- Repars TOUJOURS de la fiche actuelle : garde ce qui reste vrai, corrige ce que " +
            "  les événements contredisent, ajoute les nouveaux signaux. Ne repars jamais de zéro.\n" +
            "- UTILISE ces sections exactes (une seule fois chacune) :\n" +
            "  ## Ce que je sais de l'usager\n" +
            "  ## Ce qui a marché\n" +
            "  ## Ce qui a échoué et pourquoi\n" +
            "  ## Stratégies pour les prochaines recommandations\n" +
            "  ## Zones d'incertitude\n" +
            "- AUTO-ÉVALUATION : dans « Ce qui a échoué et pourquoi », nomme explicitement la " +
            "  croyance ou la stratégie qui a échoué (ex. « j'ai répété une reco similaire à " +
            "  une déjà ignorée »), pas seulement le titre raté.\n" +
            "- Nuance : un signal faible (1-2 cas) est formulé avec réserve (« semble », « un " +
            "  seul cas ») ; un signal fort (3+ cas) peut être affirmé. Ne retire une croyance " +
            "  que sur une preuve contraire (3+ cas ou calibration claire).\n" +
            "- PRÉSERVE LA DIVERSITÉ : n'interdis jamais une catégorie entière sur une seule " +
            "  semaine ; garde des axes d'exploration dans « Zones d'incertitude ».\n" +
            "- N'invente rien : chaque affirmation doit découler des événements fournis.\n" +
            "- Maximum ~250 mots. Puces courtes. Pas de JSON, pas de préambule.\n" +
            "- Réponds UNIQUEMENT par la fiche Markdown (commençant par « ## »), sans préambule.";

        // ------------------------------------------------------------------
        //  Exécution
        // ------------------------------------------------------------------

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
            {
                _logger?.Warn("[LLM_AI] Mémoire : aucune configuration disponible — ignorée.");
                return;
            }
            if (!cfg.MemoryCardEnabled)
            {
                _logger?.Info("[LLM_AI] Mémoire : désactivée (MemoryCardEnabled) — rien à faire.");
                return;
            }
            if (!(cfg.DecisionLogEnabled || cfg.PlaybackTelemetryEnabled))
            {
                _logger?.Info("[LLM_AI] Mémoire : aucun store activé (DecisionLogEnabled / PlaybackTelemetryEnabled) — rien à faire.");
                return;
            }

            progress?.Report(5);

            var weekCutoff = DateTimeOffset.Now.AddDays(-7);
            var decisions = DecisionStoreParse();
            var pools = DecisionStoreParsePools();
            var playback = DecisionStoreParsePlayback();
            var snapshot = EpgSnapshotStore.Parse();
            var (current, history) = MemoryCard.Load();

            var week = decisions.Where(d => d.Date >= weekCutoff).ToList();
            var weekPlayback = playback.Where(p => p.Date >= weekCutoff).ToList();
            if (week.Count == 0 && weekPlayback.Count == 0)
            {
                _logger?.Info("[LLM_AI] Mémoire : aucun événement cette semaine (décisions ni lectures) — rien à faire.");
                return;
            }
            _logger?.Info("[LLM_AI] Mémoire : {0} décision(s) et {1} lecture(s) cette semaine (store complet : {2} déc., {3} lect.).",
                week.Count, weekPlayback.Count, decisions.Count, playback.Count);

            try
            {
                string table = BuildEventTable(cfg, decisions, week, playback, weekPlayback,
                    pools, snapshot, current);
                if (table == null)
                {
                    _logger?.Info("[LLM_AI] Mémoire : tableau vide (aucun signal exploitable) — rien à faire.");
                    return;
                }

                progress?.Report(40);

                var system = MEMORY_ROLE + "\n\n" + MEMORY_RULES
                    + "\n\n" + LlmAgentService.BuildLanguageDirective(cfg?.ResponseLanguage);
                var (reply, ok) = await _runner.RunSynthesisAsync(cfg, "MÉMOIRE", system, table,
                    cancellationToken).ConfigureAwait(false);
                if (!ok || string.IsNullOrWhiteSpace(reply))
                {
                    _logger?.Info("[LLM_AI] Mémoire : pas de réponse LLM — fiche précédente conservée.");
                    return;
                }

                var text = NormalizeCard(reply);
                if (string.IsNullOrWhiteSpace(text))
                {
                    _logger?.Info("[LLM_AI] Mémoire : réponse inutilisable — fiche précédente conservée.");
                    return;
                }

                progress?.Report(85);

                // Historisation immuable : la version courante devient une
                // entrée d'historique (les 4 dernières gardées).
                if (current.Version > 0 && !string.IsNullOrWhiteSpace(current.Text))
                {
                    history.Insert(0, new MemoryCardData
                    {
                        Version = current.Version,
                        Updated = current.Updated,
                        Text = current.Text
                    });
                    while (history.Count > MemoryCard.HistoryMax)
                        history.RemoveAt(history.Count - 1);
                }
                var updated = new MemoryCardData
                {
                    Version = current.Version + 1,
                    Updated = DateTimeOffset.UtcNow,
                    Text = text
                };
                MemoryCard.Save(updated, history, _logger);
                _logger?.Info("[LLM_AI] Mémoire : fiche v{0} enregistrée ({1} caractères) :\n{2}",
                    updated.Version, text.Length, text);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Fail-open : la fiche précédente reste en place.
                _logger?.ErrorException("[LLM_AI] Mémoire : échec global : {0}", ex, ex.Message);
                throw;
            }
            finally
            {
                progress?.Report(100);
            }
        }

        // ------------------------------------------------------------------
        //  Jointure déterministe (zéro LLM)
        // ------------------------------------------------------------------

        /// <summary>Catégorie comportementale d'une fraction lue.</summary>
        internal static string CategoryOf(double? pct)
        {
            if (!pct.HasValue) return "?";
            if (pct.Value < 0.05) return "rejet immédiat";
            if (pct.Value < 0.5) return "abandon";
            if (pct.Value < 0.8) return "partiel";
            return "validé";
        }

        /// <summary>
        /// Construit le tableau d'événements bruts de la semaine (texte) :
        /// décisions × résultats, calibration, écartés, vu-sans-reco, signaux
        /// explicites, fiche actuelle. Null si aucun signal exploitable.
        /// </summary>
        private string BuildEventTable(PluginConfiguration cfg,
            List<DecisionEntry> decisions, List<DecisionEntry> week,
            List<PlaybackEntry> playback, List<PlaybackEntry> weekPlayback,
            List<RunPool> pools, List<EpgSnapshotEntry> snapshot,
            MemoryCardData current)
        {
            var sb = new StringBuilder();

            // Index des pools par runId.
            var poolByRun = pools.Where(p => p != null && !string.IsNullOrEmpty(p.RunId))
                .GroupBy(p => p.RunId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            // Index des snapshots par programId.
            var snapByPid = snapshot.Where(s => s != null && !string.IsNullOrEmpty(s.ProgramId))
                .GroupBy(s => s.ProgramId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var decisionTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in decisions)
                decisionTitles.Add(LlmRunner.NormTitle(d.Title));

            // 1) Décisions de la semaine × résultat de visionnage.
            sb.AppendLine("### DÉCISIONS DE LA SEMAINE (recommandations émises, avec leur raison)");
            sb.AppendLine("(résultat : fraction lue par l'usager via la télémétrie ; IGNORÉ = aucune lecture rattachée)");
            sb.AppendLine("(créneau = heure locale de la lecture ; v = version de la fiche en vigueur à l'émission)");
            int shown = 0;
            const int DecisionCap = 40;
            foreach (var d in week.OrderBy(d => d.Date))
            {
                if (d.Kind == "drop") continue; // signalés séparément
                decisionTitles.Add(LlmRunner.NormTitle(d.Title));
                var outcome = OutcomeOf(d, playback, snapByPid);
                var cr = d.Kind == "record" ? "(foyer)" : "";
                sb.AppendLine("- [" + d.Kind + (d.Mv > 0 ? " v" + d.Mv.ToString(CultureInfo.InvariantCulture) : "") + "] « " + d.Title + " » " + cr
                    + (string.IsNullOrWhiteSpace(d.Reason) ? "" : " — raison: " + d.Reason)
                    + " → " + outcome);
                shown++;
                if (shown >= DecisionCap) break;
            }
            if (shown == 0) sb.AppendLine("(aucune)");
            if (shown < week.Count(d => d.Kind != "drop"))
                sb.AppendLine("… (+" + (week.Count(d => d.Kind != "drop") - shown) + " autres)");

            // 2) Calibration des versions de fiche.
            var byMv = week.Where(d => d.Kind != "drop" && d.Mv > 0).GroupBy(d => d.Mv).ToList();
            if (byMv.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### CALIBRATION DES VERSIONS DE LA FICHE (ta propre auto-évaluation)");
                foreach (var g in byMv.OrderBy(g => g.Key))
                {
                    int ok = 0, bad = 0, ignored = 0;
                    foreach (var d in g)
                    {
                        var o = OutcomeOf(d, playback, snapByPid);
                        if (o.StartsWith("validé") || o.StartsWith("partiel")) ok++;
                        else if (o.StartsWith("rejet") || o.StartsWith("abandon")) bad++;
                        else ignored++;
                    }
                    sb.AppendLine("- recos émises sous la fiche v" + g.Key + " : " + g.Count()
                        + " — validé/partiel " + ok + ", rejet/abandon " + bad + ", ignoré " + ignored);
                }
                sb.AppendLine("(si une version ancienne perforne nettement mieux que la courante, " +
                              "réexamine les changements que tu avais introduits)");
            }

            // 3) Candidats écartés (pools des runs de la semaine).
            sb.AppendLine();
            sb.AppendLine("### CANDIDATS ÉCARTÉS (présents dans le menu soumis, non recommandés)");
            int poolsShown = 0;
            foreach (var d in week.Where(d => d.Kind != "drop" && !string.IsNullOrEmpty(d.RunId))
                .Select(d => d.RunId).Distinct(StringComparer.Ordinal).Take(6))
            {
                if (!poolByRun.TryGetValue(d, out var pool) || pool?.Candidates == null) continue;
                var chosen = week.Where(x => string.Equals(x.RunId, d, StringComparison.Ordinal))
                    .Select(x => LlmRunner.NormTitle(x.Title)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var notChosen = pool.Candidates
                    .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Title)
                                && !chosen.Contains(LlmRunner.NormTitle(c.Title)))
                    .Take(8).ToList();
                if (notChosen.Count == 0) continue;
                sb.AppendLine("- run " + d + " (" + pool.Date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ", " +
                    pool.Candidates.Count + " candidats) — écartés notamment : "
                    + string.Join(" ; ", notChosen.Select(c => "« " + c.Title + " » (" + c.Source + ")")));
                poolsShown++;
            }
            if (poolsShown == 0) sb.AppendLine("(aucun pool disponible cette semaine)");

            // 4) Vu sans recommandation (opportunités manquées). Le titre de
            // chaque item est résolu côté C# (ItemIdResolver) pour exclure
            // proprement ce qui figure déjà dans une décision.
            sb.AppendLine();
            sb.AppendLine("### VU SANS RECOMMANDATION (cette semaine — opportunités manquées)");
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            int surprisesShown = 0;
            foreach (var p in weekPlayback
                .Where(p => p != null && !string.IsNullOrEmpty(p.ItemId) && (p.Pct ?? 0) >= 0.5)
                .OrderByDescending(p => p.Date))
            {
                if (!seenIds.Add(p.ItemId)) continue;
                var title = ResolveTitle(p.ItemId);
                if (!string.IsNullOrWhiteSpace(title)
                    && decisionTitles.Contains(LlmRunner.NormTitle(title)))
                    continue; // déjà recommandé (jointure titre)
                var slot = p.Date.ToLocalTime();
                sb.AppendLine("- « " + (string.IsNullOrWhiteSpace(title) ? "?" : title)
                    + " » (" + CategoryOf(p.Pct)
                    + ", " + slot.ToString("ddd HH\\hmm", CultureInfo.CurrentCulture)
                    + ", " + p.Src + ")");
                surprisesShown++;
                if (surprisesShown >= 12) break;
            }
            if (surprisesShown == 0) sb.AppendLine("(rien)");

            // 5) Signaux explicites (rejets).
            var drops = week.Where(d => d.Kind == "drop").ToList();
            sb.AppendLine();
            sb.AppendLine("### SIGNAUX EXPLICITES (rejets « Oublier »)");
            if (drops.Count == 0) sb.AppendLine("(aucun)");
            foreach (var d in drops.Take(15))
                sb.AppendLine("- « " + d.Title + " »");

            // 6) Contexte situationnel agrégé (créneaux de lecture).
            sb.AppendLine();
            sb.AppendLine("### CRÉNEAUX DE LECTURE (cette semaine, toutes lectures)");
            foreach (var g in weekPlayback.GroupBy(p => new
                     {
                         Jour = (int)p.Date.ToLocalTime().DayOfWeek,
                         Creneau = p.Date.ToLocalTime().Hour / 4   // tranche 4 h
                     }).OrderBy(g => g.Key.Jour).ThenBy(g => g.Key.Creneau))
            {
                sb.AppendLine("- " + JourFr(g.Key.Jour) + " " + (g.Key.Creneau * 4) + "h-"
                    + ((g.Key.Creneau * 4) + 4) + "h : " + g.Count() + " lecture(s), % moyen "
                    + (g.Average(p => p.Pct ?? 0) * 100).ToString("0", CultureInfo.InvariantCulture) + " %");
            }

            // 7) Fiche actuelle.
            sb.AppendLine();
            sb.AppendLine("### TA FICHE ACTUELLE (v" + current.Version + ")");
            sb.AppendLine(string.IsNullOrWhiteSpace(current.Text)
                ? "(aucune — première rédaction)"
                : current.Text);

            return sb.ToString();
        }

        /// <summary>Résultat de visionnage d'une décision (texte court).</summary>
        private static string OutcomeOf(DecisionEntry d, List<PlaybackEntry> playback,
            Dictionary<string, EpgSnapshotEntry> snapByPid)
        {
            // Item bibliothèque/enregistrement : jointure directe par itemId.
            if (!string.IsNullOrEmpty(d.ItemId))
            {
                var p = playback.Where(x => string.Equals(x.ItemId, d.ItemId, StringComparison.Ordinal))
                    .OrderByDescending(x => x.Date).FirstOrDefault();
                if (p != null)
                {
                    // % lu : signal direct ; à défaut (durée d'œuvre inconnue
                    // au moment de la lecture), complétion binaire.
                    var effPct = p.Pct ?? (p.Completed ? 1.0 : (double?)null);
                    var pctTxt = effPct.HasValue
                        ? (effPct.Value * 100).ToString("0", CultureInfo.InvariantCulture) + " %"
                        : "sans %";
                    return CategoryOf(effPct) + " (" + pctTxt + ", "
                        + p.DurSec / 60 + " min, " + SlotOf(p) + ")";
                }
                return "IGNORÉ (aucune lecture rattachée)";
            }
            // Programme EPG (direct) : jointure via le snapshot (chaîne +
            // fenêtre de diffusion) → % = durée lue ÷ durée de diffusion.
            if (!string.IsNullOrEmpty(d.ProgramId)
                && snapByPid.TryGetValue(d.ProgramId, out var snap)
                && snap.RuntimeMin.HasValue && snap.RuntimeMin.Value > 0)
            {
                var start = snap.Start;
                var end = start.AddMinutes(snap.RuntimeMin.Value + 30);
                var p = playback.Where(x =>
                        string.Equals(x.Src, "livetv", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.Channel ?? "", snap.Channel ?? "", StringComparison.OrdinalIgnoreCase)
                        && x.Date >= start && x.Date <= end)
                    .OrderByDescending(x => x.Date).FirstOrDefault();
                if (p != null)
                {
                    var pct = (double)p.DurSec / (snap.RuntimeMin.Value * 60);
                    pct = Math.Max(0.0, Math.Min(1.0, pct));
                    return CategoryOf(pct) + " (direct " + snap.Channel + ", " + snap.RuntimeMin.Value + " min, "
                        + (pct * 100).ToString("0", CultureInfo.InvariantCulture) + " %)";
                }
                return "IGNORÉ (aucune lecture du direct rattachée)";
            }
            return "? (non joignable — item sans id ou snapshot absent)";
        }

        private static string SlotOf(PlaybackEntry p)
        {
            var lt = p.Date.ToLocalTime();
            return JourFr((int)lt.DayOfWeek) + " " + lt.Hour + "h";
        }

        private static string JourFr(int dow) => dow switch
        {
            0 => "dim", 1 => "lun", 2 => "mar", 3 => "mer", 4 => "jeu", 5 => "ven", _ => "sam"
        };

        /// <summary>Titre d'un item bibliothèque résolu (best-effort) — sert
        /// à la jointure titre des « vu sans recommandation ».</summary>
        private string ResolveTitle(string rawItemId)
        {
            if (string.IsNullOrWhiteSpace(rawItemId)) return "";
            try
            {
                return ItemIdResolver.Resolve(_library, rawItemId)?.Name ?? "";
            }
            catch { return ""; }
        }

        /// <summary>Nettoie la réponse LLM en fiche : retire les clôtures de
        /// code, borne au plafond. Vide si rien d'utilisable.</summary>
        internal static string NormalizeCard(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return "";
            var t = reply.Trim();
            if (t.StartsWith("```", StringComparison.Ordinal))
            {
                int firstNl = t.IndexOf('\n');
                if (firstNl > 0) t = t.Substring(firstNl + 1);
                int fence = t.LastIndexOf("```", StringComparison.Ordinal);
                if (fence >= 0) t = t.Substring(0, fence);
            }
            return t.Trim();
        }

        // ------------------------------------------------------------------
        //  Accès stores (wrappers tolérants)
        // ------------------------------------------------------------------

        private static List<DecisionEntry> DecisionStoreParse() => DecisionStore.ParseAllDecisions();

        private static List<RunPool> DecisionStoreParsePools() => DecisionStore.ParseAllPools();

        private static List<PlaybackEntry> DecisionStoreParsePlayback() => DecisionStore.ParseAllPlayback();

        // ------------------------------------------------------------------
        //  Déclencheur
        // ------------------------------------------------------------------

        /// <summary>Dimanche 4 h 30 — après l'analyse hebdo classique (4 h),
        /// avant les runs diurnes. La tâche ne tourne que si
        /// <see cref="PluginConfiguration.MemoryCardEnabled"/> est coché.</summary>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = "WeeklyTrigger",
                DayOfWeek = 0,
                TimeOfDayTicks = new TimeSpan(4, 30, 0).Ticks
            };
        }
    }
}