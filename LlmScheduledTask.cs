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

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
            {
                _logger.Warn("[LLM_AI] Tâche exécutée mais aucune configuration disponible (Plugin.Instance null).");
                return;
            }

            progress?.Report(10);

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

            try
            {
                string p1 = "", p2 = "";
                bool ok1 = false, ok2 = false;

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
                    throw new Exception("Les deux runs agent (séries et films) ont échoué.");
                }

                // Fusionne les deux tableaux JSON en un payload unique. La page
                // Recommandations split par kind (series/movie) en deux sections.
                var merged = LlmRunner.MergeJsonArrays(p1, p2);

                // Owned-guard déterministe : rapproche les recos de la
                // bibliothèque (titre, puis id IMDb si le LLM l'a établi via
                // ses outils). Une reco possédée reçoit library_id → exclue du
                // record bucket (.strm/Auto-program/badges) et affichée avec
                // le bouton « Regarder (bibli.) ». Sans ce rapprochement, la
                // détection du « déjà possédé » repose uniquement sur la
                // classification source du LLM (cas vécu : reco d'enregistrer
                // « Comment tuer son mari en 10 leçons » alors que le film,
                // même titre ET même id IMDb, était déjà en bibliothèque).
                merged = _runner.EnrichWithLibrary(merged);
                PersistRecommendations(cfg, merged);
                SendRecommendationNotification(merged);

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
                        var ap = new AutoProgrammer(_liveTv, _library, _logger);
                        await ap.Program(merged, null, cfg, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.ErrorException("[LLM_AI] Auto-program (tâche planifiée) : {0}", ex, ex.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Info("[LLM_AI] Tâche annulée.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[LLM_AI] Échec de l'agent LLM : {0}", ex, ex.Message);
                throw;
            }
            finally
            {
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
        /// (ex. « Daily 03:00 », « Hourly », « Weekly Monday 03:00 »).
        /// L'utilisateur peut toujours les ajuster dans le planificateur Emby.
        /// </summary>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var cfg = Plugin.Instance?.Configuration;
            var spec = cfg?.ScheduleTask ?? string.Empty;

            int sep = spec.IndexOf('|');
            string schedule = (sep >= 0 ? spec.Substring(0, sep) : spec).Trim();

            foreach (var t in ParseTriggers(schedule))
                yield return t;
        }

        private static IEnumerable<TaskTriggerInfo> ParseTriggers(string schedule)
        {
            if (string.IsNullOrWhiteSpace(schedule))
            {
                yield return DailyAt(0, 0);
                yield break;
            }

            var parts = schedule.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
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
                    // « INTERVAL <heures> »
                    if (parts.Length >= 2 && int.TryParse(parts[1], out var hours) && hours > 0)
                        yield return new TaskTriggerInfo
                        {
                            Type = "IntervalTrigger",
                            IntervalTicks = TimeSpan.FromHours(hours).Ticks
                        };
                    else
                        yield return new TaskTriggerInfo
                        {
                            Type = "IntervalTrigger",
                            IntervalTicks = TimeSpan.FromHours(1).Ticks
                        };
                    yield break;

                case "WEEKLY":
                    {
                        var (h, m) = ParseTime(parts, 2, 1); // Weekly [Day] HH:MM
                        yield return new TaskTriggerInfo
                        {
                            Type = "WeeklyTrigger",
                            DayOfWeek = ParseDayOfWeek(parts, 1),
                            TimeOfDayTicks = new TimeSpan(h, m, 0).Ticks
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
                        yield return DailyAt(hh, mm);
                    else
                        yield return DailyAt(0, 0);
                    yield break;
            }
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
        /// <para>l'API <c>INotificationManager.SendNotification</c> attend un
        /// <c>Emby.Notifications.NotificationRequest</c> qui porte un SEUL
        /// <c>User</c> — on boucle donc sur les utilisateurs pour notifier tout
        /// le monde. <c>Severity = LogSeverity.Info</c> (niveau information).</para>
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

            int count = LlmRunner.CountRecommendations(reply);
            string desc = count > 0
                ? string.Format(CultureInfo.InvariantCulture,
                    "{0} nouvelles recommandations — voir la page « Recommandations LLM AI ».", count)
                : "Recommandations mises à jour — voir la page « Recommandations LLM AI ».";
            // URL absolue pour le click-through de la notification : dérivée de
            // la config réseau d'Emby (IServerApplicationHost.GetLocalHostApiUrl
            // — lit les ports/schéma de system.xml + l'hôte local détecté), pas
            // d'une IP codée. Repli sur EmbyPublicUrl si la détection échoue ou
            // si l'utilisateur a renseigné un domaine public explicite.
            string url = ResolveEmbyUrl();
            var now = DateTimeOffset.UtcNow;

            int sent = 0;
            foreach (var u in users)
            {
                try
                {
                    var req = new NotificationRequest
                    {
                        Title = "LLM AI",
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

        /// <summary>
        /// Résout l'URL de base d'Emby pour les liens absolus (notifications,
        /// cartes <c>.strm</c>). On ne code aucune IP.
        /// <para><b>Priorité</b> : si <see cref="PluginConfiguration.EmbyPublicUrl"/>
        /// est renseigné, on l'utilise en priorité — l'usager l'a défini exprès
        /// pour que les liens soient joignables depuis les clients externes
        /// (TV, téléphone) ; <c>GetLocalHostApiUrl</c> renvoie souvent
        /// <c>localhost</c>, injouable par un client distant qui lit un
        /// <c>.strm</c>.</para>
        /// <para>Sinon, on interroge la config réseau du serveur via
        /// <see cref="IServerApplicationHost.GetLocalHostApiUrl"/> (ports
        /// HTTP/HTTPS + schéma de <c>system.xml</c> + hôte local détecté).
        /// Repli sur chaîne vide si les deux échouent.</para>
        /// </summary>
        private string ResolveEmbyUrl()
        {
            // 1) EmbyPublicUrl explicite -> prioritaire : l'usager l'a défini
            //    exprès pour que les liens soient joignables depuis les clients
            //    externes (TV, téléphone). GetLocalHostApiUrl renvoie souvent
            //    "localhost", injouable par un client distant qui lit un .strm.
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