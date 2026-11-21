using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;

namespace LLM_AI
{
    /// <summary>
    /// Recherche « Identifier » assistée (v2.23.0) : deux fournisseurs de
    /// recherche branchés sur le dialogue <b>Identifier</b> d'Emby
    /// (<c>POST /Items/RemoteSearch/Movie|Series</c>, moteur
    /// <c>IProviderManager.GetRemoteSearchResults</c> — le même S0 que la
    /// tâche d'orphelins, dans l'autre sens).
    /// </summary>
    /// <remarks>
    /// <para><b>Ce que fait le plugin</b> : à chaque recherche, la
    /// recherche-liste TMDB (sans filtre d'année — doctrine cascade v2.4.4,
    /// l'année reste un indice de rangement) produit des candidats triés
    /// (titre exact d'abord, année concordante ensuite) ; quand aucun
    /// candidat exact n'émerge, un <b>renfort LLM</b> (miroir S2 : les ids
    /// proposés sont relus sur TMDB, jamais appliqués sans corroboration —
    /// titre exact du guide ou année concordante, TMDB reste la source de
    /// vérité) propose un complément, puis un <b>renfort web</b> (miroir S3 :
    /// SearXNG, ids extraits des résultats — le gate est la
    /// présence de <c>SearXngUrl</c>, fail-open intégral) prend le relais :
    /// un résultat web portant le titre du guide (clé exacte, matcher S3)
    /// EST la preuve qui promeut sa fiche à l'écran, et toute autre fiche
    /// web résolue est servie sans preuve en rang queue — jamais jetée
    /// (v2.23.1, cas « La dernière séance de Freud » : le titre québécois
    /// n'existe dans aucune base interrogeable ; le dialogue est une liste
    /// à choisir, et le natif MovieDb montre lui-même des matches lâches).
    /// La pont MCC (titre québécois, via
    /// <c>TmdbLookupTool.LookupMetaMultiLangAsync</c>) et la chaîne de
    /// langues <c>TmdbLangPriority</c> s'appliquent comme partout.</para>
    /// <para><b>Mécanique de fusion côté serveur (décompilé 4.10.2.0)</b> :
    /// le premier fournisseur qui retourne des résultats remplit la liste —
    /// les suivants ne fusionnent que par id. Les candidats LLM_AI
    /// apparaissent donc quand les banques natives n'ont rien trouvé (le cas
    /// utile : titres québécois), complètent sinon par fusion d'id, ou
    /// s'affichent seuls quand l'utilisateur a saisi un id dans le dialogue.</para>
    /// <para><b>Énumération</b> : <c>GetMetadataProvidersInternal</c> filtre
    /// d'abord <c>OfType&lt;IMetadataProvider&lt;Movie|Series&gtgt;</c> — un
    /// fournisseur « recherche seule » n'y entre pas (shape vérifiée sur
    /// <c>MovieDbProvider</c>, qui porte les deux interfaces) ; la classe
    /// porte donc AUSSI <c>IRemoteMetadataProvider</c> avec un
    /// <c>GetMetadata</c> volontairement muet — aucun LLM en path de refresh
    /// automatique. La consultation pour des items EXISTANTS exige
    /// l'armement du fournisseur dans la bibliothèque de l'item
    /// (<see cref="AiIdentifyLibraryArmer"/>) ; la variante « sans item » du
    /// dialogue (ItemId absent → <c>IncludeDisabledProviders=true</c>) n'est
    /// pas filtrée.</para>
    /// <para><b>Non-buts</b> : le provider ne pose RIEN — la fiche choisie
    /// par l'usager est appliquée par le geste standard d'Emby
    /// (<c>POST /Items/RemoteSearch/Apply</c> : pose des ids + FullRefresh,
    /// qui lève aussi les verrous de l'item — comportement natif). L'audit
    /// aval reste le filet : côté DVR, le <see cref="RecordingWatcher"/>
    /// juge l'id appliqué contre la vérité EPG figée.</para>
    /// </remarks>
    public class AiIdentifyMovieSearchProvider : IRemoteMetadataProvider<Movie, MovieInfo>,
        IRemoteSearchProvider<MovieInfo>
    {
        /// <summary>Nom du fournisseur (listes du serveur, fusion d'id,
        /// liste des récupérateurs du tableau de bord).</summary>
        public string Name => AiIdentifySearchCore.ProviderName;

        private readonly AiIdentifySearchCore _core;

        /// <summary>
        /// Injection DI du serveur (pattern <see cref="AiBadgeEnhancer"/>).
        /// NOTE — ne pas toucher Configuration ici (leçon ctor : un lancé
        /// dans le constructeur casse la création de tout le plugin).
        /// </summary>
        public AiIdentifyMovieSearchProvider(ILogger logger, IJsonSerializer json,
            ILibraryManager library, IUserManager users, ILiveTvManager liveTv,
            IServerApplicationHost host, IHttpClient httpClient)
        {
            _core = new AiIdentifySearchCore(logger, json, library, users, liveTv, host, httpClient);
            _httpClient = httpClient;
        }

        /// <summary>
        /// Fournisseur de métadonnées <b>volontairement muet</b> : l'interface
        /// <c>IRemoteMetadataProvider&lt;Movie, MovieInfo&gt;</c> est exigée
        /// pour entrer dans l'énumération <c>IMetadataProvider&lt;Movie&gt;</c>
        /// du serveur, mais le plugin n'écrit jamais les métadonnées en
        /// automatique (le pipeline d'orphelins le fait, avec ses règles à
        /// lui). Résultat vide = <c>HasMetadata=false</c> — la shape « rien
        /// trouvé » du natif ; zéro I/O, zéro LLM sur ce chemin.
        /// </summary>
        public Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            return Task.FromResult(new MetadataResult<Movie>());
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo,
            CancellationToken cancellationToken)
        {
            return _core.GetResultsAsync("movie", searchInfo, cancellationToken);
        }

