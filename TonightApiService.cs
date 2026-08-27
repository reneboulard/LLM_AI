using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;

namespace LLM_AI
{
    /// <summary>
    /// Endpoint HTTP plugin « À regarder ce soir » : expose
    /// <c>GET /Plugins/LLMAI/Tonight</c> à la page Recommandations pour produire
    /// une recommandation personnalisée par usager, à la demande. Croise
    /// l'historique de visionnage récent de l'usager avec les programmes EPG de
    /// la soirée, le tout analysé par le LLM (même orchestration que la tâche
    /// planifiée via <see cref="LlmRunner"/>).
    /// </summary>
    /// <remarks>
    /// Service ServiceStack découvert par scanning d'assembly : hérite
    /// <see cref="BaseApiService"/> (propriétés DI peuplées par l'hôte :
    /// Logger, UserManager, LibraryManager, ApplicationHost, AuthorizationContext,
    /// Request…) et injecte via constructeur les services non exposés par la
    /// base : <see cref="ILiveTvManager"/> (EPG/timers) et
    /// <see cref="IJsonSerializer"/> (run LLM). La route est portée par le DTO
    /// requête <see cref="TonightRequest"/> via <see cref="RouteAttribute"/>.
    /// Pattern identique aux services Emby.Api (ConfigurationService, etc.).
    /// </remarks>
    public class TonightApiService : BaseApiService
    {
        private readonly ILiveTvManager _liveTv;
        private readonly IJsonSerializer _json;

        // Workflow injecté dans le system prompt de l'agent (bloc
        // « WORKFLOW DE RECOMMANDATION ») pour le run « ce soir ».
        private const string TONIGHT_WORKFLOW =
            "SECTION « CE SOIR » (personnalisée par usager) : tu croises TROIS sources pour " +
            "recommander ce que l'usager pourrait regarder CE SOIR :\n" +
            " 1) Son profil de goût (historique de visionnage récent) — fourni dans le message.\n" +
            " 2) L'EPG de ce soir : appelle get_emby_info avec action=\"epg_tonight\" (fenêtre " +
            "temporelle bornée par la config, séries ET films). Programmes à regarder EN DIRECT " +
            "ou à enregistrer.\n" +
            " 3) Ses enregistrements récents NON visionnés — fournis dans le message (films/épisodes " +
            "enregistrés ces derniers jours mais pas encore regardés). Ce sont des candidats de " +
            "choix immédiat : déjà enregistrés, prêts à regarder. Si l'usager suit une série et " +
            "qu'un nouvel épisode enregistré de cette série est non visionné, remonte-le en priorité.\n" +
            "Pour chaque recommandation, positionne kind=\"series\" ou kind=\"movie\" (series → timer " +
            "série, movie/one-off → timer unique) et priority high/medium/low. Ajoute un champ " +
            "source : \"live\" (programme EPG du soir — à regarder en direct ou à enregistrer) ou " +
            "\"recording\" (enregistrement disponible — à regarder maintenant, DÉJÀ enregistré, ne " +
            "pas re-programmer).\n" +
            "Les items epg_tonight portent is_series/is_movie : positionne kind en conséquence. " +
            "is_scheduled=true signifie qu'un timer existe déjà : recommande « à regarder en direct » " +
            "plutôt qu'un nouvel enregistrement. Les enregistrements non visionnés portent un champ " +
            "id (l'identifiant Emby de l'enregistrement) : pour source=\"recording\", reprends ce id " +
            "tel quel dans la recommandation (l'UI proposera « Regarder »).\n" +
            "GARANTIS AU MOINS le nombre minimum demandé de recommandations (voir le message). Si " +
            "l'EPG du soir + les enregistrements non visionnés produisent MOINS que ce minimum, " +
            "complète avec des titres de la RÉSERVE BIBLIOTHÈQUE (items de la bibliothèque de l'usager " +
            "non encore visionnés, listés dans le message) qui matchent son profil de goût, en " +
            "positionnant source=\"library\" et en reprenant leur id tel quel (l'UI proposera " +
            "« Regarder » — lecture depuis la bibliothèque). N'utilise cette réserve QUE pour " +
            "atteindre le minimum, pas pour gonfler la sélection au-delà.\n" +
            "Reprends title/channel/start tels quels depuis epg_tonight pour source=\"live\" (permet " +
            "la programmation). Tu peux enrichir via tmdb_lookup/web_search si c'est utile, mais " +
            "reste pratique et rapide — l'objectif est une courte sélection personnalisée pour ce " +
            "soir, pas un audit exhaustif. Retourne un tableau JSON " +
            "[{title, kind, reason, priority, source, channel, start, id, showbizz_match}] " +
            "(id pour source=\"recording\"/\"library\" ; channel/start pour source=\"live\").";

