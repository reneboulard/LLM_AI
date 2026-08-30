using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;

namespace LLM_AI
{
    /// <summary>
    /// Outil natif read-only « get_emby_info » : accède directement aux
    /// services internes Emby (DI, in-process) — pas d'API REST, pas de
    /// token. Dispatch sur <c>action</c> et ne retourne que le JSON minimal
    /// demandé (projection explicite, équivalent du « prune » PHP).
    /// </summary>
    public class GetEmbyInfoTool : ILlmTool
    {
        private readonly ILibraryManager _library;
        private readonly IUserManager _users;
        private readonly ILiveTvManager _liveTv;
        private readonly IServerApplicationHost _host;
        private readonly ILogger _logger;

        public string Name => "get_emby_info";

        public string Description =>
            "Interroge la bibliothèque Emby et l'EPG (lecture seule). Retourne du JSON minimal. " +
            "Actions : summary, library, global_search, item_details, item_persons, person, " +
            "epg_series, epg_movies, epg_tonight, scheduled, planning.";

        // Le schéma est injecté dans le system prompt (bloc AVAILABLE TOOLS).
        public string ArgumentsSchema => @"{
  ""action"": ""summary | library | global_search | item_details | item_persons | person | epg_series | epg_movies | epg_tonight | scheduled"",
  ""type"": ""(library) movie | series | episode | audio | album | book — filtre IncludeItemTypes"",
  ""query"": ""(global_search) terme de recherche"",
  ""name"": ""(person) nom de personne à chercher"",
  ""id"": ""(item_details / item_persons) identifiant de l'item (id renvoyé par library/global_search)"",
  ""genre"": ""(library) filtre par genre"",
  ""sort_by"": ""(library) recent | year | rating | name (défaut: recent)"",
  ""min_rating"": ""(library) note communautaire minimale"",
  ""types"": ""(global_search) tableau de types à restreindre"",
  ""premieres_only"": ""(epg_series) true pour ne garder que les S01E01 (nouvelles séries). Exclut kids/news sauf si les flags correspondants sont activés en config ; exclut documentary (sauf exclude_genres) et les séries de la biblio"",
  ""new_seasons"": ""(epg_series) true pour le mode « séries absentes » d'emby-absent-series.sh : garde les nouvelles saisons de séries déjà possédées (is_new_season=true), n'exclut que les timers, conserve les kids"",
  ""exclude_genres"": ""(epg_series / epg_movies / epg_tonight) genres à exclure. Défaut epg_series premieres_only: [""documentary"", ""news""] ; sinon []"",
  ""limit"": ""nombre max de résultats retournés. epg_* : plafond dur côté serveur (défaut config MaxSeriesBatch/MaxMovieBatch/MaxTonightBatch, après pré-tri par pertinence) ; tu peux demander moins"",
  ""offset"": ""(library) indice de pagination (défaut 0)""
}";

        private static readonly JsonSerializerOptions s_json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public GetEmbyInfoTool(
            ILibraryManager library,
            IUserManager users,
            ILiveTvManager liveTv,
            IServerApplicationHost host,
            ILogger logger)
        {
            _library = library;
            _users = users;
            _liveTv = liveTv;
            _host = host;
            _logger = logger;
        }

        /// <summary>URL publique Emby pour construire les image_url.</summary>
        private string EmbyUrl => Plugin.Instance?.Configuration?.EmbyPublicUrl;

        public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            // Garde-fou : arguments optionnels (le LLM peut omettre le bloc).
            if (args.ValueKind != JsonValueKind.Object)
                args = default;

