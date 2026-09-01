using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Lecteur de la configuration du plugin <b>GenreCleaner</b>
    /// (<c>GenreCleaner.xml</c> dans le dossier des configurations de plugins) :
    /// mappe les genres bruts de l'EPG (souvent en anglais, non normalisés —
    /// « Comedy », « Drama »…) vers le vocabulaire français curaté que
    /// l'usager maintient dans GenreCleaner (« Comédie », « Drame »…).
    /// </summary>
    /// <remarks>
    /// La bibliothèque principale est déjà curatée par GenreCleaner lui-même ;
    /// l'EPG, lui, est une donnée vivante que GenreCleaner ne nettoie pas. Sans
    /// ce pont, les genres envoyés au LLM viennent de DEUX vocabulaires
    /// différents : le profil de goût « À regarder ce soir » (bibliothèque,
    /// français curaté) côte à côte avec les programmes EPG (bruts) — le LLM
    /// doit lui-même deviner que « Comedy » ≈ « Comédie ». En appliquant les
    /// mappings GenreCleaner aux genres EPG AVANT de les émettre (outils
    /// epg_series / epg_movies / epg_tonight), les deux vocabulaires
    /// s'alignent.
    /// Détection automatique : si le fichier est absent (plugin non installé)
    /// ou illisible, le lecteur est neutre — chaque genre ressort tel quel
    /// (aucune config, aucun flag). Rechargement paresseux si le fichier
    /// change (mtime, re-stat au plus toutes les 30 s).
    /// </remarks>
    internal static class GenreCleanerMap
    {
        private const string ConfigFileName = "GenreCleaner.xml";

        // Re-stat du fichier au plus toutes les 30 s (GenreCleaner est édité
        // dans son UI à la volée ; les mappings doivent suivre sans restart).
        private static readonly TimeSpan StatThrottle = TimeSpan.FromSeconds(30);

        private static readonly object _lock = new object();
        private static DateTime _lastStatUtc = DateTime.MinValue;
        private static DateTime _lastMtimeUtc = DateTime.MinValue;
        private static Dictionary<string, string> _movie;   // clé Norm(Nom) → valeur curatée
        private static Dictionary<string, string> _series;
        private static List<string> _movieAllowed;         // AllowedGenres (vocabulaire curaté, films)
        private static List<string> _seriesAllowed;        // AllowedGenres (séries)
        private static bool _loaded;

        /// <summary>
        /// Chemin du fichier de config GenreCleaner (dossier des
        /// configurations de plugins exposé par l'hôte via
        /// <see cref="MediaBrowser.Common.Configuration.IApplicationPaths.PluginConfigurationsPath"/>).
        /// </summary>
        private static string ConfigPath
        {
            get
            {
                try
                {
                    var dir = Plugin.Paths?.PluginConfigurationsPath;
                    return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, ConfigFileName);
                }
                catch { return null; }
            }
        }

        // ------------------------------------------------------------------
        //  Chargement (paresseux, re-stat throttlé)
        // ------------------------------------------------------------------

        /// <summary>
        /// Charge / recharge les mappings si nécessaire. Ne lève jamais :
        /// en cas d'absence ou d'erreur, les tables restent vides (genre
        /// renvoyés tels quels — comportement sans GenreCleaner).
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

                var movie = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var series = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var movieAllowed = new List<string>();
                var seriesAllowed = new List<string>();
                try
                {
                    if (File.Exists(path))
                    {
                        var root = XDocument.Load(path).Root;
                        if (root != null)
                        {
                            ParseSection(root.Element("MovieOptions"), movie);
                            ParseSection(root.Element("SeriesOptions"), series);
                            ParseAllowed(root.Element("MovieOptions"), movieAllowed);
                            ParseAllowed(root.Element("SeriesOptions"), seriesAllowed);
                        }
                    }
                }
                catch { /* XML invalide : tables vides = passthrough */ }

                _movie = movie;
                _series = series;
                _movieAllowed = movieAllowed;
                _seriesAllowed = seriesAllowed;
                _lastMtimeUtc = mtime;
                _loaded = true;
            }
        }

        /// <summary>
        /// Extrait les <c>GenreMappings</c> (NameValuePair Name→Value) d'une
        /// section (<c>MovieOptions</c>/<c>SeriesOptions</c>). Clé = Nom
        /// normalisé par <see cref="GetEmbyInfoTool.Norm"/> (casse/accents
        /// pliés : « Comedy » ≡ « comedy » ≡ « Comedie » côté clé — la valeur
        /// curatée est conservée telle quelle).
        /// </summary>
        private static void ParseSection(XElement section, Dictionary<string, string> map)
        {
            if (section == null) return;
            foreach (var pair in section.Element("GenreMappings")?.Elements("NameValuePair") ?? Enumerable.Empty<XElement>())
            {
                var name = pair.Element("Name")?.Value?.Trim();
                var value = pair.Element("Value")?.Value?.Trim();
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value)) continue;
                map[Norm(name)] = value;
            }
        }

        private static string Norm(string s)
        {
            var n = GetEmbyInfoTool.Norm(s);
            return string.IsNullOrEmpty(n) ? s : n;
        }

        /// <summary>
        /// Extrait la liste <c>AllowedGenres</c> d'une section — le
        /// vocabulaire curaté que l'usager maintient dans GenreCleaner. Sert
        /// à contraindre les propositions de l'outil « Traduction des genres
        /// (IA) » : le LLM ne peut choisir QUE dans cette liste.
        /// </summary>
        private static void ParseAllowed(XElement section, List<string> into)
        {
            if (section == null) return;
            foreach (var g in section.Element("AllowedGenres")?.Elements("string") ?? Enumerable.Empty<XElement>())
            {
                var v = g.Value?.Trim();
                if (!string.IsNullOrEmpty(v) && !into.Contains(v, StringComparer.OrdinalIgnoreCase))
                    into.Add(v);
            }
        }

        // ------------------------------------------------------------------
        //  API de mapping
        // ------------------------------------------------------------------

        /// <summary>
        /// Mappe UN genre EPG vers le vocabulaire curaté. <paramref name="series"/>
        /// sélectionne la table à essayer en premier (GenreCleaner distingue
        /// films et séries : « Kids »→« Enfant » côté séries, « Children »→
        /// « Familial » côté films) ; l'autre table sert de repli, puis le
        /// genre original est conservé s'il n'est mappé nulle part.
        /// </summary>
        internal static string MapGenre(string genre, bool? series)
        {
            if (string.IsNullOrWhiteSpace(genre)) return genre;
            EnsureLoaded();

            string value;
            if (series == true)
            {
                if (_series != null && _series.TryGetValue(Norm(genre), out value)) return value;
                if (_movie != null && _movie.TryGetValue(Norm(genre), out value)) return value;
            }
            else if (series == false)
            {
                if (_movie != null && _movie.TryGetValue(Norm(genre), out value)) return value;
                if (_series != null && _series.TryGetValue(Norm(genre), out value)) return value;
            }
            else
            {
                if (_series != null && _series.TryGetValue(Norm(genre), out value)) return value;
                if (_movie != null && _movie.TryGetValue(Norm(genre), out value)) return value;
            }
            return genre;
        }

        /// <summary>
        /// Mappe un tableau de genres (EPG → vocabulaire curaté) en
        /// conservant l'ordre et en dédupliquant après mapping (un programme
        /// « Comedy » + « Comedy drama » ne doit pas produire deux fois
        /// « Comédie »). Jamais null : null → tableau vide.
        /// </summary>
        internal static string[] MapGenres(IEnumerable<string> genres, bool? series)
        {
            if (genres == null) return Array.Empty<string>();
            var outList = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in genres)
            {
                if (string.IsNullOrWhiteSpace(g)) continue;
                var mapped = MapGenre(g, series);
                if (seen.Add(mapped)) outList.Add(mapped);
            }
            return outList.ToArray();
        }

        /// <summary>
        /// Clés de comparaison d'un ensemble de genres pour les filtres
        /// déterministes (whitelist genres, exclude_genres, bonus de
        /// pertinence) : pour chaque genre, sa forme BRUTE et sa forme MAPPÉE,
        /// en minuscules. Les entrées de whitelist saisies dans l'UI (peuplée
        /// depuis le vocabulaire EPG brut) continuent donc de matcher après
        /// activation du mapping — pas de regression silencieuse pour un
        /// usager qui a coché « Comedy » avant l'installation de GenreCleaner.
        /// </summary>
        internal static HashSet<string> GenreKeys(IEnumerable<string> genres, bool? series)
        {
            var keys = new HashSet<string>();
            if (genres == null) return keys;
            foreach (var g in genres)
            {
                if (string.IsNullOrWhiteSpace(g)) continue;
                keys.Add(g.ToLowerInvariant());
                var mapped = MapGenre(g, series);
                if (!string.Equals(mapped, g, StringComparison.OrdinalIgnoreCase))
                    keys.Add(mapped.ToLowerInvariant());
            }
            return keys;
        }

        // ------------------------------------------------------------------
        //  Accesseurs pour l'outil « Traduction des genres (IA) »
        // ------------------------------------------------------------------

        /// <summary>
        /// Un genre EPG est-il déjà mappé dans la section donnée
        /// (<paramref name="series"/> = table séries, sinon films) ?
        /// Seule la table de CETTE section est consultée (pas de repli sur
        /// l'autre table) : les propositions de traduction sont faites par
        /// section, comme les tables GenreCleaner.
        /// </summary>
        internal static bool IsMapped(string genre, bool series)
        {
            if (string.IsNullOrWhiteSpace(genre)) return true;
            EnsureLoaded();
            var table = series ? _series : _movie;
            return table != null && table.ContainsKey(Norm(genre));
        }

        /// <summary>
        /// Un genre EPG est-il <b>couvert</b> pour la section donnée : mappé
        /// dans la table GenreMappings, OU déjà présent tel quel dans
        /// <c>AllowedGenres</c> (GenreCleaner le conserve alors sans
        /// traduction — lui proposer un équivalent ne produirait qu'un
        /// no-op du genre « Action »→« Action »).
        /// </summary>
        internal static bool IsCovered(string genre, bool series)
        {
            if (string.IsNullOrWhiteSpace(genre)) return true;
            if (IsMapped(genre, series)) return true;
            return Allowed(series).Contains(genre.Trim(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Liste <c>AllowedGenres</c> de la section (vocabulaire curaté de
        /// l'usager) : cibles autorisées pour les propositions de traduction.
        /// Retourne une liste VIDE si GenreCleaner n'est pas installé ou si
        /// la section ne définit pas de liste (auquel cas l'outil de
        /// traduction n'a pas de vocabulaire cible : il ne propose rien).
        /// </summary>
        internal static List<string> Allowed(bool series)
        {
            EnsureLoaded();
            var list = series ? _seriesAllowed : _movieAllowed;
            return list ?? new List<string>();
        }

        // ------------------------------------------------------------------
        //  Écriture / auto-réparation (tool « Traduction des genres (IA) »)
        // ------------------------------------------------------------------

        /// <summary>Un mappage appliqué par l'outil IA (record de config).</summary>
        internal sealed class AppliedMapping
        {
            /// <summary>Genre brut EPG (ex. « Sitcom ») — clé GenreCleaner.</summary>
            public string Name { get; set; }
            /// <summary>Genre curaté cible (ex. « Comédie ») — valeur.</summary>
            public string Value { get; set; }
            /// <summary>Section GenreCleaner : <c>"movie"</c> ou <c>"series"</c>.</summary>
            public string Section { get; set; }
            /// <summary>
            /// true = la cible est un NOUVEAU genre suggéré par l'IA, absent
            /// du vocabulaire : à l'écriture, la valeur est aussi AJOUTÉE à
            /// <c>AllowedGenres</c> de la section (sinon GenreCleaner la
            /// rejetterait au nettoyage de bibliothèque). Un mappage identité
            /// « Esports »→« Esports » avec ce flag reste utile : seul
            /// l'ajout au vocabulaire compte.
            /// </summary>
            public bool NewGenre { get; set; }
        }

        /// <summary>
        /// Parse le JSON <c>GenreAliasApplied</c> de la config (tolérant :
        /// null/vide/JSON invalide → liste vide ; entrées incomplètes
        /// ignorées ; <c>Section</c> inconnue → déduite : défaut séries).
        /// </summary>
        internal static List<AppliedMapping> ParseApplied(string json)
        {
            var list = new List<AppliedMapping>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var n = el.TryGetProperty("name", out var jn) ? jn.GetString() : null;
                    var v = el.TryGetProperty("value", out var jv) ? jv.GetString() : null;
                    var s = el.TryGetProperty("section", out var js) ? js.GetString() : null;
                    var isNew = el.TryGetProperty("new", out var jng) &&
                        (jng.ValueKind == JsonValueKind.True ||
                         string.Equals(jng.GetString(), "true", StringComparison.OrdinalIgnoreCase));
                    if (string.IsNullOrWhiteSpace(n) || string.IsNullOrWhiteSpace(v)) continue;
                    list.Add(new AppliedMapping
                    {
                        Name = n.Trim(),
                        Value = v.Trim(),
                        Section = string.Equals(s, "movie", StringComparison.OrdinalIgnoreCase)
                            ? "movie" : "series",
                        NewGenre = isNew
                    });
                }
            }
            catch { /* JSON invalide : liste vide */ }
            return list;
        }

        /// <summary>
        /// Sérialise la liste <paramref name="mappings"/> en JSON compact
        /// pour <c>GenreAliasApplied</c> (les entrées null/incomplètes sont
        /// éliminées ; doublons exacts dédupliqués en conservant l'ordre).
        /// </summary>
        internal static string AppliedToJson(IEnumerable<AppliedMapping> mappings)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parts = new List<string>();
            foreach (var m in mappings ?? Enumerable.Empty<AppliedMapping>())
            {
                if (m == null || string.IsNullOrWhiteSpace(m.Name) || string.IsNullOrWhiteSpace(m.Value)) continue;
                if (!seen.Add((m.Section ?? "") + "|" + Norm(m.Name))) continue;
                parts.Add("{\"name\":" + JsonSerializer.Serialize(m.Name.Trim()) +
                          ",\"value\":" + JsonSerializer.Serialize(m.Value.Trim()) +
                          ",\"section\":" + JsonSerializer.Serialize(string.Equals(m.Section, "movie", StringComparison.OrdinalIgnoreCase) ? "movie" : "series") +
                          (m.NewGenre ? ",\"new\":true" : "") + "}");
            }
            return "[" + string.Join(",", parts) + "]";
        }

        /// <summary>
        /// Un mappage identité (genre → lui-même, casse près) est un no-op :
        /// GenreCleaner conserve déjà tel quel un genre présent dans
        /// AllowedGenres. Ces entrées ne sont ni écrites ni réparées. La
        /// comparaison est EXACTE et non normalisée : « Miniseries »→
        /// « Mini-Series » est un vrai mappage cosmétique, pas une identité.
        /// </summary>
        internal static bool IsIdentity(string name, string value)
        {
            return string.Equals(name?.Trim(), value?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// S'assure que <paramref name="genre"/> figure dans les
        /// <c>AllowedGenres</c> de la section (append silencieux si absent,
        /// comparaison insensible à la casse). Retourne true si l'entrée a
        /// été ajoutée, false si déjà présente. Ne lève jamais.
        /// </summary>
        private static bool EnsureAllowedGenre(XElement section, string genre)
        {
            var allowed = section.Element("AllowedGenres");
            if (allowed == null)
            {
                allowed = new XElement("AllowedGenres");
                section.Add(allowed);
            }
            foreach (var s in allowed.Elements("string"))
                if (string.Equals(s.Value?.Trim(), genre, StringComparison.OrdinalIgnoreCase))
                    return false;
            allowed.Add(new XElement("string", genre));
            return true;
        }

        /// <summary>
        /// Ajoute des mappages aux <c>GenreMappings</c> de
        /// <c>GenreCleaner.xml</c> (sections <c>MovieOptions</c>/
        /// <c>SeriesOptions</c> selon <see cref="AppliedMapping.Section"/>).
        /// Seuls les <c>NameValuePair</c> sont ajoutés, plus les entrées
        /// <c>AllowedGenres</c> des mappages marqués
        /// <see cref="AppliedMapping.NewGenre"/> (nouveaux genres suggérés
        /// par l'IA) — le reste du fichier n'est PAS touché.
        /// Les mappages déjà présents dans la section (même clé normalisée)
        /// sont ignorés (idempotent). Retourne le nombre d'entrées
        /// effectivement ajoutées ; -1 si le fichier est absent (plugin
        /// GenreCleaner non installé). Ne lève jamais.
        /// </summary>
        /// <remarks>
        /// CAVEAT documenté : le plugin GenreCleaner charge son XML en
        /// mémoire au démarrage d'Emby et SA page de config re-sérialise
        /// cette copie au prochain enregistrement — effaçant toute écriture
        /// externe postérieure au chargement. C'est pourquoi chaque mappage
        /// appliqué est aussi recordé dans <c>GenreAliasApplied</c> (config
        /// LLM_AI) : <see cref="HealApplied"/> ré-écrit les entrées
        /// manquantes. Après un redémarrage du serveur, la copie mémoire
        /// inclut nos entrées et les sauvegardes UI les préservent.
        /// </remarks>
        internal static int AddMappings(IEnumerable<AppliedMapping> mappings, ILogger logger)
        {
            if (mappings == null) return 0;
            var path = ConfigPath;
            if (path == null || !File.Exists(path))
            {
                logger?.Warn("[LLM_AI] GenreCleaner.xml introuvable ({0}) — mappages de genres non écrits (plugin GenreCleaner installé ?).", path);
                return -1;
            }

            lock (_lock)
            {
                try
                {
                    var root = XDocument.Load(path).Root;
                    if (root == null) return 0;

                    int added = 0;
                    foreach (var m in mappings)
                    {
                        if (m == null || string.IsNullOrWhiteSpace(m.Name) || string.IsNullOrWhiteSpace(m.Value)) continue;
                        bool series = string.Equals(m.Section, "series", StringComparison.OrdinalIgnoreCase);
                        var section = root.Element(series ? "SeriesOptions" : "MovieOptions");
                        if (section == null) continue;

                        // Nouveau genre IA : la cible doit figurer dans
                        // AllowedGenres de la section (sinon GenreCleaner la
                        // rejetterait au nettoyage de bibliothèque).
                        if (m.NewGenre && EnsureAllowedGenre(section, m.Value.Trim())) added++;

                        if (IsIdentity(m.Name, m.Value)) continue; // mappage no-op « Action »→« Action »
                        var mapEl = section.Element("GenreMappings");
                        if (mapEl == null)
                        {
                            mapEl = new XElement("GenreMappings");
                            section.Add(mapEl);
                        }
                        // Idempotent : clé déjà présente dans CETTE section ?
                        bool exists = false;
                        foreach (var pair in mapEl.Elements("NameValuePair"))
                        {
                            var n = pair.Element("Name")?.Value?.Trim();
                            if (!string.IsNullOrEmpty(n) &&
                                string.Equals(Norm(n), Norm(m.Name), StringComparison.OrdinalIgnoreCase))
                            { exists = true; break; }
                        }
                        if (exists) continue;
                        mapEl.Add(new XElement("NameValuePair",
                            new XElement("Name", m.Name.Trim()),
                            new XElement("Value", m.Value.Trim())));
                        added++;
                    }

                    if (added > 0)
                    {
                        // Horodatage de modification de section (comme l'UI GenreCleaner).
                        foreach (var s in new[] { root.Element("MovieOptions"), root.Element("SeriesOptions") })
                            s?.Element("LastChange")?.SetValue(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"));
                        root.Save(path);
                        // Invalide le cache : la prochaine MapGenre relit le fichier.
                        _lastStatUtc = DateTime.MinValue;
                        _lastMtimeUtc = DateTime.MinValue;
                    }
                    return added;
                }
                catch (Exception ex)
                {
                    logger?.Error("[LLM_AI] Écriture GenreCleaner.xml échouée : {0}", ex.Message);
                    return -1;
                }
            }
        }

        /// <summary>
        /// Auto-réparation : ré-écrit dans <c>GenreCleaner.xml</c> les
        /// mappages enregistrés dans <paramref name="appliedJson"/>
        /// (<c>GenreAliasApplied</c>) qui manquent au fichier — cas d'une
        /// sauvegarde de la page de config GenreCleaner qui a ré-sérialisé
        /// sa copie mémoire (postérieure à notre dernière écriture et
        /// antérieure à un redémarrage). Idempotent, ne lève jamais ; loggue
        /// un avertissement quand une réparation a lieu.
        /// </summary>
        internal static void HealApplied(string appliedJson, ILogger logger)
        {
            var applied = ParseApplied(appliedJson);
            if (applied.Count == 0) return;
            int healed = AddMappings(applied, logger);
            if (healed > 0)
                logger?.Warn("[LLM_AI] GenreCleaner.xml : {0} mappage(s) IA manquant(s) — ré-ajouté(s). (Une sauvegarde de la page de config GenreCleaner a probablement réécrit le fichier.)", healed);
        }
    }
}