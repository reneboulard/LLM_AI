using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using Emby.Notifications;

namespace LLM_AI
{
    /// <summary>
    /// Moteur du run « Gestion bibliothèque » (v2.10.0) : rassemble les
    /// candidats C# (films vus, puis séries partiellement vues — deux pools
    /// séparés, hors <see cref="PluginConfiguration.LibReviewKeptIds"/>),
    /// lance DEUX runs LLM indépendants (un par kind, deux contextes séparés
    /// comme LlmScheduledTask), valide les ids renvoyés (id présent dans le
    /// pool correspondant — un id hors pool = hallucination écartée), et
    /// persiste le rapport brut (id + reason + kind) via
    /// <see cref="LibReviewReportStore"/>.
    ///
    /// Contrat de sortie du LLM : tableau JSON brut
    /// <c>[{ "id": "147321", "reason": "…" }]</c> — id = InternalId en chaîne.
    /// AUCUN enrichissement de reco ici (EnrichRecommendations matche des
    /// titres EPG) : le parse se fait directement sur le payload extrait.
    ///
    /// L'affichage revalide chaque id à la lecture (GET ?Last=true) : un item
    /// effacé entre-temps ne réapparaît jamais.
    ///
    /// Le plugin ne supprime JAMAIS : les suppressions passent par l'endpoint
    /// admin LibReviewDelete (délégation à Emby).
    /// </summary>
    internal static class LibReviewEngine
    {
        /// <summary>
        /// Rôle du LLM pour ce run : lecture seule (outils read-only), la
        /// décision porte sur une liste déjà fournie — aucune action Emby.
        /// </summary>
        internal const string ROLE_INTRO =
            "Tu es un assistant Emby chargé d'évaluer la bibliothèque pour " +
            "proposer des suppressions. Tu as accès à des outils de LECTURE " +
            "SEULE qui interrogent le serveur Emby in-process (bibliothèque, " +
            "guide TV, métadonnées) — tu n'as AUCUN outil d'action et tu ne " +
            "dois JAMAIS prétendre avoir supprimé quoi que ce soit : la " +
            "suppression est toujours exécutée ensuite, manuellement, par " +
            "l'administrateur via Emby.";

        /// <summary>
        /// Bloc format de sortie personnalisé (à la place de « FORMAT DES
        /// RECOMMANDATIONS ») : le contrat [{id, reason}]. Le user prompt
        /// (prompt config) rappelle aussi le gabarit — gemma4 suit le schéma
        /// du user prompt plutôt que celui du system prompt.
        /// </summary>
        internal const string FORMAT_SECTION =
            "### FORMAT DE SORTIE\n" +
            "Ta réponse finale EST un tableau JSON d'objets, chaque objet avec " +
            "exactement deux champs : id (chaîne — l'InternalId recopié TEL QUEL " +
            "depuis la liste des candidats fournie dans le message user) et reason " +
            "(une phrase concrète justifiant la suppression). Exemple :\n" +
            "[{\"id\":\"147321\",\"reason\":\"Vu une seule fois le 2024-03-12, copie présente aussi en bibliothèque.\"}]\n" +
            "Règles : premier caractère = [, dernier = ] (sans texte autour, sans " +
            "balises ```) ; JAMAIS de prose, de tiret (« - »), de « Aucun » ou de " +
            "commentaire à la place du tableau — si rien ne se justifie, la réponse " +
            "est exactement [] et RIEN d'autre ; jamais un id absent de la liste des " +
            "candidats ; échappe les guillemets doubles internes (\\\") dans reason.";

        private const int MaxProposalsPerRun = 20;

