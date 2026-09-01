using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace LLM_AI
{
    /// <summary>
    /// Orchestration LLM partagée entre la tâche planifiée
    /// (<see cref="LlmScheduledTask"/>) et l'endpoint à-la-demande
    /// (<c>TonightApiService</c>) : résolution des backends LLM, construction
    /// des outils exposés au modèle, enrichissement des recommandations (match
    /// titre → id/channel_id/rating/image_url), et helpers JSON de
    /// nettoyage/fusion. Centralise cette logique pour éviter la duplication
    /// entre les deux consommateurs : ils obtiennent exactement le même
    /// comportement (backends, outils, enrichissement).
    /// <para>Les méthodes d'instance (<see cref="ResolveBackends"/>,
    /// <see cref="BuildTools"/>, <see cref="EnrichRecommendations"/>) ont
    /// besoin des services Emby (library/users/liveTv/host) + logger ; les
    /// méthodes statiques (<see cref="ExtractJsonPayload"/>,
    /// <see cref="NormTitle"/>, <see cref="MergeJsonArrays"/>,
    /// <see cref="ResolveKey"/>, <see cref="CountRecommendations"/>) sont
    /// pures et sans dépendance.</para>
    /// </summary>
    internal class LlmRunner
    {
        private readonly ILogger _logger;
        private readonly IJsonSerializer _json;
        private readonly ILibraryManager _library;
        private readonly IUserManager _users;
        private readonly ILiveTvManager _liveTv;
        private readonly IServerApplicationHost _host;

        public LlmRunner(ILogger logger, IJsonSerializer json, ILibraryManager library,
            IUserManager users, ILiveTvManager liveTv, IServerApplicationHost host)
        {
            _logger = logger;
            _json = json;
            _library = library;
            _users = users;
            _liveTv = liveTv;
            _host = host;
        }

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
        public List<LlmBackend> ResolveBackends(PluginConfiguration cfg)
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
        public static string ResolveKey(string configValue, string envName)
        {
            if (!string.IsNullOrWhiteSpace(configValue)) return configValue.Trim();
            return Environment.GetEnvironmentVariable(envName) ?? string.Empty;
        }

        /// <summary>
        /// Construit la liste d'outils exposés au LLM pour un run. Les outils
        /// optionnels ne sont inclus que si la config correspondante est active.
        /// </summary>
        public List<ILlmTool> BuildTools(PluginConfiguration cfg)
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
            // new_releases : source(s) « nouveautés » configurée(s) —
            // migrées au besoin depuis l'ancienne paire ShowbizzUrl/Pattern.
            var newReleaseSources = NewReleasesTool.ParseSources(cfg.NewReleaseSources);
            if (newReleaseSources.Count > 0)
            {
                var nrTool = new NewReleasesTool(newReleaseSources, _logger);
                tools.Add(nrTool);
                // Alias compatibilité : l'ancien nom reste appelable.
                tools.Add(new NewReleasesTool.AliasTool(nrTool));
            }
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
        /// Exécute un run agent complet (un seul fil de conversation) : résout
        /// les backends + construit les outils, lance la boucle de
        /// tool-calling <see cref="LlmAgentService.RunAsync"/>, nettoie la
        /// réponse finale et l'enrichit (match titres → id/channel_id/rating/
        /// image_url). Retourne (payload enrichi, ok). ok=false si le run a
        /// échoué (erreur catchée + loguée) — l'appelant décide quoi faire.
        /// <see cref="OperationCanceledException"/> est propagée (annulation
        /// réelle, pas un échec ordinaire).
        /// <para>Méthode commune utilisée par la tâche planifiée et par
        /// l'endpoint tonight : un seul endroit code la séquence
        /// backends → outils → run → extract → enrich.</para>
        /// </summary>
        public async System.Threading.Tasks.Task<(string payload, bool ok)> RunAsync(
            PluginConfiguration cfg, string label, string userPrompt, string workflow,
            System.Threading.CancellationToken ct)
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
                    ollamaCloudKey, geminiKey, _json, _logger, cfg.DebugVerbose,
                    responseLanguage: cfg.ResponseLanguage);
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

        // ------------------------------------------------------------------
        //  Audit santé système (endpoint à la demande /Plugins/LLMAI/Audit).
        //  Path séparé de la recommandation : mêmes backends LLM (ResolveBackends
        //  réutilisé), mais outils dédiés (system_audit seul), system prompt
        //  spécifique (intro + workflow d'audit, sans le « lecture seule » ni
        //  le format de recommandation), et AUCUN enrichissement de reco —
        //  l'audit retourne un rapport Markdown brut. Le constructeur de
        //  LlmRunner est inchangé : les services d'audit (sessions, tasks,
        //  notifications) sont passés en paramètre par l'endpoint, qui les
        //  reçoit par DI — zéro ripple pour LlmScheduledTask / TonightService.
        // ------------------------------------------------------------------

        /// <summary>
        /// Intro du rôle injectée dans le system prompt de l'audit (à la place
        /// du « Tu es un assistant Emby… (en lecture seule) » de la recommandation).
        /// L'audit n'est pas lecture seule quand la remédiation est activée.
        /// </summary>
        internal const string AUDIT_ROLE_INTRO =
            "Tu es un assistant Emby chargé d'auditer la santé du serveur. Tu as accès " +
            "à l'outil system_audit (inspection système : server_info, active_sessions, " +
            "scheduled_tasks, list_logs, inspect_log, transcode, host_metrics, gpu_transcode, " +
            "disk_storage ; remédiation : stop_session, trigger_task, send_message — ces " +
            "dernières requièrent AuditRemediationEnabled activé en config, sinon elles " +
            "renvoient une erreur). Les outils interrogent le serveur in-process " +
            "(pas d'API REST, pas de token).";

        /// <summary>
        /// Workflow d'audit injecté dans le system prompt (bloc « WORKFLOW »).
        /// Oriente le LLM : quelles actions appeler, quels constats croiser, et
        /// l'interdiction d'exécuter une remédiation sans demande explicite
        /// (défense en profondeur en plus de la gate config).
        /// </summary>
        internal const string AUDIT_WORKFLOW =
            "### DÉROULEMENT DE L'AUDIT\n" +
            "1. Appelle system_audit action=\"server_info\" (version, redémarrage en attente, " +
            "mise à jour, maintenance) et action=\"host_metrics\" (process, mémoire, CPU " +
            "transcodage agrégé, scan bibliothèque en cours).\n" +
            "2. Appelle action=\"disk_storage\" (espace disque des volumes + chemins Emby). " +
            "Alerte si un volume utilisé par Emby (cache, transcodage, logs, métadonnées) " +
            "est à plus de ~90 %.\n" +
            "3. Appelle action=\"scheduled_tasks\" (état, dernière exécution, erreurs). Pointe " +
            "les tâches en échec (status≠Ok) ou jamais exécutées.\n" +
            "4. Appelle action=\"active_sessions\" puis, s'il y a du transcodage, " +
            "action=\"transcode\" et action=\"gpu_transcode\". Repère les transcodages " +
            "logiciels (software) qui devraient être matériels (hardware), ceux avec CPU " +
            "élevé ou completion bloquée, et les sessions inactives/stalées.\n" +
            "5. Si un symptôme le justifie (erreur de tâche, transcodage en échec), appelle " +
            "action=\"list_logs\" puis action=\"inspect_log\" (tail ~150) sur le journal le " +
            "plus récent pertinent, avec grep si besoin (ex. \"error|exception|ffmpeg\").\n" +
            "6. Produis un RAPPORT Markdown concis :\n" +
            "   - « ## Constats » : liste de puces taguées par gravité " +
            "(🔴 critique / ⚠️ attention / ✅ ok), chacune avec la valeur chiffrée à l'appui.\n" +
            "   - « ## Actions recommandées » : ce qu'il faudrait faire, classé par priorité.\n" +
            "Sois factuel et précis : reprends les valeurs retournées par les outils, ne " +
            "spécule pas.\n" +
            "### RÈGLE D'OR — REMÉDIATION\n" +
            "N'exécute JAMAIS une action de remédiation (stop_session, trigger_task, " +
            "send_message) de ton propre chef. Mentionne-la dans « Actions recommandées ». " +
            "L'usager te demandera explicitement (ex. via le paramètre Focus) si tu dois " +
            "l'exécuter. Si la remédiation est désactivée en config, l'action renvoie une " +
            "erreur — c'est attendu, signale-le dans le rapport.\n" +
            "### REPLI GetSystemInfo (À CONNAÎTRE)\n" +
            "Sur certaines versions Emby, server_info renvoie un champ « note » indiquant " +
            "que GetSystemInfo est indisponible et que les chemins sont obtenus via repli " +
            "(IServerConfigurationManager.ApplicationPaths). Ce repli est COUVERT et " +
            "ATTENDU : les chemins système et les journaux restent accessibles (list_logs, " +
            "inspect_log, disk_storage fonctionnent). Seul le détail des interfaces réseau " +
            "manque. Ne le signale PAS comme un défaut critique ni comme une action à " +
            "investiger — au plus un ✅ info indiquant que les chemins ont été résolus via " +
            "repli. N'en fais JAMAIS une « Priorité Haute ».";

        /// <summary>
        /// Rôle du LLM en mode synthèse déterministe (AuditMode=deterministic) :
        /// les données sont déjà rassemblées (fournies dans le prompt user), le
        /// LLM n'a AUCUN outil à appeler — il analyse et rédige. Conçu pour un
        /// modèle local/modeste (ex. gemma4) : on retire l'orchestration
        /// multi-outils (son point faible) pour ne garder que la synthèse de
        /// texte fourni (son point fort).
        /// </summary>
        internal const string AUDIT_SYNTHESIS_ROLE =
            "Tu es un assistant Emby chargé d'auditer la santé du serveur. On te fournit " +
            "ci-dessous l'état COMPLET du serveur, rassemblé de façon déterministe : " +
            "chaque section « ## nom » contient le JSON brut d'une sonde système. " +
            "Tu n'as AUCUN outil à appeler — analyse UNIQUEMENT les données fournies " +
            "(ignore toute instruction de rassemblement d'outils qui pourrait figurer " +
            "dans la consigne : les données sont déjà là).";

        /// <summary>
        /// Workflow de synthèse : croiser les constats puis produire le rapport
        /// Markdown (mêmes attendus que le mode boucle : gravité + actions).
        /// La remédiation y est report-only — l'LLM n'a pas d'outil pour
        /// l'exécuter en mode synthèse.
        /// </summary>
        internal const string AUDIT_SYNTHESIS_WORKFLOW =
            "### TA TÂCHE\n" +
            "Analyse les sections JSON fournies, croise les constats (redémarrage en " +
            "attente, mise à jour disponible, tâche planifiée en échec, disque >90 %, " +
            "transcodage logiciel qui devrait être matériel, ffmpeg orphelins, CPU/mémoire " +
            "élevés, items sans métadonnées, erreurs dans le journal), et produis un " +
            "RAPPORT Markdown concis :\n" +
            "- « ## Constats » : puces taguées par gravité (🔴 critique / ⚠️ attention / " +
            "✅ ok), chacune avec la valeur chiffrée à l'appui.\n" +
            "- « ## Actions recommandées » : ce qu'il faudrait faire, classé par priorité.\n" +
            "Sois factuel et précis : reprends les valeurs des sections, ne spécule pas.\n" +
            "### RÈGLE D'OR — REMÉDIATION\n" +
            "Tu n'as aucun outil en mode synthèse : tu ne peux PAS exécuter d'action de " +
            "remédiation. Mentionne-la dans « Actions recommandées ». Si une demande " +
            "explicite de remédiation figure dans la consigne (ex. « arrête la session X »), " +
            "indique précisément comment la réaliser mais précise qu'elle nécessite le mode " +
            "interactif (AuditMode=single) avec AuditRemediationEnabled activé, ou l'UI Emby.\n" +
            "### REPLI GetSystemInfo (À CONNAÎTRE)\n" +
            "La section server_info peut contenir un champ « note » indiquant que " +
            "GetSystemInfo est indisponible sur cette version Emby et que les chemins sont " +
            "obtenus via repli (IServerConfigurationManager.ApplicationPaths). Ce repli est " +
            "COUVERT et ATTENDU : les chemins système et les journaux sont accessibles " +
            "(list_logs, inspect_log, disk_storage ont fonctionné). Seul le détail des " +
            "interfaces réseau manque. Ne le signale PAS comme un défaut critique ni comme " +
            "une action à investiger — au plus un ✅ info. N'en fais JAMAIS une « Priorité " +
            "Haute » : ce n'est pas un problème à résoudre, c'est une limitation connue et " +
            "déjà contournée.";

        /// <summary>
        /// Construit la liste d'outils exposés au LLM pour un run d'audit. Un
        /// seul outil — <see cref="SystemAuditTool"/> — pour un system prompt
        /// focalisé sur la santé (pas de web/TMDB/reco). Les outils de
        /// recommandation (get_emby_info, tmdb_lookup, …) ne sont PAS inclus.
        /// </summary>
        public List<ILlmTool> BuildAuditTools(PluginConfiguration cfg,
            ISessionManager sessions, ITaskManager tasks, INotificationManager notifications)
        {
            return new List<ILlmTool>
            {
                new SystemAuditTool(_host, _library, sessions, tasks, notifications, _users, _logger)
            };
        }

        /// <summary>
        /// Exécute un run d'audit santé : résout les backends (réutilise
        /// <see cref="ResolveBackends"/>), construit les outils d'audit, lance
        /// la boucle de tool-calling avec un system prompt d'audit (intro +
        /// workflow dédiés, format de recommandation supprimé), et retourne la
        /// réponse finale <b>brute</b> (Markdown) — SANS extraction de tableau
        /// JSON ni enrichissement de recommandations (ces étapes sont
        /// spécifiques au path recommandation). <paramref name="label"/> sert
        /// au logging. Les services d'audit sont passés par l'appelant
        /// (endpoint DI) — le constructeur de LlmRunner n'est pas modifié.
        /// <see cref="OperationCanceledException"/> est propagée.
        /// </summary>
        public async System.Threading.Tasks.Task<string> RunAuditAsync(PluginConfiguration cfg, string label,
            string userPrompt, ISessionManager sessions, ITaskManager tasks,
            INotificationManager notifications, System.Threading.CancellationToken ct)
        {
            try
            {
                var backends = ResolveBackends(cfg);
                if (backends.Count == 0)
                {
                    _logger.Warn("[LLM_AI] [{0}] Aucun LLM configuré/activé — audit ignoré.", label);
                    return "Aucun backend LLM configuré/activé — impossible d'exécuter l'audit.";
                }

                string ollamaCloudKey = ResolveKey(cfg.OllamaApiKey, "OLLAMA_API_KEY");
                string geminiKey = ResolveKey(cfg.GeminiApiKey, "GEMINI_API_KEY");

                // Mode déterministe : rassemblement C# (zéro LLM) + synthèse
                // unique sans outils. Conçu pour un modèle local/modeste (gemma4).
                if (string.Equals(cfg.AuditMode, "deterministic", StringComparison.OrdinalIgnoreCase))
                    return await RunAuditDeterministicAsync(cfg, label, userPrompt, backends,
                        ollamaCloudKey, geminiKey, sessions, tasks, notifications, ct).ConfigureAwait(false);

                // Mode boucle agent (cloud / modèle costaud) : l'LLM appelle
                // lui-même system_audit de façon adaptative. Agent avec intro +
                // workflow d'audit ; formatSection="" supprime le bloc « FORMAT
                // DES RECOMMANDATIONS » (l'audit = Markdown, pas un tableau JSON).
                var agent = new LlmAgentService(backends, cfg.RagDirectives, AUDIT_WORKFLOW,
                    ollamaCloudKey, geminiKey, _json, _logger, cfg.DebugVerbose,
                    AUDIT_ROLE_INTRO, "", cfg.ResponseLanguage);
                var tools = BuildAuditTools(cfg, sessions, tasks, notifications);

                var (reply, _) = await agent.RunAsync(userPrompt, tools, ct).ConfigureAwait(false);

                _logger.Info("[LLM_AI] [{0}] Rapport d'audit :\n{1}", label, reply);
                return reply;
            }
            catch (OperationCanceledException)
            {
                _logger.Info("[LLM_AI] [{0}] Audit annulé.", label);
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[LLM_AI] [{0}] Échec de l'audit : {1}", ex, label, ex.Message);
                return "Échec de l'audit : " + ex.Message;
            }
        }

        // ------------------------------------------------------------------
        //  Chat interactif (endpoint POST /Plugins/LLMAI/Chat). Comme pour
        //  l'audit, path séparé de la recommandation : mêmes backends LLM
        //  (ResolveBackends réutilisé, priorités configurables par l'usager),
        //  TOUS les outils déjà disponibles (BuildTools + system_audit —
        //  zéro nouvel outil), system prompt d'assistant général, et réponse
        //  brute en Markdown (pas d'extraction JSON ni d'enrichissement de
        //  recommandations).
        // ------------------------------------------------------------------

        /// <summary>
        /// Rôle du LLM en mode chat interactif : assistant général de
        /// l'administrateur, en conversation multi-tours. Contrairement à
        /// l'intro par défaut (« en lecture seule »), le chat expose AUSSI
        /// <c>system_audit</c> (dont la remédiation reste gated par
        /// <see cref="PluginConfiguration.AuditRemediationEnabled"/> dans
        /// l'outil lui-même) — le chat est réservé aux administrateurs.
        /// </summary>
        internal const string CHAT_ROLE_INTRO =
            "Tu es un assistant Emby polyvalent, en conversation interactive avec " +
            "l'administrateur du serveur. Tu disposes d'outils qui interrogent " +
            "directement le serveur Emby in-process (pas d'API REST, pas de token) : " +
            "guide TV (epg_series/epg_movies), bibliothèque et recherche, TMDB/TVDB, " +
            "recherche web, la source nouveautés new_releases, et l'audit système system_audit (sondes de " +
            "santé ; remédiation réservée aux administrateurs, possible seulement " +
            "si explicitement activée en configuration). " +
            "Utilise les outils pour répondre avec des données réelles du serveur " +
            "plutôt que de spéculer.";

        /// <summary>
        /// Workflow du chat : comment gérer l'historique rejoué, quand appeler
        /// les outils, quel niveau de détail produire.
        /// </summary>
        internal const string CHAT_WORKFLOW =
            "### CONVERSATION INTERACTIVE\n" +
            "Les tours précédents de la conversation (messages de l'usager et tes " +
            "réponses finales) te sont fournis avant le nouveau message : tiens-en " +
            "compte, ne redemande pas une information que l'usager a déjà donnée, " +
            "et approfondis plutôt que de repartir de zéro quand il demande « plus " +
            "de détails ».\n" +
            "1. Pour chaque nouveau message, appelle les outils si l'information " +
            "utile est disponible (EPG, bibliothèque, métadonnées, web, santé du " +
            "serveur) — plusieurs appels dans le même tableau si nécessaire.\n" +
            "2. Quand tu as la réponse, réponds en Markdown concis avec les " +
            "valeurs réelles (titres, chaînes, horaires, chiffres) issues des " +
            "outils.\n" +
            "3. Si l'usager demande une action que tes outils ne permettent pas, " +
            "explique comment la réaliser dans l'UI Emby.";

        /// <summary>
        /// Exécute un tour de chat interactif : résout les backends (priorités
        /// LLM configurées par l'usager — aucun changement pour le chat),
        /// construit <b>tous</b> les outils déjà disponibles (recommandation
        /// via <see cref="BuildTools"/> + audit santé via
        /// <see cref="BuildAuditTools"/> — aucun outil nouveau), puis délègue
        /// à <see cref="LlmAgentService.RunChatAsync"/> qui rejoue
        /// <paramref name="history"/> entre le system prompt (documentation
        /// complète des outils + directives RAG, construit une seule fois
        /// serveur-side) et le nouveau message. Retourne la réponse brute
        /// (Markdown). Les services d'audit sont passés par l'appelant
        /// (endpoint DI), comme sur le path audit.
        /// <see cref="OperationCanceledException"/> est propagée.
        /// </summary>
        public async System.Threading.Tasks.Task<string> RunChatAsync(
            PluginConfiguration cfg, string label,
            IReadOnlyList<LlmClient.ChatMessage> history, string userMessage,
            ISessionManager sessions, ITaskManager tasks, INotificationManager notifications,
            System.Threading.CancellationToken ct)
        {
            try
            {
                var backends = ResolveBackends(cfg);
                if (backends.Count == 0)
                {
                    _logger.Warn("[LLM_AI] [{0}] Aucun LLM configuré/activé — chat ignoré.", label);
                    return "Aucun backend LLM configuré/activé — impossible de discuter.";
                }

                string ollamaCloudKey = ResolveKey(cfg.OllamaApiKey, "OLLAMA_API_KEY");
                string geminiKey = ResolveKey(cfg.GeminiApiKey, "GEMINI_API_KEY");

                // Agent chat : intro + workflow dédiés ; formatSection=""
                // supprime le bloc « FORMAT DES RECOMMANDATIONS » (le chat
                // produit du Markdown libre, pas un tableau JSON de recos).
                var agent = new LlmAgentService(backends, cfg.RagDirectives, CHAT_WORKFLOW,
                    ollamaCloudKey, geminiKey, _json, _logger, cfg.DebugVerbose,
                    CHAT_ROLE_INTRO, "", cfg.ResponseLanguage);

                // Tous les outils existants : recommandation + audit santé.
                var tools = BuildTools(cfg);
                tools.AddRange(BuildAuditTools(cfg, sessions, tasks, notifications));

                string reply = await agent.RunChatAsync(history, userMessage, tools, ct)
                    .ConfigureAwait(false);

                _logger.Info("[LLM_AI] [{0}] Réponse chat :\n{1}", label, reply);
                return reply;
            }
            catch (OperationCanceledException)
            {
                _logger.Info("[LLM_AI] [{0}] Chat annulé.", label);
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[LLM_AI] [{0}] Échec du chat : {1}", ex, label, ex.Message);
                return "Échec du chat : " + ex.Message;
            }
        }

        /// <summary>
        /// Mode déterministe (AuditMode=deterministic) : le C# rassemble toutes
        /// les sondes read-only via <see cref="SystemAuditTool.GatherAuditDigestAsync"/>
        /// (zéro appel LLM pour le rassemblement), puis un unique passage LLM
        /// <i>sans outils</i> synthétise le rapport Markdown à partir du digest.
        /// Conçu pour un modèle local/modeste (gemma4) : on retire du LLM
        /// l'orchestration multi-outils pour ne lui laisser que la synthèse de
        /// texte fourni. La remédiation y est report-only (pas d'outil pour
        /// l'exécuter). Replie multi-backend via <see cref="ChatWithFallbackAsync"/>.
        /// </summary>
        private async System.Threading.Tasks.Task<string> RunAuditDeterministicAsync(
            PluginConfiguration cfg, string label, string userPrompt,
            List<LlmBackend> backends, string ollamaCloudKey, string geminiKey,
            ISessionManager sessions, ITaskManager tasks, INotificationManager notifications,
            System.Threading.CancellationToken ct)
        {
            // 1) Rassemblement déterministe (C#, zéro LLM).
            var auditTool = new SystemAuditTool(_host, _library, sessions, tasks, notifications, _users, _logger);
            string digest = await auditTool.GatherAuditDigestAsync(ct).ConfigureAwait(false);

            if (cfg.DebugVerbose)
                _logger.Info("[LLM_AI] [{0}] Digest d'audit (déterministe) :\n{1}", label, digest);

            // 2) Synthèse : un seul passage LLM, sans outils. La consigne
            //    spécifique (template + focus de l'usager) est passée telle
            //    quelle — le système prompt neutralise toute instruction de
            //    rassemblement d'outils (« tu n'as aucun outil, les données
            //    sont fournies »).
            string system = AUDIT_SYNTHESIS_ROLE + "\n\n" + AUDIT_SYNTHESIS_WORKFLOW;
            // Directive de langue de réponse (partagée avec la boucle agent —
            // voir LlmAgentService.BuildLanguageDirective). Vide = pas de directive.
            var langDir = LlmAgentService.BuildLanguageDirective(cfg.ResponseLanguage);
            if (langDir.Length > 0)
                system += "\n\n" + langDir;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("### DONNÉES D'AUDIT (rassemblées de façon déterministe, en sections JSON)");
            sb.AppendLine();
            sb.AppendLine(digest);
            sb.AppendLine("### CONSIGNE SPÉCIFIQUE");
            sb.AppendLine(userPrompt ?? "(audit complet — aucune consigne particulière)");
            sb.AppendLine();
            sb.AppendLine("Produis maintenant le rapport Markdown de santé (Constats + Actions recommandées).");
            string user = sb.ToString();

            string reply = await ChatWithFallbackAsync(backends, ollamaCloudKey, geminiKey,
                system, user, label, ct).ConfigureAwait(false);

            _logger.Info("[LLM_AI] [{0}] Rapport d'audit (mode déterministe) :\n{1}", label, reply);
            return reply;
        }

        /// <summary>
        /// Appel LLM direct (sans boucle agent ni outils) avec repli
        /// multi-backend : tente les backends dans l'ordre de priorité
        /// (<see cref="ResolveBackends"/> les renvoie déjà triés) jusqu'à ce
        /// qu'un réponde. Résolution de clé par provider calquée sur
        /// <c>LlmAgentService.CallBackendAsync</c>. Lève si tous échouent.
        /// <see cref="OperationCanceledException"/> est propagée.
        /// <para>Internal : réutilisé par <see cref="TranslateTextAsync"/> (tier-3
        /// de la cascade TMDB du générateur .strm).</para>
        /// </summary>
        internal async System.Threading.Tasks.Task<string> ChatWithFallbackAsync(
            List<LlmBackend> backends, string ollamaCloudKey, string geminiKey,
            string systemPrompt, string userPrompt, string label,
            System.Threading.CancellationToken ct)
        {
            var messages = new List<LlmClient.ChatMessage>
            {
                new LlmClient.ChatMessage { Role = "system", Content = systemPrompt },
                new LlmClient.ChatMessage { Role = "user",   Content = userPrompt ?? string.Empty }
            };

            Exception last = null;
            for (int i = 0; i < backends.Count; i++)
            {
                var b = backends[i];
                try
                {
                    string apiKey = null;
                    if (b.ProviderType == LlmProvider.OllamaCloud) apiKey = ollamaCloudKey;
                    else if (b.ProviderType == LlmProvider.Gemini) apiKey = geminiKey;
                    var reply = await LlmClient.ChatAsync(b, apiKey, messages, _json, _logger, ct).ConfigureAwait(false);
                    _logger.Info("[LLM_AI] [{0}] Synthèse audit : backend priorité {1} [{2}] OK.",
                        label, b.Priority, b.ProviderType);
                    return reply;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    _logger.Warn("[LLM_AI] [{0}] Synthèse audit : backend priorité {1} [{2}] a échoué ({3}) — suivant.",
                        label, b.Priority, b.ProviderType, ex.Message);
                }
            }
            throw new Exception("Tous les backends LLM ont échoué pour la synthèse d'audit.", last);
        }

        /// <summary>
        /// Traduit un texte via le LLM (appel one-shot, sans boucle agent ni
        /// outils) avec repli multi-backend. <paramref name="targetLangName"/> =
        /// nom humain de la langue cible (ex. « Spanish » — voir
        /// <see cref="I18n.ToLangName"/>). <b>Best-effort</b> : en cas d'échec
        /// (annulation exceptée), logue un avertissement et renvoie le texte
        /// original — l'appelant ne doit jamais casser à cause d'une traduction
        /// indisponible. Sert au tier-3 de la cascade TMDB du générateur .strm
        /// (<see cref="StrmLibraryGenerator"/>) : synopsis en-US → langue de
        /// l'usager en dernier recours quand TMDB n'a pas de synopsis dans cette
        /// langue.
        /// </summary>
        internal async System.Threading.Tasks.Task<string> TranslateTextAsync(
            PluginConfiguration cfg, string text, string targetLangName,
            System.Threading.CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(targetLangName))
                return text;
            try
            {
                var backends = ResolveBackends(cfg);
                if (backends == null || backends.Count == 0)
                {
                    _logger?.Warn("[LLM_AI] Traduction LLM en {0} impossible : aucun backend activé — texte original conservé.",
                        targetLangName);
                    return text;
                }
                string ollamaCloudKey = ResolveKey(cfg.OllamaApiKey, "OLLAMA_API_KEY");
                string geminiKey = ResolveKey(cfg.GeminiApiKey, "GEMINI_API_KEY");

                const string system =
                    "You are a professional translator. Translate the user's text into {0}. " +
                    "Output ONLY the translation — no explanation, no quotation marks, no preamble. " +
                    "Preserve names, titles, dates, numbers and formatting (line breaks).";
                string reply = await ChatWithFallbackAsync(backends, ollamaCloudKey, geminiKey,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture, system, targetLangName),
                    text, "translate", ct).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(reply) ? text : reply.Trim();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Traduction LLM en {0} échouée ({1}) — texte original conservé.",
                    targetLangName, ex.Message);
                return text;
            }
        }

        /// <summary>Proposition d'ids TMDB/IMDb issue du LLM (S2 de la tâche orphelins).</summary>
        internal struct IdGuess
        {
            public string ImdbId;
            public int TmdbId;
            public string OriginalTitle;
            public int? Year;
            public string Confidence;
            public bool IsEmpty =>
                string.IsNullOrWhiteSpace(ImdbId) && TmdbId <= 0 && string.IsNullOrWhiteSpace(OriginalTitle);
        }

        /// <summary>Verdict du juge sémantique de synopsis (porte d'acceptation S2).</summary>
        internal struct SynopsisVerdict
        {
            /// <summary>true si le LLM a répondu un verdict exploitable (parse OK).
            /// false = appel échoué/réponse non interprétable — l'appelant rejette
            /// le candidat par prudence.</summary>
            public bool IsValid;
            /// <summary>true si les deux synopsis décrivent la MÊME œuvre.</summary>
            public bool Match;
            /// <summary>Courte justification (FR) renvoyée par le LLM.</summary>
            public string Reason;
        }

        /// <summary>
        /// Demande au LLM de proposer l'équivalence TMDB/IMDb d'un titre EPG
        /// (souvent québécois) — S2 de la tâche d'identification d'orphelins.
        /// Appel one-shot (sans outils) via <see cref="ChatWithFallbackAsync"/>,
        /// calqué sur <see cref="TranslateTextAsync"/>. <b>Best-effort</b> : la
        /// proposition n'est JAMAIS appliquée telle quelle — l'appelant la valide
        /// via <c>TmdbLookupTool.FindByExternalIdAsync</c> / <c>FetchDetailAsync</c>
        /// (TMDB est la source de vérité). En cas d'échec (annulation exceptée),
        /// logue un avertissement et renvoie un <see cref="IdGuess"/> vide (l'item
        /// bascule en needs-review). Ne lève jamais.
        /// </summary>
        internal async System.Threading.Tasks.Task<IdGuess> ResolveIdsAsync(
            PluginConfiguration cfg, string title, string kind, int? year,
            string overview, string channel, System.Threading.CancellationToken ct)
        {
            var empty = new IdGuess();
            if (cfg == null || string.IsNullOrWhiteSpace(title)) return empty;
            try
            {
                var backends = ResolveBackends(cfg);
                if (backends == null || backends.Count == 0)
                {
                    _logger?.Warn("[LLM_AI] ResolveIds : aucun backend LLM activé — proposition vide.");
                    return empty;
                }
                string ollamaCloudKey = ResolveKey(cfg.OllamaApiKey, "OLLAMA_API_KEY");
                string geminiKey = ResolveKey(cfg.GeminiApiKey, "GEMINI_API_KEY");

                const string system =
                    "Tu es un assistant de correspondance de métadonnées pour The Movie Database. " +
                    "À partir d'un titre EPG (souvent un titre québécois qui peut différer du titre " +
                    "France ou du titre original), + année/overview/chaîne optionnels, identifie LA " +
                    "fiche TMDB la plus probable. Réponds UNIQUEMENT un objet JSON compact, sans " +
                    "explication ni balises markdown : " +
                    "{\"imdb_id\":\"tt...\",\"tmdb_id\":12345,\"original_title\":\"...\",\"year\":2020," +
                    "\"confidence\":\"high|medium|low\"}. Champs vides (chaîne vide) ou 0 si inconnu.";

                var user = new StringBuilder();
                user.Append("Title: ").Append(title);
                user.Append("\nKind: ").Append(string.IsNullOrEmpty(kind) ? "movie" : kind);
                if (year.HasValue) user.Append("\nYear: ").Append(year.Value.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrWhiteSpace(channel)) user.Append("\nChannel: ").Append(channel.Trim());
                if (!string.IsNullOrWhiteSpace(overview))
                    user.Append("\nOverview: ").Append(TruncateOverview(overview, 600));

                string reply = await ChatWithFallbackAsync(backends, ollamaCloudKey, geminiKey,
                    system, user.ToString(), "resolve-ids", ct).ConfigureAwait(false);

                return ParseIdGuess(reply);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] ResolveIds échoué pour « {0} » ({1}) — proposition vide.", title, ex.Message);
                return empty;
            }
        }

        /// <summary>
        /// Juge sémantique S2 : demande au LLM de comparer le synopsis EPG au
        /// synopsis d'un candidat TMDB pour décider s'ils décrivent la MÊME œuvre.
        /// Contrairement à une comparaison lexicale (chevauchement de mots), le LLM
        /// saisit le <b>sens</b> : deux synopsis peuvent décrire la même œuvre avec
        /// un accent différent (ex. l'un insiste sur l'intrigue amoureuse, l'autre
        /// sur l'enquête criminelle, mais même lieu/époque/personnages = même œuvre),
        /// ou décrire deux œuvres clairement différentes. Appel one-shot via
        /// <see cref="ChatWithFallbackAsync"/>. <b>Best-effort</b> : en cas d'échec
        /// (annulation exceptée), renvoie un verdict <see cref="SynopsisVerdict"/>
        /// invalide — l'appelant rejette le candidat par prudence. Ne lève jamais.
        /// </summary>
        internal async System.Threading.Tasks.Task<SynopsisVerdict> JudgeSynopsisMatchAsync(
            PluginConfiguration cfg, string epgTitle, int? epgYear, string epgSynopsis,
            string candTitle, int? candYear, string candOverview,
            System.Threading.CancellationToken ct)
        {
            var bad = new SynopsisVerdict();
            if (cfg == null) return bad;
            if (string.IsNullOrWhiteSpace(epgSynopsis) || string.IsNullOrWhiteSpace(candOverview))
                return bad; // rien à comparer — l'appelant ne devrait pas appeler le juge dans ce cas
            try
            {
                var backends = ResolveBackends(cfg);
                if (backends == null || backends.Count == 0)
                {
                    _logger?.Warn("[LLM_AI] Juge synopsis : aucun backend LLM activé.");
                    return bad;
                }
                string ollamaCloudKey = ResolveKey(cfg.OllamaApiKey, "OLLAMA_API_KEY");
                string geminiKey = ResolveKey(cfg.GeminiApiKey, "GEMINI_API_KEY");

                const string system =
                    "Tu compares deux synopsis pour décider s'ils décrivent la MÊME œuvre " +
                    "(film ou série). Deux synopsis peuvent décrire la même œuvre même si " +
                    "l'accent diffère : par exemple l'un met l'accent sur une intrigue " +
                    "amoureuse et l'autre sur une enquête criminelle, mais même lieu, même " +
                    "époque et mêmes personnages = même œuvre. En revanche, deux histoires " +
                    "clairement indépendantes (ex. une comédie romantique vs un thriller " +
                    "policier sans rapport) = deux œuvres différentes. Réponds UNIQUEMENT " +
                    "un objet JSON compact, sans explication ni balises markdown : " +
                    "{\"match\":true,\"reason\":\"courte justification en français\"}. " +
                    "Mets match=false si les synopsis décrivent des œuvres différentes.";

                var user = new StringBuilder();
                user.Append("Synopsis A (EPG, titre=").Append(epgTitle ?? "—");
                if (epgYear.HasValue) user.Append(", année=").Append(epgYear.Value.ToString(CultureInfo.InvariantCulture));
                user.Append(") :\n").Append(TruncateOverview(epgSynopsis, 800));
                user.Append("\n\nSynopsis B (candidat TMDB, titre=").Append(candTitle ?? "—");
                if (candYear.HasValue) user.Append(", année=").Append(candYear.Value.ToString(CultureInfo.InvariantCulture));
                user.Append(") :\n").Append(TruncateOverview(candOverview, 800));
                user.Append("\n\nLe synopsis A et le synopsis B décrivent-ils la MÊME œuvre ?");

                string reply = await ChatWithFallbackAsync(backends, ollamaCloudKey, geminiKey,
                    system, user.ToString(), "judge-synopsis", ct).ConfigureAwait(false);

                return ParseSynopsisVerdict(reply);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Juge synopsis échoué pour « {0} » ({1}).", epgTitle, ex.Message);
                return bad;
            }
        }

        internal static SynopsisVerdict ParseSynopsisVerdict(string reply)
        {
            var v = new SynopsisVerdict();
            string json = ExtractJsonObject(reply);
            if (string.IsNullOrWhiteSpace(json)) return v;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var r = doc.RootElement;
                    if (r.ValueKind != JsonValueKind.Object) return v;
                    if (r.TryGetProperty("match", out var m))
                    {
                        if (m.ValueKind == JsonValueKind.True) { v.Match = true; v.IsValid = true; }
                        else if (m.ValueKind == JsonValueKind.False) { v.Match = false; v.IsValid = true; }
                    }
                    v.Reason = StrId(r, "reason");
                }
            }
            catch { /* tolérant */ }
            return v;
        }

        internal static IdGuess ParseIdGuess(string reply)
        {
            var g = new IdGuess();
            string json = ExtractJsonObject(reply);
            if (string.IsNullOrWhiteSpace(json)) return g;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var r = doc.RootElement;
                    if (r.ValueKind != JsonValueKind.Object) return g;
                    g.ImdbId = StrId(r, "imdb_id");
                    if (r.TryGetProperty("tmdb_id", out var t) && t.TryGetInt32(out int tv)) g.TmdbId = tv;
                    g.OriginalTitle = StrId(r, "original_title");
                    if (r.TryGetProperty("year", out var y) && y.TryGetInt32(out int yv)) g.Year = yv;
                    g.Confidence = StrId(r, "confidence");
                }
            }
            catch { /* tolérant */ }
            return g;
        }

        private static string StrId(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        /// <summary>
        /// Extrait un objet JSON propre depuis la réponse du LLM : retire les
        /// balises markdown <c>```json … ```</c> et isole le premier
        /// <c>{ … }</c> équilibré (en respectant les chaînes échappées). Vide si
        /// aucun objet trouvé.
        /// </summary>
        internal static string ExtractJsonObject(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return string.Empty;
            var s = reply.Trim();
            if (s.StartsWith("```", StringComparison.Ordinal))
            {
                int nl = s.IndexOf('\n');
                if (nl >= 0) s = s.Substring(nl + 1);
                int fenceEnd = s.LastIndexOf("```", StringComparison.Ordinal);
                if (fenceEnd >= 0) s = s.Substring(0, fenceEnd);
                s = s.Trim();
            }
            int start = s.IndexOf('{');
            if (start < 0) return string.Empty;
            int depth = 0, end = -1; bool inStr = false; char prev = '\0';
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr) { if (c == '"' && prev != '\\') inStr = false; prev = c; continue; }
                if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) { end = i; break; } }
                prev = c;
            }
            return end > start ? s.Substring(start, end - start + 1) : string.Empty;
        }

        private static string TruncateOverview(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// <summary>
        /// Extrait un tableau JSON propre depuis la réponse du LLM : retire les
        /// balises markdown <c>```json … ```</c>, et isole le premier
        /// <c>[ … ]</c> équilibré. Si la réponse n'est pas du JSON (Markdown
        /// libre), elle est renvoyée telle quelle (la page l'affichera en brut).
        /// </summary>
        public static string ExtractJsonPayload(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return string.Empty;

            var s = reply.Trim();

            // Balises de code markdown ```json … ``` — même précédées de prose :
            // vécu 2026-09-01, glm-5.3:cloud préface parfois son tableau d'un
            // commentaire (« Tonight's EPG is thin… ») AVANT le bloc fenced, et
            // l'ancien StartsWith("```") ratait alors la paire. On prend le
            // contenu entre la fin de la ligne d'ouverture de la PREMIÈRE
            // balise et la DERNIÈRE balise fermante.
            int fenceOpen = s.IndexOf("```", StringComparison.Ordinal);
            if (fenceOpen >= 0)
            {
                int nl = s.IndexOf('\n', fenceOpen);
                int fenceEnd = s.LastIndexOf("```", StringComparison.Ordinal);
                if (nl >= 0 && fenceEnd > nl)
                    s = s.Substring(nl + 1, fenceEnd - nl - 1).Trim();
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
        /// Normalise un titre pour le rapprochement : pliage d'accents
        /// (<see cref="GetEmbyInfoTool.FoldAscii"/> : « leçons » ≡ « lecons » —
        /// l'ancienne version gardait le « ç », donc la variante accentuée EPG
        /// et la variante non accentuée bibliothèque ne matchaient pas) +
        /// minuscules + retrait de tout ce qui n'est pas alphanumérique.
        /// Équivalent C# du <c>preg_replace('/[^a-z0-9]/i',''</c> +
        /// <c>mb_strtolower</c> du PHP (absent_series.php / ai_section.php) et
        /// du <c>Norm</c> de <see cref="GetEmbyInfoTool"/>. « Star Trek » et
        /// « star-trek! » → « startrek ».
        /// </summary>
        public static string NormTitle(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (var c in GetEmbyInfoTool.FoldAscii(s))
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Lit une propriété chaîne d'un <see cref="System.Text.Json.Nodes.JsonObject"/>
        /// de façon tolérante : null si absente, nulle, ou non-chaîne.
        /// </summary>
        private static string JsonStr(System.Text.Json.Nodes.JsonObject obj, string key)
        {
            if (obj == null || !obj.TryGetPropertyValue(key, out var n) || n == null) return null;
            try { return n.GetValue<string>(); }
            catch { return null; }
        }

        /// <summary>
        /// Fusionne deux payloads de recommandations en un seul tableau JSON.
        /// Les deux runs (séries + films) produisent chacun un tableau JSON
        /// <c>[{...}]</c> ; on concatène leurs items. Si l'un est vide ou n'est
        /// pas un tableau JSON (Markdown libre — réponse dégradée), on garde
        /// l'autre tel quel. Si les deux sont vides, renvoie une chaîne vide
        /// (la page affichera « aucune recommandation »).
        /// </summary>
        public static string MergeJsonArrays(string a, string b)
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
        /// Compte le nombre d'items d'une réponse JSON tableau
        /// (pour le libellé de la notification). 0 si ce n'est pas un tableau.
        /// </summary>
        public static int CountRecommendations(string reply)
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
        /// Entrée EPG pour le repli <b>chaîne + heure</b> de
        /// <see cref="EnrichRecommendations"/> : titre brut (log de diagnostic),
        /// nom de chaîne normalisé et date de diffusion. Une chaîne ne diffuse
        /// qu'un programme à une heure donnée — la clé (chaîne, heure) est donc
        /// aussi précise que le titre.
        /// </summary>
        private sealed class EpgEntry
        {
            public readonly string Title;
            public readonly EpgMatch Match;
            public readonly string ChannelNorm;
            public readonly DateTimeOffset? Start;
            public EpgEntry(string title, EpgMatch match, string channelNorm, DateTimeOffset? start)
            { Title = title; Match = match; ChannelNorm = channelNorm; Start = start; }
        }

        /// <summary>
        /// Enrichit le payload de recommandations (tableau JSON
        /// <c>[{title,kind,reason,priority,channel,start,showbizz_match}]</c>)
        /// en rapprochant chaque titre des résultats d'outils
        /// <c>get_emby_info</c> (actions <c>epg_series</c>/<c>epg_movies</c>/
        /// <c>epg_tonight</c>) capturés pendant la boucle agent. Les items EPG
        /// portent <c>id</c>/<c>channel_id</c>/<c>rating</c> ; on les injecte
        /// dans la recommandation + <c>image_url</c> (poster) construit depuis
        /// l'id Emby.
        /// <para>Portage C# du matching par titre du PHP : on construit une
        /// lookup <c>norm(title) → EpgMatch</c>, puis pour chaque reco on
        /// cherche <c>norm(reco.title)</c>. Si pas de match (titre rewordé ou
        /// traduit par le LLM — cas vécu : titres TMDB anglais au lieu des
        /// titres EPG), un <b>repli chaîne+heure</b>
        /// (<see cref="FindByChannelStart"/>) rattache la reco au programme
        /// EPG diffusé sur la même chaîne à la même heure. Une reco
        /// <b>intracable</b> au pool EPG (ni titre, ni chaîne+heure, ni id
        /// recopié du pool) est <b>écartée</b> du payload avec un Warn —
        /// validation stricte contre les hallucinations : on ne publie que
        /// ce qui peut être rattaché à un programme que le plugin a lui-même
        /// fourni au LLM. Retourne le payload inchangé si ce n'est pas un
        /// tableau JSON.</para>
        /// </summary>
        public string EnrichRecommendations(string payload,
            List<(string tool, string result)> toolResults)
        {
            if (string.IsNullOrWhiteSpace(payload)) return payload;

            // 1) Construit la lookup norm(title) → EpgMatch depuis les résultats
            //    d'outils. On ne garde que les résultats d'epg_series/epg_movies/
            //    epg_tonight ; leur forme est
            //    {total, results:[{title,id,channel_id,rating,...}]}.
            //    On extrait les primitives tout de suite (le JsonDocument est
            //    disposé à la sortie du bloc using — on ne garde aucune référence
            //    à ses JsonElement).
            var lookup = new Dictionary<string, EpgMatch>(StringComparer.Ordinal);
            var entries = new List<EpgEntry>();
            var epgIds = new HashSet<string>(StringComparer.Ordinal);
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

                        // Repli chaîne+heure : on collecte aussi le nom de chaîne
                        // et la date de diffusion (bruts, hors JsonDocument).
                        string channel = item.TryGetProperty("channel", out var chEl) && chEl.ValueKind == JsonValueKind.String
                            ? chEl.GetString() : null;
                        DateTimeOffset? startDt = null;
                        if (item.TryGetProperty("start", out var stEl) && stEl.ValueKind == JsonValueKind.String
                            && DateTimeOffset.TryParse(stEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var stv))
                            startDt = stv;
                        entries.Add(new EpgEntry(title, lookup[key], NormTitle(channel), startDt));
                        if (!string.IsNullOrEmpty(id)) epgIds.Add(id);
                    }
                }
            }

            if (lookup.Count == 0)
            {
                _logger?.Info("[LLM_AI] Enrichissement : aucun résultat epg capturé — reco laissées telles quelles.");
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

            int enriched = 0, matched = 0, fallback = 0;
            var dropped = new List<string>();
            var toRemove = new List<System.Text.Json.Nodes.JsonNode>();
            foreach (var node in arr)
            {
                if (node is not System.Text.Json.Nodes.JsonObject obj) continue;

                string source = JsonStr(obj, "source");
                bool isWatchItem = string.Equals(source, "recording", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(source, "library", StringComparison.OrdinalIgnoreCase);

                // source="recording" ou "library" : l'id est un item Emby concret
                // (enregistrement ou item de bibliothèque) fourni par le LLM depuis
                // les listes injectées. On NE surcharge pas avec un match EPG — le
                // titre pourrait coller à un programme du soir sans que ce soit le
                // même item ; on garderait le mauvais id. On s'assure juste du
                // poster depuis cet id.
                if (isWatchItem)
                {
                    string recId = JsonStr(obj, "id");
                    if (!string.IsNullOrEmpty(recId) && string.IsNullOrEmpty(JsonStr(obj, "image_url")))
                    {
                        obj["image_url"] = "/emby/Items/" + recId + "/Images/Primary?maxWidth=400";
                        enriched++;
                    }
                    continue;
                }

                if (!obj.TryGetPropertyValue("title", out var titleNode) || titleNode == null)
                {
                    // Reco sans titre : intracable par construction → écartée.
                    dropped.Add("(sans titre)");
                    toRemove.Add(node);
                    continue;
                }
                string title = titleNode.GetValue<string>();
                string key = NormTitle(title);
                if (string.IsNullOrEmpty(key))
                {
                    dropped.Add("(titre vide)");
                    toRemove.Add(node);
                    continue;
                }

                if (lookup.TryGetValue(key, out var epg))
                {
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
                else
                {
                    // Repli chaîne+heure : le LLM a parfois reformulé/traduit le
                    // titre (cas vécu 2026-08-31 : ResponseLanguage=English →
                    // titres TMDB anglais au lieu des titres EPG français) alors
                    // qu'il recopie correctement channel/start (champs
                    // OBLIGATOIRES du format). Une chaîne ne diffuse qu'un
                    // programme à une heure donnée : la clé (chaîne normalisée,
                    // ±10 min) rattache la reco au bon programme même avec un
                    // titre cassé.
                    var fb = FindByChannelStart(entries, JsonStr(obj, "channel"), JsonStr(obj, "start"));
                    if (fb != null)
                    {
                        matched++; fallback++;
                        _logger?.Info("[LLM_AI] Enrichissement : reco « {0} » sans match titre → rattachée par chaîne+heure à « {1} » ({2}).",
                            title, fb.Title,
                            string.IsNullOrEmpty(fb.Match.Id) ? "sans id" : "id " + fb.Match.Id);
                        if (!string.IsNullOrEmpty(fb.Match.Id))
                        {
                            obj["id"] = fb.Match.Id;
                            obj["image_url"] = "/emby/Items/" + fb.Match.Id + "/Images/Primary?maxWidth=400";
                            enriched++;
                        }
                        if (!string.IsNullOrEmpty(fb.Match.ChannelId))
                            obj["channel_id"] = fb.Match.ChannelId;
                        if (fb.Match.Rating.HasValue)
                            obj["rating"] = fb.Match.Rating.Value;
                    }
                    else
                    {
                        // Troisième clé de traçage : un id de programme que le LLM
                        // a recopié du pool EPG. Si la reco porte un id présent
                        // dans les entrées émises par le plugin, elle est
                        // traçable → on garde (et on bâtit le poster).
                        string exId = JsonStr(obj, "id");
                        if (!string.IsNullOrEmpty(exId) && epgIds.Contains(exId))
                        {
                            matched++;
                            if (string.IsNullOrEmpty(JsonStr(obj, "image_url")))
                            {
                                obj["image_url"] = "/emby/Items/" + exId + "/Images/Primary?maxWidth=400";
                                enriched++;
                            }
                        }
                        else
                        {
                            // INTRACABLE : ni le titre, ni la (chaîne, heure), ni
                            // l'id ne rattachent cette reco à une entrée EPG que
                            // le plugin a lui-même fournie au LLM. Presque
                            // certainement une hallucination (programme jamais
                            // dans le pool) ou une reco entièrement reformulée —
                            // dans les deux cas elle n'est pas programmable et ne
                            // passerait de toute façon aucun garde-fou du record
                            // bucket. On l'écarte du payload (validation stricte)
                            // plutôt que d'afficher une carte morte.
                            dropped.Add(title);
                            toRemove.Add(node);
                        }
                    }
                }
            }

            // Éviction des recos intracables (collectées pendant la boucle —
            // on ne retire pas un JsonNode pendant qu'on énumère son parent).
            foreach (var n in toRemove)
                arr.Remove(n);

            if (dropped.Count > 0)
            {
                _logger?.Warn("[LLM_AI] Enrichissement : {0} reco(s) écartée(s) — intracable(s) au pool EPG fourni au LLM (ni titre, ni chaîne+heure, ni id) : {1}.",
                    dropped.Count, string.Join(" | ", dropped));
            }

            if (arr.Count > 0)
            {
                _logger?.Info("[LLM_AI] Enrichissement : {0}/{1} reco matchées ({2} par chaîne+heure), {3} avec id/image_url, {4} écartée(s) hors-pool EPG.",
                    matched, arr.Count, fallback, enriched, dropped.Count);
            }
            else if (dropped.Count > 0)
            {
                _logger?.Warn("[LLM_AI] Enrichissement : TOUTES les recos ont été écartées ({0}) — pool EPG vs réponse du LLM sans aucun rattachement.",
                    dropped.Count);
            }

            return arr.ToJsonString();
        }

        /// <summary>
        /// Repli <b>chaîne + heure</b> de <see cref="EnrichRecommendations"/> :
        /// cherche l'entrée EPG diffusée sur la même chaîne (nom normalisé) à la
        /// même heure (±10 min, la plus proche gagne). Retourne null si la
        /// chaîne ou l'heure de la reco est absente/invalide, ou si aucune
        /// entrée EPG ne cadre — l'appelant tente alors le rattachement par id,
        /// sinon écarte la reco.
        /// </summary>
        private static EpgEntry FindByChannelStart(List<EpgEntry> entries, string channel, string start)
        {
            if (entries == null || entries.Count == 0 ||
                string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(start))
                return null;
            if (!DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.None, out var recoStart))
                return null;
            string chKey = NormTitle(channel);
            if (string.IsNullOrEmpty(chKey)) return null;

            EpgEntry best = null;
            var bestDiff = TimeSpan.MaxValue;
            foreach (var e in entries)
            {
                if (e.Start == null || e.ChannelNorm != chKey) continue;
                var diff = (e.Start.Value - recoStart).Duration();
                if (diff > TimeSpan.FromMinutes(10)) continue;
                if (best == null || diff < bestDiff) { best = e; bestDiff = diff; }
            }
            return best;
        }

        /// <summary>
        /// Rapproche les recommandations de la bibliothèque :
        /// <list type="bullet">
        /// <item><c>source="live"</c> (EPG du soir) : si le titre est déjà possédé,
        /// on injecte <c>library_id</c> (+ poster si absent) → bouton
        /// « Regarder (bibli.) ».</item>
        /// <item><c>source="library"</c> (réserve bibliothèque) : si le LLM a omis
        /// l'<c>id</c>, on le backfill depuis le match (→ bouton « Regarder »).</item>
        /// <item><c>source="recording"</c> : ignorée (l'id vient de la liste
        /// d'enregistrements injectée par <see cref="TonightApiService"/>).</item>
        /// </list>
        /// Recherche par <c>Name</c> exact (indexé, peu coûteux) puis repli flou
        /// <c>NameContains</c>, confirmé par <see cref="NormTitle"/> ; repli
        /// final par <c>imdb_id</c> (si le LLM l'a établi via ses outils) via
        /// <see cref="InternalItemsQuery.AnyProviderIdEquals"/> — attrape les
        /// titres possédés dont le titre EPG diffère du titre bibliothèque.
        /// </summary>
        public string EnrichWithLibrary(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return payload;
            System.Text.Json.Nodes.JsonNode root;
            try { root = System.Text.Json.Nodes.JsonNode.Parse(payload); }
            catch { return payload; }
            if (root is not System.Text.Json.Nodes.JsonArray arr) return payload;

            int matched = 0;
            foreach (var node in arr)
            {
                if (node is not System.Text.Json.Nodes.JsonObject obj) continue;
                string source = JsonStr(obj, "source");
                bool isLib = string.Equals(source, "library", StringComparison.OrdinalIgnoreCase);
                bool isRec = string.Equals(source, "recording", StringComparison.OrdinalIgnoreCase);
                if (isRec) continue; // id fourni par le LLM depuis la liste enregistrements
                if (!isLib && !string.IsNullOrEmpty(JsonStr(obj, "library_id"))) continue; // live déjà matché

                string title = JsonStr(obj, "title");
                string key = NormTitle(title);
                if (string.IsNullOrEmpty(key)) continue;

                string libId = FindLibraryItemId(title, key);

                // Repli IMDb : si le titre ne matche pas mais que le LLM a
                // établi l'id IMDb du contenu (via ses outils), on cherche
                // l'item bibliothèque par ProviderId — indépendant du titre
                // (le titre EPG peut différer du titre de bibliothèque :
                // « Comment tuer son mari en 10 leçons » vs titre original).
                // Trouvé = possédé → library_id → exclue du record bucket.
                if (string.IsNullOrEmpty(libId))
                {
                    string imdbId = NormalizeImdbId(JsonStr(obj, "imdb_id"));
                    if (imdbId != null)
                    {
                        var owned = FindLibraryItemByImdb(imdbId);
                        if (owned != null)
                        {
                            libId = owned.InternalId.ToString();
                            _logger?.Info("[LLM_AI] Enrichissement bibliothèque : « {0} » déjà possédé (IMDb {1} → « {2} »).",
                                title, imdbId, owned.Name);
                        }
                    }
                }

                if (string.IsNullOrEmpty(libId)) continue;

                if (isLib)
                {
                    // Always backfill/overwrite with the real libId to prevent hallucinated GUIDs from breaking validation
                    obj["id"] = libId;
                }
                else
                {
                    // source live : library_id for the button "Watch (library)".
                    obj["library_id"] = libId;
                }
                if (string.IsNullOrEmpty(JsonStr(obj, "image_url")))
                    obj["image_url"] = "/emby/Items/" + libId + "/Images/Primary?maxWidth=400";
                matched++;
            }
            _logger?.Info("[LLM_AI] Enrichissement bibliothèque : {0} reco(s) rapprochée(s).", matched);
            return arr.ToJsonString();
        }

        /// <summary>
        /// Cherche un item de bibliothèque (film ou série) dont le nom matche le
        /// titre de la reco. <c>InternalItemsQuery.Name</c> = exact (insensible à
        /// la casse, indexé) ; repli <c>NameContains</c> si l'exact ne donne rien.
        /// Confirme par <see cref="NormTitle"/> pour éviter un faux positif.
        /// </summary>
        private BaseItem FindLibraryItem(string title, string normKey)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            try
            {
                var q = new InternalItemsQuery
                {
                    Name = title,
                    IncludeItemTypes = new[] { "Movie", "Series" },
                    Recursive = true,
                    Limit = 8,
                    EnableTotalRecordCount = false
                };
                var items = _library.GetItemList(q) ?? Array.Empty<BaseItem>();
                foreach (var it in items)
                    if (it != null && NormTitle(it.Name) == normKey) return it;

                // Repli flou (sous-chaîne) si l'exact n'a rien retourné.
                q = new InternalItemsQuery
                {
                    NameContains = title,
                    IncludeItemTypes = new[] { "Movie", "Series" },
                    Recursive = true,
                    Limit = 20,
                    EnableTotalRecordCount = false
                };
                var fuzzy = _library.GetItemList(q) ?? Array.Empty<BaseItem>();
                foreach (var it in fuzzy)
                    if (it != null && NormTitle(it.Name) == normKey) return it;
            }
            catch { }
            return null;
        }

        private string FindLibraryItemId(string title, string normKey)
        {
            var it = FindLibraryItem(title, normKey);
            // InternalId (long) en chaîne : la seule forme consommable par la
            // couche REST/UI (bouton « Regarder », image_url) ET par la
            // validation ItemIds — BaseItem.Id est un Guid rejeté par REST.
            return it?.InternalId.ToString();
        }

        /// <summary>
        /// Normalise un id IMDb proposé par le LLM : accepte « tt1234567 » ou
        /// « 1234567 » (préfixe <c>tt</c> ré-ajouté), en minuscules. Retourne
        /// <c>null</c> si la valeur n'est pas un id IMDb plausible (7–8
        /// chiffres) — protège des ids hallucinés.
        /// </summary>
        private static string NormalizeImdbId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim().ToLowerInvariant();
            if (s.StartsWith("tt", StringComparison.Ordinal)) s = s.Substring(2);
            if (s.Length < 7 || s.Length > 8) return null;
            foreach (char c in s) if (c < '0' || c > '9') return null;
            return "tt" + s;
        }

        /// <summary>
        /// Cherche l'item de bibliothèque (film/série) portant l'id IMDb
        /// donné, via <see cref="InternalItemsQuery.AnyProviderIdEquals"/>
        /// (clé Provider « Imdb » — utilisée pour les films ET les séries).
        /// C'est le rapprochement le plus fiable possible : un id IMDb
        /// identique désigne le même contenu indépendamment du titre. Null si
        /// non trouvé ou requête impossible.
        /// </summary>
        private BaseItem FindLibraryItemByImdb(string imdbId)
        {
            if (string.IsNullOrEmpty(imdbId)) return null;
            try
            {
                var q = new InternalItemsQuery
                {
                    AnyProviderIdEquals = new Dictionary<string, string> { { "Imdb", imdbId } },
                    IncludeItemTypes = new[] { "Movie", "Series" },
                    Recursive = true,
                    Limit = 2,
                    EnableTotalRecordCount = false
                };
                return (_library.GetItemList(q) ?? Array.Empty<BaseItem>()).FirstOrDefault();
            }
            catch { return null; }
        }
    }
}