        public TonightApiService(ILiveTvManager liveTv, IJsonSerializer json)
        {
            _liveTv = liveTv;
            _json = json;
        }

        // ------------------------------------------------------------------
        //  Cache par usager (in-memory, TTL = TonightCacheHours)
        // ------------------------------------------------------------------

        private class CacheEntry
        {
            public string Items;
            public string Date;
            public DateTimeOffset ExpiresAt;
        }

        private static readonly Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>();
        private static readonly object _cacheLock = new object();

        // ------------------------------------------------------------------
        //  DTO requête
        // ------------------------------------------------------------------

        /// <summary>
        /// Requête GET <c>/Plugins/LLMAI/Tonight</c>.
        /// <c>UserId</c> : optionnel — l'usager pour lequel produire la reco
        /// (défaut : l'usager authentifié par le token). Un usager non-admin ne
        /// peut consulter que son propre historique. <c>Refresh</c> : "1"/"true"
        /// = bypass le cache et force un nouveau run LLM. Chaîne (pas bool) car
        /// le binder DTO d'Emby appelle <c>Boolean.Parse</c> qui rejette "0"/"1"
        /// (n'accepte que "True"/"False") — on parse manuellement.
        /// </summary>
        [Route("/Plugins/LLMAI/Tonight", "GET")]
        public class TonightRequest : IReturn<object>
        {
            public string UserId { get; set; }
            public string Refresh { get; set; }
        }

        /// <summary>
        /// Réponse renvoyée au navigateur. <c>Items</c> est le payload JSON
        /// (tableau de recommandations) en chaîne — le JS le <c>JSON.parse</c>
        /// comme il le fait déjà pour <c>Recommendations</c>. <c>Date</c> :
        /// date/heure (UTC ISO) de production. <c>FromCache</c> : true si
        /// servi depuis le cache. <c>Enabled</c> : false si la section est
        /// désactivée en config (le JS n'affiche alors pas la section).
        /// </summary>
        public class TonightResponse
        {
            public bool Enabled { get; set; }
            public string Items { get; set; }
            public string Date { get; set; }
            public bool FromCache { get; set; }
            public string Error { get; set; }
        }

        // ------------------------------------------------------------------
        //  Handler GET
        // ------------------------------------------------------------------

        public async Task<object> Get(TonightRequest req)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new TonightResponse { Enabled = false, Error = "Configuration du plugin indisponible." };

            if (!cfg.TonightEnabled)
                return new TonightResponse { Enabled = false };

            bool refresh = IsTrue(req.Refresh);

            // Résolution de l'usager : priorité au token (AuthorizationContext),
            // puis au UserId passé en param — en vérifiant qu'un usager non-admin
            // ne consulted que son propre historique.
            User user = ResolveUser(req);
            if (user == null)
                return new TonightResponse { Enabled = true, Error = "Utilisateur non authentifié." };

            // Cache par usager (sauf Refresh).
            int cacheHours = Math.Max(0, cfg.TonightCacheHours);
            string cacheKey = user.Id.ToString();
            if (!refresh && cacheHours > 0)
            {
                lock (_cacheLock)
                {
                    if (_cache.TryGetValue(cacheKey, out var e) && e.ExpiresAt > DateTimeOffset.UtcNow)
                        return new TonightResponse { Enabled = true, Items = e.Items, Date = e.Date, FromCache = true };
                }
            }

            // LlmRunner (construit ici : à cet instant les props DI de la base
            // — Logger, LibraryManager, UserManager, ApplicationHost — sont
            // peuplées par le résolveur Emby).
            var runner = new LlmRunner(Logger, _json, LibraryManager, UserManager, _liveTv, ApplicationHost);

            // CancellationToken de la requête (partagé par le fetch
            // d'enregistrements et le run agent).
            var ct = Request?.CancellationToken ?? CancellationToken.None;

            // Mode compact : si le backend LLM principal est un Ollama LOCAL
            // (modèle souvent petit, fenêtre de contexte limitée), on réduit les
            // données injectées (profil, enregistrements, réserve) pour éviter
            // de « noyer » le modèle. Sur cloud (ollama_cloud / gemini), on
            // garde toutes les données (qualité optimale).
            bool compact = IsLocalPrimary(cfg);
            if (compact) Logger?.Info("[LLM_AI] Tonight : backend local détecté — mode compact (données injectées réduites).");