        /// <summary>
        /// Exécute un run complet « Gestion bibliothèque » : rassemblement +
        /// deux runs LLM + validation + persistance. Retourne le rapport
        /// persisté (ou null si échec total). Les jalons de progression sont
        /// publiés via <paramref name="progress"/> (LibReviewRunState.SetProgress
        /// ou no-op). <see cref="OperationCanceledException"/> propagée.
        /// </summary>
        public static async Task<LastLibReviewReport> RunAsync(
            PluginConfiguration cfg,
            string brief,
            ILibraryManager library,
            IUserManager users,
            IUserDataManager userData,
            ILiveTvManager liveTv,
            IServerApplicationHost host,
            IJsonSerializer json,
            INotificationManager notifications,
            ILogger logger,
            Action<string> progress,
            CancellationToken ct)
        {
            progress ??= _ => { };
            var kept = ParseKeptIds(cfg.LibReviewKeptIds);

            int cap = cfg.LibReviewMaxBatch > 0 ? cfg.LibReviewMaxBatch : 60;

            progress(I18n.SDisplay("librev.progress.collect", host) ?? "Rassemblement des candidats…");

            // ---- Pool FILMS : movies vus par au moins un usager, hors kept,
            //      triés du plus ancien au plus récent, capés.
            var moviePool = CollectMovies(library, users, userData, kept, cap, logger, ct);
            // ---- Pool SÉRIES : séries partiellement vues, hors kept, hors
            //      séries à enregistrer (SeriesTimers + timers programmés —
            //      même règle que emby-delete-series.sh : exclusion stricte par
            //      titre normalisé EXACT), capées.
            int timerExcluded = 0;
            var seriesPool = CollectSeries(library, users, userData, liveTv, kept, cap, ref timerExcluded, logger, ct);

            int totalCandidates = moviePool.Count + seriesPool.Count;
            logger.Info("[LLM_AI] [LibReview] Candidats : {0} film(s), {1} série(s) (hors {2} conservé(s), {3} série(s) en enregistrement).",
                moviePool.Count, seriesPool.Count, kept.Count, timerExcluded);

            var report = new LastLibReviewReport
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                Brief = brief ?? "",
                Candidates = totalCandidates
            };

            var runner = new LlmRunner(logger, json, library, users, liveTv, host);
            string proseLang = I18n.ResolveProseLangName(cfg, host);

            string moviesPrompt = ParsePrompt(cfg.LibReviewTask);
            string seriesPrompt = ParsePrompt(cfg.LibReviewTaskSeries);
            if (!string.IsNullOrWhiteSpace(brief))
            {
                if (!string.IsNullOrWhiteSpace(moviesPrompt)) moviesPrompt += "\n\nCONSigne de l'administrateur pour ce run :\n" + brief.Trim();
                if (!string.IsNullOrWhiteSpace(seriesPrompt)) seriesPrompt += "\n\nConsigne de l'administrateur pour ce run :\n" + brief.Trim();
            }

            // Deux runs indépendants (deux contextes) — un échec de l'un
            // n'empêche pas l'autre (même doctrine que LlmScheduledTask).
            if (!string.IsNullOrWhiteSpace(moviesPrompt) && moviePool.Count > 0)
            {
                progress(string.Format(CultureInfo.InvariantCulture,
                    I18n.SDisplay("librev.progress.movies", host) ?? "Évaluation des films ({0} candidats)…",
                    moviePool.Count));
                var entries = await RunOneAsync(runner, cfg, proseLang, "LibReview-FILMS", moviesPrompt,
                    SerializePool(moviePool), "movie", knownIds: null, library, json, logger, progress, ct)
                    .ConfigureAwait(false);
                ValidateAndAdd(report, entries, moviePool, "movie", logger);
            }
            else if (moviePool.Count == 0)
            {
                logger.Info("[LLM_AI] [LibReview] Run FILMS ignoré : aucun candidat.");
            }

            if (!string.IsNullOrWhiteSpace(seriesPrompt) && seriesPool.Count > 0)
            {
                progress(string.Format(CultureInfo.InvariantCulture,
                    I18n.SDisplay("librev.progress.series", host) ?? "Évaluation des séries ({0} candidats)…",
                    seriesPool.Count));
                var known = new HashSet<string>(seriesPool.Select(p => p.Id), StringComparer.Ordinal);
                var entries = await RunOneAsync(runner, cfg, proseLang, "LibReview-SÉRIES", seriesPrompt,
                    SerializePool(seriesPool), "series", known, library, json, logger, progress, ct)
                    .ConfigureAwait(false);
                ValidateAndAdd(report, entries, seriesPool, "series", logger);
            }
            else if (seriesPool.Count == 0)
            {
                logger.Info("[LLM_AI] [LibReview] Run SÉRIES ignoré : aucun candidat.");
            }

