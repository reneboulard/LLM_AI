using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;

namespace LLM_AI
{
    /// <summary>
    /// Centre de résolution de langue + ressources de chaînes localisées pour le
    /// plugin LLM_AI. Sans <c>.resx</c> ni <c>ILocalizationManager</c> (aucun
    /// utilisé dans le projet) : reprend le pattern « dictionnaires inline » de
    /// <c>i18n.js</c>, côté C#.
    /// </summary>
    /// <remarks>
    /// <para><b>Deux buckets de langue</b> (décision de conception) :</para>
    /// <list type="bullet">
    /// <item><b>Métadonnées</b> (scaffolding du <c>.nfo</c>, synopsis TMDB, prose
    /// du LLM) → suivent <see cref="PluginConfiguration.ResponseLanguage"/> ;
    /// « Auto » (vide) → langue d'affichage Emby ( <c>UICulture</c>), sinon
    /// <see cref="PluginConfiguration.TmdbLanguage"/> (legacy), sinon anglais.
    /// Résolu par <see cref="ResolveMetaLangKey"/>.</item>
    /// <item><b>Interface</b> (libellés web via <c>i18n.js</c>, nom/description
    /// des tâches planifiées) → suivent la langue d'affichage Emby. Résolu par
    /// <see cref="ResolveDisplayLangKey"/>.</item>
    /// </list>
    /// <para><b>Extensible par la donnée</b> : ajouter une langue = ajouter une
    /// entrée dans <see cref="s_res"/> (+ optionnellement un dictionnaire
    /// <c>i18n.js</c>). FR + EN fournis ; les langues sans dictionnaire retombent
    /// sur l'anglais (EN) pour les courts libellés de scaffolding — le synopsis
    /// TMDB et la prose LLM, eux, sont dans la langue de l'usager (cascade TMDB
    /// + traduction LLM en dernier recours).</para>
    /// <para><b>Clé de langue</b> : code 2 lettres minuscules
    /// (« fr », « en », « es », « de », « it », « pt »). L'anglais
    /// (<see cref="En"/>) est le repli universel.</para>
    /// </remarks>
    internal static class I18n
    {
        /// <summary>Clé de langue par défaut / repli universel : anglais.</summary>
        public const string En = "en";

        /// <summary>Clé de langue française (seule autre langue fournie).</summary>
        public const string Fr = "fr";

        // ------------------------------------------------------------------
        //  Résolution de la clé de langue
        // ------------------------------------------------------------------

        /// <summary>
        /// Mappe un nom de langue libre (<see cref="PluginConfiguration.ResponseLanguage"/>
        /// — ex. « English », « Français », « Español ») vers une clé 2 lettres.
        /// Tolérant : casse et accents ignorés. Retourne <c>null</c> si non reconnu
        /// (l'appelant retombe alors sur la langue d'affichage / legacy / anglais).
        /// </summary>
        internal static string ParseLangName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string s = NoDiacritics(name.Trim()).ToLowerInvariant();

            // Correspondance exacte d'abord (valeurs du <select> de config).
            if (s_nameToKey.TryGetValue(s, out var key)) return key;

            // Repli par préfixe 2 lettres (ex. « en-us », « fr_ca »).
            if (s.Length >= 2 && s_prefixToKey.TryGetValue(s.Substring(0, 2), out key)) return key;

            return null;
        }

        /// <summary>
        /// Mappe une locale Emby (<c>UICulture</c> / <c>TmdbLanguage</c>, ex.
        /// « fr-CA », « en-US », « es-ES ») vers une clé 2 lettres, par préfixe.
        /// Retourne <c>null</c> si non reconnu.
        /// </summary>
        internal static string LocaleToKey(string locale)
        {
            if (string.IsNullOrWhiteSpace(locale)) return null;
            string s = NoDiacritics(locale.Trim()).ToLowerInvariant();
            int i = s.IndexOf('-');
            if (i > 0) s = s.Substring(0, i);
            return s_prefixToKey.TryGetValue(s, out var key) ? key : null;
        }

