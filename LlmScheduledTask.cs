using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using Emby.Notifications;

namespace LLM_AI
{
    /// <summary>
    /// Tâche planifiée autonome : fait tourner l'agent LLM (boucle de
    /// tool-calling) qui appelle <see cref="GetEmbyInfoTool"/> pour accomplir
    /// la tâche configurée, puis logue la réponse finale dans le journal Emby.
    /// Les services Emby sont injectés par DI (accès natif in-process).
    /// </summary>
    public class LlmScheduledTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly ILogger _logger;
        private readonly IJsonSerializer _json;
        private readonly ILibraryManager _library;
        private readonly IUserManager _users;
        private readonly ILiveTvManager _liveTv;
        private readonly IServerApplicationHost _host;
        private readonly INotificationManager _notifications;

        // Orchestration LLM partagée avec TonightApiService (résolution backends,
        // construction des outils, run agent, enrichissement). Construit une
        // fois à l'instanciation de la tâche.
        private readonly LlmRunner _runner;

        public LlmScheduledTask(
            ILogger logger,
            IJsonSerializer jsonSerializer,
            ILibraryManager library,
            IUserManager users,
            ILiveTvManager liveTv,
            IServerApplicationHost host,
            INotificationManager notifications)
        {
            _logger = logger;
            _json = jsonSerializer;
            _library = library;
            _users = users;
            _liveTv = liveTv;
            _host = host;
            _notifications = notifications;
            _runner = new LlmRunner(logger, jsonSerializer, library, users, liveTv, host);
        }

        public string Name => I18n.S("task.llm.name", I18n.ResolveDisplayLangKey(_host));

        /// <summary>Identifiant stable de la tâche (GUID du plugin).</summary>
        public string Key => "e7d3dee6-ef19-46a9-985f-06318b682e60";

        public string Description => I18n.S("task.llm.desc", I18n.ResolveDisplayLangKey(_host));

        public string Category => I18n.S("task.category", I18n.ResolveDisplayLangKey(_host));

        public bool IsHidden => false;

        public bool IsEnabled => true;

        public bool IsLogged => true;

        // Workflow d'exécution injecté dans le system prompt de chaque run agent.
        // Le run SÉRIES couvre les étapes 1 (premieres S01E01) + 2 (nouvelles
        // saisons) ; le run FILMS couvre l'étape 3 (epg_movies). Deux runs
        // indépendants = deux contextes séparés : les séries ne polluent pas
        // les films (et inversement), ce qui réduit la charge du modèle local.
        private const string SERIES_WORKFLOW =
            "1. NOUVELLES SÉRIES (S01E01) : appelle get_emby_info avec action=\"epg_series\" " +
            "et premieres_only=true. Tu obtiens les premières diffusions de nouvelles séries " +
            "à venir.\n" +
            "2. NOUVELLES SAISONS de séries que je possède déjà : appelle get_emby_info avec " +
            "action=\"epg_series\" et new_seasons=true. Tu obtiens les saisons à venir des " +
            "séries déjà en bibliothèque qui ne sont PAS dans mes enregistrements planifiés " +
            "(l'outil exclut déjà les timers existants).\n" +
            "premieres_only et new_seasons sont mutuellement exclusifs — fais deux appels " +
            "epg_series séparés dans le même tableau JSON.\n" +
            "ENRICHISSEMENT OBLIGATOIRE (web_search + web_fetch à CHAQUE série recommandée) : " +
            "pour chaque série que tu recommandes, tu DOIS appeler tmdb_lookup (ou tvdb_search) " +
            "sur le titre PUIS web_search sur le titre (ex. « <titre> série synopsis »), et " +
            "web_fetch sur le lien le plus pertinent pour confirmer/croiser synopsis, statut, " +
            "note, popularité. web_search et web_fetch NE SONT JAMAIS OPTIONNELS — même si " +
            "l'EPG a un synopsis complet. web_search sert aussi à croiser l'actu récente et " +
            "les sorties à venir non couvertes par TMDB/TVDB. Tu peux regrouper plusieurs " +
            "web_search dans une même itération (tableau d'appels outils) pour gagner du " +
            "temps. Si un web_search/web_fetch échoue ou ne donne rien, mentionne-le " +
            "brièvement dans la reason et passe au suivant — ne bloque pas la recommandation. " +
            "Mieux vaut moins de recos bien enrichies que beaucoup sans vérification.\n" +
            "PROTOCOLE WEB (important) : ne devine JAMAIS d'URL pour web_fetch — les slugs " +
            "Wikipedia sont presque toujours faux et renvoient 404. Procède en deux temps : " +
            "(1) appelle web_search sur le titre, (2) lis l'URL du résultat le plus pertinent, " +
            "(3) appelle web_fetch sur CETTE URL. N'envoie JAMAIS web_search et web_fetch dans " +
            "la même itération : web_fetch dépend du résultat de web_search, attends donc la " +
            "réponse de web_search avant d'appeler web_fetch.\n" +
            "Concentre-toi sur la qualité plutôt que la quantité : recommande les séries les " +
            "plus prometteuses, avec priority high/medium/low selon l'intérêt. Reprends " +
            "title/channel/start tels quels depuis les résultats pour permettre la programmation.";

