using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace LLM_AI
{
    /// <summary>
    /// Tâche planifiée « Gestion bibliothèque » (v2.10.0) : évaluation LLM de
    /// la bibliothèque pour suggérer des suppressions (deux runs
    /// indépendants — films / séries, mêmes prompts que le lancement manuel
    /// depuis la page de config). Le rapport produit est persisté
    /// (libreview_report.json) et affiché sous forme de cartes dans l'onglet
    /// « Gestion bibliothèque » ; le plugin ne supprime JAMAIS — seules les
    /// suppressions explicites via l'endpoint admin (délégation à Emby).
    ///
    /// Planification lue dans la partie gauche de
    /// <see cref="PluginConfiguration.LibReviewTask"/> (« Weekly Sunday
    /// 05:10 | prompt films »), même mécanisme que
    /// <see cref="LlmScheduledTask.GetDefaultTriggers"/>.
    /// </summary>
    public class LibReviewTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly ILogger _logger;
        private readonly IJsonSerializer _json;
        private readonly ILibraryManager _library;
        private readonly IUserManager _users;
        private readonly IUserDataManager _userData;
        private readonly ILiveTvManager _liveTv;
        private readonly IServerApplicationHost _host;
        private readonly INotificationManager _notifications;

        public LibReviewTask(
            ILogger logger,
            IJsonSerializer jsonSerializer,
            ILibraryManager library,
            IUserManager users,
            IUserDataManager userData,
            ILiveTvManager liveTv,
            IServerApplicationHost host,
            INotificationManager notifications)
        {
            _logger = logger;
            _json = jsonSerializer;
            _library = library;
            _users = users;
            _userData = userData;
            _liveTv = liveTv;
            _host = host;
            _notifications = notifications;
        }

        public string Name => I18n.S("task.libreview.name", I18n.ResolveDisplayLangKey(_host));

        /// <summary>Identifiant stable de la tâche (GUID du plugin).</summary>
        public string Key => "a3f1c7d2-9b4e-4e58-8c1a-2d6f0b9e5a71";

        public string Description => I18n.S("task.libreview.desc", I18n.ResolveDisplayLangKey(_host));

        public string Category => I18n.S("task.category", I18n.ResolveDisplayLangKey(_host));

        public bool IsHidden => false;

        /// <summary>Désactivée par défaut (opt-in — consomme du LLM).</summary>
        public bool IsEnabled => Plugin.Instance?.Configuration?.LibReviewEnabled ?? false;

        public bool IsLogged => true;

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
            {
                _logger.Warn("[LLM_AI] [LibReview] Tâche exécutée mais aucune configuration disponible.");
                return;
            }
            if (!cfg.LibReviewEnabled)
            {
                _logger.Info("[LLM_AI] [LibReview] Tâche désactivée en configuration — ignorée.");
                return;
            }

            progress?.Report(5);

            // Porte LLM : sérialise avec les autres tâches LLM (défaut 1
            // concurrent) — même gate que LlmScheduledTask.
            await LlmTaskGate.AcquireAsync("Gestion bibliothèque", _logger, cancellationToken).ConfigureAwait(false);
            try
            {
                _logger.Info("[LLM_AI] [LibReview] Run planifié — consigne : « {0} »",
                    string.IsNullOrWhiteSpace(cfg.LibReviewBrief) ? "(aucune)" : cfg.LibReviewBrief);

                // Pas de run détaché ici : la tâche EST déjà un fil de fond.
                // Les jalons vont dans le log.
                Action<string> prog = s => _logger.Info("[LLM_AI] [LibReview] {0}", s);

                var report = await LibReviewEngine.RunAsync(cfg, cfg.LibReviewBrief,
                    _library, _users, _userData, _liveTv, _host, _json, _notifications,
                    _logger, prog, cancellationToken).ConfigureAwait(false);

                progress?.Report(100);
                _logger.Info("[LLM_AI] [LibReview] Tâche terminée : {0} suppression(s) proposée(s), {1} à discuter, {2} à garder ({3} candidat(s)).",
                    report?.Items.Count(i => i.Section == "delete") ?? 0,
                    report?.Items.Count(i => i.Section == "can_delete") ?? 0,
                    report?.Items.Count(i => i.Section == "keep") ?? 0,
                    report?.Candidates ?? 0);
            }
            catch (OperationCanceledException)
            {
                _logger.Info("[LLM_AI] [LibReview] Tâche annulée.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[LLM_AI] [LibReview] Échec de la tâche : {0}", ex, ex.Message);
                throw;
            }
            finally
            {
                LlmTaskGate.Release("Gestion bibliothèque");
            }
        }

        // ------------------------------------------------------------------
        //  Triggers par défaut (partie gauche de LibReviewTask)
        // ------------------------------------------------------------------

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var cfg = Plugin.Instance?.Configuration;
            var spec = cfg?.LibReviewTask ?? string.Empty;
            foreach (var t in LlmScheduledTask.ParseTriggers(LibReviewEngine.ParseSchedule(spec)))
                yield return t;
        }
    }
}
