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

        /// <summary>
        /// Libellé localisé <paramref name="key"/> dans la langue d'affichage
        /// résolue depuis <paramref name="host"/> — raccourci monolithe pour
        /// les endpoints/services à un ou deux sites d'erreur (chat, config,
        /// actions du chat). Même bucket que les noms de tâches.
        /// </summary>
        internal static string SDisplay(string key, IServerApplicationHost host)
            => S(key, ResolveDisplayLangKey(host));

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
                    // --- Chat admin + page config : erreurs des endpoints
                    // (v1.15.0.2) — résolues via la langue d'affichage
                    // (ResolveDisplayLangKey), même bucket que les noms de
                    // tâches. Avant : chaînes codées en dur FR.
                    ["err.admin"] = "Réservé aux administrateurs.",
                    ["err.noconfig"] = "Configuration du plugin indisponible.",
                    ["err.emptymsg"] = "Message vide.",
                    ["err.chatext.disabled"] = "Chat externe désactivé.",
                    ["err.llmtimeout"] = "Le LLM n'a pas répondu à temps (délai dépassé). Réessayez.",
                    ["err.chatfail"] = "Échec du chat : ",
                    ["err.pending.expired.save"] = "Action introuvable ou expirée (attente valable 10 minutes) — demandez à nouveau la sauvegarde dans la conversation.",
                    ["err.pending.expired.action"] = "Action introuvable ou expirée (attente valable 10 minutes) — demandez à nouveau l'action dans la conversation.",
                    ["err.proposal.invalid"] = "Proposition invalide — rien n'a été écrit.",
                    ["err.actionlayer.disabled"] = "La couche d'action du chat est désactivée (budget 0) — rien n'a été exécuté.",
                    ["err.tool.unknown"] = "Outil d'action indisponible ou inconnu — proposition invalide.",
                    ["err.exec.cancelled"] = "Exécution annulée.",
                    ["detail.executed"] = "Action exécutée.",
                    ["err.exec.failed"] = "Échec de l'exécution — consultez le journal du serveur.",
                    // --- Chat admin : carte de diff (libellé du champ,
                    // bandeau de divergence, indice de test) ---
                    ["chat.field.rag_directives"] = "Directives RAG",
                    ["chat.field.schedule_task"] = "Tâche séries",
                    ["chat.field.schedule_task_movies"] = "Tâche films",
                    ["chat.field.tonight_prompt"] = "Run « ce soir »",
                    ["chat.field.audit_prompt"] = "Prompt d'audit",
                    ["chat.warn.divergence"] = "⚠ Ce texte diffère fortement du texte actuel du champ (recouvrement {0}%) — vérifiez qu'il s'agit bien d'une révision de « {1} » et non d'un autre prompt.",
                    ["chat.hint.tonight_prompt"] = "Test réel : demandez dans cette conversation « lance le run ce soir » (tool run_tonight_run — opt-in « déclenchement par le chat » en config) : il exécute le VRAI code TonightService avec la nouvelle directive ; le résultat apparaît sur la page Recommandations (badge « générée via chat »).",
                    ["chat.hint.schedule_task"] = "Test : (a) dry-run conversationnel — demandez « applique la directive aux données epg_series et montre le tableau JSON » (vérifie le format et les champs) ; (b) exécution réelle — déclenchez la tâche « Enregistrements séries » (tableau de bord Emby, ou system_audit action=trigger_task si la remédiation est activée) et vérifiez les recommandations produites.",
                    ["chat.hint.schedule_task_movies"] = "Test : (a) dry-run conversationnel — demandez « applique la directive aux données epg_movies et montre le tableau JSON » ; (b) exécution réelle — déclenchez la tâche « Enregistrements films » et vérifiez les recommandations.",
                    ["chat.hint.audit_prompt"] = "Test réel : le bouton « Lancer l'audit santé » de la page de configuration (le chat, lui, exécute system_audit avec son workflow interne, pas ce prompt).",
                    ["chat.hint.rag_directives"] = "Test : ces directives s'appliquent à TOUS les runs — le meilleur indicateur est un run « ce soir » (ou le dry-run conversationnel d'une directive de tâche) : les garde-fous (jamais un titre possédé, jamais une donnée devinée) doivent y être visibles.",
                    // --- Chat admin : actions Emby (libellés des cartes de
                    // proposition, toasts « Actions du tour », détails
                    // d'exécution affichés sur la carte + poussés au LLM) ---
                    ["act.label.record"] = "Enregistrement « {0} » (DVR, {1})",
                    ["act.label.card"] = "Carte .strm « {0} »{1}",
                    ["act.label.tag"] = "Tag « AI Tonight » : {0} item(s)",
                    ["act.label.colladd"] = "Ajout à la collection « AI Tonight » : {0} item(s)",
                    ["act.label.collremove"] = "Retrait de la collection « AI Tonight » : {0} item(s)",
                    ["act.label.pladd"] = "Ajout à la playlist privée de l'admin : {0} item(s)",
                    ["act.label.plremove"] = "Retrait de la playlist privée de l'admin : {0} item(s)",
                    ["act.label.run"] = "Run « À regarder ce soir »",
                    ["act.label.run.dir"] = " (directives : {0})",
                    ["act.label.stop.playing"] = "Arrêt de la lecture de {0} — « {1} »",
                    ["act.label.stop.idle"] = "Arrêt de la session de {0} (aucune lecture en cours)",
                    ["act.label.task"] = "Déclenchement de la tâche « {0} »",
                    ["act.label.msg"] = "Message Emby à {0} ({1}) : {2} — {3}",
                    ["act.user.fallback"] = "un usager",
                    ["act.toast.record"] = "Chat : timer programmé pour « {0} »",
                    ["act.toast.card"] = "Chat : carte .strm « {0} » créée",
                    ["act.toast.tag"] = "Chat : {0} item(s) tagué(s) « {1} »",
                    ["act.toast.colladd"] = "Chat : {0} item(s) ajouté(s) à la collection « {1} »",
                    ["act.toast.collremove"] = "Chat : {0} item(s) retiré(s) de la collection « {1} »",
                    ["act.toast.pladd"] = "Chat : {0} item(s) ajouté(s) à la playlist « {1} »",
                    ["act.toast.plremove"] = "Chat : {0} item(s) retiré(s) de la playlist « {1} »",
                    ["act.toast.run"] = "Chat : run « ce soir » lancé ({0} reco(s))",
                    ["act.toast.stop.idle"] = "Chat : rien à arrêter pour {0} (aucune lecture en cours)",
                    ["act.toast.stop"] = "Chat : lecture arrêtée pour {0} (« {1} »)",
                    ["act.toast.task"] = "Chat : tâche « {0} » déclenchée",
                    ["act.toast.msg"] = "Chat : message ({0}) envoyé à {1}",
                    ["act.ok.record"] = "Enregistrement programmé : {0}",
                    ["act.ok.card"] = "Carte créée dans la bibliothèque « AI Suggestions » : {0}",
                    ["act.ok.tag"] = "{0} item(s) étiqueté(s) « {1} ».",
                    ["act.ok.colladd"] = "{0} item(s) ajouté(s) à la collection « {1} ».",
                    ["act.ok.collremove"] = "{0} item(s) retiré(s) de la collection.",
                    ["act.ok.pladd"] = "{0} item(s) ajouté(s) à la playlist « {1} » (privée, compte admin).",
                    ["act.ok.plremove"] = "{0} item(s) retiré(s) de la playlist privée du compte admin.",
                    ["act.ok.run"] = "{0} recommandation(s) générée(s) et livrée(s) via les surfaces habituelles (page Recommandations, genre/collection/playlist selon la config) : {1}",
                    ["act.ok.stop.idle"] = "Aucune lecture en cours pour {0} — rien à arrêter.",
                    ["act.ok.stop"] = "Lecture arrêtée pour {0} (« {1} »).",
                    ["act.ok.task"] = "Tâche « {0} » mise en file d'exécution.",
                    ["act.ok.msg.osd"] = "Toast OSD : {0} session(s) atteinte(s) sur {1} destinataire(s).{2}",
                    ["act.ok.msg.notif"] = "Notification envoyée à {0} ({1}/{2}).",
                    // Refus ATTEIGNABLES à l'approbation (le détail s'affiche
                    // sur la carte en erreur) — les refus défensifs de
                    // re-validation (doublons de la phase de dépôt, lus par
                    // le LLM) restent en français.
                    ["act.ref.record"] = "Enregistrement non programmé ({0}). Rapportez le motif à l'admin, ne réessayez pas à l'identique.",
                    ["act.ref.card"] = "Écriture de la carte impossible (bibliothèque .strm absente ou erreur).",
                    ["act.ref.noids"] = "aucun id résolvable dans la bibliothèque.",
                    ["act.ref.colladd"] = "ajout impossible (API collection).",
                    ["act.ref.notadded"] = "Aucun de ces ids n'a été ajouté par vous dans cette conversation — retrait refusé.",
                    ["act.ref.nopluser"] = "aucun usager admin résolvable pour la playlist.",
                    ["act.ref.noadd"] = "aucun ajout effectué (items déjà présents dans la playlist, ou échec API).",
                    ["act.ref.plremove.noop"] = "Le retrait n'a PAS été appliqué : RemoveFromPlaylist est inopérant sur ce build Emby. Ne réessayez pas — le prochain run Tonight recrée la playlist de toute façon.",
                    ["act.ref.tonight.off"] = "Le module « À regarder ce soir » est désactivé dans la config.",
                    ["act.ref.tonight.running"] = "Un run « ce soir » déclenché par le chat est déjà en cours — réessayez plus tard.",
                    ["act.ref.tonight.limit"] = "Limite de 2 runs par conversation atteinte.",
                    ["act.ref.nouser"] = "aucun usager résolvable pour le run.",
                    ["act.ref.cancelled"] = "annulé",
                    ["act.ref.budget.turn"] = "Budget d'actions atteint pour ce tour ({0}). Finissez votre proposition ou réformez-la — n'insistez pas.",
                    ["act.ref.budget.conv"] = "Budget d'actions de la conversation épuisé ({0}). Poursuivez en lecture seule.",
                    // --- Run « ce soir » : erreurs TonightResult (carte du
                    // chat via run_tonight_run + page Recommandations) ---
                    ["tonight.err.nouser"] = "Utilisateur non résolu.",
                    ["tonight.err.noreco"] = "Le run LLM n'a pas produit de recommandation.",
                    ["tonight.err.noitems"] = "Toutes les recommandations pointaient vers des items introuvables (EPG expiré ou items supprimés).",
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
                    // --- Admin chat + config page: endpoint errors (v1.15.0.2)
                    // — resolved through the display language
                    // (ResolveDisplayLangKey), same bucket as task names.
                    // Previously: hardcoded FR strings.
                    ["err.admin"] = "Administrators only.",
                    ["err.noconfig"] = "Plugin configuration unavailable.",
                    ["err.emptymsg"] = "Empty message.",
                    ["err.chatext.disabled"] = "External chat disabled.",
                    ["err.llmtimeout"] = "The LLM did not respond in time (timeout exceeded). Try again.",
                    ["err.chatfail"] = "Chat failed: ",
                    ["err.pending.expired.save"] = "Proposal not found or expired (valid for 10 minutes) — request the save again in the conversation.",
                    ["err.pending.expired.action"] = "Action not found or expired (valid for 10 minutes) — request the action again in the conversation.",
                    ["err.proposal.invalid"] = "Invalid proposal — nothing was written.",
                    ["err.actionlayer.disabled"] = "The chat action layer is disabled (budget 0) — nothing was executed.",
                    ["err.tool.unknown"] = "Action tool unavailable or unknown — invalid proposal.",
                    ["err.exec.cancelled"] = "Execution cancelled.",
                    ["detail.executed"] = "Action executed.",
                    ["err.exec.failed"] = "Execution failed — check the server log.",
                    // --- Admin chat: diff card (field label, divergence
                    // banner, test hint) ---
                    ["chat.field.rag_directives"] = "RAG directives",
                    ["chat.field.schedule_task"] = "Series task",
                    ["chat.field.schedule_task_movies"] = "Movies task",
                    ["chat.field.tonight_prompt"] = "\"Watch tonight\" run",
                    ["chat.field.audit_prompt"] = "Audit prompt",
                    ["chat.warn.divergence"] = "⚠ This text differs strongly from the field's current text ({0}% overlap) — make sure it really is a revision of \"{1}\" and not another prompt.",
                    ["chat.hint.tonight_prompt"] = "Real test: ask in this conversation \"run tonight's run\" (tool run_tonight_run — opt-in \"chat triggering\" in config): it executes the REAL TonightService code with the new directive; the result appears on the Recommendations page (\"generated via chat\" badge).",
                    ["chat.hint.schedule_task"] = "Test: (a) conversational dry-run — ask \"apply the directive to the epg_series data and show the JSON table\" (checks the format and fields); (b) real run — trigger the series-recordings task (Emby dashboard, or system_audit action=trigger_task if remediation is enabled) and check the recommendations produced.",
                    ["chat.hint.schedule_task_movies"] = "Test: (a) conversational dry-run — ask \"apply the directive to the epg_movies data and show the JSON table\"; (b) real run — trigger the movies-recordings task and check the recommendations.",
                    ["chat.hint.audit_prompt"] = "Real test: the \"Run health audit\" button on the configuration page (the chat itself runs system_audit with its own internal workflow, not this prompt).",
                    ["chat.hint.rag_directives"] = "Test: these directives apply to ALL runs — the best indicator is a \"tonight\" run (or the conversational dry-run of a task directive): the guardrails (never an owned title, never a guessed fact) must be visible there.",
                    // --- Admin chat: Emby actions (proposal card labels,
                    // "Actions this turn" toasts, execution details shown
                    // on the card + pushed to the LLM) ---
                    ["act.label.record"] = "Recording \"{0}\" (DVR, {1})",
                    ["act.label.card"] = ".strm card \"{0}\"{1}",
                    ["act.label.tag"] = "\"AI Tonight\" tag: {0} item(s)",
                    ["act.label.colladd"] = "Add to the \"AI Tonight\" collection: {0} item(s)",
                    ["act.label.collremove"] = "Remove from the \"AI Tonight\" collection: {0} item(s)",
                    ["act.label.pladd"] = "Add to the admin's private playlist: {0} item(s)",
                    ["act.label.plremove"] = "Remove from the admin's private playlist: {0} item(s)",
                    ["act.label.run"] = "\"Watch tonight\" run",
                    ["act.label.run.dir"] = " (directives: {0})",
                    ["act.label.stop.playing"] = "Stop playback of {0} — \"{1}\"",
                    ["act.label.stop.idle"] = "Stop {0}'s session (nothing playing)",
                    ["act.label.task"] = "Trigger the task \"{0}\"",
                    ["act.label.msg"] = "Emby message to {0} ({1}): {2} — {3}",
                    ["act.user.fallback"] = "a user",
                    ["act.toast.record"] = "Chat: timer scheduled for \"{0}\"",
                    ["act.toast.card"] = "Chat: .strm card \"{0}\" created",
                    ["act.toast.tag"] = "Chat: {0} item(s) tagged \"{1}\"",
                    ["act.toast.colladd"] = "Chat: {0} item(s) added to the \"{1}\" collection",
                    ["act.toast.collremove"] = "Chat: {0} item(s) removed from the \"{1}\" collection",
                    ["act.toast.pladd"] = "Chat: {0} item(s) added to the \"{1}\" playlist",
                    ["act.toast.plremove"] = "Chat: {0} item(s) removed from the \"{1}\" playlist",
                    ["act.toast.run"] = "Chat: \"tonight\" run started ({0} recommendation(s))",
                    ["act.toast.stop.idle"] = "Chat: nothing to stop for {0} (nothing playing)",
                    ["act.toast.stop"] = "Chat: playback stopped for {0} (\"{1}\")",
                    ["act.toast.task"] = "Chat: task \"{0}\" triggered",
                    ["act.toast.msg"] = "Chat: message ({0}) sent to {1}",
                    ["act.ok.record"] = "Recording scheduled: {0}",
                    ["act.ok.card"] = "Card created in the \"AI Suggestions\" library: {0}",
                    ["act.ok.tag"] = "{0} item(s) tagged \"{1}\".",
                    ["act.ok.colladd"] = "{0} item(s) added to the \"{1}\" collection.",
                    ["act.ok.collremove"] = "{0} item(s) removed from the collection.",
                    ["act.ok.pladd"] = "{0} item(s) added to the playlist \"{1}\" (private, admin account).",
                    ["act.ok.plremove"] = "{0} item(s) removed from the admin account's private playlist.",
                    ["act.ok.run"] = "{0} recommendation(s) generated and delivered through the usual surfaces (Recommendations page, genre/collection/playlist per config): {1}",
                    ["act.ok.stop.idle"] = "Nothing playing for {0} — nothing to stop.",
                    ["act.ok.stop"] = "Playback stopped for {0} (\"{1}\").",
                    ["act.ok.task"] = "Task \"{0}\" queued for execution.",
                    ["act.ok.msg.osd"] = "OSD toast: {0} session(s) reached out of {1} recipient(s).{2}",
                    ["act.ok.msg.notif"] = "Notification sent to {0} ({1}/{2}).",
                    // Refusals REACHABLE at approval (the detail is shown on
                    // the card as an error) — defensive re-validation
                    // refusals (twins of the deposit phase, read by the LLM)
                    // stay in French.
                    ["act.ref.record"] = "Recording not scheduled ({0}). Report the reason to the admin; do not retry the same way.",
                    ["act.ref.card"] = "Card could not be written (.strm library missing or error).",
                    ["act.ref.noids"] = "no resolvable id in the library.",
                    ["act.ref.colladd"] = "add failed (collection API).",
                    ["act.ref.notadded"] = "None of these ids were added by you in this conversation — removal refused.",
                    ["act.ref.nopluser"] = "no resolvable admin user for the playlist.",
                    ["act.ref.noadd"] = "nothing added (items already in the playlist, or API failure).",
                    ["act.ref.plremove.noop"] = "The removal was NOT applied: RemoveFromPlaylist is a no-op on this Emby build. Do not retry — the next Tonight run rebuilds the playlist anyway.",
                    ["act.ref.tonight.off"] = "The \"Watch tonight\" module is disabled in the config.",
                    ["act.ref.tonight.running"] = "A chat-triggered \"tonight\" run is already in progress — try again later.",
                    ["act.ref.tonight.limit"] = "Limit of 2 runs per conversation reached.",
                    ["act.ref.nouser"] = "no resolvable user for the run.",
                    ["act.ref.cancelled"] = "cancelled",
                    ["act.ref.budget.turn"] = "Action budget reached for this turn ({0}). Finish your proposal or rephrase it — do not insist.",
                    ["act.ref.budget.conv"] = "Conversation action budget exhausted ({0}). Continue read-only.",
                    // --- "Watch tonight" run: TonightResult errors (chat card
                    // via run_tonight_run + Recommendations page) ---
                    ["tonight.err.nouser"] = "Could not resolve the user.",
                    ["tonight.err.noreco"] = "The LLM run produced no recommendation.",
                    ["tonight.err.noitems"] = "All recommendations pointed to items that could not be found (expired EPG or deleted items).",
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