        private const string FILMS_WORKFLOW =
            "FILMS : appelle get_emby_info avec action=\"epg_movies\". Tu obtiens les films " +
            "à venir absents de la bibliothèque (l'outil exclut la biblio et les timers, et " +
            "applique les filtres chaines/genres et les flags Kids/News/Sports).\n" +
            "ENRICHISSEMENT OBLIGATOIRE (web_search + web_fetch à CHAQUE film recommandé) : " +
            "pour chaque film que tu recommandes, tu DOIS appeler tmdb_lookup sur le titre " +
            "PUIS web_search sur le titre (ex. « <titre> film synopsis »), et web_fetch sur " +
            "le lien le plus pertinent pour confirmer/croiser synopsis, genre, note, " +
            "popularité. web_search et web_fetch NE SONT JAMAIS OPTIONNELS — même si l'EPG a " +
            "un synopsis complet. web_search sert aussi à croiser l'actu récente et les " +
            "sorties à venir non couvertes par TMDB. Tu peux regrouper plusieurs web_search " +
            "dans une même itération (tableau d'appels). Si un web_search/web_fetch échoue " +
            "ou ne donne rien, mentionne-le brièvement dans la reason et passe au suivant. " +
            "Mieux vaut moins de recos bien enrichies que beaucoup sans vérification.\n" +
            "PROTOCOLE WEB (important) : ne devine JAMAIS d'URL pour web_fetch — les slugs " +
            "Wikipedia sont presque toujours faux et renvoient 404. Procède en deux temps : " +
            "(1) appelle web_search sur le titre, (2) lis l'URL du résultat le plus pertinent, " +
            "(3) appelle web_fetch sur CETTE URL. N'envoie JAMAIS web_search et web_fetch dans " +
            "la même itération : web_fetch dépend du résultat de web_search, attends donc la " +
            "réponse de web_search avant d'appeler web_fetch.\n" +
            "Recommande ceux qui méritent l'enregistrement, avec kind=\"movie\" et priority " +
            "high/medium/low selon l'intérêt. Reprends title/channel/start tels quels depuis " +
            "les résultats pour permettre la programmation.";

        // Cloche « recommandations » (v2.20.0) : plafond d'items détaillés dans
        // la description et troncature de la raison LLM — la description part
        // par courriel/webhook (canaux qui rendent Title + Description seuls),
        // elle doit rester lisible.
        private const int BellDetailCap = 8;
        private const int BellReasonMax = 200;

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
            {
                _logger.Warn("[LLM_AI] Tâche exécutée mais aucune configuration disponible (Plugin.Instance null).");
                return;
            }

            progress?.Report(10);

            // Auto-réparation des mappages de genres : une sauvegarde depuis la
            // page de config de GenreCleaner peut écraser les mappages ajoutés
            // par l'IA. On les ré-applique pour garantir la persistance.
            GenreCleanerMap.HealApplied(cfg.GenreAliasApplied, _logger);

            // Deux prompts : séries (ScheduleTask) et films (ScheduleTaskMovies).
            // La planification (gauche du '|') ne vient que de ScheduleTask ; on
            // n'en tient pas compte ici à l'exécution (juste le texte du prompt).
            string seriesPrompt = ParsePrompt(cfg.ScheduleTask);
            string filmsPrompt = ParsePrompt(cfg.ScheduleTaskMovies);

            if (string.IsNullOrWhiteSpace(seriesPrompt) && string.IsNullOrWhiteSpace(filmsPrompt))
            {
                _logger.Warn("[LLM_AI] Aucun prompt configuré (ScheduleTask et ScheduleTaskMovies vides). Tâche ignorée.");
                return;
            }

            // Directive de rétroaction (opt-in, boucle hebdo) : fusion des
            // directives de TOUS les usagers (la reco d'enregistrement est
            // globale au foyer, pas par usager). Vide si désactivée/jamais
            // analysée — prompts inchangés (fail-open).
            // Mémoire réflexive : la fiche mémoire a priorité sur
            // la directive de la boucle classique (repli fail-open sur celle-ci).
            string memory = MemoryCard.BuildInjectionBlock(cfg);
            if (memory.Length > 0)
            {
                if (!string.IsNullOrWhiteSpace(seriesPrompt))
                    seriesPrompt += "\n\n" + memory;
                if (!string.IsNullOrWhiteSpace(filmsPrompt))
                    filmsPrompt += "\n\n" + memory;
                _logger.Info("[LLM_AI] Fiche mémoire injectée dans le(s) prompt d'enregistrement.");
            }
            else if (cfg.RecoFeedbackEnabled)
            {
                string feedback = RecoFeedback.BuildRecordBlock(cfg);
                if (feedback.Length > 0)
                {
                    if (!string.IsNullOrWhiteSpace(seriesPrompt))
                        seriesPrompt += "\n\n" + feedback;
                    if (!string.IsNullOrWhiteSpace(filmsPrompt))
                        filmsPrompt += "\n\n" + feedback;
                    _logger.Info("[LLM_AI] Directive de rétroaction injectée dans le(s) prompt d'enregistrement.");
                }
            }

            // Porte LLM : sérialise les tâches planifiées LLM entre elles
            // (capacité LlmMaxConcurrentTasks, défaut 1) — attend un run en
            // cours au lieu de l'empiler sur le backend. Acquisition AVANT
            // le try : une annulation pendant l'attente remonte sans rien
            // avoir à libérer (le finally ci-dessous libère tout le reste).
            await LlmTaskGate.AcquireAsync("Recommandations d'enregistrement (EPG)", _logger, cancellationToken).ConfigureAwait(false);

