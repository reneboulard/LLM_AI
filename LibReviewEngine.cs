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
using MediaBrowser.Model.Entities;
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
    /// persiste le rapport brut via <see cref="LibReviewReportStore"/>.
    ///
    /// Contrat de sortie du LLM (v2.12.0) : tableau JSON brut qui classe
    /// CHAQUE candidat — <c>[{ "id": "147321", "section":
    /// "delete|can_delete|keep", "reason": "…" }]</c>, id = InternalId en
    /// chaîne, exactement UNE entrée par candidat. Repris d'emby-delete.sh /
    /// emby-delete-series.sh (vécu prod : le MÊME modèle local classe
    /// correctement sous ce contrat mais répondait « - » / « Aucun. » sous
    /// l'ancien contrat de sélection conservatrice — l'échappatoire « vide
    /// [] si rien ne se justifie » est le chemin de moindre résistance d'un
    /// petit modèle). Rétro-compat : une réponse SANS champ « section » sur
    /// toutes les entrées = ancien contrat [{id, reason}] (prompt stocké
    /// d'une install existante) → entrées traitées comme propositions de
    /// suppression, comportement v2.10.x inchangé.
    ///
    /// Run « liste fermée » (v2.12.0) : AUCUN outil injecté (la liste est
    /// complète dans le message ; manifest + protocole tool-calling
    /// déroutent les petits modèles) et AUCUNE directive RAG de
    /// recommandation. AUCUN enrichissement de reco ici
    /// (<see cref="LlmRunner.EnrichRecommendations"/> matche des titres
    /// EPG) : le parse se fait directement sur le payload extrait.
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
        /// Rôle du LLM pour ce run (v2.12.0 : liste fermée, AUCUN outil) :
        /// la décision porte sur une liste déjà fournie — aucune action Emby.
        /// </summary>
        internal const string ROLE_INTRO =
            "Tu es un assistant Emby chargé d'évaluer la bibliothèque pour proposer " +
            "des suppressions. La liste COMPLÈTE des candidats t'est fournie dans le " +
            "message : classe chaque candidat selon les règles de la tâche, en t'appuyant " +
            "sur ses champs (note, année, genres, synopsis, dates, épisodes). Tu n'as " +
            "AUCUN outil et AUCUNE action Emby — ne prétends JAMAIS avoir supprimé " +
            "quoi que ce soit : la suppression est toujours exécutée ensuite, " +
            "manuellement, par l'administrateur via Emby.";

        /// <summary>
        /// Bloc format de sortie personnalisé (à la place de « FORMAT DES
        /// RECOMMANDATIONS ») : le contrat de classement [{id, section, reason}].
        /// Le user prompt (prompt config) rappelle aussi le gabarit — les petits
        /// modèles suivent le schéma du user prompt plutôt que celui du system
        /// prompt (quirk documenté).
        /// </summary>
        internal const string FORMAT_SECTION =
            "### FORMAT DE SORTIE\n" +
            "Ta réponse finale EST un tableau JSON qui classe TOUS les candidats de la " +
            "liste fournie dans le message — EXACTEMENT UNE entrée par candidat, chaque " +
            "objet avec trois champs : id (chaîne — l'InternalId recopié TEL QUEL depuis " +
            "la liste), section (\"delete\", \"can_delete\" ou \"keep\"), reason (une " +
            "phrase concrète justifiant le classement, qui cite les motifs du candidat). " +
            "Exemple :\n" +
            "[{\"id\":\"147321\",\"section\":\"delete\",\"reason\":\"Documentaire d'actualité jamais regardé, sans lien avec les goûts constatés.\"},{\"id\":\"147345\",\"section\":\"keep\",\"reason\":\"Classique acclamé (note 8.1), genre que l'admin regarde.\"}]\n" +
            "Règles : premier caractère = [, dernier = ] (sans texte autour, sans " +
            "balises ```) ; JAMAIS de prose, de tiret (« - »), de « Aucun » ou de " +
            "commentaire à la place du tableau — même si rien ne mérite la suppression, " +
            "classe tous les candidats \"keep\" ; jamais un id absent de la liste ; " +
            "échappe les guillemets doubles internes (\\\") dans reason.";

        /// <summary>
        /// Gabarit embarqué dans la demande de renvoi (réparation v2.12.0,
        /// quirk documenté : le schéma doit être dans le message USER).
        /// </summary>
        internal const string REPAIR_GABARIT =
            "[{\"id\":\"<id exact recopié de la liste>\"," +
            "\"section\":\"delete ou can_delete ou keep\"," +
            "\"reason\":\"Motif court en une phrase\"}]";

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

            // ---- Exclusion FAVORIS (v2.11.1) : un item en favori par QUELQU'UN
            //      n'est JAMAIS candidat — règle absolue, avant même le LLM.
            //      Un favori sur un épisode protège aussi la série parente.
            var favIds = CollectFavoriteIds(library, users, userData, logger, ct);
            if (favIds.Count > 0)
                logger.Info("[LLM_AI] [LibReview] {0} favori(s) protégé(s) — exclus des pools.", favIds.Count);
            int favExcluded = 0;

            // ---- Pool FILMS : deux familles (v2.11.0, critères repris de
            //      emby-delete.sh) — (a) movies VUS par au moins un usager, du
            //      plus ancien visionnage d'abord ; (b) movies JAMAIS VUS par
            //      personne ET ajoutés il y a plus de LibReviewUnplayedMinDays,
            //      triés par note croissante (les « canards boîteux » d'abord).
            //      Hors kept ET hors favoris, capés au total.
            var moviePool = CollectMovies(library, users, userData, kept, favIds, cap,
                cfg.LibReviewUnplayedMinDays, ref favExcluded, logger, ct);
            // ---- Pool SÉRIES : séries partiellement vues, hors kept, hors
            //      favoris, hors séries à enregistrer (SeriesTimers + timers
            //      programmés — même règle que emby-delete-series.sh :
            //      exclusion stricte par titre normalisé EXACT), capées.
            int timerExcluded = 0;
            var seriesPool = CollectSeries(library, users, userData, liveTv, kept, favIds, cap,
                ref timerExcluded, ref favExcluded, logger, ct);

            logger.Info("[LLM_AI] [LibReview] {0} favori(s) exclu(s) des pools.", favExcluded);

            int totalCandidates = moviePool.Count + seriesPool.Count;
            logger.Info("[LLM_AI] [LibReview] Candidats : {0} film(s), {1} série(s) (hors {2} conservé(s), {3} favori(s), {4} série(s) en enregistrement).",
                moviePool.Count, seriesPool.Count, kept.Count, favExcluded, timerExcluded);

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
                // v2.11.0 — profil de goûts du premier admin (historique +
                // favoris, repris d'emby-delete.sh) : permet au LLM de
                // justifier « sans lien avec les goûts constatés ».
                string taste = cfg.LibReviewTasteProfile ? BuildTasteProfile(library, users, userData, logger) : null;
                var entries = await RunOneAsync(runner, cfg, proseLang, "LibReview-FILMS", moviesPrompt,
                    SerializePool(moviePool), "movie", knownIds: null, library, json, logger, progress, ct,
                    tasteProfile: taste)
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
            int nDel = report.Items.Count(i => i.Section == "delete");
            int nCan = report.Items.Count(i => i.Section == "can_delete");
            int nKeep = report.Items.Count(i => i.Section == "keep");
            logger.Info("[LLM_AI] [LibReview] Rapport persisté : {0} suppression(s) proposée(s), {1} à discuter, {2} à garder — sur {3} candidat(s) classé(s).",
                nDel, nCan, nKeep, report.Items.Count);

            try
            {
                NotifyAdmins(notifications, users, host, cfg, nDel, nCan, nKeep, totalCandidates, logger);
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
            Action<string> progress, CancellationToken ct, string tasteProfile = null)
        {
            var backends = runner.ResolveBackends(cfg);
            if (backends.Count == 0)
            {
                logger.Warn("[LLM_AI] [{0}] Aucun LLM configuré/activé — run ignoré.", label);
                return new List<LibReviewEntry>();
            }

            string ollamaCloudKey = LlmRunner.ResolveKey(cfg.OllamaApiKey, "OLLAMA_API_KEY");
            string geminiKey = LlmRunner.ResolveKey(cfg.GeminiApiKey, "GEMINI_API_KEY");

            // v2.12.0 (P3) — run « liste fermée » : ragDirectives null (les
            // directives RAG sont celles de la RECOMMANDATION — « vérifie
            // avec les outils avant d'affirmer » contredit le classement
            // d'une liste déjà fournie), finalArrayKey « id » + gabarit de
            // renvoi pour la réparation v2.12.0 (P2).
            var agent = new LlmAgentService(backends, null, "",
                ollamaCloudKey, geminiKey, json, logger, cfg.DebugVerbose,
                roleIntro: ROLE_INTRO,
                formatSection: FORMAT_SECTION,
                responseLanguage: proseLang,
                finalArrayKey: "id",
                finalRepairHint: REPAIR_GABARIT);
            // v2.12.0 (P3) : AUCUN outil — la liste des candidats est déjà
            // complète dans le user prompt. Manifest + protocole tool-calling
            // déroutaient les petits modèles locaux (prod 2026-11-02 :
            // ministral-3:14b « - » / « Aucun. » sous le plugin alors que le
            // MÊME modèle classe via emby-delete*.sh sans aucun outil) ;
            // la boucle agent tourne ici à 1 itération, comme un appel plat.
            var tools = new List<ILlmTool>();

            string fullUser = userPrompt.Trim() + "\n\n### CANDIDATS (" + kind.ToUpperInvariant() + ")\n" +
                "Liste fournie par l'extension — classe TOUS les candidats de cette liste, " +
                "chacun exactement UNE fois, en recopiant les id tels quels :\n" + poolJson;
            if (!string.IsNullOrWhiteSpace(tasteProfile))
                fullUser += "\n\n### PROFIL DE GOÛTS DE L'ADMINISTRATEUR (référence pour tes justifications)\n" + tasteProfile;

            var (reply, _toolResults) = await agent.RunAsync(fullUser, tools, ct).ConfigureAwait(false);
            logger.Info("[LLM_AI] [{0}] Réponse brute (tronquée) :\n{1}", label, Truncate(reply, 1500));

            // v2.10.5 (rôle affiné v2.12.0) : tolérance « réponse courte » —
            // un petit modèle local peut répondre « - » ou « Aucun. » au lieu
            // de [] (vu en prod 2026-11-02, ministral-3:14b, run FILMS/SÉRIES
            // → 0 proposition). La réparation v2.12.0 (P2) a déjà relancé le
            // modèle (borné) DANS la boucle avant d'arriver ici : cette
            // tolérance n'est plus que le DERNIER filet (échec après relances).
            // Toute réponse SANS tableau JSON = liste vide valide (côté sûr :
            // aucune suppression proposée), et on logue le début pour
            // diagnostiquer. Jamais d'invention de propositions ici.
            string payload = LlmRunner.ExtractJsonPayload(reply);
            if (!payload.StartsWith("[", StringComparison.Ordinal))
            {
                logger.Warn("[LLM_AI] [{0}] Réponse sans tableau JSON après relances (« {1} ») — interprétée comme liste vide [].",
                    label, Truncate(payload, 80));
                payload = "[]";
            }
            return ParseProposals(payload, kind, knownIds, library, logger);
        }

        /// <summary>
        /// Parse le payload [{id, section, reason}…] et valide chaque id contre
        /// le pool du run (knownIds null pour les films — set construit ici à
        /// partir du pool passé à ValidateAndAdd ; pour les séries, passé
        /// explicitement). Id inconnu → écarté + log (hallucination).
        /// v2.12.0 : section normalisée delete|can_delete|keep, défaut
        /// « keep » (côté sûr — jamais de suppression proposée sur ambigüité).
        /// RÉTRO-COMPAT : si AUCUNE entrée ne porte de champ « section », la
        /// réponse vient de l'ancien contrat [{id, reason}] (prompt stocké
        /// d'une install existante) → toutes les entrées sont des
        /// PROPOSITIONS de suppression (comportement v2.10.x inchangé —
        /// leçon v2.6.10 : le filet serveur doit couvrir le prompt stocké,
        /// pas seulement celui par défaut).
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

                // Rétro-compat ancien contrat : au moins un champ « section » ?
                bool hasSections = false;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("section", out _))
                    {
                        hasSections = true;
                        break;
                    }
                }
                if (!hasSections && doc.RootElement.GetArrayLength() > 0)
                    logger.Info("[LLM_AI] [LibReview-{0}] Réponse sans champ « section » — ancien contrat {{id, reason}} supposé : {1} proposition(s) de suppression.",
                        kind.ToUpperInvariant(), doc.RootElement.GetArrayLength());

                foreach (var el in doc.RootElement.EnumerateArray())
                {
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

                    // Section : normalisée, défaut sûr « keep ». Sous l'ancien
                    // contrat (aucune section nulle part) → « delete » : les
                    // entrées sont des propositions de suppression.
                    string section = hasSections ? "keep" : "delete";
                    if (el.TryGetProperty("section", out var sEl) && sEl.ValueKind == JsonValueKind.String)
                    {
                        var s = (sEl.GetString() ?? "").Trim().ToLowerInvariant();
                        if (s == "delete" || s == "can_delete" || s == "keep") section = s;
                    }

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
                    list.Add(new LibReviewEntry { Id = id, Kind = kind, Reason = reason, Section = section });
                }
            }
            catch (Exception ex)
            {
                logger.Warn("[LLM_AI] [LibReview-{0}] Parse du payload échoué : {1}", kind.ToUpperInvariant(), ex.Message);
            }
            return list;
        }

        /// <summary>
        /// Ajoute les entrées validées au rapport (dédup par id) + journalise
        /// la ventilation par section et la complétude du classement
        /// (v2.12.0 — visibilité : un run qui classe moins de candidats que
        /// le pool doit se VOIR, même leçon que la règle « 0 toast(s) »).
        /// </summary>
        private static void ValidateAndAdd(LastLibReviewReport report, List<LibReviewEntry> entries,
            List<PoolItem> pool, string kind, ILogger logger)
        {
            var known = new HashSet<string>(pool.Select(p => p.Id), StringComparer.Ordinal);
            var seen = new HashSet<string>(report.Items.Select(i => i.Id), StringComparer.Ordinal);
            int added = 0, del = 0, can = 0, keep = 0;
            foreach (var e in entries)
            {
                if (!known.Contains(e.Id))
                {
                    logger.Warn("[LLM_AI] [LibReview-{0}] id « {1} » hors pool — écarté à l'ajout.", kind.ToUpperInvariant(), e.Id);
                    continue;
                }
                if (!seen.Add(e.Id)) continue; // dédup
                report.Items.Add(e);
                added++;
                switch (e.Section)
                {
                    case "delete": del++; break;
                    case "can_delete": can++; break;
                    default: keep++; break;
                }
            }
            logger.Info("[LLM_AI] [LibReview-{0}] Classement : {1} entrée(s) sur {2} candidat(s) — {3} delete, {4} can_delete, {5} keep.",
                kind.ToUpperInvariant(), added, pool.Count, del, can, keep);
            if (added < pool.Count)
                logger.Warn("[LLM_AI] [LibReview-{0}] {1} candidat(s) NON classé(s) par le LLM (absents de la réponse).",
                    kind.ToUpperInvariant(), pool.Count - added);
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

        /// <summary>
        /// InternalId (chaîne) des items en favori par AU MOINS un usager
        /// (v2.11.1 — règle absolue : jamais de candidat favori). Union des
        /// UserData.IsFavorite sur tous les usagers.
        /// </summary>
        private static HashSet<string> CollectFavoriteIds(ILibraryManager library, IUserManager users,
            IUserDataManager userData, ILogger logger, CancellationToken ct)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var u in users?.GetUserList(new UserQuery()) ?? Array.Empty<User>())
                {
                    if (u == null) continue;
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var q = new InternalItemsQuery
                        {
                            User = u,
                            IsFavorite = true,
                            Recursive = true,
                            EnableTotalRecordCount = false
                            // NB : AUCUN filtre Tags/Genres assigné (piège InternalItemsQuery).
                        };
                        foreach (var it in library.GetItemList(q) ?? Array.Empty<BaseItem>())
                        {
                            if (it == null || it.Id == Guid.Empty) continue;
                            set.Add(it.InternalId.ToString(CultureInfo.InvariantCulture));
                            // Un favori posé sur un ÉPISODE protège aussi sa
                            // série parente (la suppression d'une série efface
                            // tous ses épisodes).
                            if (it is Episode ep && ep.SeriesId != 0)
                            {
                                var parent = library.GetItemById(ep.SeriesId) as Series;
                                if (parent != null)
                                    set.Add(parent.InternalId.ToString(CultureInfo.InvariantCulture));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.Warn("[LLM_AI] [LibReview] Requête favoris (usager {0}) échouée : {1}", u.Name, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] [LibReview] Lecture des favoris échouée : {0}", ex.Message);
            }
            return set;
        }

        /// <summary>
        /// Films candidats, hors kept ET hors favoris, capés au total (v2.11.0) :
        /// moitié du plafond pour les films VUS (union des usagers, du plus
        /// ancien visionnage d'abord), moitié pour les films JAMAIS VUS par
        /// personne et ajoutés il y a plus de <paramref name="unplayedMinDays"/>
        /// jours (triés par note croissante — « canards boîteux » en tête, repris
        /// de emby-delete.sh). Si unplayedMinDays &lt;= 0 : pool vus seul
        /// (comportement < v2.11.0).
        /// </summary>
        private static List<PoolItem> CollectMovies(ILibraryManager library, IUserManager users,
            IUserDataManager userData, HashSet<string> kept, HashSet<string> favIds, int cap,
            int unplayedMinDays, ref int favExcluded, ILogger logger, CancellationToken ct)
        {
            int napCap = unplayedMinDays > 0 ? cap / 2 : cap;
            var list = new List<PoolItem>();

            // (a) Films vus (union des usagers), du plus ancien visionnage d'abord.
            var played = CollectPlayedPerUser(library, users, userData, new[] { "Movie" }, logger, ct);
            foreach (var kv in played.OrderBy(p => p.Value.LastPlayedUtc ?? DateTime.MinValue))
            {
                if (list.Count >= napCap) break;
                var item = kv.Key;
                string id = item.InternalId.ToString(CultureInfo.InvariantCulture);
                if (kept.Contains(id)) continue;
                if (favIds.Contains(id)) { favExcluded++; continue; }   // v2.11.1 — jamais de favori
                list.Add(ToPoolItem(item, id, kv.Value.LastPlayedUtc, logger));
            }

            // (b) Films JAMAIS vus (intersection des IsUnplayed de tous les
            // usagers — même règle qu'emby-delete.sh : « IsUnplayed » exige un
            // UserId, on boucle sur les usagers), ajoutés avant la coupure.
            if (unplayedMinDays > 0 && list.Count < cap)
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-unplayedMinDays);
                var neverSeen = CollectNeverSeenPerUser(library, users, new[] { "Movie" },
                    cutoff, logger, ct);
                int unplayedAdded = 0;
                foreach (var item in neverSeen.OrderBy(i => i.CommunityRating ?? 99.0))
                {
                    if (list.Count >= cap) break;
                    string id = item.InternalId.ToString(CultureInfo.InvariantCulture);
                    if (kept.Contains(id)) continue;
                    if (favIds.Contains(id)) { favExcluded++; continue; }   // v2.11.1
                    list.Add(ToPoolItem(item, id, null, logger));
                    unplayedAdded++;
                }
                if (unplayedAdded > 0)
                    logger?.Info("[LLM_AI] [LibReview] {0} film(s) jamais vus (+{1} j) ajoutés au pool (tri note croissante).",
                        unplayedAdded, unplayedMinDays);
            }
            return list;
        }

        private static PoolItem ToPoolItem(BaseItem item, string id, DateTimeOffset? lastPlayed,
            ILogger logger) => new PoolItem
        {
            Id = id,
            Name = item.Name ?? "",
            SizeGb = FileSizeGb(item.Path, logger),
            DateAdded = item.DateCreated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            LastPlayed = lastPlayed?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            CommunityRating = item.CommunityRating,
            ProductionYear = item.ProductionYear,
            Status = null, // les films n'ont pas de statut de diffusion
            Genres = item.Genres,
            Overview = TruncateOverview(item.Overview)
        };

        /// <summary>
        /// Items d'un type donné JAMAIS vus par AUCUN usager — sémantique robuste
        /// (v2.11.2) : union des listes « IsPlayed=true » de tous les usagers
        /// (set des « vus »), puis complément dans la bibliothèque complète.
        /// PAS une intersection des « IsUnplayed » : un film invisible pour un
        /// usager à accès restreint n'apparaît pas dans sa liste non-vus et
        /// disparaîtrait faussement de l'intersection (constaté au banc :
        /// 4 usagers restreints → intersection = 2 films au lieu de ~3300).
        /// Filtre ensuite DateCreated &lt; <paramref name="cutoff"/>.
        /// </summary>
        private static List<BaseItem> CollectNeverSeenPerUser(ILibraryManager library, IUserManager users,
            string[] types, DateTimeOffset cutoff, ILogger logger, CancellationToken ct)
        {
            // (1) union des « vus » sur tous les usagers.
            var seen = new HashSet<long>();
            List<User> userList;
            try { userList = users?.GetUserList(new UserQuery()).ToList() ?? new List<User>(); }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] [LibReview] Lecture des usagers échouée : {0}", ex.Message);
                return new List<BaseItem>();
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
                        // NB : AUCUN filtre Tags/Genres assigné (piège InternalItemsQuery).
                    };
                    foreach (var it in library.GetItemList(q) ?? Array.Empty<BaseItem>())
                        if (it != null && it.Id != Guid.Empty) seen.Add(it.InternalId);
                }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] [LibReview] Requête vus (usager {0}) échouée : {1}", u.Name, ex.Message);
                }
            }
            // (2) complément : tous les items du type, hors vus, créés avant cutoff.
            var final = new List<BaseItem>();
            try
            {
                var q = new InternalItemsQuery
                {
                    IncludeItemTypes = types,
                    Recursive = true,
                    EnableTotalRecordCount = false
                    // NB : AUCUN filtre Tags/Genres/User assigné (piège InternalItemsQuery).
                };
                foreach (var it in library.GetItemList(q) ?? Array.Empty<BaseItem>())
                {
                    ct.ThrowIfCancellationRequested();
                    if (it == null || it.Id == Guid.Empty) continue;
                    if (seen.Contains(it.InternalId)) continue;
                    if (it.DateCreated >= cutoff) continue; // trop récent
                    final.Add(it);
                }
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] [LibReview] Requête bibliothèque (jamais vus) échouée : {0}", ex.Message);
                return new List<BaseItem>();
            }
            logger?.Info("[LLM_AI] [LibReview] Jamais vus par personne et +{0} j : {1} film(s) (vus par au moins un usager : {2}).",
                (int)(DateTimeOffset.UtcNow - cutoff).TotalDays, final.Count, seen.Count);
            return final;
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
            IUserDataManager userData, ILiveTvManager liveTv, HashSet<string> kept, HashSet<string> favIds,
            int cap, ref int timerExcluded, ref int favExcluded, ILogger logger, CancellationToken ct)
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
                // v2.11.1 — règle absolue : une série en favori (par quiconque)
                // n'est JAMAIS candidate.
                if (favIds.Contains(id))
                {
                    favExcluded++;
                    logger?.Info("[LLM_AI] [LibReview] Série « {0} » exclue du pool : en favori.", series.Name);
                    continue;
                }
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

        internal static bool IsPlayedByAny(BaseItem item, IUserManager users, IUserDataManager userData)
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

        /// <summary>
        /// Date du dernier visionnage de l'item par N'IMPORTE QUEL usager
        /// (max des UserData.LastPlayedDate), format yyyy-MM-dd — null si
        /// jamais vu. Utilisé pour l'affichage des cartes (v2.11.2).
        /// </summary>
        internal static string LastPlayedDateByAny(BaseItem item, IUserManager users, IUserDataManager userData)
        {
            DateTime utc = DateTime.MinValue;
            try
            {
                foreach (var u in users?.GetUserList(new UserQuery()) ?? Array.Empty<User>())
                {
                    var ud = userData?.GetUserData(u, item);
                    if (ud != null && ud.Played && ud.LastPlayedDate.HasValue
                        && ud.LastPlayedDate.Value.UtcDateTime > utc)
                        utc = ud.LastPlayedDate.Value.UtcDateTime;
                }
            }
            catch { /* best-effort */ }
            return utc == DateTime.MinValue ? null
                : utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Profil de goûts du premier administrateur (v2.11.0, repris
        /// d'emby-delete.sh avec René) : films vus récemment (nom, année,
        /// genres, 60 max) + favoris. Injecté dans le prompt du run films
        /// pour permettre des justifications « sans lien avec les goûts ».
        /// Best-effort : jamais d'exception.
        /// </summary>
        private static string BuildTasteProfile(ILibraryManager library, IUserManager users,
            IUserDataManager userData, ILogger logger)
        {
            try
            {
                var admin = users?.GetUserList(new UserQuery())
                    ?.FirstOrDefault(u => u != null && u.Policy != null && u.Policy.IsAdministrator);
                if (admin == null) return null;

                var sb = new StringBuilder();
                sb.Append("Films vus récemment par ").Append(admin.Name).Append(" (nom (année) [genres]) :");
                var q = new InternalItemsQuery
                {
                    User = admin,
                    IsPlayed = true,
                    IncludeItemTypes = new[] { "Movie" },
                    Recursive = true,
                    EnableTotalRecordCount = false,
                    Limit = 60,
                    OrderBy = new[] { ("DatePlayed", SortOrder.Descending) }
                    // NB : AUCUN filtre Tags/Genres assigné (piège InternalItemsQuery).
                };
                foreach (var it in library.GetItemList(q) ?? Array.Empty<BaseItem>())
                {
                    string genres = (it.Genres != null && it.Genres.Length > 0) ? " [" + string.Join(", ", it.Genres) + "]" : "";
                    sb.Append("\n- ").Append(it.Name ?? "?")
                      .Append(" (").Append(it.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "n/a").Append(")")
                      .Append(genres);
                }

                sb.Append("\n\nFavoris de ").Append(admin.Name).Append(" (films/séries) :");
                var fq = new InternalItemsQuery
                {
                    User = admin,
                    IsFavorite = true,
                    Recursive = true,
                    EnableTotalRecordCount = false,
                    Limit = 30,
                    OrderBy = new[] { ("DateCreated", SortOrder.Descending) }
                };
                foreach (var it in library.GetItemList(fq) ?? Array.Empty<BaseItem>())
                    sb.Append("\n- ").Append(it.Name ?? "?")
                      .Append(" (").Append(it.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "n/a").Append(")");

                var s = sb.ToString();
                return s.Length > 6000 ? s.Substring(0, 6000) + "\n…" : s;
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] [LibReview] Profil de goûts illisible : {0}", ex.Message);
                return null;
            }
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
            IServerApplicationHost host, PluginConfiguration cfg,
            int del, int can, int keep, int candidates, ILogger logger)
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
            // v2.12.0 — ventilation 3 sections (le classement remplace la
            // sélection de propositions ; l'admin voit l'étendue du verdict).
            string desc = string.Format(CultureInfo.InvariantCulture,
                I18n.SDisplay("librev.notif.desc", host) ??
                "{0} suppression(s) proposée(s), {1} à discuter, {2} à garder sur {3} candidat(s) — onglet « Gestion bibliothèque » de la configuration.",
                del, can, keep, candidates);
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