            // 1) Profil de goût : historique de visionnage récent de l'usager.
            string profile = BuildTasteProfile(user, compact);

            // 2) Enregistrements récents non visionnés (candidats « à regarder
            //    ce soir » — déjà enregistrés, prêts à lire). Injectés comme le
            //    profil : l'usager est résolu ici (le tool get_emby_info, lui,
            //    est global/sans usager).
            string recs = BuildUnwatchedRecordings(user, cfg, ct, compact);

            // 2b) Réserve bibliothèque (items non visionnés) : utilisée par le
            //    LLM uniquement si EPG + enregistrements < TonightMinRecommendations.
            string reserve = BuildLibraryFallbackPool(user, compact);

            // 3) Prompt personnalisé = template config + profil + enregistrements
            //    + réserve + contrainte de minimum dynamique.
            int minRec = Math.Max(0, cfg?.TonightMinRecommendations ?? 3);
            string prompt = (cfg.TonightPrompt ?? string.Empty).Trim()
                + "\n\n" + profile + recs + reserve
                + "\n\n### CONTRAINTE DE SÉLECTION\n"
                + $"Garantis AU MOINS {minRec} recommandation(s). Si l'EPG du soir + les "
                + "enregistrements non visionnés en produisent moins, complète avec la RÉSERVE "
                + "BIBLIOTHÈQUE ci-dessus (source=\"library\", reprends id). Ne dépasse pas le "
                + "minimum avec la réserve — l'EPG et les enregistrements restent prioritaires.";

            // 4) Run agent (boucle de tool-calling) — même logique que la tâche
            //    planifiée : backends, outils, enrichissement (match titres →
            //    id/channel_id/rating/image_url) gérés par LlmRunner.
            var (payload, ok) = await runner.RunAsync(cfg, "TONIGHT", prompt, TONIGHT_WORKFLOW, ct).ConfigureAwait(false);

            if (!ok || string.IsNullOrWhiteSpace(payload))
                return new TonightResponse { Enabled = true, Error = "Le run LLM n'a pas produit de recommandation." };

            // Enrichissement bibliothèque : pour les reco source="live" dont le
            // titre est déjà possédé, injecte library_id (bouton « Regarder »
            // depuis la bibliothèque). S'exécute après l'enrichissement EPG.
            payload = runner.EnrichWithLibrary(payload);

            string date = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            // Mise en cache du résultat.
            if (cacheHours > 0)
            {
                lock (_cacheLock)
                {
                    _cache[cacheKey] = new CacheEntry
                    {
                        Items = payload,
                        Date = date,
                        ExpiresAt = DateTimeOffset.UtcNow.AddHours(cacheHours)
                    };
                }
            }

            return new TonightResponse { Enabled = true, Items = payload, Date = date, FromCache = false };
        }

