using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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
                    ["task.cleanup.name"] = "LLM AI — Nettoyage genre « AI Tonight »",
                    ["task.cleanup.desc"] = "Nettoyage nocturne des surfaces natives « À regarder ce soir » : retire le genre « AI Tonight » de tous les items Emby ET vide la collection « AI Tonight » de ses membres (la coquille reste, re-remplie au prochain run). Tourne quotidiennement à 3 h ; les runs Tonight suivants reconstruisent les surfaces sur les recos toujours pertinentes. Ne touche pas au genre « AI Suggestion » de la bibliothèque .strm.",
                    ["task.orphan.name"] = "LLM AI — Identification des enregistrements orphelins",
                    ["task.orphan.desc"] = "Passe quotidienne (4 h) qui repère les enregistrements DVR non identifiés (sans id IMDb/TMDB — souvent des titres québécois absents du catalogue TMDB/TVDB), tente de les résoudre par nettoyage du titre + recherche multilingue (S1), puis par proposition LLM d'un id IMDb validé via TMDB /find (S2), et écrit l'id + métadonnées + affiche en verrouillant le titre EPG. Les irrésolus sont marqués pour revue. Le titre original EPG est toujours préservé (verrouillé).",
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
                    ["task.cleanup.name"] = "LLM AI — AI Tonight genre cleanup",
                    ["task.cleanup.desc"] = "Nightly cleanup of the native \"Watch tonight\" surfaces: removes the \"AI Tonight\" genre from all Emby items AND empties the \"AI Tonight\" collection of its members (the shell remains, refilled on the next run). Runs daily at 3 AM; subsequent Tonight runs rebuild the surfaces on still-relevant recos. Does not touch the \"AI Suggestion\" genre of the .strm library.",
                    ["task.orphan.name"] = "LLM AI — Orphan recording identification",
                    ["task.orphan.desc"] = "Daily pass (4 AM) that finds unidentified DVR recordings (no IMDb/TMDB id — often Quebec titles missing from TMDB/TVDB), resolves them via title cleanup + multi-language search (S1), then an LLM-proposed IMDb id validated through TMDB /find (S2), and writes the id + metadata + poster while locking the EPG title. Unresolved ones are tagged for review. The original EPG title is always preserved (locked).",
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