        /// <summary>
        /// Clé de langue des <b>métadonnées</b> (NFO + synopsis TMDB). Précédence :
        /// <list type="number">
        /// <item><see cref="PluginConfiguration.ResponseLanguage"/> si non vide
        /// (langue explicite choisie par l'usager) ;</item>
        /// <item>sinon langue d'affichage Emby via <see cref="ResolveDisplayLangKey"/>
        /// (si <paramref name="host"/> est fourni) — « Auto » = langue de l'interface ;</item>
        /// <item>sinon préfixe de <see cref="PluginConfiguration.TmdbLanguage"/>
        /// (legacy, tant que l'UI Emby n'est pas lisible) ;</item>
        /// <item>sinon anglais (<see cref="En"/>).</item>
        /// </list>
        /// </summary>
        internal static string ResolveMetaLangKey(PluginConfiguration cfg, IServerApplicationHost host)
        {
            if (cfg != null)
            {
                var key = ParseLangName(cfg.ResponseLanguage);
                if (!string.IsNullOrEmpty(key)) return key;
            }
            if (host != null)
            {
                var key = ResolveDisplayLangKey(host);
                if (!string.IsNullOrEmpty(key) && key != En) return key;
                // Si l'UI Emby résout en anglais, on laisse la chance au legacy
                // TmdbLanguage ci-dessous avant de retomber sur l'anglais.
            }
            if (cfg != null)
            {
                var key = LocaleToKey(cfg.TmdbLanguage);
                if (!string.IsNullOrEmpty(key)) return key;
            }
            return En;
        }

        // ------------------------------------------------------------------
        //  Détection de la langue d'un CONTENU (synopsis EPG / titre) pour
        //  les métadonnées S1/S2/S3 (OrphanResolver). Règle terrain
        //  2026-10-01 : Emby aligne les fiches sur la langue de la chaîne
        //  (chaîne FR → fiche FR, chaîne EN → fiche EN) ; le plugin suit —
        //  bibliothèque Emby d'abord (PreferredMetadataLanguage), puis
        //  cette détection, puis config. Déterministe, zéro LLM.
        // ------------------------------------------------------------------

        /// <summary>Sépare les mots : tout ce qui n'est pas une lettre coupe
        /// (apostrophes comprises : « l'homme » → « l », « homme »).</summary>
        private static readonly Regex s_langWordSplit = new Regex(@"[^\p{L}]+", RegexOptions.Compiled);

        /// <summary>Accents français (texte en minuscules).</summary>
        private const string s_frAccents = "àâäçéèêëîïôöùûüœæ";

        /// <summary>Mots-outils français — aucun n'existe comme mot en anglais.</summary>
        private static readonly HashSet<string> s_frStops = new HashSet<string>(StringComparer.Ordinal)
        {
            "le","la","les","des","du","une","un","de","et","à","au","aux","dans","pour","avec","sur",
            "son","sa","ses","est","qui","que","pas","plus","chez","sans","contre","où","grand","petit",
            "nouveau","nouvelle","histoire","vie","monde","guerre","amour","femme","homme","enfant",
            "nuit","jour","ville","mort","dernier","premier","cette","cet","mais","comme","tout","toute",
            "tous","leur","leurs","notre","votre"
        };

        /// <summary>Mots-outils anglais — aucun n'existe comme mot en français
        /// (« or », « in »/« is » anglais mais « or » français → exclus).</summary>
        private static readonly HashSet<string> s_enStops = new HashSet<string>(StringComparer.Ordinal)
        {
            "the","of","and","to","in","with","for","is","who","that","his","her","its","an","from",
            "by","at","into","over","after","before","new","night","day","man","men","story","world",
            "war","love","city","girl","boy","dead","game","last","first","secret","time","house","home",
            "life","when","they","them","their","this","these","those","was","are"
        };