            LibReviewReportStore.Save(report, logger);
            logger.Info("[LLM_AI] [LibReview] Rapport persisté : {0} proposition(s) sur {1} candidat(s).",
                report.Items.Count, totalCandidates);

            try
            {
                NotifyAdmins(notifications, users, host, cfg, report.Items.Count, totalCandidates, logger);
            }
            catch (Exception ex)
            {
                logger.Warn("[LLM_AI] [LibReview] Notification admin échouée : {0}", ex.Message);
            }

            return report;
        }

        // ------------------------------------------------------------------
        //  Un run LLM (un kind) : agent read-only, format [{id, reason}],
        //  AUCUN enrichissement reco — parse direct du payload extrait.
        // ------------------------------------------------------------------

        private static async Task<List<LibReviewEntry>> RunOneAsync(
            LlmRunner runner, PluginConfiguration cfg, string proseLang, string label, string userPrompt,
            string poolJson, string kind, HashSet<string> knownIds,
            ILibraryManager library, IJsonSerializer json, ILogger logger,
            Action<string> progress, CancellationToken ct)
        {
            var backends = runner.ResolveBackends(cfg);
            if (backends.Count == 0)
            {
                logger.Warn("[LLM_AI] [{0}] Aucun LLM configuré/activé — run ignoré.", label);
                return new List<LibReviewEntry>();
            }

            string ollamaCloudKey = LlmRunner.ResolveKey(cfg.OllamaApiKey, "OLLAMA_API_KEY");
            string geminiKey = LlmRunner.ResolveKey(cfg.GeminiApiKey, "GEMINI_API_KEY");

            var agent = new LlmAgentService(backends, cfg.RagDirectives, "",
                ollamaCloudKey, geminiKey, json, logger, cfg.DebugVerbose,
                roleIntro: ROLE_INTRO,
                formatSection: FORMAT_SECTION,
                responseLanguage: proseLang);
            // Outils read-only (get_emby_info + web) — la liste des candidats
            // est déjà dans le user prompt ; les outils servent à creuser si
            // le modèle veut vérifier. Aucune couche d'action (jamais).
            var tools = runner.BuildTools(cfg, null);

            string fullUser = userPrompt.Trim() + "\n\n### CANDIDATS (" + kind.ToUpperInvariant() + ")\n" +
                "Liste fournie par l'extension — ne recommande QUE des id de cette liste :\n" + poolJson;

            var (reply, _toolResults) = await agent.RunAsync(fullUser, tools, ct).ConfigureAwait(false);
            logger.Info("[LLM_AI] [{0}] Réponse brute (tronquée) :\n{1}", label, Truncate(reply, 1500));

            // v2.10.5 : tolérance « réponse courte » — un petit modèle local
            // peut répondre « - » ou « Aucun. » au lieu de [] (vu en prod
            // 2026-11-02, ministral-3:14b, run FILMS/SÉRIES → 0 proposition).
            // Toute réponse SANS tableau JSON = liste vide valide (côté sûr :
            // aucune suppression proposée), et on ne logue que le début pour
            // diagnostiquer. Jamais d'invention de propositions ici.
            string payload = LlmRunner.ExtractJsonPayload(reply);
            if (!payload.StartsWith("[", StringComparison.Ordinal))
            {
                logger.Info("[LLM_AI] [{0}] Réponse sans tableau JSON (« {1} ») — interprétée comme liste vide [].",
                    label, Truncate(payload, 80));
                payload = "[]";
            }
            return ParseProposals(payload, kind, knownIds, library, logger);
        }

