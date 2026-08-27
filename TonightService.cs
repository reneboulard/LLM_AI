using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;

namespace LLM_AI
{
    /// <summary>
    /// Génération partagée de la section « À regarder ce soir » : construit le
    /// profil de goût de l'usager, les enregistrements récents non visionnés et
    /// la réserve bibliothèque, lance le run agent LLM (via
    /// <see cref="LlmRunner"/>), enrichit, et met en cache le résultat par
    /// usager. Centralise cette logique pour qu'elle soit réutilisée par
    /// l'endpoint HTTP (<c>TonightApiService</c>) ET par le déclencheur de login
    /// (<c>TonightLoginService</c>) — un seul run par usager par fenêtre de
    /// cache, même sur plusieurs appareils.
    /// <para>Le cache par usager est <b>statique</b> (partagé entre toutes les
    /// instances du service) : l'endpoint, le login et la page web voient le
    /// même cache. TTL = <see cref="PluginConfiguration.TonightCacheHours"/>.
    /// </para>
    /// </summary>
    internal class TonightService
    {
        private readonly IUserManager _users;
        private readonly ILibraryManager _library;
        private readonly ILiveTvManager _liveTv;
        private readonly IJsonSerializer _json;
        private readonly IServerApplicationHost _host;
        private readonly ILogger _logger;

        public TonightService(IUserManager users, ILibraryManager library,
            ILiveTvManager liveTv, IJsonSerializer json, IServerApplicationHost host,
            ILogger logger)
        {
            _users = users;
            _library = library;
            _liveTv = liveTv;
            _json = json;
            _host = host;
            _logger = logger;
        }

        // Workflow injecté dans le system prompt de l'agent (bloc
        // « WORKFLOW DE RECOMMANDATION ») pour le run « ce soir ».
        internal const string TONIGHT_WORKFLOW =
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

        // ------------------------------------------------------------------
        //  Résultat de génération
        // ------------------------------------------------------------------

        /// <summary>
        /// Résultat d'un run « ce soir ». <see cref="Payload"/> est le tableau
        /// JSON de recommandations (chaîne, éventuellement vide). <see cref="Date"/>
        /// : date/heure (UTC ISO) de production. <see cref="FromCache"/> : true si
        /// servi depuis le cache (pas de run LLM). <see cref="Error"/> : message
        /// d'erreur (null si succès).
        /// </summary>
        public struct TonightResult
        {
            public string Payload;
            public string Date;
            public bool FromCache;
            public string Error;
        }

        // ------------------------------------------------------------------
        //  Cache par usager (in-memory, statique, partagé ; TTL = TonightCacheHours)
        // ------------------------------------------------------------------

        private class CacheEntry
        {
            public string Items;
            public string Date;
            public DateTimeOffset ExpiresAt;
        }

        private static readonly Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>();
        private static readonly object _cacheLock = new object();

