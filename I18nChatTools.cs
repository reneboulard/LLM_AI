using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>Résolution des natives + overlay et plomberie de réponse pour
    /// les tools d'atelier (T1d) — partagée par <see cref="I18nSearchTool"/>,
    /// <see cref="I18nGetTool"/> et <see cref="I18nSetKeyTool"/> : une clé
    /// d'interface vit dans UNE famille
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
        /// mini-JSON d'échec que la boucle de l'agent sait afficher).
        /// Encodeur RELAXÉ (miroir <see cref="I18nGenerator"/>.Serialize et
        /// <c>LlmAgentService.RelaxedJsonOpts</c>) : l'échappement par défaut
        /// transforme chaque <c>&lt;</c> en <c>\u003C</c> — les balises
        /// natives et la directive du contrat partaient en SOUPE
        /// D'ÉCHAPPEMENTS que les petits modèles ne décodent pas (terrain
        /// 2026-10-04 : l'original redemandé au chat sortait sans balises —
        /// le modèle n'en avait jamais vu une vraie, donc n'en recopiait
        /// aucune ; la campagne de génération, elle, sérialise relaxé et
        /// tient 626/626). Le JSON part au LLM et aux logs, jamais
        /// embarqué dans une page HTML.</summary>
        internal static string Json(object o)
        {
            try { return System.Text.Json.JsonSerializer.Serialize(o, s_relaxedJson); }
            catch { return "{\"status\":\"failed\",\"detail\":\"sérialisation\"}"; }
        }

        private static readonly System.Text.Json.JsonSerializerOptions s_relaxedJson = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>Étiquette stable (pour le LLM) d'un verdict du
        /// validateur — miroir des règles du chargeur, pas une prose
        /// localisée : le JSON de tool est lu par le modèle (les libellés
        /// humains vivent dans la prose qu'il écrit).</summary>
        internal static string VerdictLabel(I18nDoses.Verdict v)
            => v == I18nDoses.Verdict.Ok ? "ok"
            : v == I18nDoses.Verdict.Empty ? "vide"
            : v == I18nDoses.Verdict.PlaceholderMismatch ? "placeholders divergents"
            : "balises divergentes";

        /// <summary>Sig borné (miroir I18nGenerator.BoundedSig, cap 60) —
        /// une raison de refus ne doit pas noyer un petit modèle.</summary>
        internal static string BoundedSig(string sig)
        {
            if (string.IsNullOrEmpty(sig)) return "";
            return sig.Length <= 60 ? sig : sig.Substring(0, 57) + "…";
        }

        /// <summary>Raison de refus structurel — la MÊME consigne que la
        /// campagne : native NUE → nommer le dés-échappement (terrain
        /// cfg.crosskind.convert.hint) ; sinon multisets de balises
        /// attendu/reçu bornés + la règle « raccourcir le texte oui, le
        /// contrat de balises non » (terrain 2026-10-04,
        /// cfg.orphan.firstpass.desc : 4 dépôts refusés d'affilée — le
        /// modèle abandonnait au lieu de re-poser la balise sur son texte
        /// écourté, faute d'inventaire explicite dans la raison générique).
        /// </summary>
        internal static string RefusalReason(I18nDoses.Verdict verdict, string en, string val)
        {
            if (verdict == I18nDoses.Verdict.TagMismatch)
            {
                var sigEn = I18nOverlay.HtmlTagSig(en);
                var sigV = I18nOverlay.HtmlTagSig(val);
                return sigEn.Length == 0
                    ? "balises HTML ajoutées (la native n'en porte AUCUNE ; recopie les entités " +
                      "HTML &lt;…&gt; à l'identique — les dés-échapper crée une balise vraie et est refusé)"
                    : "balises HTML divergentes (native EN : « " + BoundedSig(sigEn) +
                      " » ; ta valeur : « " + BoundedSig(sigV) + " ») — recopie chaque balise " +
                      "caractère par caractère, une ouvrante = sa fermante, même nombre ; le " +
                      "raccourcissement du TEXTE est permis, celui du CONTRAT de balises non — " +
                      "pose la balise sur le segment équivalent (ex. la phrase mise en gras)";
            }
            if (verdict == I18nDoses.Verdict.PlaceholderMismatch)
                return "placeholders {n} divergents — recopie EXACTEMENT les {n} de la native EN " +
                       "(raccourcissement du texte permis, du contrat {n} non)";
            return verdict == I18nDoses.Verdict.Empty ? "valeur vide" : "refus structurel";
        }

        /// <summary>Construit le bloc « contract » de la réponse
        /// <c>i18n_get</c> pour une clé (null = bloc OMIS : rien
        /// d'immuable, le contrat doit rester un signal, pas du bruit —
        /// une clé de texte simple n'en porte pas). C'est la pièce
        /// proactive de l'atelier : le modèle lit le contrat AVANT de
        /// construire la valeur, au lieu de découvrir la règle dans un
        /// refus. Miroir des doses « tags » de la campagne (DoseUser) —
        /// terrain 2026-10-04 : sans inventaire explicite, le modèle
        /// « retouche » les balises de mémoire (4 dépôts refusés
        /// d'affilée puis abandon) ; l'inventaire liste EXACTEMENT ce
        /// que la porte <see cref="I18nDoses.Validate"/> exigera au
        /// <c>i18n_set_key</c> (même regex, même comptage) — le refus
        /// motivé reste le filet, le contrat est la prévention.</summary>
        internal static object ContractFor(string family, string en)
        {
            if (en == null) return null;
            var rules = new List<string>();
            var contract = new Dictionary<string, object>(StringComparer.Ordinal);

            if (string.Equals(family, "ext", StringComparison.Ordinal))
            {
                // Famille texte brut : la porte interdit LITTÉRALEMENT le
                // HTML (native ext sans balise → toute balise ajoutée est
                // divergente) — le rendu textContent/str.format afficherait
                // la balise en clair.
                rules.Add("famille ext = texte BRUT : aucune balise HTML dans la " +
                          "valeur (le rendu est textuel, une balise s'afficherait " +
                          "littéralement)");
            }
            else
            {
                var tags = I18nOverlay.HtmlTagsInOrder(en);
                if (tags.Count > 0)
                {
                    contract["tags"] = I18nOverlay.Inventory(tags);
                    rules.Add("balises HTML immuables : recopie chaque balise " +
                              "CARACTÈRE PAR CARACTÈRE, même compte, même forme exacte " +
                              "(jamais <b> en <strong>, aucun attribut inventé) ; une " +
                              "balise ouvrante = sa fermante, autour des mêmes segments " +
                              "de texte ; traduis le TEXTE entre les balises, jamais les " +
                              "balises, et le contenu de <code>…</code> ne se traduit " +
                              "pas ; raccourcir le texte est permis, pas le contrat de " +
                              "balises");
                }
                else if (en.Contains("&lt;"))
                {
                    // Piège attesté (cfg.crosskind.convert.hint, 5 refus
                    // d'affilée en campagne) : le modèle dés-échappe
                    // l'entité → balise vraie → porte divergente.
                    rules.Add("entités HTML immuables : recopie &lt; &gt; &amp; " +
                              "À L'IDENTIQUE — les dés-échapper (ex. &lt;movie&gt; " +
                              "devient <movie>) crée une balise vraie et le dépôt " +
                              "sera refusé");
                }
            }

            var phs = I18nOverlay.PlaceholdersInOrder(en);
            if (phs.Count > 0)
            {
                contract["placeholders"] = I18nOverlay.Inventory(phs);
                rules.Add("placeholders immuables : recopie chaque {n} TEL QUEL, " +
                          "même compte, même position logique — jamais renuméroté " +
                          "ni fondu dans la prose");
            }

            if (en.Contains('\n'))
            {
                contract["multiline"] = true;
                rules.Add("valeur multi-lignes : conserve chaque saut de ligne " +
                          "(une ligne de la native = un \\n dans la valeur)");
            }

            if (rules.Count == 0) return null;
            contract["directive"] = string.Join(" ; ", rules);
            return contract;
        }

        /// <summary>Extrait borné d'une valeur pour le journal (100 car.,
        /// sauts réduits à l'espace — le log reste sur une ligne).</summary>
        internal static string SnipValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var single = value.Replace("\r", " ").Replace("\n", " ");
            return single.Length <= 100 ? single : single.Substring(0, 97) + "…";
        }
    }

    /// <summary>
    /// Tool de chat <c>i18n_get</c> (v1.17.0 T1d — atelier de langues) :
    /// lecture COMPLÈTE d'une clé d'interface — natives EN + FR (l'intention
    /// du mainteneur), valeurs d'overlay par langue avec leur verdict
    /// structurel (le chargeur les servira ou les laissera tomber), et
    /// depuis v1.17.1.0 un bloc <c>contract</c> quand la native porte des
    /// éléments immuables (balises HTML, placeholders {n}, sauts de ligne,
    /// entités ; famille ext = texte brut) : inventaire exact + directive —
    /// la prévention au dépôt, le refus motivé restant le filet. C'est
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
            "SAUTÉE par le chargeur, la clé sert l'anglais natif). Si la native porte des " +
            "éléments structurels (balises HTML, placeholders {n}, sauts de ligne, entités " +
            "HTML), la réponse inclut un bloc « contract » : ces éléments sont IMMUABLES — " +
            "recopiez-les caractère par caractère dans la valeur corrigée (traduisez le " +
            "TEXTE, jamais les balises ni les {n}) ; un dépôt qui brise le contrat est " +
            "refusé. Utilisez-le avant toute correction (read-modify-write) : il montre la " +
            "valeur actuelle, la famille exacte (web|server|ext) et le verdict de chaque " +
            "langue. Si vous ne connaissez que le TEXTE vu à l'écran (pas la clé), " +
            "commencez par i18n_search(text). Une clé inconnue renvoie des suggestions " +
            "proches (les familles : web = libellés des pages, server = messages HTTP/" +
            "jalons, ext = chat externe).";

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

                var resp = new Dictionary<string, object>
                {
                    ["status"] = "ok",
                    ["key"] = key,
                    ["family"] = sec,
                    ["natives"] = new { en, fr },
                    ["overlay"] = overlay
                };
                // Contrat structurel (v1.17.1.0) : la native porte des
                // éléments immuables (balises, {n}, sauts de ligne,
                // entités, ext = brut) — le bloc liste l'inventaire EXACT
                // que la porte Validate exigera au dépôt + la directive à
                // suivre. Omis pour une clé sans contrainte : un contrat
                // partout n'est plus un signal.
                var contract = I18nChatKeys.ContractFor(sec, en);
                if (contract != null) resp["contract"] = contract;

                return Task.FromResult(I18nChatKeys.Json(resp));
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
    /// Tool de chat <c>i18n_set_key</c> (v1.17.0 T1d ; DÉPÔT deux phases
    /// depuis v1.17.0.2) : propose la correction d'UNE clé pour UNE langue
    /// générée — validation structurelle DÉTERMINISTE en C# (placeholders
    /// {n}/balises identiques à la native EN, texte brut strict pour la
    /// famille ext, clé réelle, code langue normalisé), puis sérialisation
    /// dans <see cref="ChatI18nStore"/> : l'écriture n'a lieu qu'au clic
    /// « Approuver » de l'admin (endpoint déterministe, gates re-courues,
    /// .bak + re-scan) — le LLM n'a AUCUN chemin d'écriture direct. Le dépôt
    /// exige le mode déroulant « Modification texte UI » (décision usager
    /// 2026-10-04) — hors mode : refus avec la consigne. L'identique-EN
    /// légitime (marques, icônes) passe avec un avertissement. Depuis
    /// v1.17.1.0, <see cref="I18nGetTool"/> sert le contrat structurel de
    /// la clé (bloc « contract » : inventaire exact + directive) : le
    /// modèle recopie un contrat LISTÉ, il ne « retouche » pas les balises
    /// de mémoire — la porte, elle, ne change pas.
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

        private readonly string _sessionId, _userId, _contextId;
        private readonly ILogger _logger;

        /// <summary>Session/usager de liaison du dépôt (anti-détournement :
        /// l'approbation rejoue la même session) + mode déroulant résolu côté
        /// service — la validation croisée mode ↔ dépôt (miroir des prompts
        /// v1.13.9) vit ici.</summary>
        public I18nSetKeyTool(string sessionId, string userId, string contextId, ILogger logger)
        {
            _sessionId = sessionId; _userId = userId;
            _contextId = contextId; _logger = logger;
        }

        public string Name => "i18n_set_key";

        public string Description =>
            "Propose la correction d'UNE clé d'interface pour UNE langue générée (atelier de " +
            "langues) — DÉPÔT en deux phases : la proposition est VALIDÉE (placeholders {n} et " +
            "balises identiques à la native EN, ext = texte BRUT) puis déposée sur une carte " +
            "« Approuver / Refuser » ; l'ÉCRITURE n'a lieu qu'au clic « Approuver » de l'admin " +
            "(code déterministe). N'annoncez JAMAIS une écriture faite : annoncez la proposition " +
            "(clé, langue, valeur) et attendez le résultat de la carte. Procédure : i18n_get(key) " +
            "D'ABORD et relevez son bloc « contract » s'il y en a un (balises HTML, placeholders " +
            "{n}, sauts de ligne, entités) — ces éléments sont IMMUABLES : recopiez-les " +
            "caractère par caractère, traduisez le texte entre eux. Puis set_key(key, lang, " +
            "value) avec la valeur COMPLÈTE (registre poli, longueur proche de la native — " +
            "un bouton reste 2-3 mots), jamais une copie anglaise d'une chaîne traduisible. " +
            "Le dépôt exige le mode « Modification texte UI » (dropdown de la page).";

        public string ArgumentsSchema =>
            "{\"type\":\"object\",\"properties\":{" +
            "\"key\":{\"type\":\"string\",\"description\":\"Clé exacte (ex. act.ok.2)\"}," +
            "\"lang\":{\"type\":\"string\",\"description\":\"Code de langue cible (ex. es)\"}," +
            "\"value\":{\"type\":\"string\",\"description\":\"Valeur corrigée complète (max " +
            MaxValueChars + " caractères) — le contract de i18n_get (balises HTML, {n}, " +
            "sauts de ligne) se recopie EXACTEMENT\"}}," +
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

                // MODE EXCLUSIF (décision usager 2026-10-04) : la modification
                // des chaînes exige la sélection du mode « Modification texte
                // UI » dans le dropdown de la page chat — miroir de la
                // validation champ ↔ mode des prompts (v1.13.9). Le refus est
                // une CONSIGNE : le modèle relit l'invite au tour suivant.
                var mode = ChatContexts.Find(_contextId);
                if (mode == null || !string.Equals(mode.Id, ChatContexts.I18nEditModeId,
                        StringComparison.Ordinal))
                {
                    _logger?.Info("[LLM_AI] Chat i18n_set_key : dépôt refusé (mode « Modification texte UI » non sélectionné).");
                    return Task.FromResult(I18nChatKeys.Json(new { status = "refused",
                        detail = "la modification des chaînes i18n exige le mode « Modification " +
                                 "texte UI » — sélectionnez-le dans la liste déroulante de la page " +
                                 "chat puis réessayez." }));
                }

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
                // contourne pas : refus motivé, aucune écriture). La raison
                // est la MÊME que la campagne ET est loggée — terrain
                // 2026-10-04 : 4 dépôts refusés d'affilée n'ont laissé
                // AUCUNE trace loggable, le diagnostic ne tenait qu'à la
                // prose (paraphrase) du modèle.
                var verdict = I18nDoses.Validate(en, value);
                if (verdict != I18nDoses.Verdict.Ok)
                {
                    string reason = I18nChatKeys.RefusalReason(verdict, en, value);
                    _logger?.Warn("[LLM_AI] Chat i18n_set_key : dépôt refusé ({0}) — clé={1}, lang={2}, value « {3} ».",
                        reason, key, lang, I18nChatKeys.SnipValue(value));
                    return Task.FromResult(I18nChatKeys.Json(new { status = "refused",
                        detail = "valeur refusée par la validation structurelle : " + reason }));
                }

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

                // Valeur d'overlay actuelle (carte Avant/Après ; null =
                // première écriture pour cette langue — la carte l'affiche).
                var overlay = I18nChatKeys.OverlayFor(key);
                string oldValue = overlay.TryGetValue(langKey, out var ov) ? ov.Value : null;

                // DÉPÔT (two phases) : sérialisation côté serveur — l'écriture
                // n'a lieu qu'à l'approbation (endpoint I18nKey/Approve, code
                // déterministe). Le LLM n'a aucun chemin d'écriture direct.
                var pending = ChatI18nStore.Create(_sessionId, _userId, sec, key, langKey,
                    oldValue, value, warn.Count > 0 ? string.Join(" ; ", warn) : null, _logger);
                if (pending == null)
                    return Task.FromResult(I18nChatKeys.Json(new { status = "failed",
                        detail = "création de l'attente impossible." }));

                _logger?.Info("[LLM_AI] Chat i18n : proposition déposée (action_id={0}, {1}/{2}, {3} caractères).",
                    pending.ActionId, langKey, key, value.Length);
                return Task.FromResult(I18nChatKeys.Json(new
                {
                    status = "pending_approval",
                    action_id = pending.ActionId,
                    key,
                    lang = langKey,
                    family = sec,
                    detail = "Proposition déposée — la carte « Approuver / Refuser » de la page " +
                             "porte l'écriture. Annoncez la proposition à l'admin (clé, langue, " +
                             "valeur) et attendez ; ne la renvoyez PAS sans changement."
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

    /// <summary>
    /// Tool de chat <c>i18n_search</c> (v1.17.0 — atelier de langues) : l'admin
    /// voit le TEXTE à l'écran, pas la clé. Recherche par sous-chaîne
    /// (insensible à la casse) dans les natives EN+FR (web, server, ext) et
    /// TOUTES les valeurs d'overlay des langues générées — les natives restent
    /// sondées même avec un filtre de langue, car le texte vu peut être un
    /// repli natif (clé absente de l'overlay). Texte absent partout : réponse
    /// explicite « ne vient pas du plugin » (UI Emby core, autre source, texte
    /// construit en JS). Flux : i18n_search(text) → i18n_get(key) →
    /// i18n_set_key. Lecture pure — safe par construction.
    /// </summary>
    internal sealed class I18nSearchTool : ILlmTool
    {
        /// <summary>Cap de résultats (les correspondances au-delà sont
        /// tronquées — le texte générique s'affine, pas la pagination).</summary>
        internal const int MaxMatches = 20;

        private readonly ILogger _logger;

        public I18nSearchTool(ILogger logger) { _logger = logger; }

        public string Name => "i18n_search";

        public string Description =>
            "Recherche PAR TEXTE dans les chaînes du plugin (atelier de langues) : sous-chaîne " +
            "insensible à la casse cherchée DANS LES VALEURS — natives EN et FR (famille web, " +
            "server, ext) et valeurs d'overlay de toutes les langues générées. À utiliser quand " +
            "l'admin voit un texte non conforme à l'écran sans connaître la clé (« trouve la clé " +
            "contenant… »). Renvoie clé, famille, langue(s) et extrait borné — puis i18n_get(key) " +
            "pour le contexte complet et i18n_set_key pour corriger. Avec un « lang », le filtre " +
            "ne porte que sur l'overlay : les natives EN/FR restent toujours sondées (le texte vu " +
            "peut être un repli natif — alors la langue demandée n'affichera RIEN, seul « en »/« fr » " +
            "matchera). Texte absent partout → le dit explicitement : le texte ne vient pas du " +
            "plugin. Cap " + MaxMatches + " résultats : si le texte est trop générique, affinez.";

        public string ArgumentsSchema =>
            "{\"type\":\"object\",\"properties\":{" +
            "\"text\":{\"type\":\"string\",\"description\":\"Sous-chaîne du texte VU À L'ÉCRAN (ex. « anterior se mantiene »)\"}," +
            "\"lang\":{\"type\":\"string\",\"description\":\"Optionnel — filtre une langue générée (ex. es). Par défaut : toutes.\"}}," +
            "\"required\":[\"text\"]}";

        public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            string text = (I18nChatKeys.ArgString(args, "text") ?? "").Trim();
            string langF = (I18nChatKeys.ArgString(args, "lang") ?? "").Trim();
            try
            {
                if (text.Length < 2)
                    return Task.FromResult(I18nChatKeys.Json(new { status = "refused",
                        detail = "text requise (sous-chaîne du texte VU À L'ÉCRAN, ≥ 2 caractères)." }));
                string langKey = langF.Length == 0 ? null : I18nOverlay.NormalizeLang(langF, out _);
                if (langF.Length > 0 && langKey == null)
                    return Task.FromResult(I18nChatKeys.Json(new { status = "refused",
                        detail = "lang « " + langF + " » non reconnu — 2-3 lettres (ex. es, de, pt)." }));

                // Une seule passe « valeurs » (natives puis overlay) — chaque
                // correspondance mémorise lang → extrait, fusion par clé :
                // une clé qui matche en EN et en es donne UNE ligne à deux
                // langues, pas deux résultats.
                var rows = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                void SnipAdd(string key, string langTag, string val)
                {
                    if (string.IsNullOrEmpty(val)) return;
                    var idx = val.IndexOf(text, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) return;
                    if (!rows.TryGetValue(key, out var r))
                        rows[key] = r = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (!r.ContainsKey(langTag)) r[langTag] = Snip(val, idx);
                }
                foreach (var kv in Nulless(I18nApiService.WebNativesLang("en", _logger)))
                    SnipAdd(kv.Key, "en", kv.Value);
                foreach (var kv in Nulless(I18nApiService.WebNativesLang("fr", _logger)))
                    SnipAdd(kv.Key, "fr", kv.Value);
                foreach (var kv in Nulless(I18n.EnServerDict)) SnipAdd(kv.Key, "en", kv.Value);
                foreach (var kv in Nulless(I18n.ServerDictFor("fr"))) SnipAdd(kv.Key, "fr", kv.Value);
                foreach (var kv in Nulless(I18n.EnExtDict)) SnipAdd(kv.Key, "en", kv.Value);
                // Overlay de toutes les langues générées (filtre optionnel).
                foreach (var langKv in I18nGenerator.ReadOverlayValues())
                {
                    if (langKey != null && !string.Equals(langKv.Key, langKey, StringComparison.Ordinal))
                        continue;
                    foreach (var famKv in langKv.Value)
                        foreach (var kk in famKv.Value)
                            SnipAdd(kk.Key, langKv.Key, kk.Value);
                }

                var matches = new List<object>();
                foreach (var k in rows.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    var (sec, _, _) = I18nChatKeys.ResolveNative(k, _logger);
                    matches.Add(new { key = k, fam = sec ?? "(hors natives — inerte)", langs = rows[k] });
                }

                bool trunc = matches.Count > MaxMatches;
                return Task.FromResult(I18nChatKeys.Json(new
                {
                    status = "ok",
                    text,
                    lang = langKey ?? "(toutes)",
                    count = matches.Count,
                    matches = matches.Take(MaxMatches).ToArray(),
                    truncated = trunc,
                    note = matches.Count == 0
                        ? "aucune chaîne du plugin (natives EN/FR ni overlay des langues générées) ne " +
                          "contient ce texte — il ne vient pas du plugin (UI Emby core, autre plugin, " +
                          "texte construit en JS)"
                        : null
                }));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(I18nChatKeys.Json(new { status = "failed", detail = "annulé" }));
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Chat i18n_search : {0}", ex.Message);
                return Task.FromResult(I18nChatKeys.Json(new { status = "failed", detail = ex.Message }));
            }
        }

        /// <summary>Extrait borné autour de la 1re occurrence (≤ 140 car.,
        /// ellipses) — les lignes de résultat restent lisibles ; la valeur
        /// COMPLETE s'obtient par i18n_get.</summary>
        private static string Snip(string v, int idx)
        {
            const int Cap = 140, Before = 40;
            int start = Math.Max(0, idx - Before), end = Math.Min(v.Length, start + Cap);
            return (start > 0 ? "…" : "") + v.Substring(start, end - start) + (end < v.Length ? "…" : "");
        }

        /// <summary>Itération tolérante (les natives d'une section peuvent
        /// ne pas être chargées).</summary>
        private static IEnumerable<KeyValuePair<string, string>> Nulless(
            IReadOnlyDictionary<string, string> d)
            => d ?? (IEnumerable<KeyValuePair<string, string>>)Array.Empty<KeyValuePair<string, string>>();

    }
}