        /// <summary>
        /// Détecte la langue d'un texte : accents français = preuve forte (l'anglais
        /// n'en a pas) + mots-outils FR/EN comptés par mot entier. Retourne
        /// <see cref="Fr"/> / <see cref="En"/>, ou <c>null</c> si indéterminé
        /// (aucune preuve, ou ex æquo : « Le Man » — prudent par conception,
        /// l'appelant replie sur la config).
        /// </summary>
        internal static string DetectTextLangKey(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string s = text.ToLowerInvariant();

            int frAcc = 0;
            foreach (char c in s)
            {
                if (s_frAccents.IndexOf(c) >= 0 && ++frAcc >= 6) break;
            }

            int fr = 0, en = 0;
            foreach (string w in s_langWordSplit.Split(s))
            {
                if (w.Length == 0) continue;
                if (s_frStops.Contains(w)) fr++;
                else if (s_enStops.Contains(w)) en++;
            }

            int frScore = fr * 2 + frAcc;
            int enScore = en * 2;
            if (frScore == 0 && enScore == 0) return null;
            if (frScore > enScore) return Fr;
            if (enScore > frScore) return En;
            return null; // ex æquo → indéterminé
        }

        /// <summary>
        /// Langue d'un CONTENU pour les métadonnées : synopsis EPG d'abord (la
        /// langue de l'entrée de guide — une chaîne FR y parle français même
        /// quand le titre de l'œuvre reste anglais, ex. « The Walking Dead »
        /// sur une chaîne française), titre en repli. Null si indéterminé.
        /// </summary>
        internal static string DetectContentLangKey(string title, string overview)
        {
            return DetectTextLangKey(overview) ?? DetectTextLangKey(title);
        }

        /// <summary>
        /// Clé de langue de l'<b>interface</b> (tâches planifiées) : lit la langue
        /// d'affichage Emby (<c>ServerConfiguration.UICulture</c>) via
        /// <see cref="IServerConfigurationManager"/> résolu depuis
        /// <paramref name="host"/> (même pattern que <c>SystemAuditTool</c>).
        /// Repli anglais si l'hôte est nul ou la locale illisible. Ne lève jamais.
        /// </summary>
        internal static string ResolveDisplayLangKey(IServerApplicationHost host)
        {
            if (host == null) return En;
            try
            {
                var mgr = host.TryResolve<IServerConfigurationManager>();
                var ui = mgr?.Configuration?.UICulture;
                var key = LocaleToKey(ui);
                if (!string.IsNullOrEmpty(key)) return key;
            }
            catch { /* repli anglais */ }
            return En;
        }

        // ------------------------------------------------------------------
        //  Prose du LLM (rapport d'audit, raisons des recommandations)
        // ------------------------------------------------------------------

        /// <summary>
        /// Nom de langue lisible par un LLM (ex. « French ») pour TOUTE la
        /// <b>prose</b> du LLM — pastilles AI-Tonight, raisons des
        /// recommandations, rapport d'audit, fiches mémoire, chat — injecté
        /// via <see cref="LlmAgentService.BuildLanguageDirective"/>.
        /// Règle usager (2026-10-01) :
        /// <list type="number">
        /// <item><see cref="PluginConfiguration.ResponseLanguage"/> TEL QUEL si
        /// non vide (langue explicite choisie par l'usager — une valeur libre,
        /// ex. « Nederlands », est comprise par le LLM même sans clé connue) ;</item>
        /// <item>sinon langue de configuration de l'interface Emby
        /// (<c>ServerConfiguration.UICulture</c> — « Auto » = langue de
        /// l'interface) ;</item>
        /// <item>sinon anglais (défaut Emby si la locale est illisible).</item>
        /// </list>
        /// Différence assumée avec les métadonnées (<see cref="ResolveMetaLangKey"/>) :
        /// PAS de saut legacy <see cref="PluginConfiguration.TmdbLanguage"/> pour
        /// la prose — l'interface est la seule source en Auto. Les infos EPG
        /// (titres, chaînes, horaires) ne se traduisent JAMAIS : la prose
        /// enrichit autour, dans la langue résolue ici.
        /// Historique : la prose passait <c>ResponseLanguage</c> brut à
        /// <c>BuildLanguageDirective</c> — vide = AUCUNE directive, et un
        /// modèle a dérivé en chinois (terrain 2026-10-01, glm-5.3-flash).
        /// </summary>
        internal static string ResolveProseLangName(PluginConfiguration cfg, IServerApplicationHost host)
        {
            if (cfg != null && !string.IsNullOrWhiteSpace(cfg.ResponseLanguage))
                return cfg.ResponseLanguage.Trim();
            return ToLangName(ResolveDisplayLangKey(host));
        }