            string action = OptString(args, "action") ?? "summary";
            try
            {
                string result;
                switch (action.ToLowerInvariant())
                {
                    case "summary":       result = Summary(args); break;
                    case "library":       result = Library(args); break;
                    case "global_search": result = GlobalSearch(args); break;
                    case "item_details":  result = ItemDetails(args); break;
                    case "item_persons":   result = ItemPersons(args); break;
                    case "person":         result = Person(args); break;
                    case "epg_series":    result = EpgSeries(args); break;
                    case "epg_movies":    result = EpgMovies(args); break;
                    case "epg_tonight":   result = EpgTonight(args); break;
                    case "scheduled":     result = Scheduled(); break;
                    case "planning":      result = Scheduled(); break;
                    default:
                        result = Err($"action inconnue : {action}");
                        break;
                }
                _logger?.Info("[LLM_AI] get_emby_info action={0} -> {1}", action, Truncate(result, 200));
                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] get_emby_info action={0} a levé : {1}", ex, action, ex.Message);
                return Task.FromResult(Err(ex.Message));
            }
        }

        // ------------------------------------------------------------------
        //  Actions
        // ------------------------------------------------------------------

        private string Summary(JsonElement args)
        {
            int movies   = Count("Movie");
            int series    = Count("Series");
            int episodes  = Count("Episode");
            int albums    = Count("MusicAlbum");
            int songs     = Count("Audio");
            var userNames = _users.GetUserList(new UserQuery())
                                  .Select(u => u.Name).Where(n => !string.IsNullOrEmpty(n)).ToArray();

            var result = new
            {
                server = new
                {
                    name = _host.FriendlyName,
                    port = _host.HttpPort,
                    version = _host.AvailableVersion?.ToString()
                },
                library = new { movies, series, episodes, albums, songs },
                users = userNames
            };
            return JsonSerializer.Serialize(result, s_json);
        }

        private string Library(JsonElement args)
        {
            string type = OptString(args, "type") ?? "movie";
            int limit = OptInt(args, "limit", 20);
            int offset = OptInt(args, "offset", 0);
            string genre = OptString(args, "genre");
            string search = OptString(args, "search");
            string sortBy = (OptString(args, "sort_by") ?? "recent").ToLowerInvariant();
            double? minRating = OptDouble(args, "min_rating");

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { MapType(type) },
                Recursive = true,
                Limit = null,           // on récupère tout, on trie en C#, on pagine ensuite
                EnableTotalRecordCount = false
            };
            if (!string.IsNullOrEmpty(genre))   query.Genres = new[] { genre };
            if (!string.IsNullOrEmpty(search))  query.SearchTerm = search;
            if (minRating.HasValue)             query.MinCommunityRating = minRating.Value;

            var items = _library.GetItemList(query) ?? Array.Empty<BaseItem>();
            items = SortItems(items, sortBy);
            var page = items.Skip(offset).Take(Math.Max(1, limit));

            var proj = page.Select(i => new
            {
                id = i.InternalId.ToString(),
                name = i.Name,
                type = TypeLabel(i),
                year = i.ProductionYear,
                rating = i.CommunityRating,
                genres = i.Genres,
                overview = Truncate(i.Overview, 150),
                image_url = ImageUrl(i.InternalId)
            });

            return JsonSerializer.Serialize(new { total = items.Length, results = proj }, s_json);
        }

        private string GlobalSearch(JsonElement args)
        {
            string query = OptString(args, "query");
            if (string.IsNullOrWhiteSpace(query))
                return Err("paramètre 'query' requis pour global_search");

            int limit = OptInt(args, "limit", 20);
            var types = OptStringArray(args, "types");

            var q = new InternalItemsQuery
            {
                SearchTerm = query,
                Recursive = true,
                Limit = limit
            };
            if (types != null && types.Length > 0)
                q.IncludeItemTypes = types.Select(MapType).Distinct().ToArray();

            var items = _library.GetItemList(q) ?? Array.Empty<BaseItem>();

            var proj = items.Select(i => new
            {
                id = i.InternalId.ToString(),
                name = i.Name,
                type = TypeLabel(i),
                year = i.ProductionYear,
                overview = Truncate(i.Overview, 100),
                image_url = ImageUrl(i.InternalId)
            });

            return JsonSerializer.Serialize(new { total = items.Length, results = proj }, s_json);
        }

        private string ItemDetails(JsonElement args)
        {
            string idStr = OptString(args, "id");
            if (string.IsNullOrWhiteSpace(idStr))
                return Err("paramètre 'id' requis pour item_details");

            // Id interne (InternalId, forme renvoyée par library/global_search)
            // ou Guid hérité — cf. ItemIdResolver.
            var item = ItemIdResolver.Resolve(_library, idStr);
            if (item == null)
                return Err($"item introuvable : {idStr}");

            var result = new
            {
                id = item.InternalId.ToString(),
                name = item.Name,
                type = TypeLabel(item),
                year = item.ProductionYear,
                rating = item.CommunityRating,
                genres = item.Genres,
                overview = item.Overview,
                official_rating = item.OfficialRating,
                path = item.Path,
                date_created = item.DateCreated,
                image_url = ImageUrl(item.InternalId)
            };
            return JsonSerializer.Serialize(result, s_json);
        }

        private string ItemPersons(JsonElement args)
        {
            string idStr = OptString(args, "id");
            if (string.IsNullOrWhiteSpace(idStr))
                return Err("paramètre 'id' requis pour item_persons");

            // Id interne (InternalId) ou Guid hérité — cf. ItemIdResolver.
            var item = ItemIdResolver.Resolve(_library, idStr);
            if (item == null)
                return Err($"item introuvable : {idStr}");

            var people = _library.GetItemPeople(item) ?? new List<PersonInfo>();
            var proj = people.Select(p => new
            {
                name = p.Name,
                role = p.Role,
                type = p.Type.ToString(),
                image_url = !string.IsNullOrEmpty(p.ImageUrl) ? p.ImageUrl : null
            });
            return JsonSerializer.Serialize(new { results = proj }, s_json);
        }

        private string Person(JsonElement args)
        {
            string name = OptString(args, "name");
            if (string.IsNullOrWhiteSpace(name))
                return Err("paramètre 'name' requis pour person");

            int limit = OptInt(args, "limit", 20);

            // GetPeople renvoie QueryResult<Tuple<BaseItem, ItemCounts>> :
            // Item1 = la personne (BaseItem), Item2 = ses compteurs (films/séries…).
            var q = new InternalItemsQuery { SearchTerm = name, Limit = limit };
            var res = _library.GetPeople(q);
            var tuples = res?.Items ?? Array.Empty<Tuple<BaseItem, ItemCounts>>();

            var proj = tuples.Select(t =>
            {
                var person = t.Item1;
                var counts = t.Item2;
                return new
                {
                    // Item1 est un BaseItem : on émet son InternalId (long) —
                    // la seule forme consommable par la couche REST/UI (son Id
                    // Guid est rejeté par /emby/Items/{id}/...).
                    id = person?.InternalId.ToString(),
                    name = person?.Name,
                    movie_count = counts?.MovieCount,
                    series_count = counts?.SeriesCount,
                    image_url = person != null ? ImageUrl(person.InternalId) : null
                };
            });
            return JsonSerializer.Serialize(new { total = res?.TotalRecordCount ?? tuples.Length, results = proj }, s_json);
        }

        private string Scheduled()
        {
            // Series timers (enregistrements récurrents) + single timers programmés.
            var st = _liveTv.GetSeriesTimers(new SeriesTimerQuery());
            var seriesTimers = st?.Items ?? Array.Empty<SeriesTimerInfoDto>();
            var tt = _liveTv.GetTimers(new TimerQuery { IsScheduled = true });
            var timers = tt?.Items ?? Array.Empty<TimerInfoDto>();

            var seriesProj = seriesTimers.Select(s => new
            {
                name = s.Name,
                channel = s.ChannelName
            });
            var singleProj = timers.Select(d => new
            {
                name = !string.IsNullOrEmpty(d.ProgramInfo?.SeriesName) ? d.ProgramInfo.SeriesName : d.ProgramInfo?.Name,
                channel = d.ProgramInfo?.ChannelName,
                start = d.ProgramInfo?.StartDate,
                status = d.Status.ToString(),
                is_series = d.ProgramInfo?.IsSeries
            });
            return JsonSerializer.Serialize(new
            {
                series_timers = seriesProj,
                single_timers = singleProj
            }, s_json);
        }

        // ------------------------------------------------------------------
        //  EPG : programmes à venir absents de la bibliothèque (diff fait ici)
        //  Reproduit la logique des scripts /usr/local/bin/emby-absent*.sh et
        //  emby-ai-suggest.sh, mais en C# in-process (pas de REST/jq).
        // ------------------------------------------------------------------

        /// <summary>
        /// Séries de l'EPG à venir. Deux modes, calqués sur les scripts bash :
        ///
        /// <list type="bullet">
        /// <item><c>premieres_only=true</c> — reproduit <c>emby-ai-suggest.sh</c> :
        ///   S01E01 uniquement, exclut kids/news/documentary, exclut les séries
        ///   de la bibliothèque + series timers + single timers. Sert à détecter
        ///   les <b>nouvelles séries d'intérêt</b> pour la recommandation IA.</item>
        /// <item><c>new_seasons=true</c> — reproduit <c>emby-absent-series.sh</c> :
        ///   garde les séries absentes <i>et</i> les nouvelles saisons de séries
        ///   déjà possédées (détectées via les Season de la biblio, marquées
        ///   <c>is_new_season=true</c>). N'exclut que les timers (pas la biblio).
        ///   Sert à la liste « séries absentes / nouvelles saisons ».</item>
        /// <item>défaut — séries à venir absentes de la biblio, exclusion simple
        ///   (séries biblio + series timers + single timers).</item>
        /// </list>
        /// </summary>
        private string EpgSeries(JsonElement args)
        {
            bool premieresOnly = OptBool(args, "premieres_only", false);
            bool newSeasons = OptBool(args, "new_seasons", false);

            // Défaut des genres exclus : seulement en mode premieres_only
            // (emby-ai-suggest.sh exclut news + documentary). Sinon aucun.
            string[] defGenres = premieresOnly ? new[] { "documentary", "news" } : Array.Empty<string>();
            var excludeGenres = NormGenreSet(OptStringArray(args, "exclude_genres") ?? defGenres);

            // Plafond dur côté serveur : le LLM peut demander moins (limit),
            // jamais plus que MaxSeriesBatch. Le pool Emby est plus large (POOL)
            // pour donner matière au pré-tri par pertinence avant le cap.
            var cfg = Plugin.Instance?.Configuration;
            int maxBatch = Math.Max(1, cfg?.MaxSeriesBatch ?? 40);
            int limit = Math.Min(OptInt(args, "limit", maxBatch), maxBatch);
            const int POOL = 300;

            // Flags orthogonaux (kids/news/sports) pour les séries — opt-in.
            var flags = LoadFlags(series: true);

            var q = new InternalItemsQuery
            {
                IsSeries = true,
                HasAired = false,          // à venir (équivalent EndDate > now)
                Limit = POOL
            };
            if (premieresOnly)
            {
                // emby-ai-suggest.sh : S01E01 uniquement. IsKids/IsNews exclus
                // SAUF si le flag correspondant est activé (sinon aucun kids/news
                // n'atteindrait jamais le post-filtre PassesFlags).
                q.IsKids = flags.Contains("kids") ? null : (bool?)false;
                q.IsNews = flags.Contains("news") ? null : (bool?)false;
                q.ParentIndexNumber = 1;
                q.IndexNumber = 1;
            }
            // new_seasons : pas de filtre Kids/News (emby-absent-series.sh ne filtre pas).

            var programs = (_liveTv.GetPrograms(q)?.Items) ?? Array.Empty<BaseItemDto>();

            // GetPrograms renvoie des DTO SANS Genres peuplé (l'API REST exige
            // Fields=Genres). On enrichit depuis les BaseItem (LiveTvProgram) en
            // une requête library : BuildGenreMap retourne Id -> Genres.
            var genreMap = BuildGenreMap(q);

            // Ensembles d'exclusion selon le mode.
            HashSet<string> excluded;
            Dictionary<string, HashSet<int>> ownedSeasons = null;
            if (newSeasons)
            {
                // emby-absent-series.sh : on n'exclut QUE les timers (la biblio
                // sert à détecter les nouvelles saisons, pas à exclure).
                excluded = TimerNamesOnly();
                ownedSeasons = OwnedSeasonsMap();
            }
            else
            {
                // emby-ai-suggest.sh / défaut : biblio Series + series timers + single timers.
                excluded = ExcludedNames("Series", addSeriesTimers: true, addSingleTimers: true);
            }

            // Drop list persistante (bouton « Oublier » / page de config) : on retire
            // ces titres de la liste envoyée au LLM (épuration en amont).
            excluded.UnionWith(DroppedTitlesSet());

            // Whitelists chaines/genres (inclusion) — set vide = pas de filtre.
            var wl = LoadWhitelists();
            if (cfg?.DebugVerbose ?? false)
            {
                _logger?.Info("[LLM_AI] epg_series whitelists: channels={0} genres={1}",
                    wl.Channels?.Count ?? 0, wl.Genres?.Count ?? 0);
                if (wl.Any)
                    _logger?.Info("[LLM_AI] epg_series wl channels=[{0}] genres=[{1}]",
                        wl.Channels == null ? "" : string.Join(",", wl.Channels),
                        wl.Genres == null ? "" : string.Join(",", wl.Genres));
            }

            var seen = new HashSet<string>();
            var kept = new List<(BaseItemDto p, bool? isNewSeason, string[] genres)>();
            int wlFiltered = 0, flagRejected = 0, wlRejected = 0;
            var flagSamples = new List<string>();
            var wlSamples = new List<string>();
            foreach (var p in programs.OrderBy(x => x.StartDate ?? DateTimeOffset.MaxValue))
            {
                var title = !string.IsNullOrEmpty(p.SeriesName) ? p.SeriesName : p.Name;
                if (string.IsNullOrEmpty(title)) continue;
                var key = Norm(title);
                if (excluded.Contains(key)) continue;
                if (!seen.Add(key)) continue;              // dédupliquer par série (unique_by)
                var genres = GenreFor(p, genreMap);        // genres enrichis (BaseItem)
                if (IsExcludedGenre(genres, excludeGenres)) continue;
                if (wl.Any && !PassesWhitelists(p, genres, wl))
                {
                    wlFiltered++; wlRejected++;
                    if (wlSamples.Count < 8)
                        wlSamples.Add("{" + title + " ch=" + Norm(p.ChannelName ?? "") +
                                       " chId=" + (p.ChannelId ?? "?") +
                                       " g=[" + (genres == null ? "" : string.Join("/", genres)) + "]}");
                    continue;
                }
                if (!PassesFlags(p, flags))
                {
                    wlFiltered++; flagRejected++;
                    if (flagSamples.Count < 6)
                        flagSamples.Add("{" + title + " K=" + (p.IsKids == true) + " N=" + (p.IsNews == true) +
                                        " S=" + (p.IsSports == true) + " Ser=" + (p.IsSeries == true) + "}");
                    continue;
                }

                // Mode new_seasons : exclusion fine par saison possédée.
                bool? isNewSeason = null;
                if (newSeasons && ownedSeasons != null)
                {
                    int epSeason = p.ParentIndexNumber ?? 1;
                    if (ownedSeasons.TryGetValue(key, out var owned) && owned.Count > 0)
                    {
                        if (owned.Contains(epSeason)) continue;   // saison déjà possédée
                        isNewSeason = true;                        // nouvelle saison d'une série possédée
                    }
                    // sinon : série absente de la biblio -> gardée, is_new_season=null/false
                }
                kept.Add((p, newSeasons ? isNewSeason : null, genres));
            }

            // Pré-tri par pertinence (note + genre préféré + synopsis), cap,
            // puis re-tri chronologique pour la lisibilité.
            var picked = kept
                .OrderByDescending(t => RelevanceScore(t.p, t.genres, wl))
                .Take(limit)
                .OrderBy(t => t.p.StartDate ?? DateTimeOffset.MaxValue)
                .ToList();

            var results = new List<object>();
            foreach (var t in picked)
            {
                var p = t.p;
                var title = !string.IsNullOrEmpty(p.SeriesName) ? p.SeriesName : p.Name;
                results.Add(new
                {
                    title,
                    id = p.Id,
                    channel_id = p.ChannelId,
                    overview = Truncate(p.Overview, 300),
                    genres = t.genres ?? Array.Empty<string>(),
                    channel = p.ChannelName,
                    channel_number = p.ChannelNumber,
                    start = p.StartDate,
                    end = p.EndDate,
                    rating = p.CommunityRating,
                    year = p.ProductionYear,
                    season = p.ParentIndexNumber,
                    episode = p.IndexNumber,
                    episode_title = p.EpisodeTitle,
                    is_new_season = t.isNewSeason
                });
            }
            _logger?.Info("[LLM_AI] epg_series : pool filtré {0} → cap {1} retenu(s) (whitelists/flags : {2} rejeté(s), plafond {3}).",
                kept.Count, results.Count, wlFiltered, limit);
            if ((cfg?.DebugVerbose ?? false) && wlRejected > 0)
                _logger?.Info("[LLM_AI] epg_series wl rejetés={0}, échantillons : {1}",
                    wlRejected, string.Join(" | ", wlSamples));
            if ((cfg?.DebugVerbose ?? false) && flagRejected > 0)
                _logger?.Info("[LLM_AI] epg_series flags rejetés={0}, échantillons : {1}",
                    flagRejected, string.Join(" | ", flagSamples));
            return JsonSerializer.Serialize(new { total = results.Count, results }, s_json);
        }

        /// <summary>
        /// Films de l'EPG à venir, absents de la bibliothèque et non déjà
        /// programmés (single timers). Reproduit <c>emby-absent.sh</c> :
        /// IsMovie=true (pas de filtre Kids/News), aucun genre exclu par défaut,
        /// exclusion = films biblio + single timers, tri par date, dédupliquer.
        /// </summary>
        private string EpgMovies(JsonElement args)
        {
            // emby-absent.sh n'exclut aucun genre par défaut.
            var excludeGenres = NormGenreSet(OptStringArray(args, "exclude_genres") ?? Array.Empty<string>());

            // Plafond dur côté serveur + pool élargi pour le pré-tri.
            var cfg = Plugin.Instance?.Configuration;
            int maxBatch = Math.Max(1, cfg?.MaxMovieBatch ?? 30);
            int limit = Math.Min(OptInt(args, "limit", maxBatch), maxBatch);
            const int POOL = 300;

            // Flags orthogonaux (kids/news/sports) pour les films — opt-in.
            var flags = LoadFlags(series: false);

            var q = new InternalItemsQuery
            {
                IsMovie = true,
                HasAired = false,
                Limit = POOL
            };
            var programs = (_liveTv.GetPrograms(q)?.Items) ?? Array.Empty<BaseItemDto>();
            // GetPrograms ne peuple pas Genres sur le DTO ; on enrichit depuis
            // les BaseItem (LiveTvProgram) via une requête library (Id -> Genres).
            var genreMap = BuildGenreMap(q);
            var excluded = ExcludedNames("Movie", addSeriesTimers: false, addSingleTimers: true);
            // Drop list persistante : retire ces titres de la liste envoyée au LLM.
            excluded.UnionWith(DroppedTitlesSet());

            // Whitelists chaines/genres (inclusion) — set vide = pas de filtre.
            var wl = LoadWhitelists();
            if (cfg?.DebugVerbose ?? false)
            {
                _logger?.Info("[LLM_AI] epg_movies whitelists: channels={0} genres={1}",
                    wl.Channels?.Count ?? 0, wl.Genres?.Count ?? 0);
                if (wl.Any)
                    _logger?.Info("[LLM_AI] epg_movies wl channels=[{0}] genres=[{1}]",
                        wl.Channels == null ? "" : string.Join(",", wl.Channels),
                        wl.Genres == null ? "" : string.Join(",", wl.Genres));
            }

            var seen = new HashSet<string>();
            var kept = new List<(BaseItemDto p, string[] genres)>();
            int wlFiltered = 0, flagRejected = 0, wlRejected = 0;
            var flagSamples = new List<string>();
            var wlSamples = new List<string>();
            foreach (var p in programs.OrderBy(x => x.StartDate ?? DateTimeOffset.MaxValue))
            {
                var title = p.Name;
                if (string.IsNullOrEmpty(title)) continue;
                var key = Norm(title);
                if (excluded.Contains(key)) continue;
                if (!seen.Add(key)) continue;
                var genres = GenreFor(p, genreMap);        // genres enrichis (BaseItem)
                if (IsExcludedGenre(genres, excludeGenres)) continue;
                if (wl.Any && !PassesWhitelists(p, genres, wl))
                {
                    wlFiltered++; wlRejected++;
                    if (wlSamples.Count < 8)
                        wlSamples.Add("{" + title + " ch=" + Norm(p.ChannelName ?? "") +
                                       " chId=" + (p.ChannelId ?? "?") +
                                       " g=[" + (genres == null ? "" : string.Join("/", genres)) + "]}");
                    continue;
                }
                if (!PassesFlags(p, flags))
                {
                    wlFiltered++; flagRejected++;
                    if (flagSamples.Count < 6)
                        flagSamples.Add("{" + title + " K=" + (p.IsKids == true) + " N=" + (p.IsNews == true) +
                                        " S=" + (p.IsSports == true) + " M=" + (p.IsMovie == true) + "}");
                    continue;
                }
                kept.Add((p, genres));
            }
            if ((cfg?.DebugVerbose ?? false) && wlRejected > 0)
                _logger?.Info("[LLM_AI] epg_movies wl rejetés={0}, échantillons : {1}",
                    wlRejected, string.Join(" | ", wlSamples));
            if ((cfg?.DebugVerbose ?? false) && flagRejected > 0)
                _logger?.Info("[LLM_AI] epg_movies flags rejetés={0}, échantillons : {1}",
                    flagRejected, string.Join(" | ", flagSamples));

            // Pré-tri par pertinence, cap, puis re-tri chronologique.
            var picked = kept
                .OrderByDescending(t => RelevanceScore(t.p, t.genres, wl))
                .Take(limit)
                .OrderBy(t => t.p.StartDate ?? DateTimeOffset.MaxValue)
                .ToList();

            var results = new List<object>();
            foreach (var t in picked)
            {
                var p = t.p;
                results.Add(new
                {
                    title = p.Name,
                    id = p.Id,
                    channel_id = p.ChannelId,
                    overview = Truncate(p.Overview, 300),
                    genres = t.genres ?? Array.Empty<string>(),
                    channel = p.ChannelName,
                    channel_number = p.ChannelNumber,
                    start = p.StartDate,
                    end = p.EndDate,
                    rating = p.CommunityRating,
                    year = p.ProductionYear
                });
            }
            _logger?.Info("[LLM_AI] epg_movies : pool filtré {0} → cap {1} retenu(s) (whitelists/flags : {2} rejeté(s), plafond {3}).",
                kept.Count, results.Count, wlFiltered, limit);
            return JsonSerializer.Serialize(new { total = results.Count, results }, s_json);
        }

        /// <summary>
        /// Programmes de l'EPG pour « ce soir » : fenêtre temporelle bornée par
        /// <see cref="PluginConfiguration.TonightWindowStart"/> /
        /// <see cref="PluginConfiguration.TonightWindowEnd"/> (défaut : maintenant
        /// → 23:59), tous types confondus (séries ET films, pas de filtre
        /// IsSeries/IsMovie), <c>HasAired=false</c>. Contrairement à
        /// <see cref="EpgSeries"/>/<see cref="EpgMovies"/> :
        /// <list type="bullet">
        /// <item>la bibliothèque n'est PAS exclue (un film qu'on possède mais qui
        ///   passe ce soir = à regarder, pas à enregistrer — l'LLM décidera).</item>
        /// <item>les timers ne sont pas exclus mais marqués <c>is_scheduled=true</c>
        ///   (un programme déjà enregistré reste recommandable « à regarder en
        ///   direct »).</item>
        /// <item>les flags Kids/News/Sports fusionnent séries + films (un programme
        ///   kid passe si l'un des deux flags est activé).</item>
        /// </list>
        /// Même forme de retour que epg_series/epg_movies + <c>is_series</c>/
        /// <c>is_movie</c>/<c>is_scheduled</c> pour que l'LLM positionne
        /// <c>kind</c> et que l'UI sache si un timer existe. Plafond
        /// <see cref="PluginConfiguration.MaxTonightBatch"/> après pré-tri par
        /// pertinence.
        /// </summary>
        private string EpgTonight(JsonElement args)
        {
            var excludeGenres = NormGenreSet(OptStringArray(args, "exclude_genres") ?? Array.Empty<string>());

            var cfg = Plugin.Instance?.Configuration;
            int maxBatch = Math.Max(1, cfg?.MaxTonightBatch ?? 10);
            int limit = Math.Min(OptInt(args, "limit", maxBatch), maxBatch);
            const int POOL = 300;

            // Fenêtre temporelle « ce soir » (HH:mm, heure locale). Défaut :
            // maintenant → 23:59. MinStartDate/MaxStartDate sont DateTimeOffset?.
            DateTimeOffset minStart, maxStart;
            {
                var now = DateTimeOffset.Now;
                var today = now.Date;
                minStart = TryParseHHmm(cfg?.TonightWindowStart, out var st)
                    ? new DateTimeOffset(today.Add(st), now.Offset)
                    : now;
                // Fin de fenêtre : ce soir 23:59 (défaut), ou l'heure configurée.
                // Si l'heure de fin est avant l'heure de début (ex. 01:00 pour
                // veiller tard), on la reporte au lendemain.
                var endStr = cfg?.TonightWindowEnd;
                if (string.IsNullOrWhiteSpace(endStr)) endStr = "23:59";
                if (TryParseHHmm(endStr, out var et))
                {
                    var endDt = today.Add(et);
                    if (endDt < minStart) endDt = endDt.AddDays(1);
                    maxStart = new DateTimeOffset(endDt, now.Offset);
                }
                else
                {
                    maxStart = new DateTimeOffset(today.AddHours(23).AddMinutes(59), now.Offset);
                }
            }

            // Flags fusionnés séries + films : un programme kid/news/sports passe
            // si l'un OU l'autre des flags catégorie est activé.
            var flags = LoadFlags(series: true);
            foreach (var f in LoadFlags(series: false))
                flags.Add(f);

            var q = new InternalItemsQuery
            {
                HasAired = false,
                MinStartDate = minStart,
                MaxStartDate = maxStart,
                Limit = POOL
            };
            var programs = (_liveTv.GetPrograms(q)?.Items) ?? Array.Empty<BaseItemDto>();

            var genreMap = BuildGenreMap(q);

            // Drop list persistante : retire ces titres de la liste envoyée au LLM.
            var excluded = DroppedTitlesSet();
            // Timers : non exclus, mais marqués is_scheduled (recommandable en direct).
            var scheduled = TimerNamesOnly();

            var wl = LoadWhitelists();
            if (cfg?.DebugVerbose ?? false)
            {
                _logger?.Info("[LLM_AI] epg_tonight fenêtre {0:o} → {1:o} ; whitelists: channels={2} genres={3}",
                    minStart, maxStart, wl.Channels?.Count ?? 0, wl.Genres?.Count ?? 0);
            }

            var seen = new HashSet<string>();
            var kept = new List<(BaseItemDto p, string[] genres, bool isScheduled)>();
            int wlFiltered = 0, flagRejected = 0, wlRejected = 0;
            var flagSamples = new List<string>();
            var wlSamples = new List<string>();
            foreach (var p in programs.OrderBy(x => x.StartDate ?? DateTimeOffset.MaxValue))
            {
                var title = !string.IsNullOrEmpty(p.SeriesName) ? p.SeriesName : p.Name;
                if (string.IsNullOrEmpty(title)) continue;
                var key = Norm(title);
                if (excluded.Contains(key)) continue;
                if (!seen.Add(key)) continue;              // dédupliquer par titre
                var genres = GenreFor(p, genreMap);
                if (IsExcludedGenre(genres, excludeGenres)) continue;
                if (wl.Any && !PassesWhitelists(p, genres, wl))
                {
                    wlFiltered++; wlRejected++;
                    if (wlSamples.Count < 8)
                        wlSamples.Add("{" + title + " ch=" + Norm(p.ChannelName ?? "") +
                                       " chId=" + (p.ChannelId ?? "?") +
                                       " g=[" + (genres == null ? "" : string.Join("/", genres)) + "]}");
                    continue;
                }
                if (!PassesFlags(p, flags))
                {
                    wlFiltered++; flagRejected++;
                    if (flagSamples.Count < 6)
                        flagSamples.Add("{" + title + " K=" + (p.IsKids == true) + " N=" + (p.IsNews == true) +
                                        " S=" + (p.IsSports == true) + "}");
                    continue;
                }
                kept.Add((p, genres, scheduled.Contains(key)));
            }
            if ((cfg?.DebugVerbose ?? false) && wlRejected > 0)
                _logger?.Info("[LLM_AI] epg_tonight wl rejetés={0}, échantillons : {1}",
                    wlRejected, string.Join(" | ", wlSamples));
            if ((cfg?.DebugVerbose ?? false) && flagRejected > 0)
                _logger?.Info("[LLM_AI] epg_tonight flags rejetés={0}, échantillons : {1}",
                    flagRejected, string.Join(" | ", flagSamples));

            // Pré-tri par pertinence, cap, puis re-tri chronologique.
            var picked = kept
                .OrderByDescending(t => RelevanceScore(t.p, t.genres, wl))
                .Take(limit)
                .OrderBy(t => t.p.StartDate ?? DateTimeOffset.MaxValue)
                .ToList();

            var results = new List<object>();
            foreach (var t in picked)
            {
                var p = t.p;
                var title = !string.IsNullOrEmpty(p.SeriesName) ? p.SeriesName : p.Name;
                results.Add(new
                {
                    title,
                    id = p.Id,
                    channel_id = p.ChannelId,
                    overview = Truncate(p.Overview, 200),
                    genres = t.genres ?? Array.Empty<string>(),
                    channel = p.ChannelName,
                    channel_number = p.ChannelNumber,
                    start = p.StartDate,
                    end = p.EndDate,
                    rating = p.CommunityRating,
                    year = p.ProductionYear,
                    season = p.ParentIndexNumber,
                    episode = p.IndexNumber,
                    episode_title = p.EpisodeTitle,
                    is_series = p.IsSeries == true,
                    is_movie = p.IsMovie == true,
                    is_scheduled = t.isScheduled
                });
            }
            _logger?.Info("[LLM_AI] epg_tonight : pool filtré {0} → cap {1} retenu(s) (whitelists/flags : {2} rejeté(s), plafond {3}).",
                kept.Count, results.Count, wlFiltered, limit);
            return JsonSerializer.Serialize(new { total = results.Count, results }, s_json);
        }

        /// <summary>
        /// Parse un <c>HH:mm</c> en <see cref="TimeSpan"/>. Retourne false si la
        /// chaîne est vide ou mal formée. Tolérant sur les espaces.
        /// </summary>
        private static bool TryParseHHmm(string s, out TimeSpan ts)
        {
            ts = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            int colon = s.IndexOf(':');
            if (colon <= 0 || colon >= s.Length - 1) return false;
            if (!int.TryParse(s.Substring(0, colon), out var h) ||
                !int.TryParse(s.Substring(colon + 1), out var m)) return false;
            if (h < 0 || h > 23 || m < 0 || m > 59) return false;
            ts = new TimeSpan(h, m, 0);
            return true;
        }

        // ------------------------------------------------------------------
        //  Helpers EPG / diff / whitelists
        // ------------------------------------------------------------------

        /// <summary>
        /// Drop list persistante (config <see cref="PluginConfiguration.DroppedTitles"/>)
        /// → ensemble de noms normalisés à exclure des résultats epg_series/epg_movies
        /// (épuration en amont : ces titres ne sont jamais envoyés au LLM, qui ne peut
        /// donc plus les recommander). Alimentée par le bouton « Oublier » de la page
        /// de recommandations et par le champ éditable de la page de config.
        /// Tolère un JSON mal formé (renvoie un ensemble vide).
        /// </summary>
        internal static HashSet<string> DroppedTitlesSet()
        {
            var set = new HashSet<string>();
            var raw = Plugin.Instance?.Configuration?.DroppedTitles;
            if (string.IsNullOrWhiteSpace(raw)) return set;
            try
            {
                using (var doc = JsonDocument.Parse(raw))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            if (el.ValueKind == JsonValueKind.String)
                            {
                                var n = Norm(el.GetString() ?? "");
                                if (!string.IsNullOrEmpty(n)) set.Add(n);
                            }
                        }
                    }
                }
            }
            catch { /* JSON invalide : on ignore (set vide) */ }
            return set;
        }

        /// <summary>
        /// Filtres d'inclusion (chaines/genres) lus depuis la config
        /// (<see cref="PluginConfiguration.ChannelWhitelist"/>,
        /// <see cref="PluginConfiguration.GenreWhitelist"/>). Chaque set vide
        /// = pas de filtre sur cette dimension (on garde tout). Les flags
        /// orthogonaux Kids/News/Sports sont gérés séparément par
        /// <see cref="LoadFlags"/>/<see cref="PassesFlags"/> (opt-in par
        /// catégorie). Spécifiques à la tâche de recommandation.
        /// </summary>
        private readonly struct WhitelistFilter
        {
            public readonly HashSet<string> Channels; // Norm(ChannelName)
            public readonly HashSet<string> Genres;    // lowercase
            public WhitelistFilter(HashSet<string> ch, HashSet<string> ge)
            { Channels = ch; Genres = ge; }
            public bool Any => (Channels != null && Channels.Count > 0)
                            || (Genres != null && Genres.Count > 0);
        }

        private static WhitelistFilter LoadWhitelists()
        {
            var cfg = Plugin.Instance?.Configuration;
            return new WhitelistFilter(
                ParseNormSet(cfg?.ChannelWhitelist),
                ParseLowerSet(cfg?.GenreWhitelist));
        }

        /// <summary>
        /// Flags orthogonaux par catégorie (séries/films) : subset de
        /// {kids,news,sports} à INCLURE en plus de la fiction. Vide = fiction
        /// seulement (les programmes IsKids/IsNews/IsSports sont exclus).
        /// Modèle opt-in : la fiction pure (aucun de ces flags) passe toujours ;
        /// un programme marqué passe si son flag est activé. Les catégories
        /// series/films sont garanties par l'appel outil (epg_series/epg_movies),
        /// pas par un flag — d'où l'absence de series/films ici.
        /// </summary>
        private static HashSet<string> LoadFlags(bool series)
        {
            var cfg = Plugin.Instance?.Configuration;
            return ParseLowerSet(series ? cfg?.SeriesFlags : cfg?.MovieFlags);
        }

        private static bool PassesFlags(BaseItemDto p, HashSet<string> enabled)
        {
            bool kids = p.IsKids == true, news = p.IsNews == true, sports = p.IsSports == true;
            bool hasFlag = kids || news || sports;
            if (enabled == null || enabled.Count == 0)
                return !hasFlag;                       // vide = fiction seulement
            return !hasFlag                            // fiction pure : toujours OK
                || (kids   && enabled.Contains("kids"))
                || (news   && enabled.Contains("news"))
                || (sports && enabled.Contains("sports"));
        }

        /// <summary>Parse un tableau JSON de chaînes en HashSet normalisé (Norm).</summary>
        private static HashSet<string> ParseNormSet(string raw)
        {
            var set = new HashSet<string>();
            if (string.IsNullOrWhiteSpace(raw)) return set;
            try
            {
                using (var doc = JsonDocument.Parse(raw))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        foreach (var el in doc.RootElement.EnumerateArray())
                            if (el.ValueKind == JsonValueKind.String)
                            { var n = Norm(el.GetString() ?? ""); if (!string.IsNullOrEmpty(n)) set.Add(n); }
                }
            }
            catch { /* JSON invalide : set vide */ }
            return set;
        }

        /// <summary>Parse un tableau JSON de chaînes en HashSet lowercase.</summary>
        private static HashSet<string> ParseLowerSet(string raw)
        {
            var set = new HashSet<string>();
            if (string.IsNullOrWhiteSpace(raw)) return set;
            try
            {
                using (var doc = JsonDocument.Parse(raw))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        foreach (var el in doc.RootElement.EnumerateArray())
                            if (el.ValueKind == JsonValueKind.String)
                            { var s = (el.GetString() ?? "").ToLowerInvariant(); if (!string.IsNullOrEmpty(s)) set.Add(s); }
                }
            }
            catch { /* JSON invalide : set vide */ }
            return set;
        }

        /// <summary>
        /// Vrai si le programme passe les whitelists chaines/genres (inclusion).
        /// Un set vide sur une dimension = pas de filtre sur cette dimension.
        /// Les flags orthogonaux Kids/News/Sports sont gérés séparément par
        /// <see cref="PassesFlags"/> (modèle opt-in par catégorie).
        /// </summary>
        private static bool PassesWhitelists(BaseItemDto p, string[] genres, WhitelistFilter f)
        {
            if (f.Channels != null && f.Channels.Count > 0)
                if (!f.Channels.Contains(Norm(p.ChannelName ?? ""))) return false;

            if (f.Genres != null && f.Genres.Count > 0)
            {
                bool hasGenre = false;
                if (genres != null)
                    foreach (var g in genres)
                        if (!string.IsNullOrEmpty(g) && f.Genres.Contains(g.ToLowerInvariant()))
                        { hasGenre = true; break; }
                if (!hasGenre) return false;
            }
            return true;
        }

        /// <summary>
        /// Score de pertinence déterministe pour le pré-tri avant le cap :
        /// note communautaire (principal) + bonus si un genre fait partie de
        /// la whitelist de genres (préférence explicite) + léger bonus si un
        /// synopsis est disponible (meilleure matière à recommander).
        /// </summary>
        private static double RelevanceScore(BaseItemDto p, string[] genres, WhitelistFilter wl)
        {
            double s = (p.CommunityRating ?? 0) * 2.0;
            if (wl.Genres != null && wl.Genres.Count > 0 && genres != null)
            {
                foreach (var g in genres)
                    if (!string.IsNullOrEmpty(g) && wl.Genres.Contains(g.ToLowerInvariant()))
                    { s += 2.0; break; }
            }
            if (!string.IsNullOrWhiteSpace(p.Overview)) s += 0.5;
            return s;
        }

        /// <summary>
        /// Construit un dictionnaire <c>Id (Guid) -> Genres</c> des programmes
        /// LiveTv correspondant aux mêmes filtres que la requête <paramref name="src"/>.
        /// <see cref="ILiveTvManager.GetPrograms(InternalItemsQuery)"/> renvoie des
        /// <c>BaseItemDto</c> SANS le champ <c>Genres</c> peuplé (l'API REST exige
        /// Fields=Genres pour l'obtenir). En revanche, les <c>BaseItem</c>
        /// (LiveTvProgram) récupérés via <see cref="ILibraryManager.GetItemsResult"/>
        /// portent bien <c>Genres</c>. On fait donc une seconde requête library pour
        /// enrichir les genres, et on les recroise par Id avec les DTO de GetPrograms
        /// (qui portent canal/dates/flags/overview, non couverts par la requête library
        /// de la même façon). Aucune régression si un Id n'est pas retrouvé (fallback
        /// vide = comportement précédent).
        /// </summary>
        private Dictionary<string, string[]> BuildGenreMap(InternalItemsQuery src)
        {
            var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var gq = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Program" },
                    IsSeries = src.IsSeries,
                    IsMovie = src.IsMovie,
                    HasAired = src.HasAired,
                    IsKids = src.IsKids,
                    IsNews = src.IsNews,
                    IsSports = src.IsSports,
                    ParentIndexNumber = src.ParentIndexNumber,
                    IndexNumber = src.IndexNumber,
                    Limit = 1000
                };
                var items = _library.GetItemsResult(gq)?.Items ?? Array.Empty<BaseItem>();
                bool verbose = Plugin.Instance?.Configuration?.DebugVerbose ?? false;
                if (verbose)
                    _logger?.Info("[LLM_AI] BuildGenreMap: {0} programmes (IsSeries={1} IsMovie={2} HasAired={3} IsKids={4} IsNews={5} IsSports={6})",
                        items.Length, gq.IsSeries, gq.IsMovie, gq.HasAired, gq.IsKids, gq.IsNews, gq.IsSports);
                int withGenres = 0;
                foreach (var it in items)
                {
                    // Le DTO.Id de GetPrograms est l'InternalId (Int64) exposé en
                    // string (EnableInternalIdsExternally), pas le Guid BaseItem.Id.
                    var id = it.InternalId.ToString();
                    if (!map.ContainsKey(id))
                        map[id] = it.Genres ?? Array.Empty<string>();
                    if (it.Genres != null && it.Genres.Length > 0) withGenres++;
                }
                if (verbose && items.Length > 0)
                    _logger?.Info("[LLM_AI] BuildGenreMap sample: id={0} genres=[{1}] (avec genres: {2}/{3})",
                        items[0].InternalId.ToString(),
                        string.Join("/", items[0].Genres ?? Array.Empty<string>()),
                        withGenres, items.Length);
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] BuildGenreMap: {0}", ex);
            }
            return map;
        }

        /// <summary>
        /// Renvoie les genres enrichis d'un programme DTO : priorité au dictionnaire
        /// <see cref="BuildGenreMap"/> (genres réels du BaseItem), fallback au champ
        /// <c>p.Genres</c> du DTO (souvent vide car non peuplé par GetPrograms).
        /// </summary>
        private static string[] GenreFor(BaseItemDto p, Dictionary<string, string[]> map)
        {
            if (p?.Id == null) return Array.Empty<string>();
            if (map != null && map.TryGetValue(p.Id, out var g) && g != null)
                return g;
            return p.Genres ?? Array.Empty<string>();
        }

        /// <summary>
        /// Noms normalisés des items d'un type de la bibliothèque, optionnellement
        /// augmenté des series timers et/ou single timers programmés.
        /// (emby-ai-suggest.sh : biblio Series + series timers + single timers ;
        ///  emby-absent.sh : biblio Movie + single timers.)
        /// </summary>
        private HashSet<string> ExcludedNames(string libraryType, bool addSeriesTimers, bool addSingleTimers)
        {
            var set = new HashSet<string>();
            var q = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { libraryType },
                Recursive = true,
                Limit = null,
                EnableTotalRecordCount = false
            };
            foreach (var n in (_library.GetItemList(q) ?? Array.Empty<BaseItem>()).Select(i => i.Name))
                set.Add(Norm(n));

            if (addSeriesTimers)
            {
                var st = _liveTv.GetSeriesTimers(new SeriesTimerQuery())?.Items;
                if (st != null)
                    foreach (var s in st)
                        if (!string.IsNullOrEmpty(s.Name)) set.Add(Norm(s.Name));
            }
            if (addSingleTimers)
            {
                // Single timers programmés : on prend SeriesName (séries) sinon Name (films).
                var tt = _liveTv.GetTimers(new TimerQuery { IsScheduled = true })?.Items;
                if (tt != null)
                    foreach (var t in tt)
                    {
                        var nm = t.ProgramInfo != null
                            ? (!string.IsNullOrEmpty(t.ProgramInfo.SeriesName) ? t.ProgramInfo.SeriesName : t.ProgramInfo.Name)
                            : null;
                        if (!string.IsNullOrEmpty(nm)) set.Add(Norm(nm));
                    }
            }
            return set;
        }

        /// <summary>
        /// Noms normalisés des timers uniquement (series timers + single timers),
        /// sans la bibliothèque — pour le mode <c>new_seasons</c>
        /// (emby-absent-series.sh n'exclut que les timers).
        /// </summary>
        private HashSet<string> TimerNamesOnly()
        {
            var set = new HashSet<string>();
            var st = _liveTv.GetSeriesTimers(new SeriesTimerQuery())?.Items;
            if (st != null)
                foreach (var s in st)
                    if (!string.IsNullOrEmpty(s.Name)) set.Add(Norm(s.Name));
            var tt = _liveTv.GetTimers(new TimerQuery { IsScheduled = true })?.Items;
            if (tt != null)
                foreach (var t in tt)
                {
                    var nm = t.ProgramInfo != null
                        ? (!string.IsNullOrEmpty(t.ProgramInfo.SeriesName) ? t.ProgramInfo.SeriesName : t.ProgramInfo.Name)
                        : null;
                    if (!string.IsNullOrEmpty(nm)) set.Add(Norm(nm));
                }
            return set;
        }

        /// <summary>
        /// Construit la map « nom de série normalisé → numéros de saisons
        /// possédées » à partir des Season de la bibliothèque
        /// (équivalent du <c>group_by</c> jq d'emby-absent-series.sh sur
        /// <c>IncludeItemTypes=Season</c>). Sert à détecter les nouvelles saisons.
        /// </summary>
        private Dictionary<string, HashSet<int>> OwnedSeasonsMap()
        {
            var map = new Dictionary<string, HashSet<int>>();
            var q = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Season" },
                Recursive = true,
                Limit = null,
                EnableTotalRecordCount = false
            };
            foreach (var b in _library.GetItemList(q) ?? Array.Empty<BaseItem>())
            {
                // GetItemList retourne BaseItem[] : on caste vers le type dérivé
                // Season pour accéder à SeriesName (nom de la série parente).
                var s = b as MediaBrowser.Controller.Entities.TV.Season;
                var seriesName = s != null ? s.SeriesName : null;
                var key = Norm(!string.IsNullOrEmpty(seriesName) ? seriesName : b.Name);
                if (string.IsNullOrEmpty(key)) continue;
                if (!map.TryGetValue(key, out var set)) { set = new HashSet<int>(); map[key] = set; }
                if (b.IndexNumber.HasValue) set.Add(b.IndexNumber.Value);
            }
            return map;
        }

        /// <summary>Normalise un titre pour la comparaison (lower + [^a-z0-9] retiré).</summary>
        internal static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var lower = s.ToLowerInvariant();
            // Retire un article de tête (FR/EN) séparé par une espace, de sorte
            // que « Le suspect », « The Suspect » et « Suspect » normalisent tous
            // vers « suspect ». Indispensable pour l'exclusion biblio/timers :
            // l'EPG porte souvent le titre localisé (« Le suspect ») tandis que
            // la bibliothèque/les timers portent le titre original ou écorché
            // (« The Suspect » / « Suspect »). Sans ce retrait, aucun match et la
            // série déjà possédée ressort dans les recommandations.
            // Les articles à apostrophe (« l' », « d' ») n'ont pas besoin d'être
            // retirés : l'apostrophe est supprimée plus bas, donc « l'avocat »
            // devient « lavocat » des deux côtés (biblio + EPG) → déjà cohérent.
            // \b + \s+ évite d'écorcher « Device », « Dune », « United » (article
            // soudé au reste, sans limite de mot ni espace).
            lower = s_ArticleRe.Replace(lower, "");
            return Regex.Replace(lower, "[^a-z0-9]", "");
        }

        private static readonly Regex s_ArticleRe = new Regex(
            @"^(?:le|la|les|un|une|des|du|de|the|a|an)\b\s+",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static HashSet<string> NormGenreSet(string[] genres)
        {
            var set = new HashSet<string>();
            if (genres != null)
                foreach (var g in genres) set.Add((g ?? "").ToLowerInvariant());
            return set;
        }

        private static bool IsExcludedGenre(string[] genres, HashSet<string> exclude)
        {
            if (genres == null || exclude.Count == 0) return false;
            foreach (var g in genres)
                if (!string.IsNullOrEmpty(g) && exclude.Contains(g.ToLowerInvariant())) return true;
            return false;
        }

        // ------------------------------------------------------------------
        //  Helpers
        // ------------------------------------------------------------------

        /// <summary>Compte les items d'un type via TotalRecordCount (fetch 1).</summary>
        private int Count(string embyType)
        {
            try
            {
                var q = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { embyType },
                    Recursive = true,
                    Limit = 1,
                    EnableTotalRecordCount = true
                };
                return _library.GetItemsResult(q)?.TotalRecordCount ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private static BaseItem[] SortItems(BaseItem[] items, string sortBy)
        {
            switch (sortBy)
            {
                case "year":   return items.OrderByDescending(i => i.ProductionYear ?? 0).ToArray();
                case "rating": return items.OrderByDescending(i => i.CommunityRating ?? 0).ToArray();
                case "name":   return items.OrderBy(i => i.SortName ?? i.Name ?? "").ToArray();
                default:       return items.OrderByDescending(i => i.DateCreated).ToArray(); // recent
            }
        }

        /// <summary>Mappe un libellé court du LLM vers un IncludeItemTypes Emby.</summary>
        private static string MapType(string label)
        {
            if (string.IsNullOrEmpty(label)) return "Movie";
            switch (label.ToLowerInvariant())
            {
                case "movie":   return "Movie";
                case "series":  return "Series";
                case "episode": return "Episode";
                case "audio":
                case "song":    return "Audio";
                case "album":   return "MusicAlbum";
                case "book":    return "Book";
                default:        return char.ToUpperInvariant(label[0]) + label.Substring(1);
            }
        }

        private static string TypeLabel(BaseItem item)
        {
            var n = item.GetType().Name;
            return n.ToLowerInvariant();
        }

        private string ImageUrl(long internalId)
        {
            var base_ = EmbyUrl;
            return string.IsNullOrEmpty(base_) ? null : base_.TrimEnd('/') + "/Items/" + internalId + "/Images/Primary";
        }

        private static string Err(string msg) => JsonSerializer.Serialize(new { error = msg }, s_json);

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");

        // --- Lecture optionnelle des arguments JSON -----------------------

        private static string OptString(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            return e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }

        private static int OptInt(JsonElement e, string name, int dflt)
        {
            if (e.ValueKind != JsonValueKind.Object) return dflt;
            return e.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : dflt;
        }

        private static bool OptBool(JsonElement e, string name, bool dflt)
        {
            if (e.ValueKind != JsonValueKind.Object) return dflt;
            if (!e.TryGetProperty(name, out var p)) return dflt;
            if (p.ValueKind == JsonValueKind.True) return true;
            if (p.ValueKind == JsonValueKind.False) return false;
            return dflt;
        }

        private static double? OptDouble(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            return e.TryGetProperty(name, out var p) && p.TryGetDouble(out var v) ? v : (double?)null;
        }

        private static string[] OptStringArray(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array) return null;
            return p.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null)
                                       .Where(s => !string.IsNullOrEmpty(s)).ToArray();
        }
    }
}