            try
            {
                string p1 = "", p2 = "";
                bool ok1 = false, ok2 = false;

                // Mémoire réflexive (opt-in DecisionLogEnabled) :
                // runId des runs d'enregistrement — relie les décisions
                // journalisées aux candidats EPG capturés (epg_series /
                // epg_movies par le tool get_emby_info).
                string runId = "r" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                if (cfg.DecisionLogEnabled)
                    DecisionStore.BeginRun(runId);

                // Run 1 : SÉRIES (étapes 1 + 2).
                if (!string.IsNullOrWhiteSpace(seriesPrompt))
                {
                    _logger.Info("[LLM_AI] Run SÉRIES — prompt: {0}", Truncate(seriesPrompt, 200));
                    (p1, ok1) = await RunOne(cfg, "SÉRIES", seriesPrompt, SERIES_WORKFLOW, cancellationToken).ConfigureAwait(false);
                    progress?.Report(55);
                }

                // Run 2 : FILMS (étape 3). Indépendant : un échec du run séries
                // n'empêche pas le run films (et inversement).
                if (!string.IsNullOrWhiteSpace(filmsPrompt))
                {
                    _logger.Info("[LLM_AI] Run FILMS — prompt: {0}", Truncate(filmsPrompt, 200));
                    (p2, ok2) = await RunOne(cfg, "FILMS", filmsPrompt, FILMS_WORKFLOW, cancellationToken).ConfigureAwait(false);
                    progress?.Report(90);
                }

                if (!ok1 && !ok2)
                {
                    if (cfg.DecisionLogEnabled) DecisionStore.EndRun(runId); // purge
                    throw new Exception("Les deux runs agent (séries et films) ont échoué.");
                }

                // Fusionne les deux tableaux JSON en un payload unique. La page
                // Recommandations réparties par kind (series/movie) en deux sections.
                var merged = LlmRunner.MergeJsonArrays(p1, p2);

                // Owned-guard déterministe : rapproche les recos de la
                // bibliothèque (titre, puis id IMDb si le LLM l'a établi via
                // ses outils). Une reco possédée reçoit library_id → exclue du
                // record bucket (.strm/Auto-program/badges) et affichée avec
                // le bouton « Regarder (bibli.) ». Sans lui, le « déjà possédé »
                // dépend de la seule classification du LLM.
                merged = _runner.EnrichWithLibrary(merged);

                // Garde anti-effacement : si le LLM produit une réponse vide
                // (ex: backend dégradé), on conserve les recommandations du
                // run précédent pour ne pas vider la liste et déclencher des
                // nettoyages non désirés (badges, fichiers .strm).
                int newCount = LlmRunner.CountRecommendations(merged);
                int oldCount = LlmRunner.CountRecommendations(cfg.Recommendations);
                if (newCount == 0 && oldCount > 0)
                {
                    _logger?.Warn("[LLM_AI] Run terminé SANS aucune recommandation — probable LLM indisponible/dégradé (voir les réponses [SÉRIES]/[FILMS] ci-dessus). {0} recommandation(s) précédente(s) conservées, consommateurs (badges/.strm/auto-program) non exécutés.",
                        oldCount);
                    SendFailureNotification(string.Format(CultureInfo.InvariantCulture,
                        "Aucune recommandation produite (LLM indisponible ou réponse invalide) — recommandations précédentes ({0}) conservées.",
                        oldCount));
                    return;
                }

                PersistRecommendations(cfg, merged);
                // Opt-out pour la notification "N nouvelles recommandations".
                // Le filtrage par utilisateur n'étant pas possible côté Emby
                // pour ce type de notification, il est géré ici.
                if (cfg.NotifyRecommendationsEnabled)
                    SendRecommendationNotification(merged);

                // Journalisation des recos d'enregistrement pour la boucle de
                // rétroaction hebdo (opt-in) : entrées kind=record, sans
                // usager (reco globale du foyer — corrélée aux visionnages
                // de chaque usager par RecoAnalysisTask). Best-effort.
                if (cfg.RecoFeedbackEnabled)
                {
                    try
                    {
                        var entries = new List<RecoLogEntry>();
                        foreach (var r in AutoProgrammer.ParseRecommendations(merged))
                        {
                            if (string.IsNullOrWhiteSpace(r.Title)) continue;
                            entries.Add(new RecoLogEntry
                            {
                                User = "",
                                Kind = "record",
                                Title = r.Title,
                                Source = r.Kind ?? "",
                                Id = r.Id ?? "",
                                Date = DateTimeOffset.UtcNow
                            });
                        }
                        if (entries.Count > 0)
                            RecoFeedback.AppendLog(cfg, entries, _logger);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn("[LLM_AI] Rétroaction : journalisation du run échouée : {0}", ex.Message);
                    }
                }

                // Mémoire réflexive (opt-in) : journalise les décisions
                // (kind=record) et le pool de candidats EPG du run.
                // Redondant avec RecoLog pour assurer la compatibilité avec
                // l'ancienne boucle de rétroaction. Best-effort.
                if (cfg.DecisionLogEnabled)
                {
                    try
                    {
                        var poolCands = DecisionStore.EndRun(runId);
                        var decisions = new List<DecisionEntry>();
                        foreach (var r in AutoProgrammer.ParseRecommendations(merged))
                        {
                            if (string.IsNullOrWhiteSpace(r.Title)) continue;
                            decisions.Add(new DecisionEntry
                            {
                                RunId = runId,
                                Kind = "record",
                                User = "",               // reco globale du foyer
                                Date = DateTimeOffset.UtcNow,
                                Title = r.Title,
                                ItemId = r.LibraryId ?? "", // possédé → bibliothèque
                                ProgramId = r.Id ?? "",     // programme EPG à programmer
                                Source = r.Kind ?? "",
                                Reason = r.Reason ?? "",
                                Priority = r.Priority ?? "",
                                Mv = 0 // version de fiche mémoire : non renseignée ici
                            });
                        }
                        if (decisions.Count > 0)
                            DecisionStore.AppendDecisions(cfg, decisions, _logger);
                        DecisionStore.SavePool(cfg, new RunPool
                        {
                            RunId = runId,
                            User = "",
                            Date = DateTimeOffset.UtcNow,
                            Mv = 0,
                            Candidates = poolCands
                        }, _logger);
                        _logger.Info("[LLM_AI] Décisions : run {0} — {1} reco(s) d'enregistrement journalisée(s), pool {2} candidat(s).",
                            runId, decisions.Count, poolCands.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn("[LLM_AI] Décisions : journalisation du run échouée : {0}", ex.Message);
                        DecisionStore.EndRun(runId); // purge du contexte statique
                    }
                }
                else if (DecisionStore.ActiveRunId != null)
                {
                    DecisionStore.EndRun(runId); // run démarré avant un toggle off
                }

                // Badge « AI » sur les images EPG (opt-out, non destructif) :
                // alimente le registre des programmes suggérés à enregistrer
                // (record bucket — même filtre que .strm/AutoProgrammer).
                // L'enrichisseur AiBadgeEnhancer badge ensuite leur image
                // Primary dans le guide natif, au moment du service. Sémantique
                // « tout remplacer » : les suggestions du run précédent ne
                // badgent plus.
                if (cfg.AiBadgeEnabled)
                {
                    try
                    {
                        AiBadgeRegistry.ApplyRecos(merged, _logger);
                    }
                    catch (Exception ex)
                    {
                        _logger?.ErrorException("[LLM_AI] Badge AI : {0}", ex, ex.Message);
                    }
                }

                // Bibliothèque .strm dédiée (opt-in, indépendant de
                // AutoProgram) : écrit une carte .strm+.nfo+poster par reco du
                // record bucket. L'usager parcourt la bibliothèque ; lire une
                // carte déclenche l'enregistrement via /Plugins/LLMAI/Activate.
                if (cfg.StrmLibraryEnabled)
                {
                    try
                    {
                        var gen = new StrmLibraryGenerator(_library, _liveTv, _host, _logger, _runner);
                        await gen.GenerateAsync(merged, cfg, ResolveEmbyUrl(), cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.ErrorException("[LLM_AI] Strm library : {0}", ex, ex.Message);
                    }
                }

                // Auto-programmation (opt-in) : si cfg.AutoProgram est coché,
                // crée les timers Emby du record bucket (recos EPG à venir non
                // possédées, non déjà programmées, hors drop list) → les recos
                // ressortent dans le guide EPG natif (badge d'enregistrement)
                // sur tous les clients, y compris Android / Android TV. GATING
                // ABSOLU : aucun timer tant que AutoProgram == false.
                if (cfg.AutoProgram)
                {
                    try
                    {
                        var ap = new AutoProgrammer(_liveTv, _library, _logger, _host);
                        await ap.Program(merged, null, cfg, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.ErrorException("[LLM_AI] Auto-program (tâche planifiée) : {0}", ex, ex.Message);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.Info("[LLM_AI] Tâche annulée.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[LLM_AI] Échec de l'agent LLM : {0}", ex, ex.Message);
                // Notifie l'échec pour que l'utilisateur sache que les
                // recommandations n'ont pas été mises à jour, au lieu de
                // le découvrir en consultant le journal.
                SendFailureNotification("Échec de la tâche recommandations : " + ex.Message);
                throw;
            }
            finally
            {
                LlmTaskGate.Release("Recommandations d'enregistrement (EPG)");
                progress?.Report(100);
            }
        }

        /// <summary>
        /// Extrait le texte du prompt (droite du '|') d'un champ ScheduleTask*.
        /// Tolérant : si pas de '|', renvoie la chaîne entière (trimée).
        /// </summary>
        private static string ParsePrompt(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return string.Empty;
            int sep = spec.IndexOf('|');
            return (sep >= 0 ? spec.Substring(sep + 1) : spec).Trim();
        }

        /// <summary>
        /// Exécute un run agent (un seul fil de conversation) et l'enrichit.
        /// Délègue à <see cref="LlmRunner.RunAsync"/> (logique partagée avec
        /// <c>TonightApiService</c>). ok=false si le run a échoué — sans faire
        /// planter la tâche : l'appelant fusionne ce qu'il a et déclenche une
        /// erreur globale seulement si les deux runs ont échoué.
        /// </summary>
        private Task<(string payload, bool ok)> RunOne(
            PluginConfiguration cfg, string label, string userPrompt, string workflow, CancellationToken ct)
            => _runner.RunAsync(cfg, label, userPrompt, workflow, ct);

        /// <summary>
        /// Triggers par défaut déduits de la partie gauche du champ ScheduleTask
        /// (crontab restreint : « 10 3 * * * », « 10 5 * * 0 » ; l'ancienne
        /// syntaxe « Daily 03:00 » reste acceptée).
        /// L'utilisateur peut toujours les ajuster dans le planificateur Emby.
        /// </summary>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var cfg = Plugin.Instance?.Configuration;
            var spec = cfg?.ScheduleTask ?? string.Empty;

            int sep = spec.IndexOf('|');
            string schedule = (sep >= 0 ? spec.Substring(0, sep) : spec).Trim();

            foreach (var t in ParseTriggers(schedule, _logger))
                yield return t;
        }

        /// <summary>Parseur de planification partagé. Supporte un format
        /// crontab restreint (« MM HH * * J ») et conserve la
        /// compatibilité avec l'ancienne syntaxe par mots-clés
        /// (Daily, Weekly...). Le crontab est agnostique de la langue,
        /// contrairement aux mots-clés qui doivent être en anglais.</summary>
        internal static IEnumerable<TaskTriggerInfo> ParseTriggers(string schedule, ILogger logger = null)
        {
            if (string.IsNullOrWhiteSpace(schedule))
            {
                yield return DailyAt(0, 0);
                yield break;
            }

            var parts = schedule.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            // Crontab restreint : exactement 5 champs « MM HH DoM Mo J ».
            // Convention du jour : 0 = dimanche — identique au cron
            // classique ET au DayOfWeek .NET/Emby (zéro conversion avec
            // TaskTriggerInfo.DayOfWeek ; l'ISO 8601, elle, compte
            // 1 = lundi : ne jamais y toucher).
            if (parts.Length == 5)
            {
                if (TryParseCron(parts, out var cronTrigger, out var cronReason))
                {
                    yield return cronTrigger;
                    yield break;
                }
                logger?.Warn(
                    "[LLM_AI] Planification « {0} » non supportée ({1}) — repli : tous les jours à 00:00. " +
                    "Syntaxe : « MM HH * * * », « MM HH * * J » (J : 0=dimanche…6=samedi), « 0 */N * * * ».",
                    schedule, cronReason);
                yield return DailyAt(0, 0);
                yield break;
            }

            var keyword = parts[0].ToUpperInvariant();
            switch (keyword)
            {
                case "HOURLY":
                    yield return new TaskTriggerInfo
                    {
                        Type = "IntervalTrigger",
                        IntervalTicks = TimeSpan.FromHours(1).Ticks
                    };
                    yield break;

                case "INTERVAL":
                    // « INTERVAL <heures> » (ancienne syntaxe)
                    if (parts.Length >= 2 && int.TryParse(parts[1], out var hours) && hours > 0)
                        yield return new TaskTriggerInfo
                        {
                            Type = "IntervalTrigger",
                            IntervalTicks = TimeSpan.FromHours(hours).Ticks
                        };
                    else
                    {
                        logger?.Warn("[LLM_AI] Planification « {0} » : INTERVAL sans nombre d'heures valide — repli : toutes les heures.",
                            schedule);
                        yield return new TaskTriggerInfo
                        {
                            Type = "IntervalTrigger",
                            IntervalTicks = TimeSpan.FromHours(1).Ticks
                        };
                    }
                    yield break;

                case "WEEKLY":
                    {
                        // « Weekly Jour HH:MM » (ancienne syntaxe) : le jour doit
                        // être un nom anglais valide et l'heure présente, sinon le
                        // trigger pourrait être invalide ou ignoré silencieusement
                        // par le serveur. On force un repli explicite.
                        var day = ParseDayOfWeek(parts, 1);
                        int wh = 0, wm = 0;
                        var hasTime = parts.Length >= 3 && TryParseTime(parts[2], out wh, out wm);
                        if (day == null || !hasTime)
                        {
                            logger?.Warn(
                                "[LLM_AI] Planification « {0} » : WEEKLY exige un jour reconnu et une heure — " +
                                "repli : tous les jours à 00:00. Forme canonique : « MM HH * * J » (J : 0=dimanche…6=samedi).",
                                schedule);
                            yield return DailyAt(0, 0);
                            yield break;
                        }
                        yield return new TaskTriggerInfo
                        {
                            Type = "WeeklyTrigger",
                            DayOfWeek = day,
                            TimeOfDayTicks = new TimeSpan(wh, wm, 0).Ticks
                        };
                        yield break;
                    }

                case "DAILY":
                    {
                        var (h, m) = ParseTime(parts, 1, 0);
                        yield return DailyAt(h, m);
                        yield break;
                    }

                default:
                    // Peut-être juste une heure « 03:00 » → quotidien à cette heure.
                    if (TryParseTime(schedule, out var hh, out var mm))
                    {
                        yield return DailyAt(hh, mm);
                    }
                    else
                    {
                        // Le repli est journalisé pour rendre visible une éventuelle
                        // planification invalide.
                        logger?.Warn(
                            "[LLM_AI] Planification « {0} » non reconnue — repli : tous les jours à 00:00. " +
                            "Syntaxe : « MM HH * * * » (tous les jours), « MM HH * * J » (J : 0=dimanche…6=samedi), " +
                            "« 0 */N * * * » (toutes les N heures).",
                            schedule);
                        yield return DailyAt(0, 0);
                    }
                    yield break;
            }
        }

        /// <summary>Crontab restreint : « MM HH * * * » (quotidien),
        /// « MM HH * * J » (hebdomadaire, J = 0..6, 0 = dimanche) et
        /// « 0 */N * * * » (toutes les N heures). Les autres syntaxes cron
        /// (plages, listes, etc.) ne sont pas supportées car non
        /// représentables par les types de triggers d'Emby.</summary>
        private static bool TryParseCron(string[] f, out TaskTriggerInfo trigger, out string reason)
        {
            trigger = null;
            reason = null;

            // Minute : nombre simple 0-59.
            if (!int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute)
                || minute < 0 || minute > 59)
            {
                reason = "minute invalide (0-59, pas de */N ni de plage)";
                return false;
            }

            // Jour du mois et mois : uniquement « * ».
            if (f[2] != "*" || f[3] != "*")
            {
                reason = "jour du mois / mois : seul « * » est supporté";
                return false;
            }

            // Heure : « */N » = intervalle de N heures (uniquement avec
            // minute 0 et sans jour de semaine), sinon nombre simple 0-23.
            if (f[1].StartsWith("*/", StringComparison.Ordinal))
            {
                if (minute != 0 || f[4] != "*")
                {
                    reason = "intervalle : forme « 0 */N * * * » uniquement";
                    return false;
                }
                if (!int.TryParse(f[1].Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                    || n < 1)
                {
                    reason = "intervalle invalide (N >= 1)";
                    return false;
                }
                trigger = new TaskTriggerInfo
                {
                    Type = "IntervalTrigger",
                    IntervalTicks = TimeSpan.FromHours(n).Ticks
                };
                return true;
            }

            if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour)
                || hour < 0 || hour > 23)
            {
                reason = "heure invalide (0-23)";
                return false;
            }

            // Jour de semaine : « * » = quotidien, 0-6 = hebdomadaire
            // (0 = dimanche — convention cron classique ET DayOfWeek .NET).
            if (f[4] == "*")
            {
                trigger = DailyAt(hour, minute);
                return true;
            }

            if (int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dow)
                && dow >= 0 && dow <= 6)
            {
                trigger = new TaskTriggerInfo
                {
                    Type = "WeeklyTrigger",
                    DayOfWeek = (DayOfWeek)dow,
                    TimeOfDayTicks = new TimeSpan(hour, minute, 0).Ticks
                };
                return true;
            }

            reason = "jour de la semaine invalide (0-6, 0=dimanche ; noms refusés)";
            return false;
        }

