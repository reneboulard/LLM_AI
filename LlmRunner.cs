using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
            // Le moniteur de sécurité logge [LLM_AI][SEC] via ce logger (posé
            // une fois — idempotent, premier non null gagne).
            SecurityMonitor.SetLogger(logger);
            // Le chargeur d'overlay i18n (v1.16.0) logge [LLM_AI] I18n overlay
            // via ce logger (même pattern — les lignes pré-enregistrement sont
            // mises en tampon et vidées ici).
            I18nOverlay.SetLogger(logger);
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
        /// <param name="runUser">Usager porteur du run ( Tonight uniquement ;
        /// null pour la tâche planifiée et le chat) — transposé au tool
        /// <c>get_emby_info</c> pour la gate EPG (v1.13.12.0).</param>
        public List<ILlmTool> BuildTools(PluginConfiguration cfg, User runUser = null)
        {
            var tools = new List<ILlmTool>
            {
                new GetEmbyInfoTool(_library, _users, _liveTv, _host, _logger)
                    { RunUser = runUser }
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
        /// <para><paramref name="runUser"/> : usager porteur du run (Tonight
        /// per-usager uniquement) — active la gate EPG du tool
        /// <c>get_emby_info</c> si l'usager n'a pas le droit TV en direct
        /// (<see cref="PermissionGate.CanWatchLive"/>) ; null = comportement
        /// global (tâche planifiée, chat).</para>
        /// </summary>
        public async System.Threading.Tasks.Task<(string payload, bool ok)> RunAsync(
            PluginConfiguration cfg, string label, string userPrompt, string workflow,
            System.Threading.CancellationToken ct, User runUser = null)
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
                    responseLanguage: I18n.ResolveProseLangName(cfg, _host));
                var tools = BuildTools(cfg, runUser);

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
        //  Synthèse LLM sans outils (un seul appel, repli multi-backend).
        //  Path générique partagé : la boucle de rétroaction hebdo
        //  (RecoAnalysisTask) l'utilise pour produire la directive de
        //  recommandation à partir d'un tableau de corrélations construit
        //  en C# — même forme que la synthèse déterministe de l'audit
        //  (rassemblement C# + un passage LLM sans outils).
        // ------------------------------------------------------------------

        /// <summary>
        /// Un seul appel LLM SANS outils ni boucle agent, avec repli
        /// multi-backend (<see cref="ResolveBackends"/> dans l'ordre de
        /// priorité). Retourne (réponse brute, ok) ; ok=false si aucun
        /// backend configuré ou si tous échouent (erreur catchée + loguée —
        /// l'appelant décide quoi faire ; fail-open naturel).
        /// <see cref="OperationCanceledException"/> est propagée.
        /// </summary>
        public async System.Threading.Tasks.Task<(string reply, bool ok)> RunSynthesisAsync(
            PluginConfiguration cfg, string label, string systemPrompt, string userPrompt,
            System.Threading.CancellationToken ct)
        {
            try
            {
                var backends = ResolveBackends(cfg);
                if (backends.Count == 0)
                {
                    _logger.Warn("[LLM_AI] [{0}] Aucun LLM configuré/activé — synthèse ignorée.", label);
                    return (string.Empty, false);
                }

                string ollamaCloudKey = ResolveKey(cfg.OllamaApiKey, "OLLAMA_API_KEY");
                string geminiKey = ResolveKey(cfg.GeminiApiKey, "GEMINI_API_KEY");

                string reply = await ChatWithFallbackAsync(backends, ollamaCloudKey, geminiKey,
                    systemPrompt, userPrompt, label, ct).ConfigureAwait(false);
                return (reply, true);
            }
            catch (OperationCanceledException)
            {
                _logger.Info("[LLM_AI] [{0}] Synthèse annulée.", label);
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[LLM_AI] [{0}] Échec de la synthèse : {1}", ex, label, ex.Message);
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
            "à l'outil system_audit (inspection système : server_info, security_check " +
            "(sécurité : mots de passe des comptes, accès distant/HTTPS, UPnP, en-têtes " +
            "proxy), ratings_check (hygiène des cotes), security_metrics (compteurs et " +
            "événements de sécurité du plugin), active_sessions, " +
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
            "6. Appelle action=\"security_check\" (mots de passe manquants des comptes — " +
            "admins surtout, accès distant/HTTPS, UPnP, en-têtes proxy, preuves d'accès externe : " +
            "sessions actives ET appareils historiques avec IP publique — si un accès externe est " +
            "observé, les avertissements y sont déjà rehaussés critique) puis action=\"upnp_check\" " +
            "(interroge le routeur en lecture seule : passerelle UPnP, IP WAN, table de redirection — " +
            "un mapping UPnP vers le port Emby 8096/8920 est CRITIQUE) et reprends leurs constats " +
            "(severity critique/avertissement/ok + fix) tels quels dans le rapport : n'atténue " +
            "JAMAIS un constat critique de sécurité. La sonde UPnP figure TOUJOURS dans les " +
            "constats : aucun mapping trouvé = constat ✅ explicite (« UPnP désactivé / aucun " +
            "mapping routeur ») — ne tais JAMAIS la ligne UPnP du rapport, et ne la classe " +
            "JAMAIS en 🔴 : « aucun mapping » est un état sain (✅), une seule liste UPnP " +
            "(pas un doublon critique + ok). Dès qu'une surface " +
            "distante existe (accès distant " +
            "activé ou accès externe observé), inclus le test externe GRC ShieldsUP!! du champ " +
            "external_test dans les « Actions recommandées » — l'usager seul peut confirmer la " +
            "joignabilité réelle du WAN.\n" +
            "6b. Appelle action=\"ratings_check\" (hygiène des cotes : OfficialRating des " +
            "films/séries et de l'EPG vs table parentale du serveur) et reprends son constat " +
            "tel quel : cotes non reconnues = limite parentale aveugle sur ces items — " +
            "« avertissement » avec le conseil de normalisation fourni (ex. Classification " +
            "Mapper), sinon constat ✅. N'invente pas de valeurs : liste celles du champ " +
            "unrecognized_top.\n" +
            "6c. Appelle action=\"security_metrics\" (compteurs d'activité et fenêtre " +
            "d'événements de sécurité DU PLUGIN : appels/erreurs web_fetch, SSRF bloqués, " +
            "appels d'outils malformés ou inconnus, échecs backend LLM, tours de chat " +
            "refusés, actions déposées/approuvées/refusées). Reprends tout événement " +
            "anormal (SSRF_BLOQUE répété, TOOL_ERREUR ou CHAT_REFUSE en rafale, " +
            "ACTION_CONSOMMATION_REFUSEE) comme constat ⚠️ ou 🔴 avec le détail : c'est la " +
            "trace de détection d'une tentative d'injection ou d'abus — ne la tais JAMAIS. " +
            "Aucun événement de sécurité = constat ✅ explicite.\n" +
            "7. Produis un RAPPORT Markdown concis :\n" +
            "   - « ## Constats » : liste de puces taguées par gravité " +
            "(🔴 critique / ⚠️ attention / ✅ ok), chacune avec la valeur chiffrée à l'appui.\n" +
            "   - « ## Actions recommandées » : ce qu'il faudrait faire, classé par priorité.\n" +
            "   - Markdown PUR : JAMAIS de notation math/LaTeX ($...$, \\rightarrow — écris " +
            "« → » en texte simple) ni de balises HTML (<code>, <b>…) — ces artefacts rendent " +
            "le rapport illisible à la restitution.\n" +
            "Sois factuel et précis : reprends les valeurs retournées par les outils, ne " +
            "spécule pas.\n" +
            "### RÈGLE — FIDÉLITÉ AUX SONDES (les sorties d'outils sont la SEULE source de vérité)\n" +
            "1. COMPLET : chaque constat de chaque sonde (security_check, upnp_check, " +
            "ratings_check, security_metrics, log_scan…) figure dans le rapport sous sa " +
            "sévérité d'origine — fusionner dans une rubrique est permis, OMETTRE un constat " +
            "est interdit, y compris les constats Information/ℹ️ (ex. « Sonde de mot de passe " +
            "suspendue », « Mot de passe récemment défini », « Plusieurs comptes " +
            "administrateurs ») : un constat absent du rapport est un constat que l'usager " +
            "ne verra jamais.\n" +
            "2. VERBATIM : noms de comptes, nombres, âges, états sont repris TELS QUELS des " +
            "sorties d'outils — jamais recomptés, arrondis ou interprétés (écris « 10/20 " +
            "validés », pas « 20/40 » ; si le JSON dit « dernier usage il y a 0 j », ne dis " +
            "JAMAIS « pas utilisée depuis longtemps ») ; nomme TOUJOURS le(s) compte(s) " +
            "concerné(s) dans les Constats, pas seulement dans les Actions.\n" +
            "3. RIEN DE FABRIQUÉ : ne transforme JAMAIS un champ descriptif nu (ex. " +
            "certificate_configured=false quand HTTPS est désactivé, enable_remote_access=false) " +
            "en constat d'une sévérité quelconque — seul un constat posé par une sonde fait " +
            "foi ; un champ de contexte se cite comme Information avec son nom de champ, " +
            "jamais comme une alerte.\n" +
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
            "La ligne UPnP (section upnp_check du digest) figure TOUJOURS dans les constats : " +
            "aucun mapping trouvé = constat ✅ explicite (« UPnP désactivé / aucun mapping " +
            "routeur »), mapping vers 8096/8920 = 🔴 critique.\n" +
            "### SECTION log_scan (COMPTAGES EXACTS)\n" +
            "Les comptages de la section log_scan sont calculés en C# (zéro LLM) : exceptions " +
            "groupées par classe avec première trame de stack, codes HTTP 4xx/5xx entrants et " +
            "sortants, échecs ffmpeg, pannes de providers, santé du plugin. Reprends-les TELS " +
            "QUELS avec leurs valeurs chiffrées, groupe par groupe — même un groupe bénin se " +
            "cite avec sa classe (ex. une requête pendant le démarrage du serveur) : ne les " +
            "recompte PAS depuis le tail brut du " +
            "journal. L'absence d'un motif dans la fenêtre finie (3 fichiers, ≤ 7 jours) ne " +
            "prouve pas l'absence de problème antérieur : formule l'absence comme « aucun motif " +
            "observé dans la fenêtre de journal analysée », jamais comme « aucun problème ». " +
            "Les 401/403 entrants répétés se croisent avec security_check (surface exposée) et " +
            "les échecs ffmpeg avec la section processes (orphelins). Si security_check signale " +
            "que la sonde de mots de passe triviaux a été effectuée, les échecs d'authentification " +
            "observés dans log_scan pour ces comptes admin peuvent être les TENTATIVES DE LA SONDE : " +
            "elle produit au plus 3 échecs par compte par audit (sous le seuil de verrouillage ~5 " +
            "d'Emby) et suspend tout compte dont le compteur d'échecs n'est pas à zéro — croise les " +
            "deux sections avant de conclure à une tentative d'intrusion. En revanche une ligne " +
            "« Temporarily locking out » (verrouillage de compte) ne peut PAS venir de la sonde : " +
            "c'est le signal d'échecs répétés réels (ou d'un compte mal déverrouillé) — à signaler.\n" +
            "Markdown PUR : JAMAIS de notation math/LaTeX ($...$, \\rightarrow — écris « → » en " +
            "texte simple) ni de balises HTML (<code>, <b>…).\n" +
            "### RÈGLE — FIDÉLITÉ AU DIGEST (le digest est la SEULE source de vérité)\n" +
            "1. COMPLET : chaque constat de chaque section figure dans le rapport sous sa " +
            "sévérité d'origine — fusionner dans une rubrique est permis, OMETTRE un constat " +
            "est interdit, y compris les constats Information/ℹ️ (ex. « Sonde de mot de passe " +
            "suspendue », « Mot de passe récemment défini », « Plusieurs comptes " +
            "administrateurs ») : un constat absent du rapport = constat que l'usager ne " +
            "verra jamais, ce n'est pas acceptable.\n" +
            "2. VERBATIM : noms de comptes, nombres, âges, états sont repris TELS QUELS des " +
            "sections — jamais recomptés, arrondis ou interprétés (écris « 10/20 validés » " +
            "et non « 20/40 » ; si le JSON dit « dernier usage il y a 0 j », ne dis JAMAIS " +
            "« pas utilisée depuis longtemps ») ; nomme TOUJOURS le(s) compte(s) concerné(s) " +
            "dans les Constats, pas seulement dans les Actions.\n" +
            "3. RIEN DE FABRIQUÉ : aucun constat qui n'existe pas dans le digest — ne " +
            "transforme JAMAIS un champ descriptif nu (ex. certificate_configured=false) en " +
            "constat de sévérité quelconque ; seul un constat posé par la section fait foi. " +
            "Un champ du JSON se cite comme Information avec son nom de champ, jamais comme " +
            "une alerte.\n" +
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
                    // Chaîne AFFICHÉE (fenêtre d'audit via FinishError) :
                    // langue d'interface, plus de FR en dur (v1.15.0.5).
                    return I18n.S("audit.err.nobackend", I18n.ResolveDisplayLangKey(_host));
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
                var tools = BuildAuditTools(cfg, sessions, tasks, notifications);

                // Langue du rapport : règle usager 2026-10-01 —
                // ResponseLanguage (choix explicite) → langue de l'interface
                // Emby (« Auto ») → anglais. La directive est TOUJOURS
                // injectée : sans elle, un modèle a dérivé en chinois
                // (terrain 2026-10-01). Elle ne suffit PAS seule sur ce
                // path : terrain 2026-10-02 (UICulture en-US, prompt d'audit
                // anglais, gemma4:latest priorité 1) — rapport pourtant EN
                // FRANÇAIS : tout ce que le plugin injecte (workflow,
                // descriptions d'outils, sorties des sondes) est français,
                // la boucle agent se termine sur des résultats d'outils
                // français (récence au moment où le rapport s'écrit) et la
                // directive — noyée en fin de system prompt, elle-même
                // rédigée en français — perd. Correctif validé sur le
                // terrain le même jour (l'usager l'a d'abord testé en le
                // collant à la fin du prompt d'audit configuré) : l'exigence
                // de langue est réinjectée en FIN de user prompt (récence +
                // quirk documenté : gemma suit le user prompt) via
                // AppendAuditLangRequirement.
                string langName = I18n.ResolveProseLangName(cfg, _host);
                var agent = new LlmAgentService(backends, cfg.RagDirectives, AUDIT_WORKFLOW,
                    ollamaCloudKey, geminiKey, _json, _logger, cfg.DebugVerbose,
                    AUDIT_ROLE_INTRO, "", langName);

                var (reply, _) = await agent.RunAsync(
                    AppendAuditLangRequirement(userPrompt, langName), tools, ct).ConfigureAwait(false);
                reply = SanitizeReport(reply);

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
                // Affiché dans la fenêtre d'audit — langue d'interface
                // (v1.15.0.5) ; le message d'exception reste brut.
                return string.Format(I18n.S("audit.err.fail", I18n.ResolveDisplayLangKey(_host)), ex.Message);
            }
        }

        /// <summary>
        /// Réinjecte l'exigence de langue de l'audit EN FIN de user prompt —
        /// les DEUX modes, les TROIS sites : boucle agent (single), doses
        /// déterministes, assemblage final. Pourquoi la directive de system
        /// prompt (<see cref="LlmAgentService.BuildLanguageDirective"/>) ne
        /// suffit pas sur le path audit : tout ce que le plugin injecte —
        /// workflow, descriptions d'outils, sorties des sondes — est en
        /// français, la boucle agent se termine sur des résultats d'outils
        /// français (récence au moment où le rapport s'écrit) et la
        /// directive (elle-même en français) perd. Terrain 2026-10-02 :
        /// UICulture en-US + prompt d'audit anglais → rapport pourtant en
        /// français (gemma4:latest, priorité 1). Validé le même jour : la
        /// même exigence collée en fin de user prompt tient (récence + quirk
        /// documenté : gemma suit le user prompt). L'exigence est rédigée
        /// DANS la langue cible pour l'anglais (la phrase elle-même est un
        /// signal de langue) ; pour toute autre valeur (libre, ex.
        /// « Nederlands ») gabarit français — même compromis que
        /// <see cref="LlmAgentService.BuildLanguageDirective"/>. Les valeurs
        /// de sondes restent VERBATIM (règles de fidélité) : l'exigence
        /// interdit explicitement de les traduire.
        /// </summary>
        internal static string AppendAuditLangRequirement(string userPrompt, string langName)
        {
            if (string.IsNullOrWhiteSpace(langName)) return userPrompt ?? string.Empty;
            bool english = string.Equals(I18n.ParseLangName(langName), I18n.En, StringComparison.Ordinal);
            string req = english
                ? "\n\nLANGUAGE — ABSOLUTE REQUIREMENT: Write ALL your output in English. " +
                  "All section headings in English (\"## Findings\", \"## Recommended actions\", " +
                  "\"## Metadata health\") — even when other instructions use the French titles " +
                  "« ## Constats » or « ## Actions recommandées ». Every finding, explanation and " +
                  "recommended action must be in English, even though the workflow, the tool " +
                  "descriptions and the tool outputs are in French: the French data NEVER changes " +
                  "your output language. EXCEPTION — never translate probe values: account names, " +
                  "paths, log lines, JSON field names and quoted finding text are copied verbatim."
                : "\n\nLANGUE — EXIGENCE ABSOLUE : rédige TOUTE ta sortie en " + langName.Trim() + " — " +
                  "titres de sections, constats, explications et actions recommandées, même si le " +
                  "workflow, les descriptions d'outils et les données des sondes sont en français : " +
                  "cela ne change PAS la langue de ta sortie. EXCEPTION — ne traduis JAMAIS les " +
                  "valeurs des sondes : noms de comptes, chemins, lignes de journal, champs JSON " +
                  "et constats cités se reprennent tels quels.";
            return (userPrompt ?? string.Empty).TrimEnd() + req;
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
        /// Bloc « HEURE ACTUELLE » du chat (v1.13.21.1) : le LLM n'a AUCUN
        /// accès à l'horloge (aucun tool « heure ») — sans ancre, il déduisait
        /// « maintenant » des horaires EPG servis en UTC et se croyait
        /// plusieurs heures plus tard qu'il ne l'est (incident live : « il
        /// est 5 h plus tard », programme déclaré terminé alors qu'il n'avait
        /// pas commencé, donc « non disponible »). Recalculé à CHAQUE tour
        /// (RunChatAsync est appelé par tour) — jamais mis en cache : une
        /// conversation qui traverse minuit ou une veille longue doit rester
        /// juste. L'heure est donnée en heure locale du serveur (celle de
        /// l'usager du foyer) avec son décalage UTC explicite.
        /// </summary>
        private static string BuildTimeBlock()
        {
            var now = DateTimeOffset.Now;
            var local = TimeZoneInfo.Local;
            var offset = now.Offset.ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture);
            var sign = now.Offset >= TimeSpan.Zero ? "+" : "−";
            return "\n### HEURE ACTUELLE\n" +
                "Il est actuellement le " +
                now.ToString("yyyy-MM-dd 'à' HH:mm", System.Globalization.CultureInfo.InvariantCulture) +
                " (heure locale du serveur, fuseau " + local.Id + ", UTC" + sign + offset + ").\n" +
                "C'est ta SEULE source fiable pour « maintenant » : les horaires EPG renvoyés par les " +
                "outils (champs start/end) sont déjà convertis en heure locale du serveur — cite-les tels quels. " +
                "Ne déduis JAMAIS l'heure actuelle des dates de la mémoire de conversation ni des " +
                "horodatages internes : ils peuvent être en UTC.\n" +
                "Pour « qu'est-ce qui passe maintenant/présentement » : epg_now est la vue " +
                "informative brute (tout ce qui est à l'antenne, champ is_onair — les programmes " +
                "en cours y figurent, même hors de tes genres favoris) ; epg_tonight reste la " +
                "liste curatée pour les recommandations.\n";
        }

        /// <summary>
        /// Bloc « liens profonds » du chat (v1.13.9.12) : quand le LLM cite un
        /// item dont un outil a fourni l'id, il le rend cliquable vers la fiche
        /// web d'Emby (même origine — la page chat est servie par Emby lui-même,
        /// aucun hôte à deviner ; ouverture dans un nouvel onglet côté page).
        /// Le serverId est résolu une fois via <c>GetPublicSystemInfo</c> (le
        /// chemin fiable — <c>GetSystemInfo</c> lève une NRE sur certains hôtes
        /// Windows) ; vide = bloc omis (fail-open, jamais de lien fabriqué).
        /// </summary>
        private string _deepLinkBlock;

        private async System.Threading.Tasks.Task<string> BuildDeepLinkBlockAsync()
        {
            if (_deepLinkBlock != null) return _deepLinkBlock;
            string serverId = null;
            try
            {
                var pub = _host == null
                    ? null
                    : await _host.GetPublicSystemInfo(System.Threading.CancellationToken.None).ConfigureAwait(false);
                serverId = pub?.Id;
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Liens profonds chat : serverId indisponible ({0}) — bloc omis.", ex.Message);
            }
            if (string.IsNullOrWhiteSpace(serverId))
            {
                _deepLinkBlock = string.Empty;
                return _deepLinkBlock;
            }
            _deepLinkBlock =
                "\n### LIENS PROFONDS EMBY\n" +
                "Quand tu cites un item (film, série, épisode, enregistrement, programme EPG) dont un " +
                "outil t'a fourni l'identifiant (champ id), rends le titre cliquable vers sa fiche Emby " +
                "avec ce gabarit exact : [Titre](/web/index.html#!/item?id=ID&serverId=" + serverId +
                ") — remplace Titre et ID, ne change rien d'autre. Pour une série (vue groupée par " +
                "passages), ajoute &asSeries=true avant la parenthèse. Si l'id n'est pas connu, cite le " +
                "titre SANS lien — ne fabrique JAMAIS un lien avec un id inventé.";
            return _deepLinkBlock;
        }

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
            System.Threading.CancellationToken ct,
            string conversationMemory = null,
            List<ILlmTool> extraTools = null,
            string extraWorkflow = null,
            string contextBlock = null,
            User toolUser = null,
            bool includeAuditTools = true)
        {
            try
            {
                var backends = ResolveBackends(cfg);
                if (backends.Count == 0)
                {
                    _logger.Warn("[LLM_AI] [{0}] Aucun LLM configuré/activé — chat ignoré.", label);
                    // Affiché dans le chat admin — langue d'interface (v1.15.0.5).
                    return I18n.S("err.chatnobackend", I18n.ResolveDisplayLangKey(_host));
                }

                string ollamaCloudKey = ResolveKey(cfg.OllamaApiKey, "OLLAMA_API_KEY");
                string geminiKey = ResolveKey(cfg.GeminiApiKey, "GEMINI_API_KEY");

                // Agent chat : intro + workflow dédiés ; formatSection=""
                // supprime le bloc « FORMAT DES RECOMMANDATIONS » (le chat
                // produit du Markdown libre, pas un tableau JSON de recos).
                // Mémoire réflexive (Phase C) : la fiche mémoire (si active)
                // est accolée au workflow — tendance générale de fond, sans
                // exclure la diversité ; vide = inchangé (fail-open).
                // Mémoire de conversation (chat_memory) : résumé de la
                // session précédente + derniers échanges (ChatApiService).
                // Contexte déroulant (v1.13.8) : le bloc du mode choisi
                // (guide d'édition + texte courant du prompt + langue
                // cible) est réinjecté à CHAQUE tour — changer de contexte
                // n'exige jamais de réinitialiser la conversation.
                string workflow = CHAT_WORKFLOW + BuildTimeBlock()
                    + await BuildDeepLinkBlockAsync().ConfigureAwait(false)
                    + (contextBlock ?? "")
                    + MemoryCard.BuildInjectionBlock(cfg)
                    + (conversationMemory ?? "")
                    + (extraWorkflow ?? "");
                var agent = new LlmAgentService(backends, cfg.RagDirectives, workflow,
                    ollamaCloudKey, geminiKey, _json, _logger, cfg.DebugVerbose,
                    CHAT_ROLE_INTRO, "", I18n.ResolveProseLangName(cfg, _host));

                // Tous les outils existants : recommandation + audit santé +
                // (couche d'action du chat, v1.13 : tools construits par
                // ChatActions et injectés par l'endpoint — budget géré là-bas).
                // v1.13.21 : chat externe — <paramref name="toolUser"/> porte
                // l'usager résolu (get_emby_info filtre parentalement ses
                // vues bibliothèque), et <paramref name="includeAuditTools"/>
                // est false (aucun system_audit sur ce chemin).
                var tools = BuildTools(cfg, toolUser);
                var extTool = toolUser != null && tools.Count > 0
                    ? (tools[0] as GetEmbyInfoTool) : null;
                if (extTool != null) extTool.ParentalUser = toolUser;
                // v1.13.21 : commandes client non destructives — UNIQUEMENT
                // sur le chemin chat externe (toolUser), allowlist stricte,
                // session bornée à l'usager résolu (ClientCommandTool).
                if (toolUser != null)
                    tools.Add(new ClientCommandTool(sessions, _library, _host, toolUser, _logger));
                if (includeAuditTools)
                    tools.AddRange(BuildAuditTools(cfg, sessions, tasks, notifications));
                if (extraTools != null && extraTools.Count > 0)
                    tools.AddRange(extraTools);

                string reply = SanitizeReport(await agent.RunChatAsync(history, userMessage, tools, ct)
                    .ConfigureAwait(false));

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
                // Préfixe localisé (v1.15.0.2) — la réponse s'affiche telle
                // quelle dans le fil ; les contrôleurs StartsWith des deux
                // chats comparent la MÊME clé résolue de la même façon
                // (langue d'affichage depuis le même hôte).
                return I18n.S("err.chatfail", I18n.ResolveDisplayLangKey(_host)) + ex.Message;
            }
        }

        // ------------------------------------------------------------------
        //  Filet de formatage des rapports (v1.13.9.13) : les petits modèles
        //  émettent de la notation math LaTeX ($\rightarrow$) et du HTML cru
        //  (<code>) que la restitution Markdown rend illisible (flèches
        //  « $ ightarrow$ », balises visibles). Le prompt interdit désormais
        //  ces notations ; ce filet rattrape ce qui passe quand même — même
        //  philosophie que RepairUnescapedQuotes pour le JSON des outils.
        // ------------------------------------------------------------------

        /// <summary>
        /// Filet de formatage d'une réponse LLM « rapport » (audit, chat) :
        /// remplace les flèches LaTeX par leur glyphe texte (« → », « ⇒ »,
        /// « ← »), retire les dollars de math-mode qui les entourent encore
        /// (« $ → $ » → « → »), convertit les <c>&lt;br&gt;</c> en sauts de
        /// ligne et dénude les balises d'habillage courantes (code/b/strong/
        /// i/em — le texte interne est conservé). Best-effort : n'élève jamais.
        /// </summary>
        internal static string SanitizeReport(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            // Normalisation des fins de ligne + retrait des CR isolés : un
            // LaTeX « \rightarrow » qui passe par un décodage JSON d'un petit
            // modèle est dégradé en « \r » (retour chariot) + « ightarrow » —
            // le backslash a disparu, le CR isolé n'est qu'un déchet (vu en
            // production dans un rapport d'audit : « $ ightarrow$ »). On le
            // retire AVANT les mappings, sinon aucun ne reconnaît le fragment.
            string s = text.Replace("\r\n", "\n").Replace("\r", "");
            s = Regex.Replace(s, @"\\(?:longrightarrow|Longrightarrow)\b", "→");
            s = Regex.Replace(s, @"\\(?:rightarrow|to)\b", "→");
            s = Regex.Replace(s, @"\\Rightarrow\b", "⇒");
            s = Regex.Replace(s, @"\\leftarrow\b", "←");
            // Fragment corrompu (backslash décodé) : « $ ightarrow$ » /
            // « $ightarrow$ » / « $\rightarrow$ » restant → « → ». Le motif
            // est borné au segment math ($...$ sans autre contenu) pour
            // éviter tout faux positif sur un montant en dollars.
            s = Regex.Replace(s, @"\$\s*(?:\\)?[a-zA-Z]*ightarrow\s*\$", "→");
            s = Regex.Replace(s, @"\$\s*(→|⇒|←)\s*\$", "$1");
            s = Regex.Replace(s, @"<br\s*/?>", "\n");
            s = Regex.Replace(s, @"</?(?:code|strong|b|em|i)>", "");
            return s;
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

            // 2) Pipeline « dosé » (2026-10-01) : le rassemblement reste C# ;
            //    la synthèse se fait en passes BORNÉES — une dose par groupe de
            //    sections (petit contexte, fidélité tenable pour un petit
            //    modèle), croisements pré-calculés en C# (« FAITS ÉTABLIS »),
            //    puis un assemblage final LLM qui ne voit que les blocs déjà
            //    rédigés + les faits. Motivation terrain : gemma4:latest
            //    batchait 12 actions en un seul tableau → fallback silencieux
            //    server_info → boucle d'agent expirée (10 itérations, run
            //    16:25) ; et l'assemblage d'un long rapport en UNE passe
            //    perdait des constats info/ℹ️ (omissions 16:09/16:13/16:18).
            //    Côté plugin : chaque dose est repliée mécaniquement si tous
            //    les backends échouent — le rapport sort toujours, jamais une
            //    erreur d'audit.
            var sections = SplitDigestSections(digest);
            string crossFacts = BuildAuditCrossFacts(sections);

            // Directive de langue de réponse (partagée avec la boucle agent —
            // voir LlmAgentService.BuildLanguageDirective) : règle usager
            // 2026-10-01 — ResponseLanguage (choix explicite) → langue de
            // l'interface Emby (« Auto ») → anglais. Toujours injectée :
            // sans directive, un modèle a dérivé en chinois (terrain
            // 2026-10-01). Elle ne suffit pas seule (terrain 2026-10-02,
            // boucle agent : contexte massivement français) : chaque user
            // prompt du pipeline dosé (doses + assemblage) reçoit AUSSI
            // l'exigence de langue en FIN de prompt via
            // AppendAuditLangRequirement — mêmes raisons (récence + petits
            // modèles qui suivent le user prompt).
            string langName = I18n.ResolveProseLangName(cfg, _host);
            var langDir = LlmAgentService.BuildLanguageDirective(langName);

            // Langue d'INTERFACE pour les jalons de progression affichés dans
            // la fenêtre d'audit (bucket v1.15.0.2 — ≠ prose du rapport qui
            // suit ResolveProseLangName ci-dessus) : un run sur une interface
            // EN affiche « Step 3/7 — System and performance ».
            var uiLang = I18n.ResolveDisplayLangKey(_host);

            // Groupes de doses : petits contextes (2-4 sondes), regroupés par
            // thème de rapport. Une section absente du digest est ignorée
            // (tolérance aux évolutions du digest). Title = titre du bloc du
            // RAPPORT (structurelle, française — limite assumée v1.15.0.3) ;
            // TitleEn = variante d'AFFICHAGE des jalons de progression sur
            // une interface non française (le rapport garde ses titres FR).
            var doses = new (string Title, string TitleEn, string[] SectionNames)[]
            {
                ("Système et performance", "System and performance", new[] { "server_info", "system_config", "host_metrics" }),
                ("Processus et stockage", "Processes and storage", new[] { "processes", "disk_storage" }),
                ("Activité et tâches planifiées", "Activity and scheduled tasks", new[] { "active_sessions", "scheduled_tasks", "transcode", "gpu_transcode" }),
                ("Bibliothèque et métadonnées", "Library and metadata", new[] { "library_stats", "missing_metadata", "metadata_health" }),
                ("Hygiène des cotes", "Ratings hygiene", new[] { "ratings_check" }),
                ("Sécurité", "Security", new[] { "security_check", "security_metrics" }),
                ("Réseau et journaux", "Network and logs", new[] { "upnp_check", "log_scan", "list_logs", "inspect_log" }),
            };

            var blocks = new System.Text.StringBuilder();
            int doseNo = 0;
            foreach (var dose in doses)
            {
                var doseData = new System.Text.StringBuilder();
                foreach (string name in dose.SectionNames)
                {
                    string body = DigestSection(sections, name);
                    if (string.IsNullOrEmpty(body)) continue;   // absente du digest
                    doseData.AppendLine("## " + name);
                    doseData.AppendLine(body);
                }
                if (doseData.Length == 0) continue;
                doseNo++;

                // Jalon de progression du run détaché (v1.14.2) : la page de
                // config pole ?Status=true et affiche « Dose 3/7 — … ». No-op
                // hors run détaché (appel depuis la tâche planifiée, tests).
                // Localisé dans la langue d'INTERFACE (v1.15.0.5) : titre
                // d'affichage EN hors interface FR — le rapport, lui, garde
                // ses titres de rubriques français.
                AuditRunState.SetProgress(string.Format(
                    I18n.S("audit.progress.dose", uiLang), doseNo, doses.Length,
                    uiLang == I18n.Fr ? dose.Title : dose.TitleEn));

                string block;
                try
                {
                    string sys = AUDIT_DOSE_SECTION_ROLE;
                    if (langDir.Length > 0) sys += "\n\n" + langDir;
                    // Exigence de langue en fin de user prompt (voir
                    // AppendAuditLangRequirement) : le rôle de dose et les
                    // données du digest sont en français — sans elle, un
                    // petit modèle dérive vers la langue du contexte.
                    string user = AppendAuditLangRequirement(
                        "Titre du bloc à produire : « " + dose.Title + " » (le titre " +
                        "« ### » est ajouté par le plugin — ne l'écris pas).\n\n" +
                        "### DONNÉES DE LA SECTION (JSON du digest)\n" + doseData +
                        "\nRédige maintenant le bloc Markdown de CETTE section.", langName);
                    block = await ChatWithFallbackAsync(backends, ollamaCloudKey, geminiKey,
                        sys, user, label + "::dose" + doseNo + "-" + dose.Title, ct).ConfigureAwait(false);
                    _logger.Info("[LLM_AI] [{0}] Dose {1}/{2} « {3} » : {4} car.", label, doseNo, doses.Length, dose.Title, block.Length);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.Warn("[LLM_AI] [{0}] Dose « {1} » indisponible sur tous les backends ({2}) — bloc mécanique.", label, dose.Title, ex.Message);
                    block = MechanicalSectionBlock(dose.SectionNames, sections, ex);
                }
                if (cfg.DebugVerbose)
                    _logger.Info("[LLM_AI] [{0}] Bloc « {1} » :\n{2}", label, dose.Title, block);

                blocks.AppendLine("### " + dose.Title);
                blocks.AppendLine();
                blocks.AppendLine(block.Trim());
                blocks.AppendLine();
            }

            // 3) Assemblage final : l'entrée = FAITS ÉTABLIS + blocs rédigés +
            //    consigne spécifique. Le LLM ne peut plus inventer d'alerte
            //    (il ne voit pas de champ nu) ni omettre une section entière
            //    (chaque dose est fournie) — et si l'assemblage échoue, les
            //    blocs constituent déjà un rapport lisible.
            string systemAsm = AUDIT_SYNTHESIS_ROLE + "\n\n" + AUDIT_SYNTHESIS_WORKFLOW +
                "\n\n" + AUDIT_DOSE_ASSEMBLY_MODE;
            if (langDir.Length > 0) systemAsm += "\n\n" + langDir;

            var up = new System.Text.StringBuilder();
            up.AppendLine("### FAITS ÉTABLIS (pré-calculés en C# par le plugin — autorité sur toute lecture croisée)");
            up.AppendLine(crossFacts);
            up.AppendLine("### BLOCS DE SECTIONS (rédigés dosé par dose, à partir du digest déterministe)");
            up.AppendLine();
            up.Append(blocks.ToString());
            up.AppendLine("### CONSIGNE SPÉCIFIQUE");
            up.AppendLine(userPrompt ?? "(audit complet — aucune consigne particulière)");
            up.AppendLine();
            up.AppendLine("Assemble maintenant le rapport final Markdown de santé : Constats regroupés par " +
                "sévérité (🔴 / ⚠️ / ℹ️ / ✅) et par rubrique, en REPRENANT les blocs tels quels (fusion de " +
                "style permise — omettre un constat ou en inventer un est interdit), puis « Actions " +
                "recommandées » classées par priorité. Les FAITS ÉTABLIS se citent tels quels.");
            // Exigence de langue en fin de user prompt, comme les doses (voir
            // AppendAuditLangRequirement) : les blocs rédigés doivent déjà être
            // dans la langue cible, l'assemblage y tient la prose de liaison.
            string userAsm = AppendAuditLangRequirement(up.ToString(), langName);

            // Jalon final de progression (run détaché) : l'assemblage est la
            // passe la plus lente après les doses elles-mêmes. Localisé dans
            // la langue d'interface (v1.15.0.5).
            AuditRunState.SetProgress(string.Format(I18n.S("audit.progress.assembly", uiLang), doseNo));

            string reply;
            try
            {
                reply = await ChatWithFallbackAsync(backends, ollamaCloudKey, geminiKey,
                    systemAsm, userAsm, label + "::assemblage", ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Warn("[LLM_AI] [{0}] Assemblage final indisponible ({1}) — rapport = blocs + faits.", label, ex.Message);
                reply = blocks.ToString() + crossFacts +
                    "\n(Assemblage final indisponible : " + ex.Message + " — les blocs de sections sont fournis tels quels.)";
            }
            reply = SanitizeReport(reply);

            _logger.Info("[LLM_AI] [{0}] Rapport d'audit (mode déterministe dosé) :\n{1}", label, reply);
            return reply;
        }

        /// <summary>
        /// Rôle des passes « dosées » du mode déterministe (AuditMode=
        /// deterministic) : UNE dose de sections → UN bloc Markdown. Les règles
        /// de fidélité tiennent dans un prompt court — un petit modèle les
        /// garde dans un contexte borné, là où la rédaction d'un long rapport
        /// en une passe les perdait (omissions ℹ️ constatées en terrain,
        /// 2026-10-01). Le titre « ### » n'est pas demandé : le plugin le
        /// préfixe lui-même (pas de doublon possible).
        /// </summary>
        internal const string AUDIT_DOSE_SECTION_ROLE =
            "Tu rédiges le bloc Markdown d'UNE section d'un rapport d'audit de " +
            "santé Emby. Tu n'as AUCUN outil : les données JSON fournies sont ta " +
            "seule source — analyse-les uniquement.\n" +
            "Règles de fidélité :\n" +
            "1. Cite CHAQUE constat du champ findings sous sa sévérité d'origine : " +
            "critique → 🔴, avertissement → ⚠️, info → ℹ️, ok → ✅. Fusionner deux " +
            "constats très proches est permis ; omettre un constat est interdit " +
            "(y compris les info/ℹ️) — un constat absent de ta sortie ne sera " +
            "visible nulle part : ce n'est pas acceptable.\n" +
            "2. Les valeurs (noms de comptes, nombres, âges, états) sont repris " +
            "TELS QUELS du JSON — jamais recomptés, arrondis ou interprétés ; " +
            "nomme TOUJOURS le(s) compte(s) concerné(s).\n" +
            "3. N'invente RIEN : un champ descriptif nu (ex. certificate_configured=false) " +
            "n'est pas un constat — ne le transforme JAMAIS en alerte ; cite un " +
            "champ, si utile, comme simple information avec son nom.\n" +
            "4. Ne divulgue jamais la valeur d'un mot de passe (elle n'est pas fournie).\n" +
            "5. Une section avec erreur JSON devient une ligne « (non vérifiable sur " +
            "ce serveur : raison) » — jamais une spéculation.\n" +
            "6. Markdown PUR : jamais de LaTeX ni de balises HTML.\n" +
            "7. Section log_scan : cite CHAQUE motif non vide du champ motifs, " +
            "groupe par groupe, avec sa gravité suggérée et son (ses) témoin(s) — " +
            "même un groupe bénin (1 seul événement, signal du plugin lui-même) " +
            "se cite avec sa classe. Le champ profil est un COMPTE NU : ne le " +
            "cite JAMAIS seul — ses champs temoins_* disent de quoi il s'agit " +
            "(catégorie + propos), cite-les.\n" +
            "Sortie : UNIQUEMENT le contenu du bloc, SANS titre « ### » (ajouté par " +
            "le plugin), sans préambule, sans conclusion générale, sans rubrique " +
            "« Actions recommandées » (rédigée à l'assemblage final).";

        /// <summary>
        /// Complément de système-prompt pour l'assemblage final dosé : l'entrée
        /// n'est pas le digest brut mais les blocs déjà rédigés + les FAITS
        /// ÉTABLIS pré-calculés. Replace le cadrage « JSON brut » du rôle de
        /// synthèse sans toucher les règles de fidélité (qui restent valables :
        /// le bloc est fidèle par construction, l'omission y est interdite).
        /// </summary>
        internal const string AUDIT_DOSE_ASSEMBLY_MODE =
            "### MODE D'ASSEMBLAGE DOSÉ\n" +
            "L'entrée ci-dessous n'est PAS le JSON brut : ce sont des BLOCS DE SECTIONS " +
            "déjà rédigés dose par dose (fidèles par construction), précédés des " +
            "FAITS ÉTABLIS pré-calculés en code C# (croisement log_scan × " +
            "security_check : signature de la sonde vs échecs réels ; règle UPnP). " +
            "Ton travail : ASSEMBLER — ordre, regroupement par sévérité, fluidité, " +
            "rubrique « Actions recommandées ». Les FAITS ÉTABLIS font autorité sur " +
            "toute lecture croisée : cite-les tels quels, ne les re-juge pas, ne " +
            "conclus jamais à une intrusion pour un compte que les FAITS ÉTABLIS " +
            "attribuent à la sonde.";

        /// <summary>
        /// Découpe le digest (sections « ## titre » + JSON) en liste ordonnée
        /// (titre, corps). Tolérant : JSON multi-lignes accepté, sections sans
        /// corps conservées (corps = ""), digest vide → liste vide.
        /// </summary>
        private static List<(string Title, string Body)> SplitDigestSections(string digest)
        {
            var list = new List<(string Title, string Body)>();
            if (string.IsNullOrEmpty(digest)) return list;
            string cur = null;
            var body = new System.Text.StringBuilder();
            foreach (var raw in digest.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    if (cur != null) list.Add((cur, body.ToString().Trim()));
                    cur = line.Substring(3).Trim();
                    body.Clear();
                }
                else if (cur != null) { body.AppendLine(line); }
            }
            if (cur != null) list.Add((cur, body.ToString().Trim()));
            return list;
        }

        /// <summary>Corps (JSON) d'une section du digest par nom, null si absente.</summary>
        private static string DigestSection(List<(string Title, string Body)> sections, string name)
        {
            foreach (var s in sections)
                if (string.Equals(s.Title, name, StringComparison.Ordinal))
                    return s.Body;
            return null;
        }

        /// <summary>
        /// Croisements déterministes de l'audit dosé : sonde de mots de passe
        /// triviaux (security_check.password_probe) × comptages d'authentifi-
        /// cation du journal (log_scan, motif « authentification »). Produit
        /// les « FAITS ÉTABLIS » de l'assemblage : le croisement était à la
        /// charge du LLM (deux sections lointaines à re-croiser — dérives
        /// constatées en terrain) ; ici le plugin calcule la lecture, le LLM
        /// la cite. Tolérant : donnée absente ou inattendue → « non calculable »,
        /// jamais une erreur (compat 4.9.x et pannes de sondes).
        /// </summary>
        private static string BuildAuditCrossFacts(List<(string Title, string Body)> sections)
        {
            try
            {
                string probeState = "absente";
                int adminsScanned = -1;
                List<string> probeMatches = null, probeSuspended = null;
                var denials = new List<(string Compte, int N)>();
                var lockouts = new List<string>();

                foreach (var s in sections)
                {
                    if (string.Equals(s.Title, "security_check", StringComparison.Ordinal))
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(s.Body);
                            if (doc.RootElement.TryGetProperty("password_probe", out var pp))
                            {
                                if (pp.TryGetProperty("available", out var av))
                                    probeState = av.ValueKind == System.Text.Json.JsonValueKind.True ? "exécutée" : "indisponible";
                                if (pp.TryGetProperty("admins_scanned", out var ac) && ac.ValueKind == System.Text.Json.JsonValueKind.Number)
                                    adminsScanned = ac.GetInt32();
                                probeMatches = JsonStringList(pp, "matches");
                                probeSuspended = JsonStringList(pp, "suspended");
                            }
                        }
                        catch { probeState = "JSON illisible"; }
                    }
                    else if (string.Equals(s.Title, "log_scan", StringComparison.Ordinal))
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(s.Body);
                            if (doc.RootElement.TryGetProperty("motifs", out var motifs) &&
                                motifs.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                foreach (var m in motifs.EnumerateArray())
                                {
                                    if (!(m.TryGetProperty("motif", out var mn) &&
                                          mn.ValueKind == System.Text.Json.JsonValueKind.String &&
                                          "authentification".Equals(mn.GetString(), StringComparison.Ordinal))) continue;
                                    if (m.TryGetProperty("par_compte", out var pc) && pc.ValueKind == System.Text.Json.JsonValueKind.Array)
                                        foreach (var r in pc.EnumerateArray())
                                            if (r.TryGetProperty("cle", out var ck) && ck.ValueKind == System.Text.Json.JsonValueKind.String &&
                                                r.TryGetProperty("compte", out var cn) && cn.ValueKind == System.Text.Json.JsonValueKind.Number &&
                                                cn.TryGetInt32(out int n))
                                                denials.Add((ck.GetString(), n));
                                    if (m.TryGetProperty("verrouillages", out var vl) && vl.ValueKind == System.Text.Json.JsonValueKind.Array)
                                        foreach (var r in vl.EnumerateArray())
                                            if (r.TryGetProperty("cle", out var lk) && lk.ValueKind == System.Text.Json.JsonValueKind.String)
                                                lockouts.Add(lk.GetString());
                                }
                            }
                        }
                        catch { /* fenêtre non lisible — les autres faits restent */ }
                    }
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("- Sonde de mots de passe triviaux : " + probeState +
                    (adminsScanned >= 0 ? " ; comptes admin scannés : " + adminsScanned : "") +
                    (probeMatches != null && probeMatches.Count > 0
                        ? " ; CORRESPONDANCE trivial : " + string.Join(", ", probeMatches)
                        : " ; correspondance trivial : aucune") +
                    (probeSuspended != null && probeSuspended.Count > 0
                        ? " ; sonde suspendue pour (compteur d'échecs ≠ 0) : " + string.Join(", ", probeSuspended)
                        : " ; sonde suspendue : aucun compte"));
                if (denials.Count > 0)
                    sb.AppendLine("- Échecs d'authentification dans la fenêtre du journal : " +
                        string.Join(", ", denials.Select(d => d.Compte + " → " + d.N)) + ".");
                else
                    sb.AppendLine("- Échecs d'authentification dans la fenêtre du journal : aucun.");
                if (lockouts.Count > 0)
                    sb.AppendLine("- Verrouillages « Temporarily locking out » : " + string.Join(", ", lockouts) +
                        " — échecs répétés RÉELS (jamais la sonde) : à signaler sans réserve.");
                sb.AppendLine("- Lecture du croisement (à citer telle quelle) : pour un compte SONDÉ, au plus " +
                    "3 échecs d'authentification sur la fenêtre = signature des tentatives contrôlées de la sonde " +
                    "de CE RUN, pas une intrusion ; un « Temporarily locking out » ne peut PAS venir de la sonde. " +
                    "Le compteur d'échecs d'un compte n'est remis à zéro QUE par une connexion réussie du compte.");
                sb.AppendLine("- UPnP : l'absence de mapping / de routeur n'est PAS un défaut — état sûr.");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "- FAITS ÉTABLIS non calculables ce run : " + ex.Message;
            }
        }

        /// <summary>Liste de chaînes d'un tableau JSON, null si absent/non tableau.</summary>
        private static List<string> JsonStringList(System.Text.Json.JsonElement parent, string prop)
        {
            if (!parent.TryGetProperty(prop, out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;
            var list = new List<string>();
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == System.Text.Json.JsonValueKind.String) list.Add(e.GetString());
            return list;
        }

        /// <summary>
        /// Bloc mécanique de repli d'une dose : si la passe LLM a échoué sur
        /// tous les backends, les constats de la dose sont extraits tel quels
        /// du JSON (sévérité → emoji + titre + détail court). Le rapport sort
        /// toujours — la pire dose est dégradée, jamais manquante.
        /// </summary>
        private static string MechanicalSectionBlock(string[] sectionNames,
            List<(string Title, string Body)> sections, Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            foreach (string name in sectionNames)
            {
                string body = DigestSection(sections, name);
                if (string.IsNullOrWhiteSpace(body)) continue;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("findings", out var f) &&
                        f.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var item in f.EnumerateArray())
                        {
                            string sev = item.TryGetProperty("severity", out var sv) && sv.ValueKind == System.Text.Json.JsonValueKind.String ? sv.GetString() : null;
                            string t = item.TryGetProperty("title", out var ti) && ti.ValueKind == System.Text.Json.JsonValueKind.String ? ti.GetString() : "?";
                            string d = item.TryGetProperty("detail", out var de) && de.ValueKind == System.Text.Json.JsonValueKind.String ? de.GetString() : "";
                            sb.Append(SeverityEmoji(sev)).Append(' ').Append(t)
                              .AppendLine(string.IsNullOrEmpty(d) ? "" : " — " + d);
                        }
                        continue;
                    }
                    sb.AppendLine(SeverityEmoji(null) + " section « " + name + " » : pas de champ findings — données brutes conservées dans le digest.");
                }
                catch
                {
                    sb.AppendLine("(section « " + name + " » : synthèse indisponible et JSON non lisible — " + ex.Message + ")");
                }
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Emoji de sévérité du digest (critique/avertissement/info/ok).</summary>
        private static string SeverityEmoji(string sev) =>
            "critique".Equals(sev, StringComparison.Ordinal) ? "🔴" :
            "avertissement".Equals(sev, StringComparison.Ordinal) ? "⚠️" :
            "info".Equals(sev, StringComparison.Ordinal) ? "ℹ️" : "✅";

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

        /// <summary>Proposition d'ids TMDB/IMDb/TVDB issue du LLM (S2 de la tâche orphelins).</summary>
        internal struct IdGuess
        {
            public string ImdbId;
            public int TmdbId;
            /// <summary>Id TVDB (séries) — validé via TMDB /find tvdb_id.</summary>
            public string TvdbId;
            public string OriginalTitle;
            public int? Year;
            public string Confidence;
            public bool IsEmpty =>
                string.IsNullOrWhiteSpace(ImdbId) && TmdbId <= 0
                && string.IsNullOrWhiteSpace(TvdbId) && string.IsNullOrWhiteSpace(OriginalTitle);
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
                    "fiche TMDB la plus probable. Pour une série, tu peux aussi proposer son id TVDB " +
                    "si tu le connais mieux que son id TMDB. Réponds UNIQUEMENT un objet JSON compact, sans " +
                    "explication ni balises markdown : " +
                    "{\"imdb_id\":\"tt...\",\"tmdb_id\":12345,\"tvdb_id\":\"12345\",\"original_title\":\"...\",\"year\":2020," +
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
                    int? tmdbId = IntId(r, "tmdb_id");
                    if (tmdbId.HasValue) g.TmdbId = tmdbId.Value;
                    g.TvdbId = StrId(r, "tvdb_id");
                    g.OriginalTitle = StrId(r, "original_title");
                    // Convention du prompt : « 0 si inconnu ». 0 doit rester
                    // null : sinon la porte reçoit expectedYear=0 et
                    // YearCompatible(0, 2026) rejette la vraie fiche (cas réel
                    // 2026-09-20 : « La foudre, un éclair de génie » — fiche
                    // trouvée par la recherche, rejetée par la garde d'année).
                    int? guessYear = IntId(r, "year");
                    if (guessYear.HasValue && guessYear.Value > 0) g.Year = guessYear.Value;
                    g.Confidence = StrId(r, "confidence");
                }
            }
            catch { /* tolérant */ }
            return g;
        }

        private static string StrId(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        /// <summary>
        /// Entier tolérant depuis la sortie brute du LLM : <c>TryGetInt32</c>
        /// LÈVE InvalidOperationException sur une valeur en chaîne (piège .NET
        /// documenté 2026-10-01 — le « Try » ne couvre que la conversion), ce
        /// qui jetait tout le reste du guess ; on accepte donc aussi "2026"
        /// encodé en chaîne.
        /// </summary>
        private static int? IntId(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var p)) return null;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int v)) return v;
            if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out int vs)) return vs;
            return null;
        }

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
            public readonly int? Year;
            public EpgMatch(string id, string channelId, double? rating, int? year)
            { Id = id; ChannelId = channelId; Rating = rating; Year = year; }
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
            // id programme → EpgMatch : 3e clé de traçage (id recopié du pool),
            // pour injecter year/rating même quand le titre a été reformulé.
            var epgById = new Dictionary<string, EpgMatch>(StringComparer.Ordinal);
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
                        int? year = null;
                        if (item.TryGetProperty("year", out var yEl) && yEl.ValueKind == JsonValueKind.Number
                            && yEl.TryGetInt32(out int yv))
                            year = yv;

                        // Premier vu gagne (dedup) — les EPG peuvent lister la même
                        // série sur plusieurs chaînes ; on garde la 1re occurrence.
                        if (!lookup.ContainsKey(key))
                            lookup[key] = new EpgMatch(id, channelId, rating, year);

                        // Repli chaîne+heure : on collecte aussi le nom de chaîne
                        // et la date de diffusion (bruts, hors JsonDocument).
                        string channel = item.TryGetProperty("channel", out var chEl) && chEl.ValueKind == JsonValueKind.String
                            ? chEl.GetString() : null;
                        DateTimeOffset? startDt = null;
                        if (item.TryGetProperty("start", out var stEl) && stEl.ValueKind == JsonValueKind.String
                            && DateTimeOffset.TryParse(stEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var stv))
                            startDt = stv;
                        entries.Add(new EpgEntry(title, lookup[key], NormTitle(channel), startDt));
                        if (!string.IsNullOrEmpty(id) && epgById.TryAdd(id, lookup[key])) epgIds.Add(id);
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
                    if (epg.Year.HasValue)
                        obj["year"] = epg.Year.Value;
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
                        if (fb.Match.Year.HasValue)
                            obj["year"] = fb.Match.Year.Value;
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
                            if (epgById.TryGetValue(exId, out var byId) && byId.Year.HasValue)
                                obj["year"] = byId.Year.Value;
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

                BaseItem libItem = FindLibraryItem(title, key);
                string libId = libItem?.InternalId.ToString();

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
                            libItem = owned;
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
                // Année de production depuis l'item bibliothèque : comble une
                // reco sans year (EPG muet, LLM omis) — l'UI l'affiche sur la
                // carte. Un year déjà présent (EPG matché, tmdb_lookup) prime.
                if (libItem != null && libItem.ProductionYear > 0 && obj["year"] == null)
                    obj["year"] = libItem.ProductionYear;
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