        /// <summary>
        /// Renvoie le résultat caché pour un usager S'il est encore frais (sans
        /// relancer le LLM). Retourne null si pas de cache ou cache expiré.
        /// Utilisé par le déclencheur de login pour distinguer le chemin
        /// « cache frais → toast immédiat, pas de run » du chemin
        /// « cache froid → run LLM puis toast ».
        /// </summary>
        internal static string TryGetCached(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return null;
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(userId, out var e) && e.ExpiresAt > DateTimeOffset.UtcNow)
                    return e.Items;
            }
            return null;
        }

        /// <summary>
        /// Invalide le cache d'un usager (force le prochain appel à relancer le
        /// LLM). Utilisé par le login après auto-programmation si l'on veut
        /// rafraîchir. Optionnel — l'endpoint utilise <c>Refresh=1</c>.
        /// </summary>
        internal static void InvalidateUser(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return;
            lock (_cacheLock) { _cache.Remove(userId); }
        }

        // ------------------------------------------------------------------
        //  Génération (run LLM + cache)
        // ------------------------------------------------------------------

        /// <summary>
        /// Produit les recommandations « ce soir » pour un usager. Gère le cache
        /// (sauf <paramref name="refresh"/>) : un résultat frais est renvoyé
        /// immédiatement sans relancer le LLM. Sinon : construit le profil de
        /// goût + les enregistrements non visionnés + la réserve bibliothèque,
        /// lance le run agent, enrichit, met en cache. L'appelant décide quoi
        /// faire du payload (l'afficher, l'auto-programmer, envoyer un toast).
        /// Ne vérifie PAS <see cref="PluginConfiguration.TonightEnabled"/> —
        /// c'est à l'appelant (endpoint / login) de le faire selon son contexte.
        /// </summary>
        public async Task<TonightResult> GenerateTonightAsync(User user, PluginConfiguration cfg,
            bool refresh, CancellationToken ct)
        {
            if (cfg == null)
                return new TonightResult { Error = "Configuration du plugin indisponible." };
            if (user == null)
                return new TonightResult { Error = "Utilisateur non résolu." };

            // Cache par usager (sauf Refresh).
            int cacheHours = Math.Max(0, cfg.TonightCacheHours);
            string cacheKey = user.Id.ToString();
            if (!refresh && cacheHours > 0)
            {
                lock (_cacheLock)
                {
                    if (_cache.TryGetValue(cacheKey, out var e) && e.ExpiresAt > DateTimeOffset.UtcNow)
                        return new TonightResult { Payload = e.Items, Date = e.Date, FromCache = true };
                }
            }

            var runner = new LlmRunner(_logger, _json, _library, _users, _liveTv, _host);

            // Mode compact : si le backend LLM principal est un Ollama LOCAL
            // (modèle souvent petit, fenêtre de contexte limitée), on réduit les
            // données injectées (profil, enregistrements, réserve) pour éviter
            // de « noyer » le modèle. Sur cloud (ollama_cloud / gemini), on
            // garde toutes les données (qualité optimale).
            bool compact = IsLocalPrimary(cfg);
            if (compact) _logger?.Info("[LLM_AI] Tonight : backend local détecté — mode compact (données injectées réduites).");

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
            int minRec = Math.Max(0, cfg.TonightMinRecommendations);
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
                return new TonightResult { Error = "Le run LLM n'a pas produit de recommandation." };

            // Enrichissement bibliothèque : pour les reco source="live" dont le
            // titre est déjà possédé, injecte library_id (bouton « Regarder »
            // depuis la bibliothèque + signal owned-guard pour AutoProgrammer).
            // S'exécute après l'enrichissement EPG.
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

            return new TonightResult { Payload = payload, Date = date, FromCache = false };
        }

        /// <summary>
        /// Détermine si le backend LLM de plus haute priorité est un Ollama
        /// <b>local</b> (modèle souvent petit, contexte limité). Sert à activer le
        /// mode compact (données injectées réduites). Reflète le filtrage de
        /// <see cref="LlmRunner.ResolveBackends"/> sans logger (appelé avant le
        /// run). En config legacy (LlmUrl sans LlmBackends), c'est local par
        /// construction.
        /// </summary>
        internal static bool IsLocalPrimary(PluginConfiguration cfg)
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
                var items = _library.GetItemList(q) ?? Array.Empty<BaseItem>();

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
                _logger?.Warn("[LLM_AI] BuildTasteProfile : {0}", ex.Message);
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
        /// que l'UI propose « Regarder ».
        /// <para>Résolu ici (et pas via un tool <c>get_emby_info</c>) car
        /// <c>IsPlayed</c> est par usager : le tool est global/sans usager.</para>
        /// </summary>
        private string BuildUnwatchedRecordings(User user, PluginConfiguration cfg, CancellationToken ct, bool compact)
        {
            var sb = new StringBuilder();
            try
            {
                int days = Math.Max(0, cfg?.TonightRecordingsDays ?? 7);
                var cutoff = DateTimeOffset.Now.AddDays(-days);

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
                    if ((r.DateCreated ?? DateTimeOffset.MinValue) < cutoff) continue;
                    if (r.UserData?.Played == true) continue;

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
                _logger?.Warn("[LLM_AI] BuildUnwatchedRecordings : {0}", ex.Message);
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
                var items = _library.GetItemList(q) ?? Array.Empty<BaseItem>();

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
                _logger?.Warn("[LLM_AI] BuildLibraryFallbackPool : {0}", ex.Message);
                sb.AppendLine("(Réserve bibliothèque indisponible.)");
            }
            return sb.ToString();
        }
    }
}