        private static TaskTriggerInfo DailyAt(int h, int m) => new TaskTriggerInfo
        {
            Type = "DailyTrigger",
            TimeOfDayTicks = new TimeSpan(h, m, 0).Ticks
        };

        private static (int h, int m) ParseTime(string[] parts, int index, int defaultH)
        {
            if (index < parts.Length && TryParseTime(parts[index], out var h, out var m))
                return (h, m);
            return (defaultH, 0);
        }

        private static bool TryParseTime(string s, out int h, out int m)
        {
            h = 0; m = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            int colon = s.IndexOf(':');
            if (colon <= 0 || colon >= s.Length - 1) return false;
            if (!int.TryParse(s.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out h)) return false;
            if (!int.TryParse(s.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out m)) return false;
            return h >= 0 && h < 24 && m >= 0 && m < 60;
        }

        private static DayOfWeek? ParseDayOfWeek(string[] parts, int index)
        {
            if (index >= parts.Length) return null;
            return parts[index].ToUpperInvariant() switch
            {
                "MONDAY" => DayOfWeek.Monday,
                "TUESDAY" => DayOfWeek.Tuesday,
                "WEDNESDAY" => DayOfWeek.Wednesday,
                "THURSDAY" => DayOfWeek.Thursday,
                "FRIDAY" => DayOfWeek.Friday,
                "SATURDAY" => DayOfWeek.Saturday,
                "SUNDAY" => DayOfWeek.Sunday,
                _ => (DayOfWeek?)null
            };
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");

        // ------------------------------------------------------------------
        //  Persistance + notification des recommandations
        // ------------------------------------------------------------------

        /// <summary>
        /// Écrit la réponse finale (déjà nettoyée) dans la config du plugin
        /// (champs <see cref="PluginConfiguration.Recommendations"/> /
        /// <see cref="PluginConfiguration.RecommendationsDate"/>) et persiste
        /// sur disque via <see cref="MediaBrowser.Common.Plugins.BasePlugin.SaveConfiguration"/>.
        /// La page dashboard « Recommandations LLM AI » relira ces champs.
        /// </summary>
        private void PersistRecommendations(PluginConfiguration cfg, string payload)
        {
            try
            {
                cfg.Recommendations = payload ?? string.Empty;
                cfg.RecommendationsDate = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                Plugin.Instance?.SaveConfiguration();
                _logger?.Info("[LLM_AI] Recommandations persistées ({0} octets).", payload?.Length ?? 0);
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] Persistance des recommandations échouée : {0}", ex, ex.Message);
            }
        }