        // ------------------------------------------------------------------
        //  Conversion clé -> code externe
        // ------------------------------------------------------------------

        /// <summary>
        /// Clé 2 lettres → code langue TMDB (ex. « fr »→« fr-FR »,
        /// « en »→« en-US », « es »→« es-ES »). Inconnu → « en-US ».
        /// </summary>
        internal static string ToTmdbLang(string key) => key switch
        {
            "fr" => "fr-FR",
            "es" => "es-ES",
            "de" => "de-DE",
            "it" => "it-IT",
            "pt" => "pt-PT",
            _ => "en-US"
        };

        /// <summary>
        /// Clé 2 lettres → nom humain de la langue, pour la cible de traduction LLM
        /// (ex. « fr »→« French », « es »→« Spanish »). Inconnu → « English ».
        /// </summary>
        internal static string ToLangName(string key) => key switch
        {
            "fr" => "French",
            "es" => "Spanish",
            "de" => "German",
            "it" => "Italian",
            "pt" => "Portuguese",
            _ => "English"
        };

        // ------------------------------------------------------------------
        //  Ressources de chaînes localisées (FR + EN, repli EN)
        // ------------------------------------------------------------------

        /// <summary>
        /// Retourne le libellé localisé <paramref name="key"/> dans la langue
        /// <paramref name="langKey"/> (ex. « nfo.why », « task.llm.desc »). Repli
        /// sur l'anglais, puis sur la clé brute si absente partout. Les libellés à
        /// substituants ({0}, {1}…) sont renvoyés tels quels — l'appelant applique
        /// <c>string.Format</c>.
        /// </summary>
        internal static string S(string key, string langKey)
        {
            if (!string.IsNullOrEmpty(langKey)
                && s_res.TryGetValue(langKey, out var dict)
                && dict.TryGetValue(key, out var v))
                return v;
            if (s_res.TryGetValue(En, out var en) && en.TryGetValue(key, out var ev))
                return ev;
            return key;
        }

        // --- tables de mapping nom/locale -> clé --------------------------

