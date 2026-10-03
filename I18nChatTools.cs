using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>Résolution des natives + overlay et plomberie de réponse pour
    /// les tools d'atelier (T1d) — partagée par <see cref="I18nGetTool"/> et
    /// <see cref="I18nSetKeyTool"/> : une clé d'interface vit dans UNE famille
    /// (web | server | ext) déterminée par les natives EN ; l'overlay ne
    /// sert que ce qui copie cette forme. Les fonctions de résolution sont
    /// pures en lecture (l'écriture vit dans <see cref="I18nGenerator"/>).</summary>
    internal static class I18nChatKeys
    {
        /// <summary>Résout une clé exacte dans les natives (ordre web →
        /// server → ext). Sec null + En null = clé inconnue. La paire FR suit
        /// la famille (les natives FR sont web-only dans i18n.js et
        /// server-only dans s_res ; ext n'en a pas — la famille ext parle EN
        /// seul côté sources).</summary>
        internal static (string Sec, string En, string Fr) ResolveNative(
            string key, ILogger logger)
        {
            if (string.IsNullOrEmpty(key)) return (null, null, null);
            var enWeb = I18nApiService.WebNativesLang("en", logger);
            if (enWeb != null && enWeb.TryGetValue(key, out var w))
                return ("web", w, FrOf("web", key, logger));
            var enServer = I18n.EnServerDict;
            if (enServer != null && enServer.TryGetValue(key, out var s))
                return ("server", s, FrOf("server", key, logger));
            var enExt = I18n.EnExtDict;
            if (enExt != null && enExt.TryGetValue(key, out var e))
                return ("ext", e, null);
            return (null, null, null);
        }

        private static string FrOf(string sec, string key, ILogger logger)
        {
            IReadOnlyDictionary<string, string> frDict = sec == "web"
                ? I18nApiService.WebNativesLang("fr", logger)
                : sec == "server" ? I18n.ServerDictFor("fr") : null;
            return frDict != null && frDict.TryGetValue(key, out var f) ? f : null;
        }

        /// <summary>Toutes les valeurs d'overlay portées par cette clé, par
        /// langue canonique : fam (la section où la valeur VIT — une valeur
        /// déposée hors de sa famille est inerte pour le service),
        /// dernière gagne si la clé apparaît deux fois (fichier manuel).</summary>
        internal static Dictionary<string, (string Fam, string Value)> OverlayFor(string key)
        {
            var res = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            foreach (var langKv in I18nGenerator.ReadOverlayValues())
                foreach (var famKv in langKv.Value)
                    if (famKv.Value.TryGetValue(key, out var v))
                        res[langKv.Key] = (famKv.Key, v);
            return res;
        }

        /// <summary>Suggestions « clé proche » (sous-chaîne, ordre web →
        /// server → ext, cap 12) pour la réponse à une clé inconnue — la
        /// faute de frappe revient au LLM avec un indice, pas avec un échec
        /// sec.</summary>
        internal static List<string> Hints(string key, ILogger logger)
        {
            var hints = new List<string>();
            foreach (var dict in new[]
            {
                I18nApiService.WebNativesLang("en", logger),
                I18n.EnServerDict, I18n.EnExtDict
            })
            {
                if (dict == null) continue;
                foreach (var k in dict.Keys)
                {
                    if (hints.Count >= 12) return hints;
                    if (k.IndexOf(key ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                        hints.Add(k);
                }
            }
            return hints;
        }

        /// <summary>Lecture d'un argument string du call tool (tolérant :
        /// non-objet, manque, non-string → null). Même forme que les privates
        /// de ChatPromptsTool — un seul exemplaire ici, deux classes
        /// partagent.</summary>
        internal static string ArgString(JsonElement args, string name)
        {
            try
            {
                if (args.ValueKind != JsonValueKind.Object) return null;
                if (!args.TryGetProperty(name, out var v)) return null;
                return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            }
            catch { return null; }
        }

        /// <summary>Sérialisation de réponse tool (jamais levante — repli :
        /// mini-JSON d'échec que la boucle de l'agent sait afficher).</summary>
        internal static string Json(object o)
        {
            try { return System.Text.Json.JsonSerializer.Serialize(o); }
            catch { return "{\"status\":\"failed\",\"detail\":\"sérialisation\"}"; }
        }

        /// <summary>Étiquette stable (pour le LLM) d'un verdict du
        /// validateur — miroir des règles du chargeur, pas une prose
        /// localisée : le JSON de tool est lu par le modèle (les libellés
        /// humains vivent dans la prose qu'il écrit).</summary>
        internal static string VerdictLabel(I18nDoses.Verdict v)
            => v == I18nDoses.Verdict.Ok ? "ok"
            : v == I18nDoses.Verdict.Empty ? "vide"
            : v == I18nDoses.Verdict.PlaceholderMismatch ? "placeholders divergents"
            : "balises divergentes";
    }

    /// <summary>
    /// Tool de chat <c>i18n_get</c> (v1.17.0 T1d — atelier de langues) :
    /// lecture COMPLÈTE d'une clé d'interface — natives EN + FR (l'intention
    /// du mainteneur), valeurs d'overlay par langue avec leur verdict
    /// structurel (le chargeur les servira ou les laissera tomber). C'est
    /// l'instrument de revue des langues générées : le LLM voit pourquoi une
    /// valeur est sautée AVANT de proposer une correction.
    /// Lecture pure, sans approbation ni écriture — safe par construction.
    /// </summary>
    internal sealed class I18nGetTool : ILlmTool
    {
        private readonly ILogger _logger;

        public I18nGetTool(ILogger logger) { _logger = logger; }

        public string Name => "i18n_get";

        public string Description =>
            "Lit l'état complet d'UNE chaîne d'interface du plugin (atelier de langues) : " +
            "natives EN (source formelle) et FR (intention du mainteneur), puis les valeurs " +
            "d'overlay par langue générée avec leur validité structurelle (placeholders {n}, " +
            "balises — une valeur « vide/placeholders divergents/balises divergentes » est " +
            "SAUTÉE par le chargeur, la clé sert l'anglais natif). Utilisez-le avant toute " +
            "correction (read-modify-write) : il montre la valeur actuelle, la famille exacte " +
            "(web|server|ext) et le verdict de chaque langue. Une clé inconnue renvoie des " +
            "suggestions proches (les familles : web = libellés des pages, server = messages " +
            "HTTP/jalons, ext = chat externe).";

        public string ArgumentsSchema =>
            "{\"type\":\"object\",\"properties\":{" +
            "\"key\":{\"type\":\"string\",\"description\":\"Clé exacte (ex. act.ok.2, cfg.i18n.run)\"}}," +
            "\"required\":[\"key\"]}";

        public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            string key = (I18nChatKeys.ArgString(args, "key") ?? "").Trim();
            try
            {
                if (key.Length == 0)
                    return Task.FromResult(I18nChatKeys.Json(new { status = "refused",
                        detail = "key requise (clé exacte — ex. cfg.chat.sorry)." }));

                var (sec, en, fr) = I18nChatKeys.ResolveNative(key, _logger);
                if (sec == null)
                    return Task.FromResult(I18nChatKeys.Json(new { status = "refused",
                        detail = "clé introuvable dans les natives (web/server/ext) — jamais " +
                                 "inventée : une correction ne porte que des clés réelles.",
                        hints = I18nChatKeys.Hints(key, _logger) }));

                var overlay = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var ov in I18nChatKeys.OverlayFor(key))
                {
                    bool famOk = string.Equals(ov.Value.Fam, sec, StringComparison.Ordinal);
                    var verdict = I18nDoses.Validate(en, ov.Value.Value);
                    bool identical = string.Equals(ov.Value.Value, en, StringComparison.Ordinal)
                        && !I18nDoses.TriviallyIdenticalEn(en, ov.Value.Value);
                    overlay[ov.Key] = new
                    {
                        fam = ov.Value.Fam,
                        value = ov.Value.Value,
                        served = famOk && verdict == I18nDoses.Verdict.Ok,
                        verdict = famOk ? I18nChatKeys.VerdictLabel(verdict) : "hors famille (inerte)",
                        identical_en = identical
                    };
                }

                return Task.FromResult(I18nChatKeys.Json(new
                {
                    status = "ok",
                    key,
                    family = sec,
                    natives = new { en, fr },
                    overlay
                }));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(I18nChatKeys.Json(new { status = "failed", detail = "annulé" }));
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Chat i18n_get : {0}", ex.Message);
                return Task.FromResult(I18nChatKeys.Json(new { status = "failed", detail = ex.Message }));
            }
        }

    }

    /// <summary>
    /// Tool de chat <c>i18n_set_key</c> (v1.17.0 T1d) : corrige UNE clé pour
    /// UNE langue générée — écriture DIRECTE (décision usager 2026-10-03
    /// « clé unique directe ») après validation structurelle DÉTERMINISTE en
    /// C# : placeholders {n}/balises identiques à la native EN, texte brut
    /// strict pour la famille ext, clé réelle, code langue normalisé. Le LLM
    /// n'a aucun contournement : une valeur fautive est refusée avec la
    /// raison. Écriture atomique (.bak + re-scan, effectif sans restart) —
    /// l'identique-EN légitime (marques, icônes) passe avec un avertissement.
    /// </summary>
    /// <remarks>
    /// Différent du pattern plugin_prompts (deux phases + carte) : l'atelier
    /// écrit directement — comme la génération (626 clés sans carte) — avec
    /// les mêmes gates ; le .bak du fichier protège, le log + SecurityMonitor
    /// tracent, et l'admin relit via i18n_get. Le chemin FR natif (web/server)
    /// est ÉCRITABLE ici (repli : le fr d'overlay remplace la native à
    /// l'exécution ; le contexte FR des doses lit les NATIVES — dérive mineure
    /// assumée de revue).
    /// </remarks>
    internal sealed class I18nSetKeyTool : ILlmTool
    {
        /// <summary>Plafond raisonnable d'une valeur d'interface (les natives
        /// les plus longues ≪ ; protège le fichier et l'attention des doses).</summary>
        internal const int MaxValueChars = 2000;

        private readonly ILogger _logger;

        public I18nSetKeyTool(ILogger logger) { _logger = logger; }

        public string Name => "i18n_set_key";

        public string Description =>
            "Corrige UNE clé d'interface pour UNE langue générée (atelier de langues) — " +
            "écriture directe validée : la valeur doit avoir les MÊMES placeholders {n} et " +
            "balises que la native EN (famille ext = texte BRUT, jamais de HTML), sinon elle " +
            "est refusée avec la raison. Procédure : i18n_get(key) D'ABORD (natives + valeur " +
            "actuelle), puis set_key(key, lang, value) en une VRAIE traduction (registre poli, " +
            "longueur proche de la native — un bouton reste 2-3 mots). Ne soumettez jamais une " +
            "copie anglaise d'une chaîne traduisible. Après écriture, la réponse porte ce que " +
            "le serveur SERVIT réellement (re-scan immédiat) — confirmez à l'admin avec cette " +
            "valeur.";

        public string ArgumentsSchema =>
            "{\"type\":\"object\",\"properties\":{" +
            "\"key\":{\"type\":\"string\",\"description\":\"Clé exacte (ex. act.ok.2)\"}," +
            "\"lang\":{\"type\":\"string\",\"description\":\"Code de langue cible (ex. es)\"}," +
            "\"value\":{\"type\":\"string\",\"description\":\"Valeur corrigée complète (max " +
            MaxValueChars + " caractères)\"}}," +
            "\"required\":[\"key\",\"lang\",\"value\"]}";

        public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            string key = (I18nChatKeys.ArgString(args, "key") ?? "").Trim();
            string lang = (I18nChatKeys.ArgString(args, "lang") ?? "").Trim();
            string value = I18nChatKeys.ArgString(args, "value");
            try
            {
                if (key.Length == 0 || lang.Length == 0)
                    return Task.FromResult(I18nChatKeys.Json(new { status = "refused",
                        detail = "key et lang requises (i18n_get(key) d'abord — read-modify-write)." }));
                if (value == null || value.Length == 0)
                    return Task.FromResult(I18nChatKeys.Json(new { status = "refused",
                        detail = "value requise (valeur COMPLÈTE corrigée — jamais un diff)." }));
                if (value.Length > MaxValueChars)
                    return Task.FromResult(I18nChatKeys.Json(new { status = "refused",
                        detail = "value dépasse " + MaxValueChars + " caractères (" + value.Length +
                                 ") — une valeur d'interface ne l'exige jamais." }));

                string langKey = I18nOverlay.NormalizeLang(lang, out var normNote);
                if (langKey == null)
                    return Task.FromResult(I18nChatKeys.Json(new { status = "refused",
                        detail = "lang « " + lang + " » non reconnu — 2-3 lettres attendues " +
                                 "(ex. es, de, pt) ; la langue générée doit exister dans le " +
                                 "fichier (créez-la par le panneau de config d'abord)." }));

                var (sec, en, _) = I18nChatKeys.ResolveNative(key, _logger);
                if (sec == null)
                    return Task.FromResult(I18nChatKeys.Json(new { status = "refused",
                        detail = "clé introuvable dans les natives — seule une clé réelle " +
                                 "(web/server/ext) est éditable.",
                        hints = I18nChatKeys.Hints(key, _logger) }));

                // Validation structurelle DÉTERMINISTE (le même mur que le
                // chargeur et que la campagne de génération — le LLM ne
                // contourne pas : refus motivé, aucune écriture).
                var verdict = I18nDoses.Validate(en, value);
                if (verdict != I18nDoses.Verdict.Ok)
                    return Task.FromResult(I18nChatKeys.Json(new { status = "refused",
                        detail = "valeur refusée par la validation structurelle : " +
                                 I18nChatKeys.VerdictLabel(verdict) + " (copiez le multiset des " +
                                 "placeholders {n} et des balises de la native EN exactement — " +
                                 "native : « " + (en ?? "") + " »)." }));

                var warn = new List<string>();
                if (string.Equals(value, en, StringComparison.Ordinal)
                    && !I18nDoses.TriviallyIdenticalEn(en, value))
                    warn.Add("valeur IDENTIQUE à l'EN non triviale — légitime seulement pour une " +
                             "marque/icone (vérifiez l'intention) ; sinon retraduisez réellement");
                if (string.Equals(langKey, I18n.En, StringComparison.Ordinal)
                    || string.Equals(langKey, "fr", StringComparison.Ordinal))
                    warn.Add("langue NATIVE de code : l'overlay prend le dessus à l'exécution, " +
                             "mais la correction ne vivra pas dans le plugin (prévoyez le même " +
                             "fix dans le code source) — " + langKey
                             + (normNote != null ? " (note : " + normNote + ")" : ""));
                else if (normNote != null)
                    warn.Add("langue normalisée : " + normNote);

                I18nGenerator.WriteOverlayKey(langKey, sec, key, value);
                _logger?.Info("[LLM_AI] Chat i18n : clé {0} corrigée ({1}/{2}, {3} caractères).",
                    key, langKey, sec, value.Length);
                SecurityMonitor.Record("I18N_CLE_MODIFIEE",
                    langKey + "/" + sec + " " + key + " (" + value.Length + " caractères"
                    + (warn.Count > 0 ? ", " + string.Join(" ; ", warn) : "") + ")");

                // Ce que le serveur SERVIT après re-scan : la preuve d'effet
                // (le LLM la relaie à l'admin tel quel).
                string served = I18n.S(key, langKey);
                return Task.FromResult(I18nChatKeys.Json(new
                {
                    status = "ok",
                    key,
                    lang = langKey,
                    family = sec,
                    written = true,
                    served = served ?? value,
                    warning = warn.Count > 0 ? string.Join(" ; ", warn) : null
                }));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(I18nChatKeys.Json(new { status = "failed", detail = "annulé" }));
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Chat i18n_set_key : {0}", ex.Message);
                return Task.FromResult(I18nChatKeys.Json(new { status = "failed", detail = ex.Message }));
            }
        }

    }
}
