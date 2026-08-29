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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace LLM_AI
{
    /// <summary>
    /// Tâche planifiée quotidienne (04 h) qui <b>identifie les enregistrements
    /// DVR orphelins</b> : ceux qu'Emby n'a pas réussi à identifier (aucun id
    /// IMDb/TMDB/TVDB — souvent des titres québécois absents du catalogue
    /// TMDB/TVDB, qui utilise plutôt les titres de France). L'usager corrige
    /// aujourd'hui ces cas à la main (recherche web → id IMDb) puis verrouille
    /// les champs ; cette tâche automatise la démarche.
    /// </summary>
    /// <remarks>
    /// <para><b>Stratégie en deux stages</b> :</para>
    /// <list type="number">
    /// <item><b>S1 — Nettoyage + recherche multilingue</b> : le titre EPG est
    /// débarrassé de son bruit (<c>TmdbLookupTool.CleanEpgTitle</c> : HD, VOSTFR,
    /// « Rediff. », marqueurs saison/épisode, parenthèses) puis recherché sur
    /// TMDB en plusieurs langues (en-US = titre original, fr-FR = titre France,
    /// + langue de l'usager). Un candidat est retenu si le titre normalisé
    /// correspond (garde-fou contre un mauvais match ambigu).</item>
    /// <item><b>S2 — Proposition LLM validée par TMDB</b> (si S1 échoue) : le
    /// LLM propose un id IMDb/TMDB à partir du titre EPG + overview + chaîne.
    /// La proposition n'est <b>jamais appliquée telle quelle</b> : on la valide
    /// via <c>TmdbLookupTool.FindByExternalIdAsync</c> (/find) ou
    /// <c>LookupMetaByIdAsync</c> (détail par id) — TMDB est la source de
    /// vérité, un id halluciné renvoie null.</item>
    /// </list>
    /// <para><b>Application non destructive</b> : on ne remplit que les ids
    /// provider absents, un <c>Overview</c> vide, des <c>Genres</c> vides, et un
    /// poster <c>Primary</c> manquant. <b>Le <c>Name</c> EPG n'est jamais
    /// modifié</b> — il est verrouillé (<c>MetadataFields.Name</c>) pour
    /// préserver le titre d'origine (réutilisé plus tard pour scanner l'EPG à la
    /// recherche de nouveaux programmes). Les champs qu'on remplit sont
    /// également verrouillés (add-only — on ne retire jamais un verrou
    /// existant), reflétant la pratique manuelle de l'usager.</para>
    /// <para><b>Idempotence</b> via tags : <c>llmai-identified</c> (résolu) ou
    /// <c>llmai-needs-review</c> (irrésolu) — les items déjà tagués sont
    /// ignorés au passage suivant. <b>Dry-run</b> : <c>OrphanIdentifyDryRun</c>
    /// = aucune mutation, log détaillé de la résolution proposée. Best-effort :
    /// un item en erreur n'interrompt jamais le passage.</para>
    /// <para><b>Périmètre</b> : items de bibliothèque (films et séries)
    /// issus d'enregistrements DVR — une fois l'enregistrement terminé, Emby
    /// place l'item dans une bibliothèque, où il vit comme un <c>Movie</c>/<c>Series</c>
    /// normal. Les cartes <c>.strm</c> sont exclues. Découverte via
    /// <see cref="ILibraryManager.GetItemList(InternalItemsQuery)"/>.</para>
    /// </remarks>
    public class OrphanIdentifyTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly IJsonSerializer _json;
        private readonly ILibraryManager _library;
        private readonly IServerApplicationHost _host;
        private readonly IProviderManager _providers;
        private readonly LlmRunner _runner;
        private readonly TmdbLookupTool _tmdb;

        /// <summary>HttpClient partagé pour le téléchargement des posters TMDB.</summary>
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        /// <summary>Tag posé sur un orphelin résolu (id écrit + champs verrouillés).</summary>
        public const string TagIdentified = "llmai-identified";
        /// <summary>Tag posé sur un orphelin qu'aucun stage n'a pu résoudre (à revérifier à la main).</summary>
        public const string TagNeedsReview = "llmai-needs-review";

        public OrphanIdentifyTask(
            ILogger logger,
            IJsonSerializer jsonSerializer,
            ILibraryManager library,
            IUserManager users,
            ILiveTvManager liveTv,
            IServerApplicationHost host,
            IProviderManager providers)
        {
            _logger = logger;
            _json = jsonSerializer;
            _library = library;
            _host = host;
            _providers = providers;
            _runner = new LlmRunner(logger, jsonSerializer, library, users, liveTv, host);
            _tmdb = new TmdbLookupTool(logger);
        }

        public string Name => I18n.S("task.orphan.name", I18n.ResolveDisplayLangKey(_host));

        /// <summary>Identifiant stable de la tâche (GUID dédié).</summary>
        public string Key => "f4a1c2b3-9900-4a8e-bb12-0a1b2c3d4e5f";

        public string Description => I18n.S("task.orphan.desc", I18n.ResolveDisplayLangKey(_host));

        public string Category => I18n.S("task.category", I18n.ResolveDisplayLangKey(_host));

        public bool IsHidden => false;

        public bool IsEnabled => true;

        public bool IsLogged => true;

        /// <summary>Déclencheur par défaut : quotidien à 04 h (après le nettoyage 03 h).</summary>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = "DailyTrigger",
                TimeOfDayTicks = new TimeSpan(4, 0, 0).Ticks
            };
        }

        // ------------------------------------------------------------------
        //  Exécution
        // ------------------------------------------------------------------

        public async Task Execute(CancellationToken ct, IProgress<double> progress)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null || !cfg.OrphanIdentifyEnabled)
            {
                _logger?.Info("[LLM_AI] OrphanIdentify : tâche désactivée (OrphanIdentifyEnabled=false) — passage ignoré.");
                return;
            }

            bool dry = cfg.OrphanIdentifyDryRun;
            if (string.IsNullOrWhiteSpace(cfg.TmdbApiKey))
            {
                _logger?.Warn("[LLM_AI] OrphanIdentify : clé API TMDB absente — S1/S2 impossibles, passage annulé.");
                return;
            }

            // Langue TMDB de l'usager (ResponseLanguage → langue d'affichage → legacy → en-US).
            string userTmdb = I18n.ToTmdbLang(I18n.ResolveMetaLangKey(cfg, _host));
            // Ordre de recherche multilingue : en-US (titre original) puis fr-FR (titre France)
            // puis langue de l'usager. Distinct : si l'usager est en en-US/fr-FR, pas de doublon.
            var langs = new[] { "en-US", "fr-FR", userTmdb }
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            bool verbose = cfg.DebugVerbose;
            _logger?.Info("[LLM_AI] OrphanIdentify : démarrage ({0}, langs=[{1}]{2}).",
                dry ? "DRY-RUN" : "application", string.Join(",", langs), verbose ? ", verbose" : "");

            // Les enregistrements DVR terminés sont importés par Emby dans une
            // bibliothèque (Movies/Series) — or GetRecordings ne retourne que les
            // enregistrements actifs/à venir, PAS les enregistrements complétés (qui
            // deviennent des items bibliothèque normaux). On scanne donc les items
            // Movie/Series de la bibliothèque et on retient les orphelins (aucun id
            // provider IMDb/TMDB/TVDB = identification Emby échouée). Les cartes
            // .strm de la bibliothèque ai_suggestions sont exclues (extension .strm).
            BaseItem[] items;
            try
            {
                items = _library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Movie", "Series" },
                    Recursive = true,
                    EnableTotalRecordCount = false
                }) ?? Array.Empty<BaseItem>();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] OrphanIdentify : GetItemList a échoué — passage annulé.", ex, ex.Message);
                return;
            }

            int scanned = 0, orphans = 0, resolved = 0, review = 0, skipped = 0, errors = 0;
            int total = items.Length;
            int idx = 0;

            _logger?.Info("[LLM_AI] OrphanIdentify : {0} item(s) Movie/Series en bibliothèque.", total);

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                idx++;
                try { progress?.Report((double)idx / total * 100.0); } catch { /* best-effort */ }
                scanned++;

                Status st;
                try
                {
                    st = await HandleItemAsync(item, cfg, dry, langs, userTmdb, verbose, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    errors++;
                    _logger?.Warn("[LLM_AI] OrphanIdentify : erreur sur « {0} » ({1}) — item ignoré.",
                        item?.Name, ex.Message);
                    continue;
                }

                switch (st)
                {
                    case Status.Skipped: skipped++; break;
                    // Un item résolu ou en needs-review était par construction un orphelin.
                    case Status.OrphanResolved: orphans++; resolved++; break;
                    case Status.NeedsReview: orphans++; review++; break;
                }
            }

            _logger?.Info(
                "[LLM_AI] OrphanIdentify : terminé. items={0} orphelins={1} résolus={2} needs-review={3} ignorés(non-orphelin/.strm/déjà taggé)={4} erreurs={5} ({6}).",
                scanned, orphans, resolved, review, skipped, errors, dry ? "DRY-RUN" : "application");
        }

        private enum Status { Skipped, OrphanResolved, NeedsReview }

        // ------------------------------------------------------------------
        //  Traitement d'un item bibliothèque (Movie/Series) orphelin
        // ------------------------------------------------------------------

        private async Task<Status> HandleItemAsync(BaseItem item, PluginConfiguration cfg,
            bool dry, string[] langs, string userTmdb, bool verbose, CancellationToken ct)
        {
            // La requête est déjà filtrée sur Movie/Series ; on détermine le kind via
            // le nom du type (robuste aux sous-types Emby).
            bool isSeries = item.GetType().Name.IndexOf("Series", StringComparison.OrdinalIgnoreCase) >= 0;
            string kind = isSeries ? "series" : "movie";

            var itemTags = item.Tags ?? Array.Empty<string>();
            bool tagged = Array.IndexOf(itemTags, TagIdentified) >= 0 || Array.IndexOf(itemTags, TagNeedsReview) >= 0;
            bool strmCard = IsStrmCard(item);
            bool orphan = IsOrphanItem(item);
            string itemName = item.Name;

            // Diagnostics verbose : expose pour chaque item pourquoi il est gardé ou
            // écarté — sinon les skips sont silencieux.
            if (verbose)
            {
                string reason;
                if (strmCard) reason = "carte .strm (bibliothèque ai_suggestions)";
                else if (tagged) reason = "déjà taggé " + (Array.IndexOf(itemTags, TagIdentified) >= 0 ? TagIdentified : TagNeedsReview);
                else if (!orphan) reason = "a déjà un id provider (non-orphelin)";
                else if (string.IsNullOrWhiteSpace(itemName)) reason = "titre vide";
                else reason = "ORPHELIN → à traiter";
                _logger?.Info("[LLM_AI] OrphanIdentify : id={0} « {1} » kind={2} imdb={3} tmdb={4} tvdb={5} tags=[{6}] → {7}.",
                    item.Id, itemName, kind,
                    HasItemProviderId(item, "imdb") ? "oui" : "non",
                    HasItemProviderId(item, "tmdb") ? "oui" : "non",
                    HasItemProviderId(item, "tvdb") ? "oui" : "non",
                    string.Join(",", itemTags), reason);
            }

            // Cartes .strm de la bibliothèque du plugin : hors périmètre.
            if (strmCard) return Status.Skipped;
            if (tagged) return Status.Skipped;

            // Orphelin = aucun id provider IMDb/TMDB/TVDB.
            if (!orphan) return Status.Skipped;

            string epgTitle = itemName;
            if (string.IsNullOrWhiteSpace(epgTitle)) return Status.Skipped;

            int? year = item.ProductionYear ?? YearOf(item.PremiereDate) ?? YearOf(item.DateCreated);
            string overview = item.Overview;
            string cleanTitle = TmdbLookupTool.CleanEpgTitle(epgTitle);
            if (string.IsNullOrWhiteSpace(cleanTitle)) cleanTitle = epgTitle;

            // S1 : nettoyage + recherche multilingue.
            TmdbMeta meta = null;
            string stage = null;
            try
            {
                var s1 = await _tmdb.LookupMetaMultiLangAsync(cleanTitle, kind, year, langs, ct).ConfigureAwait(false);
                if (s1 != null && TitleMatches(cleanTitle, s1.Title, year, s1.Year))
                {
                    meta = s1;
                    stage = "S1";
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger?.Info("[LLM_AI] OrphanIdentify : S1 « {0} » échoué ({1}).", epgTitle, ex.Message); }

            // S2 : proposition LLM validée par TMDB.
            if (meta == null)
            {
                try
                {
                    meta = await ResolveViaLlmAsync(cfg, epgTitle, cleanTitle, kind, year, overview, null, langs, userTmdb, ct).ConfigureAwait(false);
                    if (meta != null) stage = "S2";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger?.Info("[LLM_AI] OrphanIdentify : S2 « {0} » échoué ({1}).", epgTitle, ex.Message); }
            }

            // Aucun candidat → needs-review (sauf en dry-run : on logue seulement).
            if (meta == null || meta.TmdbId <= 0)
            {
                _logger?.Info("[LLM_AI] OrphanIdentify : « {0} » ({1}) — NON résolu (needs-review).", epgTitle, kind);
                if (dry) return Status.NeedsReview;
                AddTag(item, TagNeedsReview);
                item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                return Status.NeedsReview;
            }

            // Candidat trouvé.
            if (dry)
            {
                _logger?.Info("[LLM_AI] OrphanIdentify (DRY-RUN) : « {0} » → tmdb={1} imdb={2} tvdb={3} ({4}) — aucune écriture.",
                    epgTitle, meta.TmdbId, meta.ImdbId ?? "—", meta.TvdbId ?? "—", stage);
                return Status.OrphanResolved;
            }

            await ApplyAsync(item, meta, kind, isSeries, ct).ConfigureAwait(false);
            _logger?.Info("[LLM_AI] OrphanIdentify : « {0} » → tmdb={1} imdb={2} tvdb={3} ({4}) ; Name verrouillé, tag {5}.",
                epgTitle, meta.TmdbId, meta.ImdbId ?? "—", meta.TvdbId ?? "—", stage, TagIdentified);
            return Status.OrphanResolved;
        }

        // ------------------------------------------------------------------
        //  S2 : proposition LLM → validation TMDB
        // ------------------------------------------------------------------

        private async Task<TmdbMeta> ResolveViaLlmAsync(PluginConfiguration cfg,
            string epgTitle, string cleanTitle, string kind, int? year,
            string overview, string channel, string[] langs, string userTmdb, CancellationToken ct)
        {
            var guess = await _runner.ResolveIdsAsync(cfg, epgTitle, kind, year, overview, channel, ct).ConfigureAwait(false);
            if (guess.IsEmpty) return null;

            // 1) id IMDb → TMDB /find (validation forte : si TMDB résout l'id, c'est bon).
            //    Garde-fou année : un même titre peut exister pour deux films
            //    différents (ex. « Le guérisseur » 1953 vs un enregistrement 2023)
            //    — on refuse si les deux années sont connues et diffèrent de >1.
            int? expectedYear = year ?? guess.Year;
            if (!string.IsNullOrWhiteSpace(guess.ImdbId))
            {
                var m = await _tmdb.FindByExternalIdAsync(guess.ImdbId.Trim(), "imdb_id", kind, userTmdb, ct).ConfigureAwait(false);
                if (m != null && m.TmdbId > 0 && YearCompatible(expectedYear, m.Year)) return m;
            }

            // 2) id TMDB → relire la fiche (validation : un id halluciné renvoie null).
            if (guess.TmdbId > 0)
            {
                var m = await _tmdb.LookupMetaByIdAsync(guess.TmdbId, kind, userTmdb, ct).ConfigureAwait(false);
                if (m != null && m.TmdbId > 0 && YearCompatible(expectedYear, m.Year)) return m;
            }

            // 3) titre original proposé → recherche S1 sur ce titre (garde-fou : match de titre).
            if (!string.IsNullOrWhiteSpace(guess.OriginalTitle))
            {
                string ot = TmdbLookupTool.CleanEpgTitle(guess.OriginalTitle);
                if (!string.IsNullOrWhiteSpace(ot))
                {
                    int? y = guess.Year ?? year;
                    var m = await _tmdb.LookupMetaMultiLangAsync(ot, kind, y, langs, ct).ConfigureAwait(false);
                    if (m != null && TitleMatches(ot, m.Title, y, m.Year)) return m;
                }
            }

            return null;
        }

        // ------------------------------------------------------------------
        //  Application non destructive + verrouillage
        // ------------------------------------------------------------------

        private async Task ApplyAsync(BaseItem item, TmdbMeta meta, string kind, bool isSeries, CancellationToken ct)
        {
            // --- Provider ids (uniquement ceux absents) ---
            if (meta.TmdbId > 0 && string.IsNullOrWhiteSpace(item.GetProviderId("tmdb")))
                item.SetProviderId("tmdb", meta.TmdbId.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(meta.ImdbId) && string.IsNullOrWhiteSpace(item.GetProviderId("imdb")))
                item.SetProviderId("imdb", meta.ImdbId.Trim());
            if (isSeries && !string.IsNullOrWhiteSpace(meta.TvdbId) && string.IsNullOrWhiteSpace(item.GetProviderId("tvdb")))
                item.SetProviderId("tvdb", meta.TvdbId.Trim());

            // --- Overview : seulement si vide ---
            bool setOverview = false;
            if (string.IsNullOrWhiteSpace(item.Overview) && !string.IsNullOrWhiteSpace(meta.Overview))
            {
                item.Overview = meta.Overview;
                setOverview = true;
            }

            // --- Genres : seulement si vides ---
            bool setGenres = false;
            if ((item.Genres == null || item.Genres.Length == 0) && meta.Genres != null && meta.Genres.Length > 0)
            {
                item.Genres = meta.Genres;
                setGenres = true;
            }

            // --- Poster Primary : seulement si manquant (best-effort) ---
            bool setImage = false;
            if (!item.HasImage(ImageType.Primary, 0) && !string.IsNullOrWhiteSpace(meta.PosterUrl))
            {
                setImage = await TrySavePosterAsync(item, meta.PosterUrl, ct).ConfigureAwait(false);
            }

            // --- Verrouillage (add-only, jamais de retrait) ---
            // Name toujours verrouillé (préserver le titre EPG pour le scan EPG futur).
            AddLock(item, MetadataFields.Name);
            if (setOverview) AddLock(item, MetadataFields.Overview);
            if (setGenres) AddLock(item, MetadataFields.Genres);

            // --- Tag d'idempotence ---
            AddTag(item, TagIdentified);

            // --- Persistance ---
            var updateType = ItemUpdateType.MetadataEdit | (setImage ? ItemUpdateType.ImageUpdate : ItemUpdateType.None);
            item.UpdateToRepository(updateType);
        }

        /// <summary>
        /// Télécharge le poster TMDB et le pose comme image <see cref="ImageType.Primary"/>
        /// via <see cref="IProviderManager.SaveImage"/> (même pattern que
        /// <see cref="DefaultImageApplier"/>). Best-effort : ne lève jamais,
        /// renvoie false en cas d'échec.
        /// </summary>
        private async Task<bool> TrySavePosterAsync(BaseItem item, string posterUrl, CancellationToken ct)
        {
            try
            {
                var fs = _host.TryResolve<IFileSystem>();
                if (_providers == null || fs == null) return false;

                using (var resp = await _http.GetAsync(posterUrl, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return false;
                    using (var ms = new MemoryStream())
                    {
                        await resp.Content.CopyToAsync(ms).ConfigureAwait(false);
                        if (ms.Length == 0) return false;
                        ms.Position = 0;

                        string mime = MimeFromUrl(posterUrl);
                        var dirSvc = new DirectoryService(fs);
                        var libOpts = _library.GetLibraryOptions(item);
                        await _providers.SaveImage(item, libOpts, ms, mime.AsMemory(),
                            ImageType.Primary, null, null, dirSvc, true, ct).ConfigureAwait(false);
                        return true;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Info("[LLM_AI] OrphanIdentify : poster « {0} » échoué ({1}) — ignoré.", item.Name, ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------
        //  Helpers
        // ------------------------------------------------------------------

        /// <summary>Orphelin = aucun id provider IMDb/TMDB/TVDB présent (identification Emby échouée).</summary>
        private static bool IsOrphanItem(BaseItem item)
        {
            return !HasItemProviderId(item, "imdb")
                && !HasItemProviderId(item, "tmdb")
                && !HasItemProviderId(item, "tvdb");
        }

        private static bool HasItemProviderId(BaseItem item, string key)
        {
            if (item == null) return false;
            try { return !string.IsNullOrWhiteSpace(item.GetProviderId(key)); }
            catch { return false; }
        }

        /// <summary>
        /// Carte .strm de la bibliothèque du plugin (ai_suggestions) : hors périmètre
        /// — ce ne sont pas des enregistrements DVR. Détecté par l'extension du chemin.
        /// </summary>
        private static bool IsStrmCard(BaseItem item)
        {
            var path = item?.Path;
            if (string.IsNullOrWhiteSpace(path)) return false;
            return path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Garde-fou de correspondance : compare le titre EPG (nettoyé) au titre
        /// TMDB retourné, en mode tolérant (casse/accents/ponctuation ignorés,
        /// inclusion acceptée). Si les deux années sont connues et diffèrent de
        /// plus d'un an, on refuse (évite un match ambigu sur un titre commun).
        /// </summary>
        private static bool TitleMatches(string epgTitle, string tmdbTitle, int? epgYear, int? metaYear)
        {
            if (string.IsNullOrWhiteSpace(tmdbTitle)) return false;
            string a = NormalizeTitle(epgTitle);
            string b = NormalizeTitle(tmdbTitle);
            if (a.Length == 0 || b.Length == 0) return false;

            bool titleOk = a == b || a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
            if (!titleOk) return false;

            if (epgYear.HasValue && metaYear.HasValue && Math.Abs(epgYear.Value - metaYear.Value) > 1)
                return false;

            return true;
        }

        /// <summary>
        /// Garde-fou d'année pour S2 : deux films peuvent partager un même titre
        /// (ex. « Le guérisseur » 1953 vs un enregistrement de 2023). On accepte
        /// si l'une des deux années est inconnue, ou si elles diffèrent d'au plus
        /// un an (tolérance de date de sortie vs date d'enregistrement).
        /// </summary>
        private static bool YearCompatible(int? expected, int? actual)
        {
            if (!expected.HasValue || !actual.HasValue) return true;
            return Math.Abs(expected.Value - actual.Value) <= 1;
        }

        private static string NormalizeTitle(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            // Retire les accents.
            var sb = new StringBuilder(s.Length);
            foreach (var c in s.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            // Ne garde que les alphanumériques, minuscules, sans séparateur.
            var t = sb.ToString().ToLowerInvariant();
            sb.Clear();
            foreach (var c in t)
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        private static int? YearOf(DateTime? d) => d.HasValue ? d.Value.Year : (int?)null;
        private static int? YearOf(DateTime d) => d == default ? (int?)null : d.Year;
        private static int? YearOf(DateTimeOffset? d) => d.HasValue ? d.Value.Year : (int?)null;
        private static int? YearOf(DateTimeOffset d) => d == default ? (int?)null : d.Year;

        private static string MimeFromUrl(string url) =>
            url.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";

        /// <summary>Ajoute un verrou de champ sans retirer les verrous existants (add-only).</summary>
        private static void AddLock(BaseItem item, MetadataFields field)
        {
            var lf = item.LockedFields;
            var list = lf == null ? new List<MetadataFields>() : new List<MetadataFields>(lf);
            if (list.Contains(field)) return;
            list.Add(field);
            item.LockedFields = list.ToArray();
        }

        /// <summary>Ajoute un tag sans dupliquer.</summary>
        private static void AddTag(BaseItem item, string tag)
        {
            var t = item.Tags ?? Array.Empty<string>();
            if (Array.IndexOf(t, tag) >= 0) return;
            var list = new List<string>(t) { tag };
            item.Tags = list.ToArray();
        }
    }
}