        private static readonly Dictionary<string, string> s_nameToKey =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { "francais", "fr" }, { "fr", "fr" },
                { "english", "en" }, { "en", "en" },
                { "espanol", "es" }, { "es", "es" },
                { "deutsch", "de" }, { "de", "de" },
                { "italiano", "it" }, { "it", "it" },
                { "portugues", "pt" }, { "pt", "pt" },
            };

        private static readonly Dictionary<string, string> s_prefixToKey =
            new(StringComparer.Ordinal)
            {
                { "fr", "fr" }, { "en", "en" }, { "es", "es" },
                { "de", "de" }, { "it", "it" }, { "pt", "pt" },
            };

        // --- dictionnaires de ressources (FR + EN) -----------------------

        private static readonly Dictionary<string, Dictionary<string, string>> s_res =
            new(StringComparer.Ordinal)
            {
                ["fr"] = new(StringComparer.Ordinal)
                {
                    // --- NFO scaffolding ---
                    ["nfo.rating"] = "note {0}/10",
                    ["nfo.genres"] = "genres : {0}",
                    // (plus de « nfo.why » : les NFO .strm portent l'emoji 🤖 seul —
                    // le libellé « Pourquoi ce soir / Why tonight » est côté client,
                    // clé i18n.js « rec.tonight.why », section À regarder ce soir)
                    ["nfo.airs.prefix"] = "Diffusion à venir",
                    ["nfo.airs.chan"] = " sur {0}",
                    ["nfo.airs.date"] = " le {0}",
                    ["nfo.airs.suffix"] = " — lire cette carte programme l'enregistrement.",
                    ["nfo.epglink"] = "🔗 Fiche EPG : ",
                    ["nfo.seriesSuffix"] = " (série)",
                    // --- Tâches planifiées ---
                    ["task.llm.name"] = "LLM AI Task",
                    ["task.llm.desc"] = "Agent LLM autonome (Ollama) qui interroge la bibliothèque Emby via des outils natifs read-only pour accomplir la tâche configurée.",
                    ["task.cleanup.name"] = "LLM AI — Nettoyage tag « AI Tonight »",
                    ["task.cleanup.desc"] = "Nettoyage nocturne des surfaces natives « À regarder ce soir » : retire le tag « AI Tonight » de tous les items Emby (et, migration v1.13.3, le genre hérité du même nom) ET vide la collection « AI Tonight » de ses membres (la coquille reste, re-remplie au prochain run). Tourne quotidiennement à 3 h ; les runs Tonight suivants reconstruisent les surfaces sur les recos toujours pertinentes. Ne touche pas au genre « AI Suggestion » de la bibliothèque .strm.",
                    ["task.orphan.name"] = "LLM AI — Identification des enregistrements orphelins",
                    ["task.orphan.desc"] = "Passe quotidienne (4 h) qui repère les enregistrements DVR non identifiés (sans id IMDb/TMDB — souvent des titres québécois absents du catalogue TMDB/TVDB), tente de les résoudre par nettoyage du titre + recherche multilingue (S1), puis par proposition LLM d'un id IMDb validé via TMDB /find (S2), et écrit l'id + métadonnées + affiche en verrouillant le titre EPG. Les irrésolus sont marqués pour revue. Le titre original EPG est toujours préservé (verrouillé).",
                    ["task.analysis.name"] = "LLM AI — Analyse des recommandations (rétroaction)",
                    ["task.analysis.desc"] = "Passe hebdomadaire (dimanche 4 h, opt-in RecoFeedbackEnabled) de la boucle de rétroaction : rapproche en C# le journal des recommandations de la semaine (recos « À regarder ce soir », recos d'enregistrement, rejets « Oublier ») des visionnages réels de chaque usager (regardé / ignoré / rejeté / vu sans recommandation), puis fait produire au LLM une directive concise persistée (PromptDirectives) et réinjectée dans les prompts des runs suivants. Fail-open : sans directive, les prompts sont inchangés ; un échec d'analyse ne casse jamais un run.",
                    ["task.memory.name"] = "LLM AI — Mémoire réflexive (fiche du LLM)",
                    ["task.memory.desc"] = "Passe hebdomadaire (dimanche 4 h 30, opt-in MemoryCardEnabled) de la mémoire réflexive : joint en C# les événements bruts de la semaine (décisions journalisées avec leur raison × télémétrie de lecture avec % visionné × candidats écartés du menu × snapshot EPG pour le % du direct), puis fait RÉÉCRIRE par le LLM sa fiche mémoire (~250 mots) — ce qu'il sait de l'usager, ses réussites, ses échecs et ses stratégies. Versionnée (4 versions conservées) ; un échec LLM conserve la fiche précédente ; sans signal, rien ne tourne.",
                    ["disktag.notif.title"] = "LLM AI — Disque des enregistrements presque plein",
                    ["disktag.notif.desc"] = "{0} enregistrement(s) visionné(s) tagué(s) « {1} » (~{2:0.#} Go récupérables). Le seuil disque est franchi. Pour libérer de l'espace : filtrez la bibliothèque des enregistrements par le tag « {1} », multi-sélectionnez puis supprimez (le plugin ne supprime jamais rien lui-même).",
                    ["disktag.notif.desc.uncovered"] = "{0} enregistrement(s) visionné(s) tagué(s) « {1} » (~{2:0.#} Go récupérables) — insuffisant pour ramener le disque au-dessus du seuil. Supprimez-les, puis d'autres enregistrements (ou réduisez les timers) manuellement. Le plugin ne supprime jamais rien lui-même.",
                    // Notification « fiche croisée » (v1.13.28) — œuvre identifiée
                    // sous le type opposé à l'item (film sur série, ou l'inverse).
                    ["crosskind.notif.title"] = "LLM AI — Œuvre(s) enregistrée(s) sous le mauvais type Emby",
                    ["crosskind.notif.desc"] = "{0} enregistrement(s) identifié(s) avec une fiche du type opposé (film sur un item série, ou l'inverse) — tagué(s) « {1} ». Les ids, synopsis, genres et poster corrects sont écrits et verrouillés ; le type de l'item Emby est figé par l'import DVR et n'est jamais modifié. Pour replacer une œuvre dans la bonne bibliothèque : déplacez son fichier (ex. le .ts) vers la bibliothèque visée — Emby le ré-importera avec le bon type et le plugin le ré-identifiera. La page de configuration liste aussi ces items (file « cross-kind ») : copie vers la bonne bibliothèque, ou conversion sur place du dossier DVR (renommage « Titre (Année) » + retrait de tvshow.nfo). Filtrez par le tag « {1} » pour retrouver ces items.",
                    // Toasts d'activation d'une carte .strm (v1.12) — {0} = titre du programme.
                    ["activate.toast.programmed"] = "Enregistrement programmé : {0}",
                    ["activate.toast.duplicate"] = "Déjà programmé : {0}",
                    ["activate.toast.failed"] = "Échec de l'enregistrement : {0}",
                    ["activate.toast.diskgate"] = "Disque d'enregistrements plein — programmation suspendue : {0}",
                    ["activate.toast.unauthorized"] = "Enregistrement non autorisé pour ce compte : {0}",
                    // --- Enregistrements à la voix (chat externe, v1.13.23) ---
                    // Circuit human-in-the-loop à PIN : notices + toasts +
                    // lignes d'état. Le code lui-même ne passe JAMAIS ici
                    // (canal hors bande).
                    ["rec.toast.pending"] = "🤖 À confirmer : « {0} » (code affiché dans l'app)",
                    ["rec.toast.created"] = "🤖 Enregistrement prévu : « {0} »",
                    ["rec.locked"] = "Trop d'essais de code erronés — outil d'enregistrement verrouillé pour {0} pendant {1} minute(s).",
                    ["rec.notice.none"] = "⚠️ Aucun code en attente (ou expiré) — AUCUN enregistrement créé.",
                    ["rec.notice.locked"] = "⚠️ 3 codes erronés — outil verrouillé, AUCUN enregistrement créé.",
                    ["rec.notice.wrong"] = "⚠️ Code incorrect ({0}/{1}) — AUCUN enregistrement créé.",
                    ["rec.pending.none"] = "Aucun enregistrement en attente (ou code expiré) — demandez-le de nouveau.",
                    ["rec.quota"] = "Limite de {0} enregistrement(s) par jour atteinte pour cet usager.",
                    ["rec.quota.notice"] = "⚠️ Quota du jour atteint — AUCUN enregistrement créé.",
                    ["rec.failed"] = "⚠️ Enregistrement non programmé — AUCUN enregistrement créé. Re-demandez l'enregistrement (un nouveau code s'affichera).",
                    ["rec.ok"] = "✅ Enregistrement programmé : « {0} ».",
                    ["rec.pendingline"] = "⏳ À confirmer : « {0} » ({1}) — code affiché à l'écran, expire à {2}.",
                    ["rec.kind.series"] = "série",
                    ["rec.kind.movie"] = "film",
                    ["rec.status.none"] = "Aucun enregistrement en attente de confirmation (réservation en attente ≠ enregistrement créé).",
                    ["rec.status.locked"] = "Outil d'enregistrement verrouillé (trop de codes erronés) — réessayez dans {0} minute(s). Rapporte-le tel quel ; ne suggère pas d'autres essais.",
                    ["rec.status.pendingnote"] = "Réservation EN ATTENTE du code (≠ enregistrement créé) : le code s'affiche à l'écran de l'app ; l'usager doit le fournir dans son message, jamais toi.",
                    ["task.category"] = "LLM AI",
                },
                ["en"] = new(StringComparer.Ordinal)
                {
                    // --- NFO scaffolding ---
                    ["nfo.rating"] = "rating {0}/10",
                    ["nfo.genres"] = "genres: {0}",
                    // (no more "nfo.why": .strm NFOs carry the 🤖 emoji alone —
                    // the "Why tonight" label is client-side, i18n.js key
                    // "rec.tonight.why", Watch-tonight section)
                    ["nfo.airs.prefix"] = "Airs",
                    ["nfo.airs.chan"] = " on {0}",
                    ["nfo.airs.date"] = ", {0}",
                    ["nfo.airs.suffix"] = " — play this card to schedule the recording.",
                    ["nfo.epglink"] = "🔗 EPG page: ",
                    ["nfo.seriesSuffix"] = " (series)",
                    // --- Scheduled tasks ---
                    ["task.llm.name"] = "LLM AI Task",
                    ["task.llm.desc"] = "Autonomous LLM agent (Ollama) that queries the Emby library via read-only native tools to accomplish the configured task.",
                    ["task.cleanup.name"] = "LLM AI — AI Tonight tag cleanup",
                    ["task.cleanup.desc"] = "Nightly cleanup of the native \"Watch tonight\" surfaces: removes the \"AI Tonight\" tag from all Emby items (and, v1.13.3 migration, the legacy genre of the same name) AND empties the \"AI Tonight\" collection of its members (the shell remains, refilled on the next run). Runs daily at 3 AM; subsequent Tonight runs rebuild the surfaces on still-relevant recos. Does not touch the \"AI Suggestion\" genre of the .strm library.",
                    ["task.orphan.name"] = "LLM AI — Orphan recording identification",
                    ["task.orphan.desc"] = "Daily pass (4 AM) that finds unidentified DVR recordings (no IMDb/TMDB id — often Quebec titles missing from TMDB/TVDB), resolves them via title cleanup + multi-language search (S1), then an LLM-proposed IMDb id validated through TMDB /find (S2), and writes the id + metadata + poster while locking the EPG title. Unresolved ones are tagged for review. The original EPG title is always preserved (locked).",
                    ["task.analysis.name"] = "LLM AI — Recommendation analysis (feedback loop)",
                    ["task.analysis.desc"] = "Weekly pass (Sunday 4 AM, opt-in RecoFeedbackEnabled) of the feedback loop: correlates in C# the week's recommendation log (\"Watch tonight\" recos, record recos, \"Forget\" rejections) against each user's actual watch history (watched / ignored / rejected / watched-without-reco), then has the LLM produce a concise directive that is persisted (PromptDirectives) and re-injected into subsequent run prompts. Fail-open: without a directive, prompts are unchanged; an analysis failure never breaks a run.",
                    ["task.memory.name"] = "LLM AI — Reflective memory (LLM card)",
                    ["task.memory.desc"] = "Weekly pass (Sunday 4:30 AM, opt-in MemoryCardEnabled) of the reflective memory: joins in C# the week's raw events (logged decisions with reasoning × playback telemetry with % watched × discarded menu candidates × EPG snapshot for live percentages), then has the LLM REWRITE its own memory card (~250 words) — what it knows about the user, its wins, its failures and its strategies. Versioned (4 versions kept); a failed LLM run keeps the previous card; with no signal, nothing runs.",
                    ["disktag.notif.title"] = "LLM AI — Recording disk almost full",
                    ["disktag.notif.desc"] = "{0} watched recording(s) tagged \"{1}\" (~{2:0.#} GB reclaimable). The disk threshold is crossed. To free space: filter the recordings library by the tag \"{1}\", multi-select and delete (the plugin never deletes anything itself).",
                    ["disktag.notif.desc.uncovered"] = "{0} watched recording(s) tagged \"{1}\" (~{2:0.#} GB reclaimable) — not enough to bring the disk back above the threshold. Delete them, then other recordings (or reduce timers) manually. The plugin never deletes anything itself.",
                    // Cross-kind notification (v1.13.28) — work identified under
                    // the opposite kind of the item (movie on a series, or reverse).
                    ["crosskind.notif.title"] = "LLM AI — Recording(s) typed under the wrong Emby kind",
                    ["crosskind.notif.desc"] = "{0} recording(s) identified with a fiche of the opposite kind (movie on a series item, or the reverse) — tagged \"{1}\". Correct ids/metadata/poster are written and locked; the item's type is fixed by Emby at DVR import and is never changed. To move a work to the right library: move its file (e.g. the .ts) to the target library — Emby re-imports it under the right kind and the plugin re-identifies it. The configuration page also lists these items (the \"cross-kind\" queue): copy to the right library, or convert the DVR folder in place (\"Title (Year)\" renaming + tvshow.nfo removal). Filter by the tag \"{1}\" to find these items.",
                    // .strm card activation toasts (v1.12) — {0} = program title.
                    ["activate.toast.programmed"] = "Recording scheduled: {0}",
                    ["activate.toast.duplicate"] = "Already scheduled: {0}",
                    ["activate.toast.failed"] = "Recording failed: {0}",
                    ["activate.toast.diskgate"] = "Recordings disk full — scheduling suspended: {0}",
                    ["activate.toast.unauthorized"] = "Recording not allowed for this account: {0}",
                    // --- Voice recordings (external chat, v1.13.23) ---
                    // PIN human-in-the-loop circuit: notices + toasts + state
                    // lines. The PIN itself NEVER goes through here
                    // (out-of-band channel).
                    ["rec.toast.pending"] = "🤖 To confirm: \"{0}\" (code shown in the app)",
                    ["rec.toast.created"] = "🤖 Recording scheduled: \"{0}\"",
                    ["rec.locked"] = "Too many wrong codes — the recording tool is locked for {0} for {1} minute(s).",
                    ["rec.notice.none"] = "⚠️ No pending code (or expired) — NO recording created.",
                    ["rec.notice.locked"] = "⚠️ 3 wrong codes — tool locked, NO recording created.",
                    ["rec.notice.wrong"] = "⚠️ Wrong code ({0}/{1}) — NO recording created.",
                    ["rec.pending.none"] = "No recording awaiting confirmation (or code expired) — ask for it again.",
                    ["rec.quota"] = "Per-day limit of {0} recording(s) reached for this user.",
                    ["rec.quota.notice"] = "⚠️ Daily quota reached — NO recording created.",
                    ["rec.failed"] = "⚠️ Recording not scheduled — NO recording created. Ask for the recording again (a new code will appear).",
                    ["rec.ok"] = "✅ Recording scheduled: \"{0}\".",
                    ["rec.pendingline"] = "⏳ Awaiting confirmation: \"{0}\" ({1}) — code shown on screen, expires at {2}.",
                    ["rec.kind.series"] = "series",
                    ["rec.kind.movie"] = "movie",
                    ["rec.status.none"] = "No recording awaiting confirmation (a pending reservation is not a created recording).",
                    ["rec.status.locked"] = "The recording tool is locked (too many wrong codes) — try again in {0} minute(s). Report it as-is; do not suggest further attempts.",
                    ["rec.status.pendingnote"] = "Reservation AWAITING the code (not a created recording): the code is shown on the app's screen; the user must provide it in their message, never you.",
                    ["task.category"] = "LLM AI",
                },
            };

        // ------------------------------------------------------------------
        //  Utilitaire : retire les accents (Normalize FormD + filtre NonSpacingMark)
        // ------------------------------------------------------------------

        private static string NoDiacritics(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            foreach (var c in s.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString();
        }
    }
}