        /// <summary>
        /// Envoie une notification Emby (cloche) à tous les utilisateurs :
        /// « N nouvelles recommandations ». Pointe vers la racine Emby
        /// (la page « Recommandations LLM AI » est atteignable via le menu).
        /// <para>La description liste chaque recommandation au lieu d'un
        /// simple compteur (voir <see cref="BuildRecommendationBellDescription"/>).</para>
        /// <para>L'API de notification ne prenant qu'un utilisateur à la
        /// fois, on boucle sur la liste des utilisateurs pour notifier
        /// tout le monde.</para>
        /// </summary>
        private void SendRecommendationNotification(string reply)
        {
            if (_notifications == null) return;

            List<User> users;
            try
            {
                users = _users.GetUserList(new UserQuery()).ToList();
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] Récupération utilisateurs pour notification : {0}", ex, ex.Message);
                return;
            }
            if (users.Count == 0) return;

            string desc = BuildRecommendationBellDescription(reply);
            // URL absolue pour le click-through de la notification, résolue via
            // la configuration du serveur pour éviter toute IP codée en dur.
            string url = ResolveEmbyUrl();
            // Lien aussi en description (les canaux de lecture comme webhooks/
            // courriel ignorent le champ Url d'Emby).
            if (!string.IsNullOrWhiteSpace(url))
                desc = desc + "\n" + url;
            var now = DateTimeOffset.UtcNow;