        /// <summary>
        /// Parse le payload [{id, reason}…] et valide chaque id contre le
        /// pool du run (knownIds null pour les films — set construit ici à
        /// partir du pool passé à ValidateAndAdd ; pour les séries, passé
        /// explicitement). Id inconnu → écarté + log (hallucination).
        /// Cap 20 entrées au total par run.
        /// </summary>
        private static List<LibReviewEntry> ParseProposals(string payload, string kind,
            HashSet<string> knownIds, ILibraryManager library, ILogger logger)
        {
            var list = new List<LibReviewEntry>();
            if (string.IsNullOrWhiteSpace(payload)) return list;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    logger.Warn("[LLM_AI] [LibReview-{0}] Payload final non tableau — ignoré.", kind.ToUpperInvariant());
                    return list;
                }
                int n = 0;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (n >= MaxProposalsPerRun) break;
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    string id = null, reason = "";
                    if (el.TryGetProperty("id", out var idEl))
                    {
                        if (idEl.ValueKind == JsonValueKind.String) id = idEl.GetString();
                        else if (idEl.ValueKind == JsonValueKind.Number) id = idEl.GetRawText();
                    }
                    if (el.TryGetProperty("reason", out var rEl) && rEl.ValueKind == JsonValueKind.String)
                        reason = rEl.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    id = id.Trim();
                    // Validation : l'id doit exister en bibliothèque (InternalId)
                    // — un id inventé ne résout pas. Pour les séries, il doit
                    // EN PLUS appartenir au pool (knownIds).
                    var item = ItemIdResolver.Resolve(library, id);
                    if (item == null)
                    {
                        logger.Warn("[LLM_AI] [LibReview-{0}] id « {1} » inconnu de la bibliothèque — écarté (hallucination ?).",
                            kind.ToUpperInvariant(), id);
                        continue;
                    }
                    if (knownIds != null && !knownIds.Contains(id))
                    {
                        logger.Warn("[LLM_AI] [LibReview-{0}] id « {1} » (« {2} ») hors pool des candidats — écarté.",
                            kind.ToUpperInvariant(), id, item.Name);
                        continue;
                    }
                    list.Add(new LibReviewEntry { Id = id, Kind = kind, Reason = reason });
                    n++;
                }
            }
            catch (Exception ex)
            {
                logger.Warn("[LLM_AI] [LibReview-{0}] Parse du payload échoué : {1}", kind.ToUpperInvariant(), ex.Message);
            }
            return list;
        }

        /// <summary>Ajoute les entrées validées au rapport (dédup par id).</summary>
        private static void ValidateAndAdd(LastLibReviewReport report, List<LibReviewEntry> entries,
            List<PoolItem> pool, string kind, ILogger logger)
        {
            var known = new HashSet<string>(pool.Select(p => p.Id), StringComparer.Ordinal);
            var seen = new HashSet<string>(report.Items.Select(i => i.Id), StringComparer.Ordinal);
            foreach (var e in entries)
            {
                if (!known.Contains(e.Id))
                {
                    logger.Warn("[LLM_AI] [LibReview-{0}] id « {1} » hors pool — écarté à l'ajout.", kind.ToUpperInvariant(), e.Id);
                    continue;
                }
                if (!seen.Add(e.Id)) continue; // dédup
                report.Items.Add(e);
            }
        }

        // ------------------------------------------------------------------
        //  Rassemblement des candidats (C#, read-only)
        // ------------------------------------------------------------------

        private sealed class PoolItem
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public double SizeGb { get; set; }
            public string DateAdded { get; set; }
            public string LastPlayed { get; set; }
            public int EpisodeCount { get; set; }
            public int UnwatchedCount { get; set; }
            // v2.10.6 — mêmes motifs que emby-delete-series.sh : le LLM justifie
            // mieux quand il voit rating/année/statut/genres/synopsis.
            public double? CommunityRating { get; set; }
            public int? ProductionYear { get; set; }
            public string Status { get; set; }        // séries : "Ended"/"Continuing"…
            public string[] Genres { get; set; }
            public string Overview { get; set; }      // tronqué à MaxOverviewChars
        }

        /// <summary>Synopsis tronqué envoyé au LLM (budget contexte).</summary>
        internal const int MaxOverviewChars = 400;

        private static string SerializePool(List<PoolItem> pool)
        {
            // JSON compact, champs traînants vides retirés pour alléger le contexte.
            var sb = new StringBuilder("[");
            for (int i = 0; i < pool.Count; i++)
            {
                var p = pool[i];
                if (i > 0) sb.Append(',');
                sb.Append('{');
                sb.Append("\"id\":\"").Append(p.Id).Append("\",");
                sb.Append("\"name\":").Append(JsonSerializer.Serialize(p.Name ?? "")).Append(',');
                sb.Append("\"size_gb\":").Append(p.SizeGb.ToString("0.##", CultureInfo.InvariantCulture)).Append(',');
                if (!string.IsNullOrEmpty(p.DateAdded)) sb.Append("\"date_added\":").Append(JsonSerializer.Serialize(p.DateAdded)).Append(',');
                if (!string.IsNullOrEmpty(p.LastPlayed)) sb.Append("\"last_played\":").Append(JsonSerializer.Serialize(p.LastPlayed)).Append(',');
                if (p.EpisodeCount > 0) sb.Append("\"episode_count\":").Append(p.EpisodeCount).Append(',');
                if (p.UnwatchedCount > 0) sb.Append("\"unwatched_count\":").Append(p.UnwatchedCount).Append(',');
                if (p.CommunityRating.HasValue) sb.Append("\"community_rating\":").Append(p.CommunityRating.Value.ToString("0.0#", CultureInfo.InvariantCulture)).Append(',');
                if (p.ProductionYear.HasValue) sb.Append("\"production_year\":").Append(p.ProductionYear.Value).Append(',');
                if (!string.IsNullOrEmpty(p.Status)) sb.Append("\"status\":").Append(JsonSerializer.Serialize(p.Status)).Append(',');
                if (p.Genres != null && p.Genres.Length > 0) sb.Append("\"genres\":").Append(JsonSerializer.Serialize(p.Genres)).Append(',');
                if (!string.IsNullOrEmpty(p.Overview)) sb.Append("\"overview\":").Append(JsonSerializer.Serialize(p.Overview)).Append(',');
                sb.Length--; // virgule terminale
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>Films vus (union des usagers), hors kept, du plus ancien au plus récent, capés.</summary>
        private static List<PoolItem> CollectMovies(ILibraryManager library, IUserManager users,
            IUserDataManager userData, HashSet<string> kept, int cap, ILogger logger, CancellationToken ct)
        {
            var played = CollectPlayedPerUser(library, users, userData, new[] { "Movie" }, logger, ct);
            var list = new List<PoolItem>();
            foreach (var kv in played.OrderBy(p => p.Value.LastPlayedUtc ?? DateTime.MinValue))
            {
                var item = kv.Key;
                string id = item.InternalId.ToString(CultureInfo.InvariantCulture);
                if (kept.Contains(id)) continue;
                list.Add(new PoolItem
                {
                    Id = id,
                    Name = item.Name ?? "",
                    SizeGb = FileSizeGb(item.Path, logger),
                    DateAdded = item.DateCreated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    LastPlayed = kv.Value.LastPlayedUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                    CommunityRating = item.CommunityRating,
                    ProductionYear = item.ProductionYear,
                    Status = null, // les films n'ont pas de statut de diffusion
                    Genres = item.Genres,
                    Overview = TruncateOverview(item.Overview)
                });
                if (list.Count >= cap) break;
            }
            return list;
        }

        /// <summary>
        /// Noms normalisés (FoldAscii + minuscules + non-alnum retiré, via
        /// <see cref="GetEmbyInfoTool.Norm"/>) des séries « à enregistrer » :
        /// SeriesTimers (récurrents) + Timers programmés (SeriesName sinon Name).
        /// Même règle qu'emby-delete-series.sh : exclusion STRICTE par titre
        /// normalisé exact (les correspondances partielles préfixe/contenu ne
        /// sont qu'un drapeau « limite timer » dans le script, JAMAIS une
        /// exclusion).
        /// </summary>
        private static HashSet<string> TimerSeriesNames(ILiveTvManager liveTv)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var st = liveTv.GetSeriesTimers(new SeriesTimerQuery())?.Items;
                if (st != null)
                    foreach (var s in st)
                        if (!string.IsNullOrEmpty(s.Name)) set.Add(GetEmbyInfoTool.Norm(s.Name));
                var tt = liveTv.GetTimers(new TimerQuery { IsScheduled = true })?.Items;
                if (tt != null)
                    foreach (var t in tt)
                    {
                        var nm = t.ProgramInfo != null
                            ? (!string.IsNullOrEmpty(t.ProgramInfo.SeriesName) ? t.ProgramInfo.SeriesName : t.ProgramInfo.Name)
                            : null;
                        if (!string.IsNullOrEmpty(nm)) set.Add(GetEmbyInfoTool.Norm(nm));
                    }
            }
            catch (Exception)
            {
                // Fail-open documenté : sans liste de timers on ne peut exclure
                // proprement — on logue dans l'appelant (le set reste vide).
                return set;
            }
            return set;
        }

        /// <summary>
        /// Séries partiellement vues (au moins un épisode vu par un usager),
        /// hors kept, HORS séries à enregistrer (titres des timers, exclusion
        /// exacte par <see cref="GetEmbyInfoTool.Norm"/> — v2.10.2, même règle
        /// qu'emby-delete-series.sh : « Découverte » ou toute série en cours
        /// d'enregistrement ne doit JAMAIS être proposée), capées. Pour chaque
        /// série : nombre d'épisodes, non vus, taille totale, dernier visionnage.
        /// <paramref name="timerExcluded"/> reçoit le nombre de séries retirées
        /// par cette exclusion (loggé).
        /// </summary>
        private static List<PoolItem> CollectSeries(ILibraryManager library, IUserManager users,
            IUserDataManager userData, ILiveTvManager liveTv, HashSet<string> kept, int cap,
            ref int timerExcluded, ILogger logger, CancellationToken ct)
        {
            var timerNames = TimerSeriesNames(liveTv);
            if (timerNames.Count > 0)
                logger?.Info("[LLM_AI] [LibReview] {0} titre(s) de timer actif(s) à exclure du pool séries.", timerNames.Count);

            var playedEpisodes = CollectPlayedPerUser(library, users, userData, new[] { "Episode" }, logger, ct);
            var seriesIds = new HashSet<Guid>();
            var lastPlayedBySeries = new Dictionary<Guid, DateTimeOffset?>();
            foreach (var kv in playedEpisodes)
            {
                var ep = kv.Key as Episode;
                Series series = ep?.Series;
                if (series == null && ep != null && ep.SeriesId != 0)
                    series = library.GetItemById(ep.SeriesId) as Series;
                if (series == null) continue;
                seriesIds.Add(series.Id);
                var lp = kv.Value.LastPlayedUtc;
                if (!lastPlayedBySeries.TryGetValue(series.Id, out var cur) || (lp != null && lp > cur))
                    lastPlayedBySeries[series.Id] = lp;
            }

            timerExcluded = 0;
            var list = new List<PoolItem>();
            foreach (var sid in seriesIds)
            {
                ct.ThrowIfCancellationRequested();
                var seriesBase = library.GetItemById(sid);
                var series = seriesBase as Series;
                if (series == null) continue;
                string id = series.InternalId.ToString(CultureInfo.InvariantCulture);
                if (kept.Contains(id)) continue;
                // Exclusion « série à enregistrer » (titre normalisé exact).
                if (timerNames.Count > 0 && !string.IsNullOrEmpty(series.Name)
                    && timerNames.Contains(GetEmbyInfoTool.Norm(series.Name)))
                {
                    timerExcluded++;
                    logger?.Info("[LLM_AI] [LibReview] Série « {0} » exclue du pool : timer d'enregistrement actif.", series.Name);
                    continue;
                }

                int epCount = 0, unwatched = 0;
                long totalBytes = 0;
                try
                {
                    var eps = library.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { "Episode" },
                        AncestorIds = new[] { series.InternalId },
                        Recursive = true,
                        EnableTotalRecordCount = false
                    }) ?? Array.Empty<BaseItem>();
                    foreach (var e in eps)
                    {
                        epCount++;
                        totalBytes += FileSizeOrNull(e.Path, logger);
                        if (!IsPlayedByAny(e, users, userData)) unwatched++;
                    }
                }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] [LibReview] Épisodes de « {0} » illisibles : {1}", series.Name, ex.Message);
                }

                list.Add(new PoolItem
                {
                    Id = id,
                    Name = series.Name ?? "",
                    SizeGb = totalBytes / (double)(1024L * 1024 * 1024),
                    DateAdded = series.DateCreated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    LastPlayed = lastPlayedBySeries.TryGetValue(sid, out var lp) && lp != null
                        ? lp.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "",
                    EpisodeCount = epCount,
                    UnwatchedCount = unwatched,
                    CommunityRating = series.CommunityRating,
                    ProductionYear = series.ProductionYear,
                    Status = series.Status?.ToString(),
                    Genres = series.Genres,
                    Overview = TruncateOverview(series.Overview)
                });
                if (list.Count >= cap) break;
            }
            // Séries les plus anciennement vues d'abord (candidats les plus mûrs).
            return list.OrderByDescending(p => p.LastPlayed).ToList();
        }

        /// <summary>Dernier visionnage connu d'un item (union des usagers, via IUserDataManager).</summary>
        private sealed class PlayedInfo
        {
            public DateTimeOffset? LastPlayedUtc;
        }

        /// <summary>
        /// Items joués (union de tous les usagers) pour les types donnés :
        /// map item → date du dernier visionnage (max des UserData). La
        /// requête IsPlayed est par usager → union sur la liste des usagers,
        /// même pattern que RecordingDiskManager ; la date exacte vient de
        /// IUserDataManager.GetUserData (pattern RecoAnalysisTask).
        /// </summary>
        private static Dictionary<BaseItem, PlayedInfo> CollectPlayedPerUser(ILibraryManager library,
            IUserManager users, IUserDataManager userData, string[] types, ILogger logger, CancellationToken ct)
        {
            var result = new Dictionary<Guid, (BaseItem item, DateTimeOffset? lp)>();
            List<User> userList;
            try { userList = users?.GetUserList(new UserQuery()).ToList() ?? new List<User>(); }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] [LibReview] Lecture des usagers échouée : {0}", ex.Message);
                return new Dictionary<BaseItem, PlayedInfo>();
            }
            foreach (var u in userList)
            {
                if (u == null) continue;
                ct.ThrowIfCancellationRequested();
                try
                {
                    var q = new InternalItemsQuery
                    {
                        User = u,
                        IsPlayed = true,
                        IncludeItemTypes = types,
                        Recursive = true,
                        EnableTotalRecordCount = false
                        // NB : AUCUN filtre Tags/Genres assigné (piège InternalItemsQuery :
                        // ne JAMAIS poser null sur un autre filtre tableau).
                    };
                    foreach (var it in library.GetItemList(q) ?? Array.Empty<BaseItem>())
                    {
                        if (it == null || it.Id == Guid.Empty) continue;
                        DateTimeOffset? lp = null;
                        try { lp = userData?.GetUserData(u, it)?.LastPlayedDate; }
                        catch { lp = null; }
                        if (!result.TryGetValue(it.Id, out var cur) || (lp != null && lp > cur.lp))
                            result[it.Id] = (it, lp);
                    }
                }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] [LibReview] Requête joués (usager {0}) échouée : {1}", u.Name, ex.Message);
                }
            }
            var map = new Dictionary<BaseItem, PlayedInfo>();
            foreach (var kv in result)
                map[kv.Value.item] = new PlayedInfo { LastPlayedUtc = kv.Value.lp };
            return map;
        }

        private static bool IsPlayedByAny(BaseItem item, IUserManager users, IUserDataManager userData)
        {
            try
            {
                foreach (var u in users?.GetUserList(new UserQuery()) ?? Array.Empty<User>())
                {
                    var ud = userData?.GetUserData(u, item);
                    if (ud != null && ud.Played) return true;
                }
            }
            catch { /* best-effort */ }
            return false;
        }

        // ------------------------------------------------------------------
        //  Helpers
        // ------------------------------------------------------------------

        /// <summary>Parse la liste « Conserver » (tableau JSON d'ids en chaînes). Tolérant.</summary>
        internal static HashSet<string> ParseKeptIds(string json)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(json)) return set;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        string s = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
                        if (!string.IsNullOrWhiteSpace(s)) set.Add(s.Trim());
                    }
                }
            }
            catch { /* liste illisible = vide (fail-open) */ }
            return set;
        }

        /// <summary>Extrait le texte du prompt (droite du '|') d'un champ LibReviewTask*.</summary>
        internal static string ParsePrompt(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return string.Empty;
            int sep = spec.IndexOf('|');
            return (sep >= 0 ? spec.Substring(sep + 1) : spec).Trim();
        }

        /// <summary>Extrait la partie planification (gauche du '|').</summary>
        internal static string ParseSchedule(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return string.Empty;
            int sep = spec.IndexOf('|');
            return (sep >= 0 ? spec.Substring(0, sep) : spec).Trim();
        }

        internal static double FileSizeGb(string path, ILogger logger) =>
            FileSizeOrNull(path, logger) / (double)(1024L * 1024 * 1024);

        /// <summary>Synopsis aplati + tronqué pour le pool LLM.</summary>
        internal static string TruncateOverview(string overview)
        {
            if (string.IsNullOrWhiteSpace(overview)) return null;
            var s = System.Text.RegularExpressions.Regex.Replace(overview.Trim(), @"\s+", " ");
            return s.Length <= MaxOverviewChars ? s : s.Substring(0, MaxOverviewChars) + "…";
        }

        internal static long FileSizeOrNull(string path, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(path)) return 0;
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists ? fi.Length : 0;
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] [LibReview] Taille de « {0} » illisible : {1}", path, ex.Message);
                return 0;
            }
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");

        // ------------------------------------------------------------------
        //  Notification admin (même pattern que LlmScheduledTask) : le
        //  rapport de nettoyage n'intéresse que l'admin (seul lui peut
        //  agir sur les cartes). Lien profond aussi en description (v2.4.5).
        // ------------------------------------------------------------------

        private static void NotifyAdmins(INotificationManager notifications, IUserManager users,
            IServerApplicationHost host, PluginConfiguration cfg, int count, int candidates, ILogger logger)
        {
            if (notifications == null || users == null) return;
            List<User> admins;
            try
            {
                admins = users.GetUserList(new UserQuery())
                    .Where(u => u != null && (u.Policy?.IsAdministrator ?? false))
                    .ToList();
            }
            catch { return; }
            if (admins.Count == 0)
            {
                logger?.Warn("[LLM_AI] [LibReview] Notification NON envoyée — aucun administrateur.");
                return;
            }

            string url = ResolveEmbyUrl(cfg, host);
            string title = I18n.SDisplay("librev.notif.title", host) ?? "LLM AI — Gestion bibliothèque";
            string desc = string.Format(CultureInfo.InvariantCulture,
                I18n.SDisplay("librev.notif.desc", host) ??
                "{0} proposition(s) de nettoyage sur {1} candidat(s) — onglet « Gestion bibliothèque » de la configuration.",
                count, candidates);
            if (!string.IsNullOrWhiteSpace(url))
                desc = desc + "\n" + url + "/web/configurationpage?name=LLMAIConfigPage#tab-libreview";

            var now = DateTimeOffset.UtcNow;
            int sent = 0;
            foreach (var u in admins)
            {
                try
                {
                    notifications.SendNotification(new NotificationRequest
                    {
                        Title = title,
                        Description = desc,
                        Url = string.IsNullOrWhiteSpace(url) ? null : url + "/web/configurationpage?name=LLMAIConfigPage#tab-libreview",
                        Date = now,
                        Severity = LogSeverity.Info,
                        User = u
                    });
                    sent++;
                }
                catch (Exception ex)
                {
                    logger?.ErrorException("[LLM_AI] [LibReview] Notification à « {0} » échouée : {1}", ex, u.Name, ex.Message);
                }
            }
            logger?.Info("[LLM_AI] [LibReview] Notification envoyée à {0}/{1} administrateur(s).", sent, admins.Count);
        }

        private static string ResolveEmbyUrl(PluginConfiguration cfg, IServerApplicationHost host)
        {
            string publicUrl = (cfg?.EmbyPublicUrl ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(publicUrl)) return publicUrl;
            try
            {
                string local = host?.GetLocalHostApiUrl();
                if (!string.IsNullOrWhiteSpace(local)) return local;
            }
            catch { /* best-effort */ }
            return string.Empty;
        }
    }
}
