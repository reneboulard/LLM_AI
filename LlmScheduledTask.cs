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
        }

        public string Name => "LLM AI Task";

        /// <summary>Identifiant stable de la tâche (GUID du plugin).</summary>
        public string Key => "e7d3dee6-ef19-46a9-985f-06318b682e60";

        public string Description => "Agent LLM autonome (Ollama) qui interroge la bibliothèque Emby via des outils natifs read-only pour accomplir la tâche configurée.";

        public string Category => "LLM AI";

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
                var merged = MergeJsonArrays(p1, p2);
                PersistRecommendations(cfg, merged);
                SendRecommendationNotification(merged);
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
        /// Construit la liste d'outils exposés au LLM pour un run. Les outils
        /// optionnels ne sont inclus que si la config correspondante est active.
        /// </summary>
        private List<ILlmTool> BuildTools(PluginConfiguration cfg)
        {
            var tools = new List<ILlmTool>
            {
                new GetEmbyInfoTool(_library, _users, _liveTv, _host, _logger)
            };
            if (!string.IsNullOrWhiteSpace(cfg.TmdbApiKey))
                tools.Add(new TmdbLookupTool(_logger));
            if (!string.IsNullOrWhiteSpace(cfg.TvdbApiKey) ||
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TVDB_API_KEY")))
                tools.Add(new TvdbSearchTool(_logger));
            if (!string.IsNullOrWhiteSpace(cfg.ShowbizzUrl))
                tools.Add(new ShowbizzTool(_logger));
            // web_search : SearXNG (auto-hébergé) OU Ollama cloud.
            bool webSearchConfigured = !string.IsNullOrWhiteSpace(cfg.SearXngUrl) ||
                !string.IsNullOrWhiteSpace(cfg.OllamaApiKey) ||
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OLLAMA_API_KEY"));
            // web_fetch : backend direct auto-hébergé (défaut, sans clé) OU
            // repli Ollama cloud. Disponible dès l'installation pour la communauté.
            bool webFetchConfigured = cfg.WebFetchDirect ||
                !string.IsNullOrWhiteSpace(cfg.OllamaApiKey) ||
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OLLAMA_API_KEY"));
            if (webSearchConfigured)
                tools.Add(new WebSearchTool(_logger));
            if (webFetchConfigured)
                tools.Add(new WebFetchTool(_logger));
            return tools;
        }

        /// <summary>
        /// Exécute un run agent (un seul fil de conversation) : construit les
        /// backends + outils, lance la boucle de tool-calling, nettoie la
        /// réponse finale et l'enrichit (match titres → id/channel_id/rating/
        /// image_url). Retourne (payload enrichi, ok). ok=false si le run a
        /// échoué (erreur catchée + loguée) — sans faire planter la tâche :
        /// l'appelant fusionne ce qu'il a et déclenche une erreur globale
        /// seulement si les deux runs ont échoué. OperationCanceledException
        /// est propagée (annulation réelle, pas un échec ordinaire).
        /// </summary>
        private async Task<(string payload, bool ok)> RunOne(
            PluginConfiguration cfg, string label, string userPrompt, string workflow, CancellationToken ct)
        {
            try
            {
                var backends = ResolveBackends(cfg);
                if (backends.Count == 0)
                {
                    _logger.Warn("[LLM_AI] [{0}] Aucun LLM configuré/activé — run ignoré.", label);
                    return (string.Empty, false);
                }

                string ollamaCloudKey = ResolveKey(cfg.OllamaApiKey, "OLLAMA_API_KEY");
                string geminiKey = ResolveKey(cfg.GeminiApiKey, "GEMINI_API_KEY");

                var agent = new LlmAgentService(backends, cfg.RagDirectives, workflow,
                    ollamaCloudKey, geminiKey, _json, _logger, cfg.DebugVerbose);
                var tools = BuildTools(cfg);

                var (reply, toolResults) = await agent.RunAsync(userPrompt, tools, ct).ConfigureAwait(false);

                _logger.Info("[LLM_AI] [{0}] Réponse :\n{1}", label, reply);

                // Nettoie (balises markdown ```json … ```) puis enrichit (match
                // titres vs résultats epg_series/epg_movies → id/channel_id/
                // rating/image_url). Portage C# du matching par titre PHP.
                var payload = ExtractJsonPayload(reply);
                payload = EnrichRecommendations(payload, toolResults);
                return (payload, true);
            }
            catch (OperationCanceledException)
            {
                _logger.Info("[LLM_AI] [{0}] Run annulé.", label);
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[LLM_AI] [{0}] Échec du run : {1}", ex, label, ex.Message);
                return (string.Empty, false);
            }
        }

        /// <summary>
        /// Fusionne deux payloads de recommandations en un seul tableau JSON.
        /// Les deux runs (séries + films) produisent chacun un tableau JSON
        /// <c>[{...}]</c> ; on concatène leurs items. Si l'un est vide ou n'est
        /// pas un tableau JSON (Markdown libre — réponse dégradée), on garde
        /// l'autre tel quel. Si les deux sont vides, renvoie une chaîne vide
        /// (la page affichera « aucune recommandation »).
        /// </summary>
        private static string MergeJsonArrays(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b)) return string.Empty;
            if (string.IsNullOrWhiteSpace(a)) return b;
            if (string.IsNullOrWhiteSpace(b)) return a;

            var merged = new System.Text.Json.Nodes.JsonArray();
            foreach (var p in new[] { a, b })
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                try
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(p);
                    if (node is System.Text.Json.Nodes.JsonArray src)
                    {
                        foreach (var item in src)
                            merged.Add(item.DeepClone());
                    }
                    // Non-tableau (Markdown) : ignoré — on garde les items de
                    // l'autre run. On ne mélange pas du prose dans le JSON.
                }
                catch
                {
                    // Non parsable : ignoré.
                }
            }
            return merged.ToJsonString();
        }

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

        /// <summary>
        /// Construit la liste ordonnée des backends LLM à essayer, depuis la
        /// config : filtre les backends <c>Enabled</c> à URL non vide, triés
        /// par <c>Priority</c> croissante (1 = essayé en premier), puis par
        /// ordre de saisie. Repli de migration : si
        /// <see cref="PluginConfiguration.LlmBackends"/> est vide, on construit
        /// un backend unique depuis les champs legacy
        /// <see cref="PluginConfiguration.LlmUrl"/>/
        /// <see cref="PluginConfiguration.ModelName"/>. Retourne une liste
        /// vide si rien n'est configuré.
        /// </summary>
        private List<LlmBackend> ResolveBackends(PluginConfiguration cfg)
        {
            var list = new List<LlmBackend>();

            if (cfg.LlmBackends != null && cfg.LlmBackends.Count > 0)
            {
                for (int i = 0; i < cfg.LlmBackends.Count; i++)
                {
                    var b = cfg.LlmBackends[i];
                    if (b == null || !b.Enabled)
                        continue;
                    // URL vide acceptée pour cloud/gemini (valeur par défaut
                    // appliquée côté LlmClient). Pour ollama_local, on exige
                    // une URL explicite.
                    if (string.IsNullOrWhiteSpace(b.Url) &&
                        b.ProviderType == LlmProvider.OllamaLocal)
                        continue;
                    list.Add(new LlmBackend
                    {
                        Provider = b.Provider,
                        Url = (b.Url ?? string.Empty).Trim(),
                        Model = b.Model ?? string.Empty,
                        Enabled = true,
                        Priority = b.Priority
                    });
                }
            }
            else if (!string.IsNullOrWhiteSpace(cfg.LlmUrl))
            {
                // Migration : vieille config sans LlmBackends mais avec LlmUrl.
                list.Add(new LlmBackend
                {
                    Provider = "ollama_local",
                    Url = cfg.LlmUrl.Trim(),
                    Model = cfg.ModelName ?? string.Empty,
                    Enabled = true,
                    Priority = 1
                });
                _logger.Info("[LLM_AI] Aucun LlmBackends configuré — utilisation du legacy LlmUrl ({0}).",
                    cfg.LlmUrl.Trim());
            }

            // Tri par priorité croissante, ordre de saisie en cas d'égalité
            // (List<T>.Sort n'étant pas stable, on utilise un tri indexé).
            var indexed = new List<(LlmBackend b, int order)>();
            for (int i = 0; i < list.Count; i++) indexed.Add((list[i], i));
            indexed.Sort((x, y) =>
            {
                int c = x.b.Priority.CompareTo(y.b.Priority);
                return c != 0 ? c : x.order.CompareTo(y.order);
            });

            list = indexed.Select(t => t.b).ToList();

            _logger.Info("[LLM_AI] {0} backend(s) LLM activé(s) — ordre de tentative :",
                list.Count);
            foreach (var b in list)
                _logger.Info("[LLM_AI]   priorité {0} [{1}] : {2} / {3}",
                    b.Priority, b.ProviderType, b.Url, b.Model);

            return list;
        }

        /// <summary>
        /// Résout une clé API : valeur de config si renseignée, sinon repli
        /// sur la variable d'environnement <paramref name="envName"/>.
        /// </summary>
        private static string ResolveKey(string configValue, string envName)
        {
            if (!string.IsNullOrWhiteSpace(configValue)) return configValue.Trim();
            return Environment.GetEnvironmentVariable(envName) ?? string.Empty;
        }

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
        /// Extrait un tableau JSON propre depuis la réponse du LLM : retire les
        /// balises markdown <c>```json … ```</c>, et isole le premier
        /// <c>[ … ]</c> équilibré. Si la réponse n'est pas du JSON (Markdown
        /// libre), elle est renvoyée telle quelle (la page l'affichera en brut).
        /// </summary>
        private static string ExtractJsonPayload(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return string.Empty;

            var s = reply.Trim();

            // Retire les balises de code markdown éventuelles.
            if (s.StartsWith("```", StringComparison.Ordinal))
            {
                int nl = s.IndexOf('\n');
                if (nl >= 0) s = s.Substring(nl + 1);
                int fenceEnd = s.LastIndexOf("```", StringComparison.Ordinal);
                if (fenceEnd >= 0) s = s.Substring(0, fenceEnd);
                s = s.Trim();
            }

            // Déjà un tableau JSON ?
            if (s.StartsWith("[", StringComparison.Ordinal)) return s;

            // Sinon, isole le premier [ ... ] si présent.
            int start = s.IndexOf('[');
            if (start >= 0)
            {
                int end = s.LastIndexOf(']');
                if (end > start) return s.Substring(start, end - start + 1);
            }

            // Pas de tableau : Markdown libre — on garde tel quel.
            return reply.Trim();
        }

        /// <summary>
        /// Normalise un titre pour le rapprochement : minuscules + retrait de
        /// tout ce qui n'est pas alphanumérique. Équivalent C# du
        /// <c>preg_replace('/[^a-z0-9]/i','')</c> + <c>mb_strtolower</c> du PHP
        /// (absent_series.php / ai_section.php) et du <c>Norm</c> de
        /// <see cref="GetEmbyInfoTool"/>. « Star Trek » et « star-trek! » →
        /// « startrek ».
        /// </summary>
        private static string NormTitle(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Données EPG rattachées à un titre (extraites au moment de la
        /// construction de la lookup) — on ne conserve pas de <c>JsonElement</c>
        /// au-delà de la durée de vie du <c>JsonDocument</c> source (sinon
        /// <c>ObjectDisposedException</c> à la lecture différée).
        /// </summary>
        private readonly struct EpgMatch
        {
            public readonly string Id;
            public readonly string ChannelId;
            public readonly double? Rating;
            public EpgMatch(string id, string channelId, double? rating)
            { Id = id; ChannelId = channelId; Rating = rating; }
        }

        /// <summary>
        /// Enrichit le payload de recommandations (tableau JSON
        /// <c>[{title,kind,reason,priority,channel,start,showbizz_match}]</c>)
        /// en rapprochant chaque titre des résultats d'outils
        /// <c>get_emby_info</c> (actions <c>epg_series</c>/<c>epg_movies</c>)
        /// capturés pendant la boucle agent. Les items EPG portent
        /// <c>id</c>/<c>channel_id</c>/<c>rating</c> ; on les injecte dans la
        /// recommandation + <c>image_url</c> (poster) construit depuis
        /// <paramref name="embyPublicUrl"/>.
        /// <para>Portage C# du matching par titre du PHP : on construit une
        /// lookup <c>norm(title) → EpgMatch</c>, puis pour chaque reco on
        /// cherche <c>norm(reco.title)</c>. Si pas de match (titre rewordé par
        /// le LLM), la reco est gardée telle quelle (pas de poster, Programmer
        /// désactivé côté UI). Retourne le payload inchangé si ce n'est pas un
        /// tableau JSON.</para>
        /// </summary>
        private string EnrichRecommendations(string payload,
            List<(string tool, string result)> toolResults)
        {
            if (string.IsNullOrWhiteSpace(payload)) return payload;

            // 1) Construit la lookup norm(title) → EpgMatch depuis les résultats
            //    d'outils. On ne garde que les résultats d'epg_series/epg_movies ;
            //    leur forme est {total, results:[{title,id,channel_id,rating,...}]}.
            //    On extrait les primitives tout de suite (le JsonDocument est
            //    disposé à la sortie du bloc using — on ne garde aucune référence
            //    à ses JsonElement).
            var lookup = new Dictionary<string, EpgMatch>(StringComparer.Ordinal);
            foreach (var tr in toolResults)
            {
                if (string.IsNullOrEmpty(tr.result)) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(tr.result); }
                catch { continue; }
                using (doc)
                {
                    if (!doc.RootElement.TryGetProperty("results", out var results) ||
                        results.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var item in results.EnumerateArray())
                    {
                        if (!item.TryGetProperty("title", out var titleEl)) continue;
                        string title = titleEl.ValueKind == JsonValueKind.String
                            ? titleEl.GetString() : null;
                        string key = NormTitle(title);
                        if (string.IsNullOrEmpty(key)) continue;

                        string id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                            ? idEl.GetString() : null;
                        string channelId = item.TryGetProperty("channel_id", out var cidEl) && cidEl.ValueKind == JsonValueKind.String
                            ? cidEl.GetString() : null;
                        double? rating = null;
                        if (item.TryGetProperty("rating", out var rEl) && rEl.ValueKind == JsonValueKind.Number)
                            rating = rEl.GetDouble();

                        // Premier vu gagne (dedup) — les EPG peuvent lister la même
                        // série sur plusieurs chaînes ; on garde la 1re occurrence.
                        if (!lookup.ContainsKey(key))
                            lookup[key] = new EpgMatch(id, channelId, rating);
                    }
                }
            }

            if (lookup.Count == 0)
            {
                _logger?.Info("[LLM_AI] Enrichissement : aucun résultat epg_series/epg_movies capturé — reco laissées telles quelles.");
                return payload;
            }

            // 2) Reconstruit le tableau de recommandations en injectant les champs
            //    rattachés. On utilise JsonNode (mutable) pour merger proprement.
            System.Text.Json.Nodes.JsonNode root;
            try { root = System.Text.Json.Nodes.JsonNode.Parse(payload); }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Enrichissement : payload non parsable ({0}) — laissé tel quel.", ex.Message);
                return payload;
            }

            if (root is not System.Text.Json.Nodes.JsonArray arr)
            {
                // Pas un tableau (Markdown libre) : rien à enrichir.
                return payload;
            }

            int enriched = 0, matched = 0;
            foreach (var node in arr)
            {
                if (node is not System.Text.Json.Nodes.JsonObject obj) continue;
                if (!obj.TryGetPropertyValue("title", out var titleNode)) continue;
                string title = titleNode?.GetValue<string>();
                string key = NormTitle(title);
                if (string.IsNullOrEmpty(key)) continue;

                if (!lookup.TryGetValue(key, out var epg)) continue;
                matched++;

                if (!string.IsNullOrEmpty(epg.Id))
                {
                    obj["id"] = epg.Id;
                    // URL racine-relative : le navigateur la résout contre l'origine
                    // depuis laquelle l'utilisateur consulte la page (localhost, IP
                    // LAN, domaine, http/https) — aucun host codé. La page
                    // recommendations.js la rebâtira en absolu via ApiClient.serverAddress().
                    obj["image_url"] = "/emby/Items/" + epg.Id + "/Images/Primary?maxWidth=400";
                    enriched++;
                }
                if (!string.IsNullOrEmpty(epg.ChannelId))
                    obj["channel_id"] = epg.ChannelId;
                if (epg.Rating.HasValue)
                    obj["rating"] = epg.Rating.Value;
            }

            _logger?.Info("[LLM_AI] Enrichissement : {0}/{1} reco matchées, {2} avec id/image_url.",
                matched, arr.Count, enriched);

            return arr.ToJsonString();
        }

        /// <summary>
        /// Compte le nombre d'items d'une réponse JSON tableau
        /// (pour le libellé de la notification). 0 si ce n'est pas un tableau.
        /// </summary>
        private static int CountRecommendations(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return 0;
            try
            {
                using (var doc = JsonDocument.Parse(reply))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        return doc.RootElement.GetArrayLength();
                }
            }
            catch { /* réponse Markdown : on signale juste « mises à jour » */ }
            return 0;
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

            int count = CountRecommendations(reply);
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
        /// Résout l'URL de base d'Emby pour les liens absolus (notification) en
        /// interrogeant la config réseau du serveur via
        /// <see cref="IServerApplicationHost.GetLocalHostApiUrl"/> (qui exploite
        /// les ports HTTP/HTTPS et le schéma de <c>system.xml</c> + l'hôte local
        /// détecté). On ne code aucune IP. Repli sur
        /// <see cref="PluginConfiguration.EmbyPublicUrl"/> si la détection
        /// renvoie vide (ou si l'utilisateur a renseigné un domaine public
        /// explicite dans la config du plugin).
        /// </summary>
        private string ResolveEmbyUrl()
        {
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
                _logger?.Warn("[LLM_AI] ResolveEmbyUrl : GetLocalHostApiUrl a échoué ({0}) — repli sur EmbyPublicUrl.", ex.Message);
            }
            return (Plugin.Instance?.Configuration?.EmbyPublicUrl ?? string.Empty).Trim();
        }
    }
}