        /// <summary>
        /// Interprète un flag query-string permissivement : "1", "true", "yes",
        /// "on" (casse insensible) → true ; tout le reste (null, "0", "false",
        /// "") → false. Évite le binder bool d'Emby qui rejette "0"/"1".
        /// </summary>
        private static bool IsTrue(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            switch (s.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":
                    return true;
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------
        //  Auth : résolution de l'usager appelant
        // ------------------------------------------------------------------

        /// <summary>
        /// Résout l'usager à partir du token d'authentification (prioritaire) et
        /// du <c>UserId</c> éventuel. Garantit qu'un usager non-administrateur ne
        /// peut consulter que son propre historique : si <c>UserId</c> désigne un
        /// autre usager sans droit admin, on refuse (renvoie null).
        /// </summary>
        private User ResolveUser(TonightRequest req)
        {
            User authUser = null;
            try
            {
                var auth = AuthorizationContext?.GetAuthorizationInfo(Request);
                authUser = auth?.User;
                if (authUser == null && auth != null && auth.UserId != 0)
                    authUser = UserManager.GetUserById(auth.UserId);
            }
            catch { /* tolérant : on repli sur UserId */ }

            if (string.IsNullOrWhiteSpace(req.UserId))
            {
                // Token sans usager (clé API : auth=ok mais User=NULL, UserId=0).
                // Repli sur le premier usager — usage domestique : une clé API est
                // créée par l'admin et vaut accès complet ; le premier usager est
                // l'usager principal du foyer. Le navigateur, lui, passe toujours
                // userId=usager courant (résolution explicite ci-dessous).
                if (authUser != null) return authUser;
                try
                {
                    var users = UserManager.GetUserList(new UserQuery());
                    if (users != null && users.Length > 0)
                    {
                        Logger?.Info("[LLM_AI] Tonight : token sans usager (clé API ?) — repli sur le premier usager « {0} ».", users[0].Name);
                        return users[0];
                    }
                }
                catch { }
                return null;
            }

            User requested = null;
            try { requested = UserManager.GetUserById(req.UserId); }
            catch { /* id invalide */ }
            if (requested == null) return authUser;

            if (authUser == null)
                return requested; // pas de token : on trust UserId (auth Emby globale couvre)

            if (requested.Id == authUser.Id)
                return requested;

            // Un autre usager : autorisé seulement si l'appelant est admin.
            bool isAdmin = false;
            try { isAdmin = authUser.Policy?.IsAdministrator ?? false; } catch { }
            return isAdmin ? requested : null;
        }

        /// <summary>
        /// Détermine si le backend LLM de plus haute priorité est un Ollama
        /// <b>local</b> (modèle souvent petit, contexte limité). Sert à activer le
        /// mode compact (données injectées réduites). Reflète le filtrage de
        /// <see cref="LlmRunner.ResolveBackends"/> sans logger (appelé avant le
        /// run). En config legacy (LlmUrl sans LlmBackends), c'est local par
        /// construction.
        /// </summary>
        private static bool IsLocalPrimary(PluginConfiguration cfg)
        {
            try
            {
                if (cfg == null) return false;
                if (cfg.LlmBackends == null || cfg.LlmBackends.Count == 0)
                    return !string.IsNullOrWhiteSpace(cfg.LlmUrl); // legacy = local
                var best = cfg.LlmBackends
                    .Where(b => b != null && b.Enabled)
                    .Where(b => !string.IsNullOrWhiteSpace(b.Url) || b.ProviderType != LlmProvider.OllamaLocal)
                    .OrderBy(b => b.Priority)
                    .FirstOrDefault();
                return best != null && best.ProviderType == LlmProvider.OllamaLocal;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        //  Profil de goût (historique récent)
        // ------------------------------------------------------------------

        /// <summary>
        /// Construit un profil de goût textuel depuis l'historique de visionnage
        /// récent de l'usager : titres récemment joués (tri <c>DatePlayed</c>
        /// descendant) + top genres. Injecté dans le prompt utilisateur pour que
        /// le LLM croise ce profil avec les programmes EPG de ce soir.
        /// <paramref name="compact"/> réduit le nombre de titres (modèle local).
        /// </summary>
        private string BuildTasteProfile(User user, bool compact)
        {
            int maxTitles = compact ? 10 : 25;
            var sb = new StringBuilder();
            try
            {
                var q = new InternalItemsQuery
                {
                    User = user,
                    IsPlayed = true,
                    Recursive = true,
                    IncludeItemTypes = new[] { "Movie", "Episode" },
                    OrderBy = new[] { ("DatePlayed", SortOrder.Descending) },
                    Limit = compact ? 25 : 60,
                    EnableTotalRecordCount = false
                };
                var items = LibraryManager.GetItemList(q) ?? Array.Empty<BaseItem>();

                var seen = new HashSet<string>(StringComparer.Ordinal);
                var titles = new List<string>();
                var genreFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var it in items)
                {
                    var episode = it as MediaBrowser.Controller.Entities.TV.Episode;
                    bool isEpisode = episode != null;
                    string title = isEpisode ? episode.SeriesName : it.Name;
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    // Dédupliquer par titre (un épisode par série suffit pour le profil).
                    string key = LlmRunner.NormTitle(title);
                    if (string.IsNullOrEmpty(key) || !seen.Add(key)) continue;

                    titles.Add(isEpisode
                        ? $"- {title} (série)"
                        : $"- {title} (film)");

                    foreach (var g in it.Genres ?? Array.Empty<string>())
                    {
                        if (string.IsNullOrWhiteSpace(g)) continue;
                        if (genreFreq.TryGetValue(g, out var c)) genreFreq[g] = c + 1;
                        else genreFreq[g] = 1;
                    }

                    if (titles.Count >= maxTitles) break;
                }

                sb.AppendLine("### PROFIL DE GOÛT DE L'USAGER (historique de visionnage récent)");
                if (titles.Count > 0)
                {
                    sb.AppendLine("Titres récemment regardés (du plus récent au plus ancien) :");
                    foreach (var t in titles) sb.AppendLine(t);
                }
                else
                {
                    sb.AppendLine("(Aucun historique de visionnage récent — recommande sur la base " +
                                  "de la note/du genre des programmes du soir.)");
                }

                var topGenres = genreFreq
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key)
                    .Take(compact ? 5 : 8)
                    .Select(kv => kv.Key)
                    .ToList();
                if (topGenres.Count > 0)
                    sb.AppendLine("Genres les plus regardés : " + string.Join(", ", topGenres) + ".");
            }
            catch (Exception ex)
            {
                Logger?.Warn("[LLM_AI] BuildTasteProfile : {0}", ex.Message);
                sb.AppendLine("(Profil de goût indisponible — recommande sur la base des programmes du soir.)");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  Enregistrements récents non visionnés
        // ------------------------------------------------------------------

        /// <summary>
        /// Construit la liste des enregistrements récents NON visionnés de
        /// l'usager (films/épisodes enregistrés ces
        /// <see cref="PluginConfiguration.TonightRecordingsDays"/> derniers jours
        /// mais pas encore regardés), formatée pour injection dans le prompt.
        /// Chaque entrée porte l'id Emby de l'enregistrement (Guid) pour que le
        /// LLM puisse le référencer (<c>source="recording"</c>, <c>id=…</c>) et
        /// que l'UI propose « Regarder ». Croisé avec le profil de goût par le
        /// LLM, ça remonte en priorité un nouvel épisode enregistré d'une série
        /// que l'usager suit.
        /// <para>Résolu ici (et pas via un tool <c>get_emby_info</c>) car
        /// <c>IsPlayed</c> est par usager : le tool est global/sans usager, alors
        /// que <see cref="TonightApiService"/> a résolu l'usager.</para>
        /// </summary>
        private string BuildUnwatchedRecordings(User user, PluginConfiguration cfg, CancellationToken ct, bool compact)
        {
            var sb = new StringBuilder();
            try
            {
                int days = Math.Max(0, cfg?.TonightRecordingsDays ?? 7);
                var cutoff = DateTimeOffset.Now.AddDays(-days);

                // GetRecordings filtre par IsPlayed (non joués pour cet usager) ;
                // on trie par DateCreated descendant et on borne en mémoire par
                // la fenêtre config (robuste : MinDateCreated n'est pas garanti
                // sur toutes les versions).
                var q = new InternalItemsQuery
                {
                    User = user,
                    IsPlayed = false,
                    Recursive = true,
                    OrderBy = new[] { ("DateCreated", SortOrder.Descending) },
                    Limit = compact ? 60 : 300,
                    EnableTotalRecordCount = false
                };
                var recs = (_liveTv.GetRecordings(q, ct)?.Items) ?? Array.Empty<BaseItemDto>();

                var lines = new List<string>();
                foreach (var r in recs)
                {
                    if (r == null) continue;
                    // Filtre temporel en mémoire (DateCreated = date d'enregistrement).
                    if ((r.DateCreated ?? DateTimeOffset.MinValue) < cutoff) continue;
                    if (r.UserData?.Played == true) continue;   // safety net (déjà joué)

                    bool isSeries = r.IsSeries == true;
                    bool isMovie = r.IsMovie == true;
                    string title = !string.IsNullOrEmpty(r.SeriesName) ? r.SeriesName : r.Name;
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    string kind = isSeries ? "series" : (isMovie ? "movie" : "other");
                    string date = (r.DateCreated ?? DateTimeOffset.MinValue).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    double? rating = r.CommunityRating;
                    var genres = r.Genres ?? Array.Empty<string>();

                    var line = new StringBuilder($"- id={r.Id} | title={title} | kind={kind} | recorded={date}");
                    if (rating.HasValue) line.Append($" | rating={rating.Value:0.0}");
                    if (genres.Length > 0) line.Append($" | genres=[{string.Join(", ", genres)}]");
                    // Pour une série, le titre d'épisode aide le LLM à contextualiser.
                    if (isSeries && !string.IsNullOrEmpty(r.EpisodeTitle) && r.EpisodeTitle != title)
                        line.Append($" | episode={r.EpisodeTitle}");
                    lines.Add(line.ToString());

                    if (lines.Count >= (compact ? 12 : 40)) break;
                }

                sb.AppendLine("### ENREGISTREMENTS RÉCENTS NON VISIONNÉS (disponibles à regarder maintenant)");
                if (lines.Count > 0)
                {
                    sb.AppendLine($"{lines.Count} enregistrement(s) de ces {days} dernier(s) jour(s), non encore regardé(s) :");
                    foreach (var l in lines) sb.AppendLine(l);
                    sb.AppendLine("Ces éléments sont DÉJÀ enregistrés (ne PAS re-programmer). Pour un de " +
                                  "ces titres qui correspond aux goûts de l'usager, recommande-le avec " +
                                  "source=\"recording\" et reprends son id tel quel (l'UI proposera « Regarder »).");
                }
                else
                {
                    sb.AppendLine($"(Aucun enregistrement récent non visionné sur ces {days} dernier(s) jour(s).)");
                }
            }
            catch (Exception ex)
            {
                Logger?.Warn("[LLM_AI] BuildUnwatchedRecordings : {0}", ex.Message);
                sb.AppendLine("(Enregistrements récents indisponibles — base-toi sur l'EPG du soir et le profil.)");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  Réserve bibliothèque (fallback < min recommandations)
        // ------------------------------------------------------------------

        /// <summary>
        /// Construit une réserve d'items de la bibliothèque de l'usager NON
        /// visionnés (films + épisodes, triés par ajout récent puis note),
        /// injectée dans le prompt comme source de complément : le LLM ne l'utilise
        /// QUE si l'EPG du soir + les enregistrements non visionnés produisent
        /// moins de <see cref="PluginConfiguration.TonightMinRecommendations"/>
        /// recommandations. Chaque entrée porte l'id Emby (Guid) pour
        /// <c>source="library"</c> + bouton « Regarder » (lecture bibliothèque).
        /// </summary>
        private string BuildLibraryFallbackPool(User user, bool compact)
        {
            var sb = new StringBuilder();
            try
            {
                var q = new InternalItemsQuery
                {
                    User = user,
                    IsPlayed = false,
                    Recursive = true,
                    IncludeItemTypes = new[] { "Movie", "Episode" },
                    OrderBy = new[] { ("DateCreated", SortOrder.Descending) },
                    Limit = compact ? 15 : 30,
                    EnableTotalRecordCount = false
                };
                var items = LibraryManager.GetItemList(q) ?? Array.Empty<BaseItem>();

                // Dédupliquer par titre (un épisode par série suffit pour la réserve).
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var lines = new List<string>();
                foreach (var it in items)
                {
                    if (it == null) continue;
                    var episode = it as MediaBrowser.Controller.Entities.TV.Episode;
                    bool isEpisode = episode != null;
                    string title = isEpisode ? episode.SeriesName : it.Name;
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    string key = LlmRunner.NormTitle(title);
                    if (string.IsNullOrEmpty(key) || !seen.Add(key)) continue;

                    string kind = isEpisode ? "series" : "movie";
                    double? rating = it.CommunityRating;
                    int? year = it.ProductionYear;
                    var genres = it.Genres ?? Array.Empty<string>();

                    var line = new StringBuilder($"- id={it.Id} | title={title} | kind={kind}");
                    if (year.HasValue) line.Append($" | year={year.Value}");
                    if (rating.HasValue) line.Append($" | rating={rating.Value:0.0}");
                    if (genres.Length > 0) line.Append($" | genres=[{string.Join(", ", genres)}]");
                    if (isEpisode && !string.IsNullOrEmpty(it.Name) && it.Name != title)
                        line.Append($" | episode={it.Name}");
                    lines.Add(line.ToString());

                    if (lines.Count >= (compact ? 8 : 20)) break;
                }

                sb.AppendLine("### RÉSERVE BIBLIOTHÈQUE (items NON visionnés — à utiliser SEULEMENT si < minimum)");
                if (lines.Count > 0)
                {
                    sb.AppendLine($"{lines.Count} titre(s) de la bibliothèque non encore regardés par l'usager :");
                    foreach (var l in lines) sb.AppendLine(l);
                    sb.AppendLine("À utiliser comme COMPLÉMENT pour atteindre le minimum de recommandations : " +
                                  "choisis ceux qui matchent le profil de goût, avec source=\"library\" et " +
                                  "l'id fourni (l'UI proposera « Regarder » — lecture depuis la bibliothèque).");
                }
                else
                {
                    sb.AppendLine("(Bibliothèque sans item non visionné — réserve vide.)");
                }
            }
            catch (Exception ex)
            {
                Logger?.Warn("[LLM_AI] BuildLibraryFallbackPool : {0}", ex.Message);
                sb.AppendLine("(Réserve bibliothèque indisponible.)");
            }
            return sb.ToString();
        }
    }
}