        /// <summary>Proxy d'affiche (route <c>Items/RemoteSearch/Image</c>,
        /// même mécanique que <c>MovieDbProviderBase</c>).</summary>
        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return AiIdentifySearchCore.GetImageResponseAsync(_httpClient, url, cancellationToken);
        }

        private readonly IHttpClient _httpClient;
    }

    /// <summary>Variante séries de <see cref="AiIdentifyMovieSearchProvider"/> —
    /// même moteur, même politique (voir ce type pour la doctrine complète).</summary>
    public class AiIdentifySeriesSearchProvider : IRemoteMetadataProvider<Series, SeriesInfo>,
        IRemoteSearchProvider<SeriesInfo>
    {
        public string Name => AiIdentifySearchCore.ProviderName;

        private readonly AiIdentifySearchCore _core;
        private readonly IHttpClient _httpClient;

        public AiIdentifySeriesSearchProvider(ILogger logger, IJsonSerializer json,
            ILibraryManager library, IUserManager users, ILiveTvManager liveTv,
            IServerApplicationHost host, IHttpClient httpClient)
        {
            _core = new AiIdentifySearchCore(logger, json, library, users, liveTv, host, httpClient);
            _httpClient = httpClient;
        }

        public Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
        {
            return Task.FromResult(new MetadataResult<Series>());
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo,
            CancellationToken cancellationToken)
        {
            return _core.GetResultsAsync("series", searchInfo, cancellationToken);
        }

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return AiIdentifySearchCore.GetImageResponseAsync(_httpClient, url, cancellationToken);
        }
    }

    /// <summary>
    /// Cœur partagé des deux fournisseurs (film / série) : recherche TMDB en
    /// liste, armement par ids saisis dans le dialogue, renfort LLM borné et
    /// corroboré, rangement par preuve (titre exact &gt; année concordante &gt;
    /// ordre TMDB). Fail-open intégral : toute erreur retourne une liste
    /// vide — jamais une exception dans la boucle du serveur.
    /// </summary>
    internal sealed class AiIdentifySearchCore
    {
        /// <summary>Nom du fournisseur (le <c>Name</c> des 2 classes).</summary>
        public const string ProviderName = "LLM_AI";

        /// <summary>Plafond des candidats phase A (liste TMDB).</summary>
        private const int s_maxTmdbCandidates = 8;

        /// <summary>Budget du renfort LLM (le dialogue attend l'appel REST).</summary>
        internal static readonly TimeSpan s_renfortBudget = TimeSpan.FromSeconds(25);

        /// <summary>Plafond de relectures TMDB pendant le renfort.</summary>
        private const int s_maxRenfortLookups = 2;

        /// <summary>Langues max interrogées en phase A.</summary>
        private const int s_maxSearchLangs = 2;

        private readonly ILogger _logger;
        private readonly LlmRunner _runner;
        private readonly TmdbLookupTool _tmdb;
        private readonly WebSearchTool _search;

        private AiIdentifySearchCore(ILogger logger, IJsonSerializer json,
            ILibraryManager library, IUserManager users, ILiveTvManager liveTv,
            IServerApplicationHost host)
        {
            _logger = logger;
            _runner = new LlmRunner(logger, json, library, users, liveTv, host);
            _tmdb = new TmdbLookupTool(logger);
            _search = new WebSearchTool(logger);
        }

        internal AiIdentifySearchCore(ILogger logger, IJsonSerializer json,
            ILibraryManager library, IUserManager users, ILiveTvManager liveTv,
            IServerApplicationHost host, IHttpClient httpClient) : this(logger, json, library, users, liveTv, host)
        {
        }

        /// <summary>Proxy d'affiche — pattern <c>MovieDbProviderBase</c>
        /// (streaming, sans buffering). Les erreurs remontent : le serveur
        /// les avale déjà sur chaque appel de fournisseur.</summary>
        internal static async Task<HttpResponseInfo> GetImageResponseAsync(IHttpClient httpClient,
            string url, CancellationToken ct)
        {
            var opts = new HttpRequestOptions
            {
                CancellationToken = ct,
                Url = url,
                BufferContent = false,
                LogErrors = true
            };
            return await httpClient.GetResponse(opts).ConfigureAwait(false);
        }

        /// <summary>Compteur d'insertion (ordre stable de pertinence TMDB) —
        /// objet boîte car un <c>ref</c> est interdit aux méthodes async.</summary>
        private sealed class OrderBox
        {
            internal int N;
        }

        /// <summary>Nombre de candidats au titre exact (rang par preuve) —
        /// sert à la ligne de log de succès.</summary>
        private static int CountExact(List<CandidateDto> candidates)
        {
            int n = 0;
            foreach (var c in candidates)
                if (c.Exact) n++;
            return n;
        }

        internal async Task<IEnumerable<RemoteSearchResult>> GetResultsAsync(string kind,
            ItemLookupInfo info, CancellationToken ct)
        {
            var empty = new List<RemoteSearchResult>(0);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                if (cfg == null || !cfg.IdentifyAiSearchEnabled) return empty;
                if (string.IsNullOrWhiteSpace(cfg.TmdbApiKey)) return empty;
                if (info == null) return empty;

                // Jamais en path automatique (défense en profondeur — le seul
                // caller réel est le SearchService REST du dialogue ;
                // IsAutomated=true signifierait refresh planifié).
                if (info.IsAutomated) return empty;

                string query = TmdbLookupTool.CleanEpgTitle(info.Name ?? string.Empty);
                if (string.IsNullOrWhiteSpace(query))
                    query = (info.Name ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(query)) return empty;
                int? year = info.Year;

                // Langues de recherche : priorité plugin (TmdbLangPriority —
                // la chaîne du synopsis), langue de métadonnées résolue par
                // le serveur pour l'item, puis en-US.
                var langs = BuildSearchLangs(cfg, info);

                // Cache 24 h du résultat final (clics répétés = zéro coût).
                string cacheKey = "llmaid:" + kind + ":" + string.Join("+", langs) + ":" +
                                  WebResultCache.NormalizeQuery(query) + ":" + (year?.ToString(CultureInfo.InvariantCulture) ?? "");
                if (WebResultCache.TryGet(cacheKey, out var cached))
                {
                    // Rank (champ internal) n'est PAS sérialisé : au retour de
                    // cache, tout rang vaut 0 et le tri de MapCandidates
                    // devient un no-op servant l'ordre TMDB brut (constaté en
                    // banc v2.23.0 sur « VR » : V.R. exact 2e derrière VR
                    // Fighter). Recalcul systématique — plus robuste qu'une
                    // sérialisation du rang (une évolution de RankOf ne serait
                    // jamais plombée par des entrées anciennes).
                    var fromCache = DeserializeCandidates(cached);
                    foreach (var c in fromCache)
                        c.Rank = RankOf(year, c);
                    var cachedMapped = MapCandidates(fromCache);
                    if (cachedMapped.Count > 0)
                    {
                        sw.Stop();
                        _logger?.Info("[LLM_AI] Identifier : « {0} » ({1}) — {2} candidat(s) (exact={3}), cache=oui, {4} ms.",
                            query, kind, cachedMapped.Count,
                            CountExact(fromCache), sw.ElapsedMilliseconds);
                        return cachedMapped;
                    }
                }

                var candidates = new List<CandidateDto>();
                var order = new OrderBox();

                // Phase A0 — ids SAISIS dans le dialogue : l'intention est
                // explicite, la fiche résolue par id passe en tête sans
                // corroboration supplémentaire (l'acte de saisie EST la
                // corroboration — même sémantique que le champ id de tout
                // dialogue d'identification).
                await AddIdCandidatesAsync(kind, info, langs, candidates, order, ct).ConfigureAwait(false);

                // Phase A — recherche-liste TMDB, langue par langue (sans
                // filtre d'année : une année de surface ne doit jamais masquer
                // la fiche) ; arrêt dès qu'un titre exact émerge.
                bool exact = candidates.Any(c => c.Exact);
                for (int li = 0; li < langs.Count && candidates.Count < s_maxTmdbCandidates && !exact; li++)
                {
                    var list = await _tmdb.SearchListAsync(query, kind, langs[li], s_maxTmdbCandidates, ct).ConfigureAwait(false);
                    foreach (var c in list)
                    {
                        if (c.TmdbId <= 0 || candidates.Any(x => x.TmdbId == c.TmdbId)) continue;
                        var dto = new CandidateDto
                        {
                            Title = c.Title,
                            OriginalTitle = c.OriginalTitle,
                            Year = c.Year,
                            Overview = c.Overview,
                            PosterUrl = c.PosterUrl,
                            TmdbId = c.TmdbId,
                            Exact = IsExactTitle(query, c),
                            Source = "tmdb",
                            Order = order.N++
                        };
                        candidates.Add(dto);
                        exact = exact || dto.Exact;
                    }
                }

                // Phase B — renfort LLM (miroir S2), UNIQUEMENT si aucun
                // titre exact n'émerge (les banques natives couvrent déjà ;
                // économiser un appel inutile).
                if (!exact)
                    await AddLlmRenfortAsync(cfg, kind, query, year, langs, candidates, order, ct).ConfigureAwait(false);

                // Phase C — renfort web (miroir S3) : dès qu'aucun titre
                // exact n'a émergé, même si le LLM a produit des candidats
                // non exacts corroborés — la porte de corroboration filtre
                // le bruit de toute façon (décision usager 2026-11-21 :
                // miroir du réflexe manuel « je cherche sur le web »).
                if (!candidates.Any(c => c.Exact))
                    await AddWebRenfortAsync(cfg, kind, query, year, langs, candidates, order, ct).ConfigureAwait(false);

                foreach (var c in candidates)
                    c.Rank = RankOf(year, c);

                var mapped = MapCandidates(candidates);
                if (mapped.Count > 0)
                {
                    sw.Stop();
                    WebResultCache.Set(cacheKey, SerializeCandidates(candidates));
                    _logger?.Info("[LLM_AI] Identifier : « {0} » ({1}) — {2} candidat(s) (exact={3}), cache=non, {4} ms.",
                        query, kind, mapped.Count,
                        candidates.Count(c => c.Exact), sw.ElapsedMilliseconds);
                }
                else
                    _logger?.Info("[LLM_AI] Identifier : « {0} » ({1}) — aucun candidat TMDB/LLM corroboré.",
                        query, kind);
                return mapped;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Identifier : recherche indisponible — {0} {1}",
                    ex.GetType().Name, ex.Message);
                return empty;
            }
        }

        // ------------------------------------------------------------------
        //  Phase A0 : ids saisis dans le dialogue (tmdb / imdb) — la fiche
        //  référencée est résolue directement et passe en tête.
        // ------------------------------------------------------------------

        private async Task AddIdCandidatesAsync(string kind, ItemLookupInfo info,
            IReadOnlyList<string> langs, List<CandidateDto> candidates, OrderBox order,
            CancellationToken ct)
        {
            string primaryLang = langs.Count > 0 ? langs[0] : "en-US";

            string tmdbRaw = StrProviderId(info, "tmdb");
            if (int.TryParse(tmdbRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var typedTmdb)
                && typedTmdb > 0)
            {
                var meta = await _tmdb.LookupMetaByIdAsync(typedTmdb, kind, primaryLang, ct).ConfigureAwait(false);
                if (meta != null)
                {
                    AddMeta(candidates, meta, "id", exact: true, order);
                    return;
                }
                _logger?.Info("[LLM_AI] Identifier : id TMDB saisi {0} introuvable ({1}) — poursuite par titre.",
                    typedTmdb, kind);
            }

            string typedImdb = StrProviderId(info, "imdb");
            if (!string.IsNullOrWhiteSpace(typedImdb) && typedImdb.Trim().StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                var meta = await _tmdb.FindByExternalIdAsync(typedImdb.Trim(), "imdb_id", kind, primaryLang, ct).ConfigureAwait(false);
                if (meta != null) AddMeta(candidates, meta, "id", exact: true, order);
            }
        }

        // ------------------------------------------------------------------
        //  Phase B : renfort LLM — miroir exact du S2 de OrphanResolver
        //  (un id de proposition n'est JAMAIS la preuve : TMDB relit, la
        //  porte exige titre exact OU année concordante fiable).
        // ------------------------------------------------------------------

        private async Task AddLlmRenfortAsync(PluginConfiguration cfg, string kind, string query,
            int? year, List<string> langs, List<CandidateDto> candidates, OrderBox order,
            CancellationToken ct)
        {
            LlmRunner.IdGuess guess;
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(s_renfortBudget);
                    guess = await _runner.ResolveIdsAsync(cfg, query, kind, year, null, null, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger?.Info("[LLM_AI] Identifier : renfort LLM dépassé le budget ({0} s) — candidats TMDB seuls.",
                    (int)s_renfortBudget.TotalSeconds);
                return;
            }
            if (guess.IsEmpty) return;

            string primaryLang = langs.Count > 0 ? langs[0] : "en-US";
            int lookups = 0;

            // 1) id IMDb → TMDB /find (miroir S2).
            if (!string.IsNullOrWhiteSpace(guess.ImdbId) && lookups < s_maxRenfortLookups)
            {
                lookups++;
                var meta = await _tmdb.FindByExternalIdAsync(guess.ImdbId.Trim(), "imdb_id", kind, primaryLang, ct).ConfigureAwait(false);
                if (TryAddCorroboratedLlmCandidate(candidates, meta, query, year, order, "imdb=" + guess.ImdbId.Trim()))
                    return;
            }

            // 1b) id TVDB (séries) — miroir S2 : la banque TVDB couvre mieux
            // certaines séries québécoises absentes de TMDB par titre.
            if (kind == "series" && !string.IsNullOrWhiteSpace(guess.TvdbId) && lookups < s_maxRenfortLookups)
            {
                lookups++;
                var meta = await _tmdb.FindByExternalIdAsync(guess.TvdbId.Trim(), "tvdb_id", kind, primaryLang, ct).ConfigureAwait(false);
                if (TryAddCorroboratedLlmCandidate(candidates, meta, query, year, order, "tvdb=" + guess.TvdbId.Trim()))
                    return;
            }

            // 2) id TMDB proposé → relecture (un id halluciné renvoie null).
            if (guess.TmdbId > 0 && lookups < s_maxRenfortLookups)
            {
                lookups++;
                var meta = await _tmdb.LookupMetaByIdAsync(guess.TmdbId, kind, primaryLang, ct).ConfigureAwait(false);
                if (TryAddCorroboratedLlmCandidate(candidates, meta, query, year, order, "tmdb=" + guess.TmdbId))
                    return;
            }

            // 3) Titre original proposé → recherche S1 sur ce titre
            // (cascade annuelle : l'année du LLM peut masquer la vraie fiche,
            // cas « La foudre » 2026-09-20).
            if (candidates.All(c => !string.Equals(c.Source, "llm", StringComparison.Ordinal)) &&
                !string.IsNullOrWhiteSpace(guess.OriginalTitle) && lookups < s_maxRenfortLookups)
            {
                lookups++;
                string ot = TmdbLookupTool.CleanEpgTitle(guess.OriginalTitle);
                if (!string.IsNullOrWhiteSpace(ot))
                {
                    var meta = await _tmdb.LookupMetaMultiLangAsync(ot, kind, guess.Year ?? year, langs, ct).ConfigureAwait(false);
                    if (meta == null && (guess.Year ?? year).HasValue)
                        meta = await _tmdb.LookupMetaMultiLangAsync(ot, kind, null, langs, ct).ConfigureAwait(false);
                    if (TryAddCorroboratedLlmCandidate(candidates, meta, query, year, order,
                            "titre proposé « " + ot + " »"))
                        return;
                }
            }
        }

        // ------------------------------------------------------------------
        //  Phase C : renfort web — miroir du S3 de OrphanResolver
        //  (SearXNG via WebSearchTool → ids IMDb/TMDB extraits des résultats
        //  web → relecture TMDB → même porte de corroboration). Le provider
        //  ne s'arrête plus là où le réflexe usager (« je cherche sur le
        //  web ») commençait. Zéro nouvelle config : le gate est la présence
        //  de SearXngUrl (vide = la phase saute silencieusement, même
        //  sémantique que les outils chat) — le repli Ollama cloud de
        //  web_search est ainsi JAMAIS déclenché ici (pas de quota cloud sur
        //  le chemin du dialogue). Fail-open intégral : SearXNG down →
        //  aucun candidat en plus, aucune erreur remontée au dialogue.
        // ------------------------------------------------------------------

        /// <summary>Budget du renfort web (le dialogue attend l'appel REST).</summary>
        internal static readonly TimeSpan s_webBudget = TimeSpan.FromSeconds(8);

        /// <summary>Plafond de relectures TMDB pendant le renfort web.</summary>
        private const int s_maxWebLookups = 3;

        private async Task AddWebRenfortAsync(PluginConfiguration cfg, string kind, string query,
            int? year, List<string> langs, List<CandidateDto> candidates, OrderBox order,
            CancellationToken ct)
        {
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.SearXngUrl)) return;

            string primaryLang = langs.Count > 0 ? langs[0] : "en-US";
            int lookups = 0;
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(s_webBudget);

                    // Requête « titre année » d'abord (miroir S3) ; relance
                    // SANS année uniquement si rien n'est ancré par le titre —
                    // même résilience ±1 an (sortie QC 2021 vs fiche 2020).
                    // web_search est caché 24 h par requête : le rappel est
                    // quasi gratuit.
                    var hits = new List<OrphanResolver.WebIdHit>();
                    string webQuery = year.HasValue
                        ? query + " " + year.Value.ToString(CultureInfo.InvariantCulture)
                        : query;
                    var (anchored, ran) = await RunWebQueryAsync(webQuery, query, hits, cts.Token).ConfigureAwait(false);
                    if (year.HasValue && ran && !anchored)
                        await RunWebQueryAsync(query, query, hits, cts.Token).ConfigureAwait(false);

                    if (hits.Count == 0) return;
                    _logger?.Info("[LLM_AI] Identifier : phase C « {0} » — ids extraits des résultats web : imdb=[{1}] tmdb=[{2}].",
                        query,
                        string.Join(", ", hits.Where(h => string.Equals(h.Source, "imdb_id", StringComparison.Ordinal)).Select(h => h.Id)),
                        string.Join(", ", hits.Where(h => string.Equals(h.Source, "tmdb_id", StringComparison.Ordinal)).Select(h => h.Id)));

                    // Ancrés d'abord : le résultat web qui PORTE le titre du
                    // guide (clé exacte) est le suspect le plus sérieux — son
                    // ancrage EST la preuve de sa fiche (v2.23.1, promotion).
                    // Les hits non ancrés passent la porte (titre exact ou
                    // année saisie) ou finissent en rang queue sans preuve.
                    foreach (var hit in hits.Where(h => h.TitleAnchored).Concat(hits.Where(h => !h.TitleAnchored)))
                    {
                        if (cts.Token.IsCancellationRequested) break;
                        if (lookups >= s_maxWebLookups) break;
                        if (candidates.Any(c => c.Exact)) break;

                        TmdbMeta meta;
                        if (string.Equals(hit.Source, "imdb_id", StringComparison.Ordinal))
                        {
                            lookups++;
                            meta = await _tmdb.FindByExternalIdAsync(hit.Id, "imdb_id", kind, primaryLang, cts.Token).ConfigureAwait(false);
                        }
                        else if (int.TryParse(hit.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId)
                            && tmdbId > 0)
                        {
                            lookups++;
                            meta = await _tmdb.LookupMetaByIdAsync(tmdbId, kind, primaryLang, cts.Token).ConfigureAwait(false);
                        }
                        else continue;

                        if (meta == null) continue;
                        TryAddCorroboratedLlmCandidate(candidates, meta, query, year, order,
                            "web « " + (hit.ResultTitle ?? "—") + " » → " + hit.Source + "=" + hit.Id,
                            source: "web", label: "résultat web", webAnchored: hit.TitleAnchored);
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger?.Info("[LLM_AI] Identifier : renfort web dépassé le budget ({0} s) — candidats TMDB/LLM seuls.",
                    (int)s_webBudget.TotalSeconds);
            }
            catch (Exception ex)
            {
                _logger?.Info("[LLM_AI] Identifier : phase C « {0} » — renfort web en erreur ({1}).",
                    query, ex.Message);
            }
        }

        /// <summary>Une requête SearXNG (miroir exact de
        /// <c>RunAndParseSearXngQueryAsync</c>) : parse les résultats
        /// (réponses, infobox, top 8) et collecte les ids IMDb/TMDB des URLs
        /// et snippets — ancrés par le titre quand celui-ci porte le titre du
        /// guide. Retourne (ancrés présents, réponse exploitable).</summary>
        private async Task<(bool Anchored, bool Ran)> RunWebQueryAsync(string webQuery, string cleanTitle,
            List<OrphanResolver.WebIdHit> hits, CancellationToken token)
        {
            string argJson = JsonSerializer.Serialize(new { query = webQuery });
            string json;
            using (var doc = JsonDocument.Parse(argJson))
            {
                json = await _search.ExecuteAsync(doc.RootElement, token).ConfigureAwait(false);
            }
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger?.Info("[LLM_AI] Identifier : phase C « {0} » — recherche web sans réponse.", webQuery);
                return (false, false);
            }
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
                    {
                        _logger?.Info("[LLM_AI] Identifier : phase C « {0} » — recherche web en erreur ({1}).",
                            webQuery, errEl.GetString());
                        return (false, false);
                    }
                    int anchored = 0;
                    if (root.TryGetProperty("results", out var res) && res.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in res.EnumerateArray())
                        {
                            string rTitle = r.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String
                                ? tEl.GetString()
                                : string.Empty;
                            bool a = OrphanResolver.WebResultMatchesTitle(rTitle, cleanTitle);
                            if (a) anchored++;
                            OrphanResolver.CollectWebHits(r, "url", rTitle, a, hits);
                            OrphanResolver.CollectWebHits(r, "content", rTitle, a, hits);
                        }
                    }
                    if (root.TryGetProperty("infobox", out var ib) && ib.ValueKind == JsonValueKind.String)
                        OrphanResolver.CollectWebHitsFromText(ib.GetString() ?? string.Empty, string.Empty, false, hits);
                    if (root.TryGetProperty("answers", out var ans) && ans.ValueKind == JsonValueKind.Array)
                        foreach (var a2 in ans.EnumerateArray())
                            if (a2.ValueKind == JsonValueKind.String)
                                OrphanResolver.CollectWebHitsFromText(a2.GetString() ?? string.Empty, string.Empty, false, hits);
                    if (hits.Count == 0)
                        _logger?.Info("[LLM_AI] Identifier : phase C « {0} » — aucun id IMDb/TMDB trouvé dans les résultats web.",
                            webQuery);
                    return (anchored > 0, true);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Info("[LLM_AI] Identifier : phase C « {0} » — échec parsing résultats ({1}).",
                    webQuery, ex.Message);
                return (false, false);
            }
        }

        /// <summary>
        /// Porte de corroboration S2 (miroir OrphanResolver, chemin sans
        /// synopsis comparable) : titre EXACT du guide — la fiche « trouvée
        /// par » la proposition n'est jamais une preuve (cercle vicieux, cas
        /// réel 2026-09-20) — OU année concordante : l'année SAISIE par
        /// l'usager (le ParseName d'Emby) est fiable ; l'année du LLM seule
        /// ne prouve rien. Rejets logués, greppables.
        /// <para>v2.23.1, bras web (<paramref name="source"/> = « web ») :
        /// <paramref name="webAnchored"/> — le résultat web porte le titre
        /// du guide (clé exacte, matcher S3) — EST la preuve : elle promeut
        /// la fiche quand le titre de l'œuvre diffère de la requête (cas « La
        /// dernière séance de Freud », titre québécois absent de TMDB) ;
        /// sans aucune preuve, la fiche web est servie sans preuve en rang
        /// queue plutôt que jetée — le dialogue est une liste à choisir.</para>
        /// </summary>
        private bool TryAddCorroboratedLlmCandidate(List<CandidateDto> candidates, TmdbMeta meta,
            string query, int? searchYear, OrderBox order, string arm,
            string source = "llm", string label = "proposition LLM", bool webAnchored = false)
        {
            if (meta == null || meta.TmdbId <= 0) return false;
            if (candidates.Any(x => x.TmdbId == meta.TmdbId)) return false;

            bool exactTitle = OrphanResolver.TitlesExactlyEqual(query, meta.Title);
            bool isWeb = string.Equals(source, "web", StringComparison.Ordinal);
            bool ok = exactTitle || (searchYear.HasValue && meta.Year == searchYear.Value);
            if (!ok && isWeb)
            {
                if (webAnchored)
                {
                    _logger?.Info(
                        "[LLM_AI] Identifier : fiche « {0} » (TMDB {1}, {2}) servie — ancrage web, le résultat porte le titre exact du guide (bras {3}).",
                        meta.Title, meta.TmdbId, meta.Year?.ToString(CultureInfo.InvariantCulture) ?? "?", arm);
                }
                else
                {
                    _logger?.Info(
                        "[LLM_AI] Identifier : résultat web sans preuve servie — fiche « {0} » (TMDB {1}, {2}) en rang queue (titre non exact, non ancrée, année non concordante ; bras {3}).",
                        meta.Title, meta.TmdbId, meta.Year?.ToString(CultureInfo.InvariantCulture) ?? "?", arm);
                }
                ok = true;
            }
            if (!ok)
            {
                _logger?.Info(
                    "[LLM_AI] Identifier : {0} rejetée — fiche « {1} » (TMDB {2}, {3}) sans titre exact ni année concordante (bras {4}).",
                    label, meta.Title, meta.TmdbId, meta.Year?.ToString(CultureInfo.InvariantCulture) ?? "?", arm);
                return false;
            }
            AddMeta(candidates, meta, source, exact: exactTitle, order, webAnchor: webAnchored && isWeb);
            return true;
        }

        // ------------------------------------------------------------------
        //  DTO de cache (liste sérialisée 24 h — usage interne uniquement).
        // ------------------------------------------------------------------

        private sealed class CandidateDto
        {
            public string Title;
            public string OriginalTitle;
            public int? Year;
            public string Overview;
            public string PosterUrl;
            public int TmdbId;
            public string ImdbId;
            public string TvdbId;
            public bool Exact;
            public string Source;
            /// <summary>Ancrage web : un résultat web portant le titre du
            /// guide (clé exacte — matcher S3) corrobore la fiche résolue de
            /// CE résultat (v2.23.1, promotion). Public sérialisé : survit
            /// au cache 24 h.</summary>
            public bool WebAnchor;
            /// <summary>Numéro d'insertion (ordre de pertinence TMDB).</summary>
            public int Order;
            /// <summary>Rang final (calculé avant émission — jamais utilement sérialisé).</summary>
            internal int Rank;
        }

        /// <summary>Rang = paquet de preuve puis ordre d'insertion : id saisi
        /// ou titre exact &gt; ancrage web (résultat portant le titre du
        /// guide) &gt; année concordante &gt; ordre TMDB brut / service web
        /// sans preuve.</summary>
        private static int RankOf(int? searchYear, CandidateDto c)
        {
            if (string.Equals(c.Source, "id", StringComparison.Ordinal) || c.Exact) return c.Order;
            if (c.WebAnchor) return 9000 + c.Order;
            if (searchYear.HasValue && c.Year == searchYear.Value) return 10000 + c.Order;
            return 20000 + c.Order;
        }

        // ------------------------------------------------------------------
        //  Utilitaires.
        // ------------------------------------------------------------------

        /// <summary>Langues uniques, bornées : priorité plugin, langue de
        /// métadonnées résolue par le serveur, en-US.</summary>
        private List<string> BuildSearchLangs(PluginConfiguration cfg, ItemLookupInfo info)
        {
            var langs = new List<string>();
            try
            {
                var prio = I18n.ParseTmdbLangPriority(cfg.TmdbLangPriority ?? string.Empty, out _);
                if (prio != null) langs.AddRange(prio);
            }
            catch { /* config absente → défauts suivants */ }
            string itemLang = info?.MetadataLanguage;
            if (!string.IsNullOrWhiteSpace(itemLang)) langs.Add(itemLang.Trim());
            langs.Add("en-US");
            return langs
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(s_maxSearchLangs)
                .ToList();
        }

        private static bool IsExactTitle(string query, TmdbSearchCandidate c)
        {
            return OrphanResolver.TitlesExactlyEqual(query, c.Title)
                || (!string.IsNullOrWhiteSpace(c.OriginalTitle) && OrphanResolver.TitlesExactlyEqual(query, c.OriginalTitle));
        }

        private void AddMeta(List<CandidateDto> candidates, TmdbMeta meta, string source, bool exact,
            OrderBox order, bool webAnchor = false)
        {
            candidates.Add(new CandidateDto
            {
                Title = meta.Title,
                Year = meta.Year,
                Overview = meta.Overview,
                PosterUrl = meta.PosterUrl,
                TmdbId = meta.TmdbId,
                ImdbId = meta.ImdbId,
                TvdbId = meta.TvdbId,
                Exact = exact,
                Source = source,
                WebAnchor = webAnchor,
                Order = order.N++
            });
        }

        private static List<RemoteSearchResult> MapCandidates(List<CandidateDto> candidates)
        {
            var results = new List<RemoteSearchResult>();
            foreach (var c in candidates.OrderBy(x => x.Rank))
            {
                results.Add(new RemoteSearchResult
                {
                    Name = c.Title,
                    OriginalTitle = c.OriginalTitle,
                    ProductionYear = c.Year,
                    Overview = c.Overview,
                    ImageUrl = c.PosterUrl,
                    SearchProviderName = ProviderName,
                    ProviderIds = BuildProviderIds(c)
                });
            }
            return results;
        }

        private static ProviderIdDictionary BuildProviderIds(CandidateDto c)
        {
            // Valeurs présentes SEULEMENT : une entrée null dans le
            // ProviderIdDictionary remonterait au dialogue (JSON null) et à
            // l'Apply natif (SetProviderIds) — les clés vides n'apportent rien.
            var dict = new ProviderIdDictionary();
            if (c.TmdbId > 0) dict["tmdb"] = c.TmdbId.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(c.ImdbId)) dict["imdb"] = c.ImdbId.Trim();
            if (!string.IsNullOrWhiteSpace(c.TvdbId)) dict["tvdb"] = c.TvdbId.Trim();
            return dict;
        }

        private static string StrProviderId(ItemLookupInfo info, string key)
        {
            try
            {
                if (info?.ProviderIds == null || string.IsNullOrEmpty(key)) return null;
                return info.ProviderIds.TryGetValue(key, out var v) ? v : null;
            }
            catch { return null; }
        }

        private static string SerializeCandidates(List<CandidateDto> candidates)
        {
            return JsonSerializer.Serialize(candidates, s_cacheOpts);
        }

        private static List<CandidateDto> DeserializeCandidates(string json)
        {
            var list = new List<CandidateDto>();
            try
            {
                list = JsonSerializer.Deserialize<List<CandidateDto>>(json, s_cacheOpts) ?? list;
            }
            catch { /* cache illisible → vide (re-exécution live) */ }
            foreach (var c in list) c.Rank = 0; // re-calculé à l'émission
            return list;
        }

        private static readonly System.Text.Json.JsonSerializerOptions s_cacheOpts =
            new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
    }

    /// <summary>
    /// Armement du fournisseur dans les bibliothèques du serveur (v2.23.0).
    /// </summary>
    /// <remarks>
    /// <para><b>Pourquoi</b> : le dialogue Identifier d'un item EXISTANT
    /// (<c>ItemId</c> présent dans la requête, <c>IncludeDisabledProviders</c>
    /// absent) n'interroge que les fournisseurs activés dans la bibliothèque
    /// de l'item (<c>CanRefresh</c> → <c>IsMetadataFetcherEnabled</c>,
    /// décompilé 4.10.2.0) — un fournisseur de plugin n'y entre pas tant
    /// qu'il n'est pas coché. Cet entrypoint arme « LLM_AI » dans chaque
    /// bibliothèque (TypeOptions Movie/Series), de façon idempotente, via le
    /// mécanisme natif <c>CollectionFolder.UpdateLibraryOptions</c> (celui du
    /// service <c>LibraryStructureService</c>). Désarmement symétrique si le
    /// toggle ou la clé TMDB disparaissent.</para>
    /// <para><b>Quand</b> : au démarrage (en fond, bibliothèques pas encore
    /// chargées au <c>Run()</c> — pattern <see cref="RecordingWatcher"/>) et
    /// sur les <c>ItemAdded</c>, borné par une fenêtre anti-repeat
    /// (<see cref="s_recheckInterval"/>, 10 min).</para>
    /// <para>Fail-open intégral : chaque bibliothèque se traite
    /// indépendamment et toute erreur est consignée, jamais levée.</para>
    /// </remarks>
    public class AiIdentifyLibraryArmer : IServerEntryPoint
    {
        private readonly ILibraryManager _library;
        private readonly ILogger _logger;

        /// <summary>Fenêtre anti-repeat entre deux armements.</summary>
        private static readonly TimeSpan s_recheckInterval = TimeSpan.FromMinutes(10);

        /// <summary>Délai d'armement au démarrage (bibliothèques chargées).</summary>
        private static readonly TimeSpan s_startupDelay = TimeSpan.FromMinutes(2);

        private long _armUntil; // Environment.TickCount64 de la prochaine fenêtre permise

        public AiIdentifyLibraryArmer(ILibraryManager library, ILogger logger)
        {
            _library = library;
            _logger = logger;
        }

        public void Run()
        {
            try
            {
                _library.ItemAdded += OnItemAdded;
                var _ = Task.Run(RunStartupArmAsync);
            }
            catch { /* fail-open : l'armement est un confort, jamais un risque */ }
        }

        public void Dispose()
        {
            try { _library.ItemAdded -= OnItemAdded; } catch { /* best-effort */ }
        }

        private async Task RunStartupArmAsync()
        {
            try
            {
                await Task.Delay(s_startupDelay).ConfigureAwait(false);
                await EnsureArmedAsync().ConfigureAwait(false);
            }
            catch { /* fail-open */ }
        }

        private void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            EnsureSoon();
        }

        /// <summary>Armement différé borné : au plus une tentative par
        /// <c>s_recheckInterval</c>, jamais en concurrence.</summary>
        private void EnsureSoon()
        {
            try
            {
                var now = Environment.TickCount64;
                var target = Interlocked.Read(ref _armUntil);
                if (now < target) return;
                if (Interlocked.CompareExchange(ref _armUntil,
                        Interlocked.Read(ref _armUntil) + (long)s_recheckInterval.TotalMilliseconds, target) != target)
                    return;
                var _ = Task.Run(EnsureArmedAsync);
            }
            catch { /* fail-open */ }
        }

        /// <summary>Passe d'armement idempotente — ne lève jamais.</summary>
        internal async Task<bool> EnsureArmedAsync()
        {
            await Task.Yield();
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                bool armed = cfg != null && cfg.IdentifyAiSearchEnabled
                             && !string.IsNullOrWhiteSpace(cfg.TmdbApiKey);
                var folders = _library?.GetVirtualFolders();
                if (folders == null || folders.Count == 0) return armed;

                foreach (var vf in folders)
                {
                    try
                    {
                        if (armed) ArmSingle(vf); else DisarmSingle(vf);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Info("[LLM_AI] Identifier : bibliothèque « {0} » non traitée — {1} : {2}",
                            vf?.Name ?? "?", ex.GetType().Name, ex.Message);
                    }
                }
                return armed;
            }
            catch (Exception ex)
            {
                _logger?.Info("[LLM_AI] Identifier : passe d'armement échouée — {0} : {1}",
                    ex.GetType().Name, ex.Message);
                return false;
            }
        }

        private void ArmSingle(VirtualFolderInfo vf)
        {
            var cf = ResolveCollectionFolder(vf);
            if (cf == null) return;
            var opts = cf.GetLibraryOptions();
            if (opts?.TypeOptions == null) return;
            bool changed = false;
            foreach (var to in opts.TypeOptions)
            {
                if (!IsRelevantType(to?.Type)) continue;
                bool hasFetcher = (to.MetadataFetchers ?? Array.Empty<string>())
                    .Any(n => string.Equals(n, AiIdentifySearchCore.ProviderName, StringComparison.OrdinalIgnoreCase));
                bool hasOrder = (to.MetadataFetcherOrder ?? Array.Empty<string>())
                    .Any(n => string.Equals(n, AiIdentifySearchCore.ProviderName, StringComparison.OrdinalIgnoreCase));
                if (hasFetcher && hasOrder) continue;
                changed = true;
                if (!hasFetcher)
                    to.MetadataFetchers = (to.MetadataFetchers ?? Array.Empty<string>())
                        .Concat(new[] { AiIdentifySearchCore.ProviderName }).ToArray();
                if (!hasOrder)
                    to.MetadataFetcherOrder = (to.MetadataFetcherOrder ?? Array.Empty<string>())
                        .Concat(new[] { AiIdentifySearchCore.ProviderName }).ToArray();
            }
            if (changed)
            {
                cf.UpdateLibraryOptions(opts);
                _logger?.Info("[LLM_AI] Identifier : fournisseur « {0} » activé dans la bibliothèque « {1} » (Movie/Series).",
                    AiIdentifySearchCore.ProviderName, vf.Name ?? "?");
            }
        }

        private void DisarmSingle(VirtualFolderInfo vf)
        {
            var cf = ResolveCollectionFolder(vf);
            if (cf == null) return;
            var opts = cf.GetLibraryOptions();
            if (opts?.TypeOptions == null) return;
            bool changed = false;
            foreach (var to in opts.TypeOptions)
            {
                if (!IsRelevantType(to?.Type)) continue;
                bool hasFetcher = (to.MetadataFetchers ?? Array.Empty<string>())
                    .Any(n => string.Equals(n, AiIdentifySearchCore.ProviderName, StringComparison.OrdinalIgnoreCase));
                bool hasOrder = (to.MetadataFetcherOrder ?? Array.Empty<string>())
                    .Any(n => string.Equals(n, AiIdentifySearchCore.ProviderName, StringComparison.OrdinalIgnoreCase));
                if (!hasFetcher && !hasOrder) continue;
                changed = true;
                if (hasFetcher)
                    to.MetadataFetchers = (to.MetadataFetchers ?? Array.Empty<string>())
                        .Where(n => !string.Equals(n, AiIdentifySearchCore.ProviderName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (hasOrder)
                    to.MetadataFetcherOrder = (to.MetadataFetcherOrder ?? Array.Empty<string>())
                        .Where(n => !string.Equals(n, AiIdentifySearchCore.ProviderName, StringComparison.OrdinalIgnoreCase)).ToArray();
            }
            if (changed)
            {
                cf.UpdateLibraryOptions(opts);
                _logger?.Info("[LLM_AI] Identifier : fournisseur « {0} » retiré de la bibliothèque « {1} ».",
                    AiIdentifySearchCore.ProviderName, vf.Name ?? "?");
            }
        }

        private CollectionFolder ResolveCollectionFolder(VirtualFolderInfo vf)
        {
            // Devise canonique : VirtualFolderInfo.ItemId = InternalId en
            // chaîne ; repli Guid hérité (ItemIdResolver : long d'abord,
            // Guid ensuite).
            return (ItemIdResolver.Resolve(_library, vf?.ItemId)
                ?? ItemIdResolver.Resolve(_library, string.IsNullOrWhiteSpace(vf?.Guid) ? vf?.Id : vf.Guid)
                ) as CollectionFolder;
        }

        /// <summary>Types portés par le fournisseur (film + série uniquement).</summary>
        private static bool IsRelevantType(string typeName)
        {
            return string.Equals(typeName, "Movie", StringComparison.OrdinalIgnoreCase)
                || string.Equals(typeName, "Series", StringComparison.OrdinalIgnoreCase);
        }
    }
}