            int sent = 0;
            foreach (var u in users)
            {
                try
                {
                    var req = new NotificationRequest
                    {
                        Title = "🤖 LLM AI — Recommandations d'enregistrement",
                        Description = desc,
                        Url = url,
                        Date = now,
                        Severity = LogSeverity.Info,
                        User = u
                    };
                    _notifications.SendNotification(req);
                    sent++;
                }
                catch (Exception ex)
                {
                    _logger?.ErrorException("[LLM_AI] Notification à « {0} » échouée : {1}", ex, u.Name, ex.Message);
                }
            }
            _logger?.Info("[LLM_AI] Notification envoyée à {0}/{1} utilisateur(s).", sent, users.Count);
        }

        // ------------------------------------------------------------------
        //  Cloche « recommandations » : description détaillée (v2.20.0)
        // ------------------------------------------------------------------

        /// <summary>
        /// Construit la description détaillée de la notification de
        /// recommandations. Le payload JSON des recommandations est mis en
        /// forme pour présenter une liste lisible des nouveaux items, au lieu
        /// d'un simple compteur.
        /// </summary>
        private string BuildRecommendationBellDescription(string reply)
        {
            int count = LlmRunner.CountRecommendations(reply);
            if (count <= 0)
            {
                // Payload non-tableau (prose) ou vide : le libellé générique
                // historique reste le plus honnête.
                return "Recommandations mises à jour — voir la page « Recommandations LLM AI ».";
            }

             // Un seul passage : total PAR TYPE sur tout le tableau (l'en-tête
             // reste juste même au-delà du plafond), lignes détaillées
             // plafonnées à BellDetailCap.
            int totalSeries = 0, totalMovies = 0, listed = 0;
            var series = new List<string>();
            var movies = new List<string>();
            try
            {
                using (var doc = JsonDocument.Parse(reply))
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        bool isMovie = BellKindIsMovie(el);
                        if (isMovie) totalMovies++; else totalSeries++;
                        if (listed >= BellDetailCap) continue; // compté, pas détaillé

                        string lines = BellItemLines(el);
                        if (lines == null) continue;           // sans titre : compté seulement
                        (isMovie ? movies : series).Add(lines);
                        listed++;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] Mise en forme de la cloche recommandations échouée : {0}", ex, ex.Message);
                return SimpleBellLine(count);
            }

            if (listed == 0)
                return SimpleBellLine(count);

            var sb = new StringBuilder();
            sb.Append(FrCount(count, "nouvelle recommandation", "nouvelles recommandations"));
            var kinds = new List<string>();
            if (totalSeries > 0) kinds.Add(FrCount(totalSeries, "série", "séries"));
            if (totalMovies > 0) kinds.Add(FrCount(totalMovies, "film", "films"));
            if (kinds.Count > 0) sb.Append(" (").Append(string.Join(", ", kinds)).Append(')');
            sb.Append('.');

            AppendBellSection(sb, "📺 Séries", series);
            AppendBellSection(sb, "🎬 Films", movies);

            sb.Append("\n— ");
            int rest = count - listed;
            if (rest > 0)
                sb.Append("… et ").Append(FrCount(rest, "autre, sur la page", "autres, sur la page"));
            else
                sb.Append("liste complète et boutons sur la page");
            sb.Append(" « Recommandations LLM AI ».");
            return sb.ToString();
        }

        /// <summary>Un item de la cloche : « ▶ Titre (année) — 13/11 21:00 ·
        /// CBFT-DT » puis, si le LLM en a produit une, la raison « 🤖 … »
        /// (tronquée à <see cref="BellReasonMax"/>). Null si l'item n'a pas de
        /// titre ; il reste compté dans le total mais n'apparaît pas dans
        /// le détail de la notification.</summary>
        private static string BellItemLines(JsonElement el)
        {
            if (!el.TryGetProperty("title", out var t) || t.ValueKind != JsonValueKind.String)
                return null;
            string title = Oneline(t.GetString());
            if (string.IsNullOrWhiteSpace(title)) return null;

            var sb = new StringBuilder("▶ ").Append(title);

            // Année de production : tolère les formats nombre et chaîne.
            if (TryBellYear(el, out int year))
                sb.Append(" (").Append(year.ToString(CultureInfo.InvariantCulture)).Append(')');

            // Disponibilité : source recording/library = déjà disponible, ni
            // date ni chaîne (même convention que la méta-ligne de la page) ;
            // sinon date de diffusion + chaîne du contrat LLM.
            string source = el.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String
                ? src.GetString() : null;
            if (string.Equals(source, "recording", StringComparison.OrdinalIgnoreCase))
                sb.Append(" — 📼 déjà enregistré");
            else if (string.Equals(source, "library", StringComparison.OrdinalIgnoreCase))
                sb.Append(" — 📚 déjà en bibliothèque");
            else
            {
                var meta = new List<string>();
                if (TryBellStart(el, out string startStr)) meta.Add(startStr);
                string channel = el.TryGetProperty("channel", out var ch) && ch.ValueKind == JsonValueKind.String
                    ? Oneline(ch.GetString()) : null;
                if (!string.IsNullOrEmpty(channel)) meta.Add(channel);
                if (meta.Count > 0)
                    sb.Append(" — ").Append(string.Join(" · ", meta));
            }

            if (el.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
            {
                string reason = Oneline(r.GetString());
                if (!string.IsNullOrWhiteSpace(reason))
                    sb.Append("\n🤖 ").Append(Truncate(reason, BellReasonMax));
            }
            return sb.ToString();
        }

        /// <summary>Type de l'item — même test que <c>kindIsMovie</c> côté page
        /// (kind commençant par « movie » ou « film »).</summary>
        private static bool BellKindIsMovie(JsonElement el)
        {
            if (!el.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String)
                return false;
            string kind = k.GetString();
            return kind != null &&
                (kind.StartsWith("movie", StringComparison.OrdinalIgnoreCase) ||
                 kind.StartsWith("film", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Année de production de l'item : nombre ou chaîne numérique
        /// entre 1900 et 2100 (mêmes bornes que la page — ignore le bruit).</summary>
        private static bool TryBellYear(JsonElement el, out int year)
        {
            year = 0;
            if (!el.TryGetProperty("year", out var y)) return false;
            if (y.ValueKind == JsonValueKind.Number && y.TryGetInt32(out year))
                return year > 1900 && year < 2100;
            if (y.ValueKind == JsonValueKind.String &&
                int.TryParse(y.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out year))
                return year > 1900 && year < 2100;
            return false;
        }

        /// <summary>Date de diffusion au format de la page (dd/MM HH:mm), heure
        /// locale du serveur — même rendu que le toast d'enregistrement
        /// (TonightRecordingService).</summary>
        private static bool TryBellStart(JsonElement el, out string start)
        {
            start = null;
            if (!el.TryGetProperty("start", out var s) || s.ValueKind != JsonValueKind.String)
                return false;
            if (!DateTimeOffset.TryParse(s.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTimeOffset dto))
                return false;
            start = dto.LocalDateTime.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>Section « 📺 Séries » / « 🎬 Films » de la cloche : titre de
        /// section puis une ligne d'items par item. Utilise LF uniquement
        /// pour la compatibilité avec les webhooks.</summary>
        private static void AppendBellSection(StringBuilder sb, string heading, List<string> lines)
        {
            if (lines == null || lines.Count == 0) return;
            sb.Append("\n\n").Append(heading);
            foreach (var l in lines)
                sb.Append("\n").Append(l);
        }

        /// <summary>Forme raccourcie historique (repli fail-open) : compteur
        /// seul, accord pluriel correct.</summary>
        private static string SimpleBellLine(int count) =>
            string.Format(CultureInfo.InvariantCulture,
                count == 1
                    ? "{0} nouvelle recommandation — voir la page « Recommandations LLM AI »."
                    : "{0} nouvelles recommandations — voir la page « Recommandations LLM AI ».",
                count);

        /// <summary>« 1 série » / « 3 séries » — accord pluriel FR, nombre en
        /// culture invariante.</summary>
        private static string FrCount(int n, string sing, string plur) =>
            n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? sing : plur);

        /// <summary>Supprime les sauts de ligne d'une chaîne pour garantir que
        /// la description de la notification reste compacte et compatible avec
        /// les webhooks, qui ne gèrent pas toujours les CR/LF.</summary>
        private static string Oneline(string s) =>
            string.IsNullOrWhiteSpace(s) ? s : s.Replace("\r", " ").Replace("\n", " ").Trim();

        /// <summary>
        /// Notification d'échec (cloche) aux administrateurs : la tâche
        /// n'a rien produit — soit run « réussi » mais vide (garde
        /// anti-effacement, LLM indisponible/dégradé), soit exception
        /// (les deux runs agent ont jeté). Le message est technique :
        /// seul l'admin peut agir (backends LLM, clés… — même motif que
        /// la cloche cross-kind). <c>Severity</c> plus élevée pour la
        /// distinguer du carillon « nouvelles recos ». Best-effort :
        /// jamais d'exception au-delà de cette méthode.
        /// </summary>
        private void SendFailureNotification(string reason)
        {
            if (_notifications == null || string.IsNullOrWhiteSpace(reason)) return;

            List<User> users;
            try
            {
                users = _users.GetUserList(new UserQuery())
                    .Where(u => u != null && (u.Policy?.IsAdministrator ?? false))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] Récupération utilisateurs pour notification d'échec : {0}", ex, ex.Message);
                return;
            }
            if (users.Count == 0)
            {
                _logger?.Warn("[LLM_AI] Notification d'échec NON envoyée — aucun administrateur.");
                return;
            }

            string url = ResolveEmbyUrl();
            // Lien aussi en description (les canaux de lecture comme webhooks/
            // courriel ignorent le champ Url d'Emby).
            if (!string.IsNullOrWhiteSpace(url))
                reason = reason + "\n" + url;
            var now = DateTimeOffset.UtcNow;

            int sent = 0;
            foreach (var u in users)
            {
                try
                {
                    var req = new NotificationRequest
                    {
                        Title = "LLM AI",
                        Description = reason,
                        Url = url,
                        Date = now,
                        Severity = LogSeverity.Error,
                        User = u
                    };
                    _notifications.SendNotification(req);
                    sent++;
                }
                catch (Exception ex)
                {
                    _logger?.ErrorException("[LLM_AI] Notification d'échec à « {0} » : {1}", ex, u.Name, ex.Message);
                }
            }
            _logger?.Info("[LLM_AI] Notification d'échec envoyée à {0}/{1} administrateur(s).", sent, users.Count);
        }

        /// <summary>
        /// Résout l'URL de base d'Emby pour les liens absolus (notifications,
        /// cartes <c>.strm</c>). On ne code aucune IP.
        /// <para><b>Priorité</b> : <see cref="PluginConfiguration.EmbyPublicUrl"/>,
        /// si défini, est utilisé en priorité pour garantir que les liens
        /// sont accessibles depuis des clients externes.
        /// <c>GetLocalHostApiUrl</c> est utilisé en repli.</para>
        /// <para>Sinon, on interroge la config réseau du serveur via
        /// <see cref="IServerApplicationHost.GetLocalHostApiUrl"/> (ports
        /// HTTP/HTTPS + schéma de <c>system.xml</c> + hôte local détecté).
        /// Repli sur chaîne vide si les deux échouent.</para>
        /// </summary>
        private string ResolveEmbyUrl()
        {
            // 1) EmbyPublicUrl explicite : prioritaire (voir le résumé de la méthode).
            string publicUrl = (Plugin.Instance?.Configuration?.EmbyPublicUrl ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(publicUrl))
                return publicUrl;

            // 2) Détection automatique (hôte local) — convient au serveur
            //    lui-même et aux clients sur la même machine.
            try
            {
                if (_host != null)
                {
                    string local = _host.GetLocalHostApiUrl();
                    if (!string.IsNullOrWhiteSpace(local))
                        return local;
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] ResolveEmbyUrl : GetLocalHostApiUrl a échoué ({0}) — aucun repli disponible (EmbyPublicUrl vide).", ex.Message);
            }
            return string.Empty;
        }
    }
}