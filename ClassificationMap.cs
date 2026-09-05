using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace LLM_AI
{
    /// <summary>
    /// Lecteur de la configuration du plugin <b>Classification Mapper</b>
    /// (<c>classification_mapper_config.json</c> dans le dossier de
    /// configuration du SERVEUR — pas celui des plugins) : normalise les
    /// classifications officielles hétérogènes (« PG-13 », « TV-14 », « 14A »,
    /// « 13+ »…) vers les valeurs canoniques maintenues dans son UI
    /// (« CA-G », « CA-PG », « CA-14A », « CA-18A », « CA-R », « CA-A », « NR »).
    /// </summary>
    /// <remarks>
    /// Miroir de <see cref="GenreCleanerMap"/> : la bibliothèque est déjà
    /// réécrite par Classification Mapper lui-même, mais l'EPG (Gracenote)
    /// émet des classifications brutes hétérogènes. Sans ce pont, les
    /// classifications envoyées au LLM viennent de DEUX vocabulaires
    /// différents et un filtre « 13+ » raterait à la fois « PG-13 » (EPG) et
    /// « CA-14A » (biblio réécrite). En normalisant les deux côtés, un filtre
    /// demandé dans n'importe quelle forme brute matche les deux sources.
    /// Détection automatique : si le fichier est absent (plugin non installé
    /// sur ce serveur) ou illisible, le lecteur est neutre — chaque
    /// classification ressort normalisée minuscule-accordée (uppercased)
    /// sans mapping (comportement sans Classification Mapper). Rechargement
    /// paresseux si le fichier change (mtime, re-stat au plus toutes les 30 s).
    /// </remarks>
    internal static class ClassificationMap
    {
        private const string ConfigFileName = "classification_mapper_config.json";

        // Re-stat du fichier au plus toutes les 30 s (Classification Mapper
        // est édité dans son UI à la volée ; les mappings doivent suivre
        // sans restart).
        private static readonly TimeSpan StatThrottle = TimeSpan.FromSeconds(30);

        private static readonly object _lock = new object();
        private static DateTime _lastStatUtc = DateTime.MinValue;
        private static DateTime _lastMtimeUtc = DateTime.MinValue;
        private static Dictionary<string, string> _reverse; // brut normalisé → canonique
        private static bool _loaded;

        /// <summary>
        /// Chemin du fichier de config Classification Mapper (dossier de
        /// configuration du serveur) — c'est le dossier racine du
        /// programdata Emby, PAS <c>PluginConfigurationsPath</c> :
        /// Classification Mapper y dépose son JSON directement (vérifié :
        /// <c>/var/lib/emby/config/</c>). Sur cette build la propriété
        /// <c>IApplicationPaths</c> s'appelle <c>ConfigurationDirectoryPath</c>
        /// (pas « ConfigurationPath ») ; repli : <c>ProgramDataPath/config</c>.
        /// </summary>
        private static string ConfigPath
        {
            get
            {
                try
                {
                    var paths = Plugin.Paths;
                    if (paths == null) return null;
                    var dir = paths.ConfigurationDirectoryPath;
                    if (string.IsNullOrEmpty(dir))
                        dir = paths.ProgramDataPath;
                    if (string.IsNullOrEmpty(dir)) return null;
                    return Path.Combine(dir, ConfigFileName);
                }
                catch { return null; }
            }
        }

        // ------------------------------------------------------------------
        //  Chargement (paresseux, re-stat throttlé)
        // ------------------------------------------------------------------

        /// <summary>
        /// Charge / recharge l'index inverse si nécessaire. Ne lève jamais :
        /// en cas d'absence ou d'erreur, la table reste vide (classification
        /// renvoyée normalisée en casse seulement — comportement sans
        /// Classification Mapper).
        /// </summary>
        private static void EnsureLoaded()
        {
            var path = ConfigPath;
            if (path == null) return;

            lock (_lock)
            {
                if (_loaded && DateTime.UtcNow - _lastStatUtc < StatThrottle) return;
                _lastStatUtc = DateTime.UtcNow;

                DateTime mtime;
                try { mtime = File.GetLastWriteTimeUtc(path); }
                catch { mtime = DateTime.MinValue; }

                if (_loaded && mtime == _lastMtimeUtc) return;

                var reverse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(path))
                    {
                        using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                        {
                            if (doc.RootElement.TryGetProperty("Mappings", out var mappings)
                                && mappings.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var canon in mappings.EnumerateObject())
                                {
                                    var canonicalKey = KeyOf(canon.Name);
                                    // Valeur D'AFFICHAGE : la casse canonique du
                                    // JSON (« CA-14A »), PAS la clé pliée — les
                                    // classifications émises au LLM doivent rester
                                    // lisibles (vécu : « ca-g » au lieu de « CA-G »).
                                    var canonical = canon.Name?.Trim();
                                    if (string.IsNullOrEmpty(canonicalKey) || string.IsNullOrEmpty(canonical)) continue;
                                    // Index inverse : chaque brut → canonique.
                                    // Le canonique lui-même est indexé (items
                                    // déjà réécrits par Classification Mapper).
                                    reverse[canonicalKey] = canonical;
                                    if (canon.Value.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var el in canon.Value.EnumerateArray())
                                        {
                                            if (el.ValueKind != JsonValueKind.String) continue;
                                            var raw = KeyOf(el.GetString());
                                            if (!string.IsNullOrEmpty(raw)) reverse[raw] = canonical;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { /* JSON invalide : table vide = passthrough */ }

                _reverse = reverse;
                _lastMtimeUtc = mtime;
                _loaded = true;
            }
        }

        /// <summary>
        /// Clé d'index d'une classification : trim + minuscules. On ne plie
        /// PAS les accents (les valeurs de classification sont numériques ou
        /// alphabétiques brutes — « 13+ », « PG-13 », « R ») mais on retire
        /// les espaces internes pour tolérer « PG 13 » ≡ « PG-13 ».
        /// </summary>
        private static string KeyOf(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return _WsRegex.Replace(s.Trim().ToLowerInvariant(), "");
        }

        /// <summary>
        /// Normalise une classification officielle vers sa valeur canonique
        /// Classification Mapper (« 13+ » → « CA-14A », « PG-13 » →
        /// « CA-14A »). Valeur inconnue → telle quelle (trim + uppercase
        /// pour un comparatif stable) ; vide/null → null.
        /// </summary>
        internal static string Normalize(string officialRating)
        {
            if (string.IsNullOrWhiteSpace(officialRating)) return null;
            EnsureLoaded();
            var key = KeyOf(officialRating);
            if (key == null) return null;
            if (_reverse != null && _reverse.TryGetValue(key, out var canonical))
                return canonical;
            // Sans mapping (ou valeur non couverte) : forme stable uppercase.
            return key.ToUpperInvariant();
        }

        private static readonly System.Text.RegularExpressions.Regex _WsRegex =
            new System.Text.RegularExpressions.Regex(
                @"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);
    }
}