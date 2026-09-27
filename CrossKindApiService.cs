using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace LLM_AI
{
    /// <summary>
    /// Endpoints HTTP « file de régularisation cross-kind » (page de config,
    /// admin uniquement) :
    /// <list type="bullet">
    /// <item><c>GET /Plugins/LLMAI/CrossKindQueue</c> — la file d'attente :
    /// (a) les items bibliothèque taggés <c>llmai-cross-kind</c> (« confirmés »),
    /// (b) les items taggés <c>llmai-not-found</c> dont le dossier vit sous la
    /// racine DVR (« suspects » — import DVR typé par le guide, œuvre absente
    /// de TMDB : la sonde du type opposé n'accroche que l'égalité exacte du
    /// titre), avec la fiche relue (titre/année), les chemins source du/des
    /// fichiers vidéo et la cible suggérée « Titre (Année)/Titre (Année).ext ».
    /// Les items taggés <c>llmai-cross-kind-ignored</c> sont masqués sauf
    /// <c>IncludeIgnored=true</c>.</item>
    /// <item><c>GET /Plugins/LLMAI/CrossKindLibraries</c> — racines des
    /// bibliothèques Emby (nom, type, locations) pour alimenter le dialogue
    /// de destination. La bibliothèque contenant la racine DVR est proposée
    /// quand elle supporte le type visé (mixte ou films/séries) — flag
    /// <c>IsDvr</c> + avertissement rétention côté client.</item>
    /// <item><c>POST /Plugins/LLMAI/CrossKindRegularize</c> — copie VÉRIFIÉE
    /// du/des fichiers vidéo vers le dossier de destination choisi par
    /// l'admin, item par item. Aucune suppression : l'original reste en place
    /// (invariant du repo) — c'est l'usager qui le retire pour que Emby
    /// nettoie l'ancien item. Un tag <c>llmai-regularized</c> évite les
    /// re-copies.</item>
    /// <item><c>POST /Plugins/LLMAI/CrossKindConvert</c> — conversion sur
    /// place d'un enregistrement DVR mal typé : renommage du dossier en
    /// « Titre (Année) », de la vidéo et du .nfo à l'identique, de
    /// poster.jpg en « Titre (Année)-poster.jpg », réécriture du .nfo en
    /// racine <c>&lt;movie&gt;</c> (un <c>&lt;episodedetails&gt;</c> relirait
    /// l'œuvre comme Épisode au re-import) et suppression opt-in de
    /// tvshow.nfo (l'ancre série) — Emby ré-importe alors l'œuvre sous le
    /// bon type au scan suivant. Journal de rollback : tout renommage est
    /// annulé en cas d'échec ; le .ts n'est JAMAIS supprimé (invariant).</item>
    /// <item><c>POST /Plugins/LLMAI/CrossKindIgnore</c> — pose/retire le tag
    /// <c>llmai-cross-kind-ignored</c> : l'item sort de la file (cas d'une
    /// vraie série pas encore dans TVDB/TMDB — « Le téléjournal »).</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Le contexte de l'exploration v1.13.31 : le fichier .ts d'un
    /// enregistrement DVR mal typé (fiche film sur un item Série) copié dans
    /// une bibliothèque de films avec un nommage « Titre (Année).ts » est
    /// ré-importé nativement par Emby sous le bon type (vérifié en réel :
    /// « France, il était une fois demain »). Ce service industrialise cette
    /// recette. Service ServiceStack découvert par scanning d'assembly :
    /// hérite <see cref="BaseApiService"/> ; routes portées par les DTO via
    /// <see cref="RouteAttribute"/>. La lecture de fiche TMDB (titre/année de
    /// la fiche croisée) réutilise <see cref="TmdbLookupTool"/> — la même
    /// cascade que l'audit (<see cref="OrphanResolver.AuditTaggedIdsAsync"/>),
    /// en lecture seule.
    /// </remarks>
    public class CrossKindApiService : BaseApiService
    {
        /// <summary>Taille max d'un chemin cible saisi par l'admin.</summary>
        private const int MaxTargetPathLength = 400;

        // HttpClient partagé (repli poster via l'endpoint image Emby — jamais
        // l'hôte distant de l'image). Pas de credentials, timeout court.
        private static readonly HttpClient s_http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        // ------------------------------------------------------------------
        //  DTO requêtes / réponses
        // ------------------------------------------------------------------

        [Route("/Plugins/LLMAI/CrossKindQueue", "GET")]
        public class CrossKindQueueRequest : IReturn<object>
        {
            /// <summary>Réafficher les items taggés llmai-cross-kind-ignored
            /// (masqués par défaut).</summary>
            public bool IncludeIgnored { get; set; }
        }

        /// <summary>Une entrée de la file (un item cross-kind ou suspect).</summary>
        public class CrossKindEntry
        {
            /// <summary>Id canonique (InternalId — id currency Emby).</summary>
            public string ItemId { get; set; }
            /// <summary>Nom actuel de l'item Emby.</summary>
            public string Name { get; set; }
            /// <summary>« confirmed » (tag llmai-cross-kind) ou « suspected »
            /// (not-found dont le dossier vit sous la racine DVR).</summary>
            public string Status { get; set; }
            /// <summary>True si l'item est un suspect (not-found + DVR), pas un item confirmé.</summary>
            public bool Suspected { get; set; }
            /// <summary>True si l'admin a posé le tag llmai-cross-kind-ignored.</summary>
            public bool Ignored { get; set; }
            /// <summary>Type de l'item Emby ("series" | "movie") — celui posé par l'import DVR.</summary>
            public string ItemKind { get; set; }
            /// <summary>Type de la fiche croisée (opposé à l'item).</summary>
            public string FicheKind { get; set; }
            /// <summary>Titre de la fiche (peut différer du nom de l'item ; défaut = nom de l'item).</summary>
            public string FicheTitle { get; set; }
            /// <summary>Année de la fiche (0 si inconnue).</summary>
            public int FicheYear { get; set; }
            /// <summary>Année par défaut pour la conversion sans fiche : année de la fiche,
            /// sinon ProductionYear de l'item (année de diffusion d'un DVR) si plausible.</summary>
            public int DefaultYear { get; set; }
            /// <summary>Id TMDB de la fiche posée (0 si absent).</summary>
            public int TmdbId { get; set; }
            /// <summary>True si la copie vers la cible est déjà faite (tag llmai-regularized).</summary>
            public bool Regularized { get; set; }
            /// <summary>True si la conversion sur place est applicable : item série
            /// dans le répertoire DVR dont le dossier porte tvshow.nfo.</summary>
            public bool Convertible { get; set; }
            /// <summary>Dossier source unique des fichiers (le dossier DVR de l'œuvre) — null si inconnu.</summary>
            public string SourceFolder { get; set; }
            /// <summary>Chemins des fichiers vidéo source (épisodes d'une Série, ou le fichier du Movie).</summary>
            public string[] SourceFiles { get; set; }
            /// <summary>Dossier cible suggéré (« Titre (Année) »).</summary>
            public string SuggestedFolder { get; set; }
            /// <summary>Nom de fichier cible suggéré (« Titre (Année).ext »).</summary>
            public string SuggestedFile { get; set; }
        }

        public class CrossKindQueueResponse
        {
            public CrossKindEntry[] Items { get; set; }
            public string Error { get; set; }
        }

        [Route("/Plugins/LLMAI/CrossKindLibraries", "GET")]
        public class CrossKindLibrariesRequest : IReturn<object> { }

        /// <summary>Une bibliothèque Emby (pour le dialogue de destination).</summary>
        public class CrossKindLibrary
        {
            public string Name { get; set; }
            /// <summary>CollectionType Emby ("movies", "tvshows", null pour contenu mixte…).</summary>
            public string Type { get; set; }
            public string[] Paths { get; set; }
            /// <summary>True si la bibliothèque contient la racine des enregistrements
            /// DVR — destination possible quand elle supporte le type visé, mais
            /// soumise à la rétention DVR (avertissement côté client).</summary>
            public bool IsDvr { get; set; }
        }

        public class CrossKindLibrariesResponse
        {
            public CrossKindLibrary[] Libraries { get; set; }
            public string Error { get; set; }
        }

        [Route("/Plugins/LLMAI/CrossKindRegularize", "POST")]
        public class CrossKindRegularizeRequest : IReturn<object>
        {
            /// <summary>Id de l'item taggué cross-kind (chaîne — résolu par ItemIdResolver).</summary>
            public string ItemId { get; set; }
            /// <summary>Dossier de destination (absolu). Ex. « /mnt/Documentaires » ou
            /// une racine de bibliothèque. Le sous-dossier « Titre (Année) » est
            /// créé sous ce dossier.</summary>
            public string TargetFolder { get; set; }
            /// <summary>Nom de fichier cible optionnel (le défaut = la suggestion
            /// « Titre (Année).ext »). L'admin peut y écrire un nommage épisode
            /// « Titre (2019) - S01E05.ts » pour le cas fiche-série.</summary>
            public string TargetFile { get; set; }
        }

        public class CrossKindRegularizeResponse
        {
            public string[] Copied { get; set; }
            public string[] Skipped { get; set; }
            public string[] Failed { get; set; }
            /// <summary>Avertissement non bloquant (destination dans le
            /// répertoire DVR soumis à la rétention, ou hors bibliothèque).</summary>
            public string Warning { get; set; }
            public string Error { get; set; }
        }

        [Route("/Plugins/LLMAI/CrossKindConvert", "POST")]
        public class CrossKindConvertRequest : IReturn<object>
        {
            /// <summary>Id de l'item (chaîne — résolu par ItemIdResolver).</summary>
            public string ItemId { get; set; }
            /// <summary>Nom de base cible « Titre (Année) » : le dossier, la vidéo
            /// et le .nfo sont renommés dessus ; poster.jpg devient
            /// « &lt;nom&gt;-poster.jpg ». Le titre (sans l'année) et l'année
            /// alimentent le .nfo réécrit.</summary>
            public string TargetName { get; set; }
            /// <summary>Supprimer tvshow.nfo — l'ancre série qui maintient
            /// l'import en Series. Opt-in explicite : c'est la SEULE suppression
            /// du flux (métadonnées, jamais le média).</summary>
            public bool DeleteTvshowNfo { get; set; }
        }

        public class CrossKindConvertResponse
        {
            public string[] Renamed { get; set; }
            public string[] Deleted { get; set; }
            public string[] Failed { get; set; }
            /// <summary>Avertissement non bloquant (rétention DVR, tvshow.nfo absent…).</summary>
            public string Warning { get; set; }
            public string Error { get; set; }
        }

        [Route("/Plugins/LLMAI/CrossKindIgnore", "POST")]
        public class CrossKindIgnoreRequest : IReturn<object>
        {
            /// <summary>Id de l'item (chaîne — résolu par ItemIdResolver).</summary>
            public string ItemId { get; set; }
            /// <summary>True = ignorer (tag posé, sort de la file) ; false = réafficher.</summary>
            public bool Ignored { get; set; }
        }

        public class CrossKindIgnoreResponse
        {
            public bool Ignored { get; set; }
            public string Error { get; set; }
        }

        // ------------------------------------------------------------------
        //  Auth (même pattern que AuditApiService)
        // ------------------------------------------------------------------

        private MediaBrowser.Controller.Entities.User ResolveAdmin()
        {
            try
            {
                var auth = AuthorizationContext?.GetAuthorizationInfo(Request);
                var user = auth?.User;
                if (user == null && auth != null && auth.UserId != 0)
                    user = UserManager.GetUserById(auth.UserId);
                return user;
            }
            catch { return null; }
        }

        private bool IsAdmin()
        {
            var user = ResolveAdmin();
            var policy = user?.Policy;
            return policy != null && policy.IsAdministrator;
        }

        private string NotAdminError() => "Réservé aux administrateurs.";

        // ------------------------------------------------------------------
        //  Helpers
        // ------------------------------------------------------------------

        /// <summary>Type de l'item tel que posé par l'import (OrphanResolver:152).</summary>
        private static bool IsSeriesItem(BaseItem item) =>
            item != null && item.GetType().Name.IndexOf("Series", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Assainit un titre pour usage en nom de dossier/fichier : les
        /// caractères invalides deviennent des espaces, points finaux retirés
        /// (Windows), espaces collées réduites. Même pattern que
        /// <c>StrmLibraryGenerator.SanitizeName</c>.
        /// </summary>
        private static string SanitizeName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var sb = new StringBuilder(s.Trim());
            foreach (char c in Path.GetInvalidFileNameChars())
                sb.Replace(c, ' ');
            string v = sb.ToString().Trim().TrimEnd('.');
            while (v.Contains("  ")) v = v.Replace("  ", " ");
            return v;
        }

        /// <summary>
        /// Relit la fiche de l'id posé sous le type OPPOSÉ à l'item (la fiche
        /// croisée) — cascade tmdb puis imdb, identique à l'audit. Null si
        /// illisible : l'appelant retombe sur le nom de l'item.
        /// </summary>
        private async Task<TmdbMeta> ReadCrossFicheAsync(BaseItem item, string otherKind, string lang, CancellationToken ct)
        {
            var tmdb = new TmdbLookupTool(Logger);
            TmdbMeta meta = null;
            int.TryParse(item.GetProviderId("tmdb"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId);
            if (tmdbId > 0)
                meta = await tmdb.LookupMetaByIdAsync(tmdbId, otherKind, lang, ct).ConfigureAwait(false);
            if (meta == null)
            {
                string imdb = item.GetProviderId("imdb");
                if (!string.IsNullOrWhiteSpace(imdb))
                    meta = await tmdb.FindByExternalIdAsync(imdb.Trim(), "imdb_id", otherKind, lang, ct).ConfigureAwait(false);
            }
            return meta;
        }

        /// <summary>
        /// Sonde TMDB du type OPPOSÉ pour un item not-found « suspect » (la
        /// passe nocturne n'y a trouvé AUCUNE fiche du type de l'item — dont
        /// le repli de type series→movie ; mais un item taggé not-found AVANT
        /// l'ajout du repli, ou avec une configuration différente depuis, peut
        /// avoir une fiche opposée jamais testée). N'accepte que l'ÉGALITÉ
        /// EXACTE du titre normalisé, sans filtre d'année — doctrine des items
        /// sans année fiable : un match lâche ici validerait une hallucination.
        /// Null si aucune fiche exacte.
        /// </summary>
        private async Task<TmdbMeta> ProbeOppositeFicheAsync(BaseItem item, string otherKind,
            string[] langs, CancellationToken ct)
        {
            string clean = TmdbLookupTool.CleanEpgTitle(item?.Name);
            if (string.IsNullOrWhiteSpace(clean)) clean = item?.Name;
            if (string.IsNullOrWhiteSpace(clean)) return null;
            try
            {
                var tmdb = new TmdbLookupTool(Logger);
                var meta = await tmdb.LookupMetaMultiLangAsync(clean, otherKind, null, langs, ct).ConfigureAwait(false);
                if (meta == null || meta.TmdbId <= 0) return null;
                if (OrphanResolver.NormalizeTitle(clean) != OrphanResolver.NormalizeTitle(meta.Title))
                    return null;
                return meta;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Logger?.Info("[LLM_AI] CrossKind : sonde type opposé « {0} » échouée ({1}).", item?.Name, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Chemins des fichiers vidéo de l'œuvre : les épisodes d'une Série
        /// (les fichiers d'enregistrement importés), sinon le fichier du Movie.
        /// Les cartes .strm (bibliothèque ai_suggestions) sont exclues —
        /// elles ne portent pas de média à copier. Déduit aussi l'extension
        /// typique des sources (pour la suggestion de nom de fichier).
        /// </summary>
        private (string[] files, string ext) CollectSourceFiles(BaseItem item, bool isSeries)
        {
            var paths = new List<string>();
            if (isSeries)
            {
                try
                {
                    // Épisodes sous l'item (Série) — le .ts importé vit sur
                    // l'Episode, pas sur la Série (item.Path = dossier DVR).
                    // AncestorIds filtre par parent (PermissionGate fait de même).
                    var episodes = LibraryManager.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { "Episode" },
                        AncestorIds = new[] { item.InternalId },
                        Recursive = true,
                        EnableTotalRecordCount = false
                    }) ?? Array.Empty<BaseItem>();
                    foreach (var e in episodes)
                    {
                        string p = e?.Path;
                        if (string.IsNullOrWhiteSpace(p)) continue;
                        if (p.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)) continue;
                        paths.Add(p);
                    }
                }
                catch (Exception ex)
                {
                    Logger?.Warn("[LLM_AI] CrossKind : énumération des épisodes de « {0} » échouée ({1}).",
                        item?.Name, ex.Message);
                }
            }
            else if (!string.IsNullOrWhiteSpace(item?.Path))
            {
                paths.Add(item.Path);
            }

            var files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string ext = ".ts";
            var first = files.FirstOrDefault(p => !string.IsNullOrWhiteSpace(Path.GetExtension(p)));
            if (first != null)
            {
                string e2 = Path.GetExtension(first);
                if (!string.IsNullOrWhiteSpace(e2)) ext = e2;
            }
            return (files, ext);
        }

        private static string BuildBaseName(string title, int year, string ext)
        {
            string t = SanitizeName(title);
            if (string.IsNullOrWhiteSpace(t)) return null;
            string baseName = year > 0 ? t + " (" + year.ToString(CultureInfo.InvariantCulture) + ")" : t;
            return baseName + (string.IsNullOrWhiteSpace(ext) ? ".ts" : ext.ToLowerInvariant());
        }

        /// <summary>
        /// Année de diffusion de repli pour un enregistrement sans fiche TMDB
        /// (l'item DVR importé par la coquille n'a souvent PAS de ProductionYear
        /// — vérifié en réel sur « Les couleurs du passé ») : ProductionYear de
        /// l'item si plausible, sinon PremiereDate du premier épisode (donnée
        /// EPG), sinon l'horodatage DVR du nom de fichier (« Titre
        /// 2026_09_26_20_00_00.ts » — dernier segment « aaaa_ » ou « aaaa- » du
        /// nom, l'horodatage étant toujours suffixé). 0 si rien de fiable.
        /// </summary>
        private int DvrAirYear(BaseItem item, bool isSeries, string[] files)
        {
            if (item?.ProductionYear.HasValue == true && item.ProductionYear.Value >= 1900)
                return item.ProductionYear.Value;

            if (isSeries)
            {
                try
                {
                    var episodes = LibraryManager.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { "Episode" },
                        AncestorIds = new[] { item.InternalId },
                        Recursive = true,
                        EnableTotalRecordCount = false
                    }) ?? Array.Empty<BaseItem>();
                    foreach (var e in episodes)
                    {
                        if (e?.PremiereDate.HasValue == true && e.PremiereDate.Value.Year >= 1900)
                            return e.PremiereDate.Value.Year;
                    }
                }
                catch (Exception ex)
                {
                    Logger?.Warn("[LLM_AI] CrossKind : lecture PremiereDate des épisodes échouée ({0}).", ex.Message);
                }
            }

            foreach (var f in files ?? Array.Empty<string>())
            {
                string name = Path.GetFileName(f);
                var ms = System.Text.RegularExpressions.Regex.Matches(name, @"(?:^|[^0-9])((?:19|20)\d{2})[_\-]");
                if (ms.Count > 0
                    && int.TryParse(ms[ms.Count - 1].Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
                    && y >= 1900 && y <= 2100)
                    return y;
            }
            return 0;
        }

        // ------------------------------------------------------------------
        //  GET Queue
        // ------------------------------------------------------------------

        public async Task<object> Get(CrossKindQueueRequest req)
        {
            if (!IsAdmin())
                return new CrossKindQueueResponse { Error = NotAdminError() };

            var ct = Request?.CancellationToken ?? CancellationToken.None;
            var cfg = Plugin.Instance?.Configuration;
            string lang = I18n.ToTmdbLang(I18n.ResolveMetaLangKey(cfg, ApplicationHost));

            BaseItem[] items;
            try
            {
                items = LibraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Movie", "Series" },
                    Recursive = true,
                    EnableTotalRecordCount = false
                }) ?? Array.Empty<BaseItem>();
            }
            catch (Exception ex)
            {
                Logger?.ErrorException("[LLM_AI] CrossKind : GetItemList a échoué.", ex, ex.Message);
                return new CrossKindQueueResponse { Error = "Lecture bibliothèque échouée : " + ex.Message };
            }

            // Racine des enregistrements DVR : le signal « suspect » (not-found
            // dont le dossier vit sous la racine DVR) et la borne de la
            // conversion sur place.
            string dvrRoot = null;
            try
            {
                if (RecordingDiskManager.TryResolveRecordingPath(ApplicationHost, Logger, out string d))
                    dvrRoot = d;
            }
            catch (Exception ex)
            {
                Logger?.Warn("[LLM_AI] CrossKind : racine DVR illisible ({0}).", ex.Message);
            }

            // Langues de sonde : même trio que la passe orphelins (en/fr + lang TMDB).
            var langs = new[] { "en-US", "fr-FR", lang }
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            var list = new List<CrossKindEntry>();
            foreach (var item in items)
            {
                if (item == null) continue;
                ct.ThrowIfCancellationRequested();
                try
                {
                    bool cross = OrphanResolver.HasTag(item, OrphanIdentifyTask.TagCrossKind);
                    bool ignored = OrphanResolver.HasTag(item, OrphanIdentifyTask.TagCrossKindIgnored);
                    if (ignored && !req.IncludeIgnored) continue;

                    // Suspect = not-found dont le dossier vit sous la racine DVR :
                    // l'import DVR type l'item d'après le guide (tvshow.nfo →
                    // Series) alors que l'œuvre peut être un film absent de TMDB
                    // (cas « Les couleurs du passé », « Du zéro à l'infini »).
                    bool underDvr = !string.IsNullOrWhiteSpace(dvrRoot)
                        && !string.IsNullOrWhiteSpace(item.Path)
                        && IsUnderPath(item.Path, dvrRoot);
                    bool suspected = !cross && OrphanResolver.HasTag(item, OrphanIdentifyTask.TagNotFound) && underDvr;
                    if (!cross && !suspected) continue;

                    bool isSeries = IsSeriesItem(item);
                    string itemKind = isSeries ? "series" : "movie";
                    string otherKind = isSeries ? "movie" : "series";

                    // Fiche (lecture seule) : titre/année pour la suggestion de
                    // nommage. Item confirmé → la fiche croisée posée est relue
                    // par ids (cascade audit). Suspect → sonde du type opposé,
                    // acceptée sur égalité exacte du titre uniquement. Illisible
                    // → nom de l'item.
                    string ficheTitle = item.Name;
                    int ficheYear = 0;
                    string ficheKind = otherKind;
                    if (cross)
                    {
                        var meta = await ReadCrossFicheAsync(item, otherKind, lang, ct).ConfigureAwait(false);
                        if (meta != null)
                        {
                            if (!string.IsNullOrWhiteSpace(meta.Title)) ficheTitle = meta.Title;
                            ficheYear = meta.Year ?? 0;
                            if (!string.IsNullOrWhiteSpace(meta.Kind)) ficheKind = meta.Kind;
                        }
                    }
                    else
                    {
                        var probe = await ProbeOppositeFicheAsync(item, otherKind, langs, ct).ConfigureAwait(false);
                        if (probe != null)
                        {
                            ficheTitle = probe.Title;
                            ficheYear = probe.Year ?? 0;
                        }
                    }

                    var (files, ext) = CollectSourceFiles(item, isSeries);

                    // Année par défaut (œuvre sans fiche) : année de la fiche,
                    // sinon année de diffusion dérivée de l'item (ProductionYear,
                    // PremiereDate du premier épisode, horodatage DVR du nom de
                    // fichier) — la suggestion reste éditable dans le dialogue.
                    int defaultYear = ficheYear;
                    if (defaultYear <= 0)
                        defaultYear = DvrAirYear(item, isSeries, files);
                    int suggestYear = ficheYear > 0 ? ficheYear : defaultYear;

                    // Dossier source unique + ancre série (tvshow.nfo) : les
                    // prérequis de la conversion sur place (le .ts d'un item
                    // Série vit sur l'Episode — item.Path = dossier DVR).
                    string srcFolder = null;
                    var firstFile = files.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
                    if (firstFile != null) srcFolder = Path.GetDirectoryName(firstFile);
                    bool tvshowNfo = false;
                    if (!string.IsNullOrWhiteSpace(srcFolder))
                    {
                        try { tvshowNfo = File.Exists(Path.Combine(srcFolder, "tvshow.nfo")); }
                        catch { /* lecteur indisponible : non bloquant */ }
                    }
                    bool convertible = isSeries && files.Length > 0
                        && !string.IsNullOrWhiteSpace(srcFolder)
                        && !string.IsNullOrWhiteSpace(dvrRoot)
                        && IsUnderPath(srcFolder, dvrRoot)
                        && tvshowNfo;

                    int tmdbId = 0;
                    int.TryParse(item.GetProviderId("tmdb"), NumberStyles.Integer, CultureInfo.InvariantCulture, out tmdbId);

                    list.Add(new CrossKindEntry
                    {
                        ItemId = item.InternalId.ToString(CultureInfo.InvariantCulture),
                        Name = item.Name,
                        Status = suspected ? "suspected" : "confirmed",
                        Suspected = suspected,
                        Ignored = ignored,
                        ItemKind = itemKind,
                        FicheKind = ficheKind,
                        FicheTitle = ficheTitle,
                        FicheYear = ficheYear,
                        DefaultYear = defaultYear,
                        TmdbId = tmdbId,
                        Regularized = OrphanResolver.HasTag(item, OrphanIdentifyTask.TagRegularized),
                        Convertible = convertible,
                        SourceFolder = srcFolder,
                        SourceFiles = files,
                        SuggestedFolder = SanitizeName(ficheTitle) +
                            (suggestYear > 0 ? " (" + suggestYear.ToString(CultureInfo.InvariantCulture) + ")" : ""),
                        SuggestedFile = BuildBaseName(ficheTitle, suggestYear, ext)
                    });
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Logger?.Warn("[LLM_AI] CrossKind : file — erreur sur « {0} » ({1}) — item ignoré.",
                        item?.Name, ex.Message);
                }
            }

            // Tri stable : à traiter d'abord (confirmés puis suspects), puis
            // régularisés, puis ignorés (réaffichés sur demande) ; par nom.
            return new CrossKindQueueResponse
            {
                Items = list
                    .OrderBy(e => e.Ignored ? 3 : e.Regularized ? 2 : e.Suspected ? 1 : 0)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
        }

        // ------------------------------------------------------------------
        //  GET Libraries (alimente le dialogue de destination)
        // ------------------------------------------------------------------

        public object Get(CrossKindLibrariesRequest req)
        {
            if (!IsAdmin())
                return new CrossKindLibrariesResponse { Error = NotAdminError() };

            var cfg = Plugin.Instance?.Configuration;
            var libs = new List<CrossKindLibrary>();
            try
            {
                // Exclusions : la bibliothèque de cartes .strm du plugin n'est
                // pas une cible de copie (pas de média réel). Les bibliothèques
                // de films/séries et à contenu mixte sont proposées — y compris
                // celle qui contient la racine des enregistrements DVR (flag
                // IsDvr + avertissement rétention côté client : l'admin peut y
                // régulariser une œuvre si la bibliothèque supporte le type
                // visé ; playlists, boxsets, musique… exclus par le filtre).
                string strmRoot = null;
                try { strmRoot = StrmLibraryGenerator.ResolveLibraryRoot(LibraryManager, cfg?.StrmLibraryName, Logger); }
                catch (Exception ex)
                {
                    Logger?.Warn("[LLM_AI] CrossKind : racine .strm illisible ({0}).", ex.Message);
                }
                string dvrRoot = null;
                try
                {
                    if (RecordingDiskManager.TryResolveRecordingPath(ApplicationHost, Logger, out string d))
                        dvrRoot = d;
                }
                catch (Exception ex)
                {
                    Logger?.Warn("[LLM_AI] CrossKind : racine DVR illisible ({0}).", ex.Message);
                }

                var folders = LibraryManager.GetVirtualFolders() ?? new List<MediaBrowser.Model.Entities.VirtualFolderInfo>();
                foreach (var f in folders)
                {
                    if (f == null) continue;
                    string ctype = f.CollectionType;
                    bool videoish = string.IsNullOrWhiteSpace(ctype)
                        || string.Equals(ctype, "movies", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(ctype, "tvshows", StringComparison.OrdinalIgnoreCase);
                    if (!videoish) continue;

                    var kept = new List<string>();
                    bool isDvr = false;
                    foreach (var loc in f.Locations ?? Array.Empty<string>())
                    {
                        if (string.IsNullOrWhiteSpace(loc)) continue;
                        if (!string.IsNullOrWhiteSpace(strmRoot) && IsUnderPath(loc, strmRoot)) continue;
                        if (!string.IsNullOrWhiteSpace(dvrRoot) && IsUnderPath(loc, dvrRoot))
                            isDvr = true;
                        kept.Add(loc);
                    }
                    if (kept.Count == 0) continue;

                    libs.Add(new CrossKindLibrary
                    {
                        Name = f.Name,
                        Type = ctype,
                        Paths = kept.ToArray(),
                        IsDvr = isDvr
                    });
                }
            }
            catch (Exception ex)
            {
                Logger?.Warn("[LLM_AI] CrossKind : GetVirtualFolders échoué ({0}).", ex.Message);
            }
            return new CrossKindLibrariesResponse { Libraries = libs.ToArray() };
        }

        // ------------------------------------------------------------------
        //  POST Regularize — copie vérifiée, JAMAIS de suppression
        // ------------------------------------------------------------------

        public async Task<object> Post(CrossKindRegularizeRequest req)
        {
            // Tout refus est loggé (Warn) : l'admin voit la cause dans la
            // réponse ET au journal Emby — un rejet invisible au journal a
            // déjà coûté une session de debug.
            CrossKindRegularizeResponse Refuse(string msg)
            {
                Logger?.Warn("[LLM_AI] CrossKind : copie refusée — {0}", msg);
                return new CrossKindRegularizeResponse { Error = msg };
            }

            if (!IsAdmin())
                return Refuse(NotAdminError());

            string itemId = (req?.ItemId ?? "").Trim();
            if (itemId.Length == 0)
                return Refuse("ItemId requis.");

            var item = ItemIdResolver.Resolve(LibraryManager, itemId);
            if (item == null)
                return Refuse("Item introuvable (id : " + itemId + ").");

            // Garde : la régularisation s'applique aux items de la file — tag
            // croisé « confirmé », ou not-found « suspect » (dossier DVR).
            // Pas d'usage détourné comme outil de copie générique. (Le refus
            // est loggé : un rejet silencieux a coûté une session de debug.)
            if (!OrphanResolver.HasTag(item, OrphanIdentifyTask.TagCrossKind)
                && !OrphanResolver.HasTag(item, OrphanIdentifyTask.TagNotFound))
            {
                return Refuse("Item non taggué « " + OrphanIdentifyTask.TagCrossKind + " » ni « "
                    + OrphanIdentifyTask.TagNotFound + " » — rien à régulariser (« " + item.Name + " »).");
            }

            string folder = (req?.TargetFolder ?? "").Trim();
            if (folder.Length == 0)
                return Refuse("Dossier de destination requis.");
            if (!Path.IsPathRooted(folder) || folder.Length > 400)
                return Refuse("Chemin de destination invalide (absolu, ≤ 400 caractères).");

            var ct = Request?.CancellationToken ?? CancellationToken.None;
            var cfg = Plugin.Instance?.Configuration;
            string lang = I18n.ToTmdbLang(I18n.ResolveMetaLangKey(cfg, ApplicationHost));

            bool isSeries = IsSeriesItem(item);
            string otherKind = isSeries ? "movie" : "series";
            var (files, ext) = CollectSourceFiles(item, isSeries);

            // Fiche croisée relue (lecture seule) pour le nommage de repli ;
            // sans fiche (suspect), l'année de diffusion DVR sert de repli.
            var meta = await ReadCrossFicheAsync(item, otherKind, lang, ct).ConfigureAwait(false);
            string ficheTitle = meta != null && !string.IsNullOrWhiteSpace(meta.Title) ? meta.Title : item.Name;
            int ficheYear = meta?.Year ?? 0;
            if (ficheYear <= 0)
                ficheYear = DvrAirYear(item, isSeries, files);
            if (files.Length == 0)
                return Refuse("Aucun fichier vidéo trouvé sur cet item (cartes .strm exclues).");

            // Avertissements non bloquants sur la destination (le dossier reste
            // libre — cas fiche-série « Season 01 » — mais l'admin est prévenu).
            string warning = BuildDestinationWarning(folder);

            string baseFile = (req?.TargetFile ?? "").Trim();
            if (baseFile.Length == 0)
                baseFile = BuildBaseName(ficheTitle, ficheYear, ext) ?? SanitizeName(item.Name) + ext;
            baseFile = SanitizeName(baseFile);
            if (string.IsNullOrWhiteSpace(baseFile))
                return Refuse("Nom de fichier cible invalide.");

            var copied = new List<string>();
            var skipped = new List<string>();
            var failed = new List<string>();
            for (int i = 0; i < files.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                string src = files[i];

                // Plusieurs enregistrements de la même œuvre : suffixe numérique.
                string fileName = baseFile;
                if (i > 0)
                {
                    string stem = Path.GetFileNameWithoutExtension(baseFile);
                    string extension = Path.GetExtension(baseFile);
                    fileName = stem + " (" + (i + 1).ToString(CultureInfo.InvariantCulture) + ")" + extension;
                }
                string dest = Path.Combine(folder, fileName);

                try
                {
                    long srcSize = new FileInfo(src).Length;
                    var di = new FileInfo(dest);
                    if (di.Exists && di.Length == srcSize)
                    {
                        skipped.Add(src + " → " + dest + " (déjà copié)");
                        continue;
                    }
                    if (di.Exists)
                    {
                        // Jamais d'écrasement : une cible de taille différente
                        // (copie périmée interrompue OU fichier sans rapport de
                        // même nom) est refusée — l'admin la supprime lui-même.
                        // Doctrine : le plugin ne supprime jamais de média.
                        failed.Add(src + " → " + dest
                            + " (cible existante de taille différente — non remplacée)");
                        Logger?.Warn("[LLM_AI] CrossKind : copie refusée « {0} » → « {1} » — cible existante de taille différente ({2} ≠ {3} octets).",
                            src, dest, di.Length, srcSize);
                        continue;
                    }

                    Directory.CreateDirectory(folder);
                    File.Copy(src, dest);

                    // Vérification de la copie (taille égale) avant de la déclarer.
                    if (new FileInfo(dest).Length != srcSize)
                        throw new IOException("taille de la copie divergente");
                    copied.Add(src + " → " + dest);
                    Logger?.Info("[LLM_AI] CrossKind : copie « {0} » → « {1} » ({2} octets).",
                        src, dest, srcSize);
                }
                catch (UnauthorizedAccessException ua)
                {
                    failed.Add(src + " → " + dest + " (" + ua.Message
                        + " — permissions : l'utilisateur « emby » doit pouvoir écrire dans le dossier cible)");
                    Logger?.Warn("[LLM_AI] CrossKind : accès refusé « {0} » → « {1} » ({2}).",
                        src, dest, ua.Message);
                }
                catch (Exception ex)
                {
                    failed.Add(src + " → " + dest + " (" + ex.Message + ")");
                    Logger?.Warn("[LLM_AI] CrossKind : échec de copie « {0} » → « {1} » ({2}).",
                        src, dest, ex.Message);
                }
            }

            // Poster canonique dans le dossier cible : l'image de l'item (EPG
            // pour un DVR, fiche TMDB pour un confirmé) accompagne la copie —
            // l'œuvre ré-importée garde son affiche. Idempotent (poster.jpg
            // déjà présent → no-op), best-effort.
            if ((copied.Count + skipped.Count) > 0)
                await TrySaveItemPosterAsync(item, folder, ct).ConfigureAwait(false);

            // Tag « copie faite » UNIQUEMENT si au moins un fichier a été
            // copié ou était déjà en place : l'item reste dans la file
            // (statut distinct) tant que l'original existe.
            if ((copied.Count + skipped.Count) > 0 && failed.Count == 0 &&
                !OrphanResolver.HasTag(item, OrphanIdentifyTask.TagRegularized))
            {
                OrphanResolver.AddTag(item, OrphanIdentifyTask.TagRegularized);
                try { item.UpdateToRepository(ItemUpdateType.MetadataEdit); }
                catch (Exception ex) { Logger?.Warn("[LLM_AI] CrossKind : UpdateToRepository échoué ({0}).", ex.Message); }
            }

            Logger?.Info("[LLM_AI] CrossKind : « {0} » régularisé — copiés={1} sautés={2} échoués={3} (original non supprimé).",
                item.Name, copied.Count, skipped.Count, failed.Count);

            return new CrossKindRegularizeResponse
            {
                Copied = copied.ToArray(),
                Skipped = skipped.ToArray(),
                Failed = failed.ToArray(),
                Warning = warning
            };
        }

        // ------------------------------------------------------------------
        //  POST Convert — conversion sur place d'un enregistrement DVR mal
        //  typé. Renommage du dossier + fichiers (JAMAIS de suppression de
        //  média), réécriture du .nfo en racine <movie>, suppression opt-in
        //  de tvshow.nfo. Journal de rollback : tout échec restaure l'état
        //  initial. Emby ré-importe l'œuvre sous le bon type au scan suivant.
        // ------------------------------------------------------------------

        public async Task<object> Post(CrossKindConvertRequest req)
        {
            var ct = Request?.CancellationToken ?? CancellationToken.None;

            // Tout refus est loggé (Warn) : l'admin voit la cause dans la
            // réponse ET au journal Emby — un rejet invisible au journal a
            // déjà coûté une session de debug.
            CrossKindConvertResponse Refuse(string msg)
            {
                Logger?.Warn("[LLM_AI] CrossKind : conversion refusée — {0}", msg);
                return new CrossKindConvertResponse { Error = msg };
            }

            if (!IsAdmin())
                return Refuse(NotAdminError());

            string itemId = (req?.ItemId ?? "").Trim();
            if (itemId.Length == 0)
                return Refuse("ItemId requis.");

            var item = ItemIdResolver.Resolve(LibraryManager, itemId);
            if (item == null)
                return Refuse("Item introuvable (id : " + itemId + ").");

            // Garde : la conversion s'applique aux items de la file (tag
            // croisé, ou not-found suspect DVR) — pas d'usage détourné.
            bool eligible = OrphanResolver.HasTag(item, OrphanIdentifyTask.TagCrossKind)
                || OrphanResolver.HasTag(item, OrphanIdentifyTask.TagNotFound);
            if (!eligible)
                return Refuse("Item non éligible : ni « " + OrphanIdentifyTask.TagCrossKind
                    + " » ni « " + OrphanIdentifyTask.TagNotFound + " ».");

            // v1 : Série → Film (le cas dominant du DVR). Le sens inverse
            // (Movie → Série) demanderait un nommage épisode — voir la copie.
            bool isSeries = IsSeriesItem(item);
            if (!isSeries)
                return Refuse("Conversion sur place réservée à un item série portant une œuvre film (cas inverse : utilisez la copie).");

            var (files, _) = CollectSourceFiles(item, isSeries);
            if (files.Length == 0)
                return Refuse("Aucun fichier vidéo trouvé sur cet item (cartes .strm exclues).");

            // Dossier source unique (le dossier DVR de l'œuvre).
            string srcFolder = Path.GetDirectoryName(files[0]);
            for (int i = 1; i < files.Length; i++)
            {
                if (!string.Equals(Path.GetDirectoryName(files[i]), srcFolder, StringComparison.OrdinalIgnoreCase))
                    return Refuse("Les fichiers de l'œuvre vivent dans plusieurs dossiers — conversion sur place impossible (utilisez la copie).");
            }

            // Borne : le dossier doit être sous la racine des enregistrements
            // DVR — ailleurs, la forme du dossier n'est pas un import DVR et
            // la copie reste l'outil adapté.
            if (!RecordingDiskManager.TryResolveRecordingPath(ApplicationHost, Logger, out string dvrRoot)
                || string.IsNullOrWhiteSpace(dvrRoot)
                || !IsUnderPath(srcFolder, dvrRoot))
            {
                return Refuse("La conversion sur place ne s'applique qu'aux dossiers du répertoire des enregistrements Live TV (ailleurs : utilisez la copie).");
            }

            // Garde « enregistrement en cours » : le DVR tient le .ts ouvert
            // en écriture — l'ouverture exclusive échoue. Testé AVANT tout
            // renommage (le handle est relâché à l'issue du test).
            foreach (string f in files)
            {
                try
                {
                    using (new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                }
                catch (IOException io)
                {
                    return Refuse("« " + Path.GetFileName(f) + " » est encore en cours d'utilisation "
                        + "(enregistrement actif ou sonde média — réessayez dans quelques minutes). Détail : " + io.Message);
                }
                catch (UnauthorizedAccessException ua)
                {
                    return Refuse("Accès refusé sur « " + Path.GetFileName(f) + " » (" + ua.Message + ").");
                }
            }

            // Nom cible « Titre (Année) » — assaini comme un nom de fichier
            // (les séparateurs sont des caractères invalides : pas d'évasion).
            string target = SanitizeName(req.TargetName ?? "");
            if (string.IsNullOrWhiteSpace(target))
                return Refuse("Nom cible requis (format « Titre (Année) »).");
            if (target.Length > 200)
                return Refuse("Nom cible trop long (≤ 200 caractères).");
            string curFolderName = Path.GetFileName(srcFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(target, curFolderName, StringComparison.OrdinalIgnoreCase))
                return Refuse("Le nom cible est identique au dossier actuel (« " + curFolderName + " ») — rien à convertir.");

            string parent = Path.GetDirectoryName(srcFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(parent))
                return Refuse("Dossier source sans parent — conversion impossible.");
            string newFolder = Path.Combine(parent, target);
            if (Directory.Exists(newFolder) || File.Exists(newFolder))
                return Refuse("La cible existe déjà : « " + newFolder + " ».");

            // Découpe « Titre (Année) » (année optionnelle) : alimente le .nfo
            // réécrit (titre + year) et le message de résultat.
            string titlePart = target;
            int yearPart = 0;
            int open = target.LastIndexOf('(');
            if (open > 0 && target.EndsWith(")", StringComparison.Ordinal))
            {
                string inner = target.Substring(open + 1, target.Length - open - 2).Trim();
                if (int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) && y >= 1900)
                {
                    yearPart = y;
                    titlePart = target.Substring(0, open).Trim();
                }
            }
            if (string.IsNullOrWhiteSpace(titlePart))
                return Refuse("Nom cible invalide (titre vide).");

            // Plan de renommage : vidéo → « nom.ext » (suffixe numérique si
            // plusieurs), .nfo voisin à l'identique, poster.jpg → « nom-poster.jpg ».
            var ordered = files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            var plan = new List<(string from, string to)>();
            for (int i = 0; i < ordered.Length; i++)
            {
                string stem = i > 0
                    ? target + " (" + (i + 1).ToString(CultureInfo.InvariantCulture) + ")"
                    : target;
                string srcExt = Path.GetExtension(ordered[i]);
                plan.Add((ordered[i],
                    Path.Combine(srcFolder, stem + (string.IsNullOrWhiteSpace(srcExt) ? ".ts" : srcExt.ToLowerInvariant()))));
                string nfoSrc = Path.ChangeExtension(ordered[i], ".nfo");
                if (File.Exists(nfoSrc))
                    plan.Add((nfoSrc, Path.Combine(srcFolder, stem + ".nfo")));
            }
            string posterSrc = Path.Combine(srcFolder, "poster.jpg");
            if (File.Exists(posterSrc))
                plan.Add((posterSrc, Path.Combine(srcFolder, target + "-poster.jpg")));

            // Collisions cibles : le contenu suit le dossier, le contrôle peut
            // se faire dans le dossier actuel (mêmes noms).
            var collisions = plan.Where(p => File.Exists(p.to)).Select(p => p.to).ToList();
            if (collisions.Count > 0)
                return Refuse("Conflit : des fichiers cibles existent déjà — " + string.Join(" ; ", collisions));

            string tvshowNfo = Path.Combine(srcFolder, "tvshow.nfo");
            bool hasTvshow = File.Exists(tvshowNfo);

            // Avertissements non bloquants : rétention DVR ; tvshow.nfo absent
            // alors que demandé (le basculement de type dépend d'une autre
            // ancre — à vérifier dans l'éditeur Emby).
            var warn = new List<string>
            {
                "le dossier converti reste dans le répertoire des enregistrements Live TV (« " + dvrRoot
                    + " »), soumis à la rétention DVR d'Emby"
            };
            if (req.DeleteTvshowNfo && !hasTvshow)
                warn.Add("aucun tvshow.nfo à supprimer dans ce dossier");
            string warning = "Attention : " + string.Join(" ; ", warn) + ".";

            // Contenu des .nfo relu AVANT tout : plot/genres EPG sont réécrits
            // en racine <movie> — un <episodedetails> relirait l'œuvre comme
            // Épisode au re-import, et les métadonnées EPG (souvent la seule
            // source : aucune fiche TMDB) seraient perdues.
            var nfoTrees = new Dictionary<string, System.Xml.Linq.XElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in plan)
            {
                if (!p.to.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    nfoTrees[p.from] = System.Xml.Linq.XElement.Load(p.from);
                }
                catch (Exception ex)
                {
                    Logger?.Warn("[LLM_AI] CrossKind : lecture du .nfo « {0} » impossible ({1}) — réécriture depuis l'item seul.",
                        p.from, ex.Message);
                }
            }

            // Exécution + journal de rollback : dossier d'abord (une seule
            // opération atomique qui détache les anciens items Emby), fichiers
            // ensuite, réécriture des .nfo, tvshow.nfo en DERNIER (il ne peut
            // pas être restauré : jamais supprimé avant un renommage complet).
            var renamed = new List<string>();
            var deleted = new List<string>();
            var failed = new List<string>();
            var journal = new List<(string to, string from)>(); // (nouveau chemin, chemin d'origine)
            var rewritten = new List<(string path, System.Xml.Linq.XElement tree)>(); // restauration du contenu
            bool done = false;
            try
            {
                Directory.Move(srcFolder, newFolder);
                journal.Add((newFolder, srcFolder));

                foreach (var p in plan)
                {
                    string src = Path.Combine(newFolder, Path.GetFileName(p.from));
                    string dst = Path.Combine(newFolder, Path.GetFileName(p.to));
                    if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase)) continue;
                    File.Move(src, dst);
                    journal.Add((dst, src));
                    renamed.Add(src + " → " + dst);
                }

                // Réécriture des .nfo (nouveaux chemins) : racine <movie>, avec
                // les ids de l'item quand l'œuvre est identifiée (fiche croisée).
                foreach (var p in plan)
                {
                    if (!p.to.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase)) continue;
                    string dst = Path.Combine(newFolder, Path.GetFileName(p.to));
                    nfoTrees.TryGetValue(p.from, out var tree);
                    string xml = BuildMovieNfo(titlePart, yearPart, tree, item);
                    System.IO.File.WriteAllText(dst, xml, new UTF8Encoding(true));
                    if (tree != null) rewritten.Add((dst, tree));
                }

                if (req.DeleteTvshowNfo && hasTvshow)
                {
                    string tv = Path.Combine(newFolder, "tvshow.nfo");
                    File.Delete(tv);
                    deleted.Add(tv);
                }
                done = true;
            }
            catch (Exception ex)
            {
                failed.Add("opération : " + ex.Message);
                Logger?.Warn("[LLM_AI] CrossKind : échec de conversion « {0} » ({1}) — rollback.",
                    item?.Name, ex.Message);
            }

            if (!done)
            {
                // Rollback en ordre inverse : contenus .nfo réécrits, puis
                // renommages, puis le dossier (journal = ordre d'exécution).
                for (int i = rewritten.Count - 1; i >= 0; i--)
                {
                    try { System.IO.File.WriteAllText(rewritten[i].path, rewritten[i].tree.ToString(), new UTF8Encoding(true)); }
                    catch (Exception ex)
                    {
                        Logger?.Error("[LLM_AI] CrossKind : rollback du contenu « {0} » échoué ({1}).",
                            rewritten[i].path, ex.Message);
                        failed.Add("rollback : " + rewritten[i].path);
                    }
                }
                for (int i = journal.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        if (Directory.Exists(journal[i].to)) Directory.Move(journal[i].to, journal[i].from);
                        else if (File.Exists(journal[i].to)) File.Move(journal[i].to, journal[i].from);
                    }
                    catch (Exception ex)
                    {
                        Logger?.Error("[LLM_AI] CrossKind : rollback « {0} » → « {1} » échoué ({2}) — état à vérifier.",
                            journal[i].to, journal[i].from, ex.Message);
                        failed.Add("rollback : " + journal[i].to);
                    }
                }
            }

            if (done && (renamed.Count + deleted.Count) > 0)
            {
                // Tag « conversion faite » (add-only, même sémantique que la
                // copie) — l'item d'origine disparaîtra au rescan de Emby.
                try
                {
                    if (!OrphanResolver.HasTag(item, OrphanIdentifyTask.TagRegularized))
                        OrphanResolver.AddTag(item, OrphanIdentifyTask.TagRegularized);
                    item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                }
                catch (Exception ex)
                {
                    Logger?.Warn("[LLM_AI] CrossKind : UpdateToRepository échoué ({0}).", ex.Message);
                }
            }

            if (done)
            {
                // Poster canonique : l'image EPG de l'item est écrite en
                // poster.jpg du dossier converti (le renommage a déjà déplacé
                // le poster d'origine en « <base>-poster.jpg »). Best-effort :
                // le chemin d'image de l'item pointe l'ancien dossier (renommé)
                // → repli naturel sur l'endpoint image d'Emby (cache local).
                await TrySaveItemPosterAsync(item, newFolder, ct).ConfigureAwait(false);
            }

            Logger?.Info("[LLM_AI] CrossKind : « {0} » {1} — renommés={2} supprimés={3} (aucun média supprimé).",
                item?.Name, done ? "converti sur place" : "conversion ÉCHOUÉE (état restauré)",
                renamed.Count, deleted.Count);

            return new CrossKindConvertResponse
            {
                Renamed = renamed.ToArray(),
                Deleted = deleted.ToArray(),
                Failed = failed.ToArray(),
                Warning = warning
            };
        }

        /// <summary>
        /// Construit le contenu d'un nfo « movie » : titre/année cibles, plot
        /// et genres repris de l'ancien nfo (repli : l'item, qui porte les
        /// champs EPG verrouillés), ids de l'item quand présents. XDocument
        /// garantit l'échappement XML (les titres contiennent « &amp; », « &lt; »…).
        /// </summary>
        private string BuildMovieNfo(string title, int year, System.Xml.Linq.XElement oldDoc, BaseItem item)
        {
            string plot = oldDoc?.Element("plot")?.Value;
            if (string.IsNullOrWhiteSpace(plot)) plot = item?.Overview;
            var genres = (oldDoc?.Elements("genre") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                .Select(g => g.Value)
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (genres.Count == 0 && (item?.Genres?.Length ?? 0) > 0)
                genres = item.Genres.Where(g => !string.IsNullOrWhiteSpace(g)).ToList();

            var root = new System.Xml.Linq.XElement("movie");
            root.Add(new System.Xml.Linq.XElement("title", (title ?? string.Empty).Trim()));
            if (year > 0)
                root.Add(new System.Xml.Linq.XElement("year", year.ToString(CultureInfo.InvariantCulture)));
            if (!string.IsNullOrWhiteSpace(plot))
                root.Add(new System.Xml.Linq.XElement("plot", plot.Trim()));
            foreach (var g in genres)
                root.Add(new System.Xml.Linq.XElement("genre", g.Trim()));
            int.TryParse(item?.GetProviderId("tmdb"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId);
            if (tmdbId > 0)
                root.Add(new System.Xml.Linq.XElement("uniqueid",
                    new System.Xml.Linq.XAttribute("type", "tmdb"),
                    tmdbId.ToString(CultureInfo.InvariantCulture)));
            string imdb = item?.GetProviderId("imdb");
            if (!string.IsNullOrWhiteSpace(imdb))
                root.Add(new System.Xml.Linq.XElement("uniqueid",
                    new System.Xml.Linq.XAttribute("type", "imdb"), imdb.Trim()));

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>");
            sb.Append(root.ToString());
            return sb.ToString();
        }

        /// <summary>
        /// Écrit l'image principale de l'item (l'affiche EPG d'un enregistrement
        /// DVR — vérifié en réel : le poster.jpg du dossier EST cette image, md5
        /// identique) en <c>poster.jpg</c> du dossier cible, en complément du
        /// renommage du poster d'origine (« &lt;base&gt;-poster.jpg ») : le nom
        /// canonique garantit que l'œuvre ré-importée garde une affiche même si
        /// le dossier n'en avait pas (ou seulement un logo de chaîne). Pattern
        /// de <see cref="StrmLibraryGenerator"/> : image en cache local → copie ;
        /// URL distante → JAMAIS l'hôte de l'image (donnée facturée du guide) —
        /// l'affiche est demandée à l'endpoint image d'Emby
        /// (<c>/emby/Items/{id}/Images/Primary</c>, le même chemin que
        /// l'affichage EPG, servi depuis son cache). Best-effort, idempotent
        /// (poster.jpg déjà présent → no-op), ne lève jamais (hors annulation
        /// réelle — timeout HttpClient : TaskCanceledException sans annulation).
        /// </summary>
        private async Task<bool> TrySaveItemPosterAsync(BaseItem item, string destFolder, CancellationToken ct)
        {
            try
            {
                if (item == null || string.IsNullOrWhiteSpace(destFolder)) return false;
                string dst = Path.Combine(destFolder, "poster.jpg");
                if (File.Exists(dst)) return false; // déjà là : idempotent

                if (!item.HasImage(ImageType.Primary, 0))
                {
                    Logger?.Info("[LLM_AI] CrossKind : pas d'image Primary sur « {0} » — poster non écrit.", item.Name);
                    return false;
                }
                string src = item.GetImageInfo(ImageType.Primary, 0)?.Path;
                if (string.IsNullOrWhiteSpace(src))
                {
                    Logger?.Info("[LLM_AI] CrossKind : chemin d'image indisponible pour « {0} » — poster non écrit.", item.Name);
                    return false;
                }

                // Image en cache local côté Emby : simple copie.
                if (File.Exists(src))
                {
                    File.Copy(src, dst, overwrite: true);
                    Logger?.Info("[LLM_AI] CrossKind : poster EPG copié → « {0} » (depuis {1}).", dst, src);
                    return true;
                }

                // URL distante : on ne contacte JAMAIS l'hôte de l'image — la
                // demande va à Emby lui-même (endpoint image, cache local).
                string baseApi = null;
                try { baseApi = ApplicationHost?.GetLocalHostApiUrl(); }
                catch (Exception ex)
                {
                    Logger?.Info("[LLM_AI] CrossKind : GetLocalHostApiUrl indisponible ({0}).", ex.Message);
                }
                if (string.IsNullOrWhiteSpace(baseApi)) return false;

                string url = baseApi.TrimEnd('/') + "/Items/"
                    + item.InternalId.ToString(CultureInfo.InvariantCulture) + "/Images/Primary?maxWidth=400";
                using (var resp = await s_http.GetAsync(url, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    using (var fs = File.Create(dst))
                        await (await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                            .CopyToAsync(fs, 81920, ct).ConfigureAwait(false);
                }
                Logger?.Info("[LLM_AI] CrossKind : poster récupéré via l'endpoint image Emby → « {0} ».", dst);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Logger?.Info("[LLM_AI] CrossKind : poster de « {0} » non écrit ({1}) — best-effort.", item?.Name, ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------
        //  POST Ignore — pose/retire le tag llmai-cross-kind-ignored :
        //  l'item sort (ou revient) de la file. Cas d'usage : une vraie série
        //  pas encore dans TVDB/TMDB (« Le téléjournal », nouvelle mouture)
        //  que la file proposerait à chaque rafraîchissement.
        // ------------------------------------------------------------------

        public object Post(CrossKindIgnoreRequest req)
        {
            // Tout refus est loggé (Warn) : l'admin voit la cause dans la
            // réponse ET au journal Emby.
            CrossKindIgnoreResponse Refuse(string msg)
            {
                Logger?.Warn("[LLM_AI] CrossKind : ignore refusé — {0}", msg);
                return new CrossKindIgnoreResponse { Error = msg };
            }

            if (!IsAdmin())
                return Refuse(NotAdminError());

            string itemId = (req?.ItemId ?? "").Trim();
            if (itemId.Length == 0)
                return Refuse("ItemId requis.");

            var item = ItemIdResolver.Resolve(LibraryManager, itemId);
            if (item == null)
                return Refuse("Item introuvable (id : " + itemId + ").");

            if (req.Ignored) OrphanResolver.AddTag(item, OrphanIdentifyTask.TagCrossKindIgnored);
            else OrphanResolver.RemoveTag(item, OrphanIdentifyTask.TagCrossKindIgnored);
            try { item.UpdateToRepository(ItemUpdateType.MetadataEdit); }
            catch (Exception ex)
            {
                Logger?.Warn("[LLM_AI] CrossKind : UpdateToRepository (ignore) échoué ({0}).", ex.Message);
            }

            Logger?.Info("[LLM_AI] CrossKind : « {0} » {1} la file cross-kind.",
                item?.Name, req.Ignored ? "ignoré — retiré de" : "réaffiché dans");
            return new CrossKindIgnoreResponse { Ignored = req.Ignored };
        }

        /// <summary>
        /// Avertissements sur le dossier de destination, sans bloquer la copie :
        /// (1) sous le répertoire des enregistrements Live TV — Emby peut y
        /// purger les fichiers selon la rétention DVR ; (2) sous aucune
        /// bibliothèque — le fichier copié ne serait pas importé du tout.
        /// Renvoie null si rien à signaler.
        /// </summary>
        private string BuildDestinationWarning(string folder)
        {
            var parts = new List<string>();

            try
            {
                if (RecordingDiskManager.TryResolveRecordingPath(ApplicationHost, Logger, out string dvrRoot)
                    && !string.IsNullOrWhiteSpace(dvrRoot)
                    && IsUnderPath(folder, dvrRoot))
                {
                    parts.Add("la destination est dans le répertoire des enregistrements Live TV "
                        + "(« " + dvrRoot + " »), soumis à la rétention DVR d'Emby — "
                        + "préférez une racine de bibliothèque films/séries");
                }
            }
            catch (Exception ex)
            {
                Logger?.Warn("[LLM_AI] CrossKind : contrôle du répertoire DVR impossible ({0}).", ex.Message);
            }

            try
            {
                bool inLibrary = false;
                foreach (var vf in LibraryManager.GetVirtualFolders())
                {
                    foreach (var loc in vf.Locations ?? Array.Empty<string>())
                    {
                        if (IsUnderPath(folder, loc)) { inLibrary = true; break; }
                    }
                    if (inLibrary) break;
                }
                if (!inLibrary)
                    parts.Add("la destination n'est sous aucune bibliothèque Emby : le fichier copié n'y sera pas importé");
            }
            catch (Exception ex)
            {
                Logger?.Warn("[LLM_AI] CrossKind : contrôle des bibliothèques impossible ({0}).", ex.Message);
            }

            if (parts.Count == 0) return null;
            return "Attention : " + string.Join(" ; ", parts) + ".";
        }

        /// <summary>True si <paramref name="candidate"/> est <paramref name="root"/>
        /// ou un sous-chemin de <paramref name="root"/> (insensible à la casse,
        /// séparateurs normalisés sur '/').</summary>
        private static bool IsUnderPath(string candidate, string root)
        {
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root)) return false;
            string a = candidate.TrimEnd('/', '\\');
            string b = root.TrimEnd('/', '\\');
            if (a.Length == 0 || b.Length == 0) return false;
            return a.Equals(b, StringComparison.OrdinalIgnoreCase)
                || a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase);
        }
    }
}