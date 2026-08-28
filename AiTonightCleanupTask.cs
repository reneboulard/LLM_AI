using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace LLM_AI
{
    /// <summary>
    /// Tâche planifiée de nettoyage nocturne des surfaces natives du watch bucket
    /// « À regarder ce soir » : (1) retire le genre
    /// <see cref="AiGenreTagger.TonightGenre"/> (« AI Tonight ») de tous les items
    /// Emby qui le portent, et (2) <b>vide</b> la collection
    /// <see cref="AiTonightCollectionManager.CollectionName"/> (« AI Tonight ») de
    /// tous ses membres (la coquille BoxSet reste). Complète l'étiquetage et le
    /// remplissage de collection faits par <see cref="TonightService"/> sur les
    /// recos du watch bucket — workflow : surface durant la journée (run Tonight),
    /// nettoyage à 3 h du matin.
    /// </summary>
    /// <remarks>
    /// <para><b>Tourne toujours</b> (non gated par
    /// <see cref="PluginConfiguration.TonightGenreTagEnabled"/> ni
    /// <see cref="PluginConfiguration.TonightCollectionEnabled"/>) : nettoie les
    /// tags/membres restants même après désactivation des features ou
    /// désinstallation du mécanisme. No-op s'il n'y a rien à nettoyer.</para>
    /// <para>Scope isolé : ne touche jamais le genre « AI Suggestion » de la
    /// bibliothèque <c>.strm</c>.</para>
    /// <para>Découverte par scanning d'assembly (comme
    /// <see cref="LlmScheduledTask"/>) — aucune inscription dans
    /// <c>Plugin.cs</c>. Services Emby injectés par DI.</para>
    /// </remarks>
    public class AiTonightCleanupTask : IScheduledTask
    {
        private readonly ILibraryManager _library;
        private readonly ILogger _logger;
        private readonly ICollectionManager _collections;

        public AiTonightCleanupTask(ILibraryManager library, ILogger logger,
            ICollectionManager collections)
        {
            _library = library;
            _logger = logger;
            _collections = collections;
        }

        public string Name => "LLM AI — Nettoyage genre « AI Tonight »";

        /// <summary>Identifiant stable de la tâche.</summary>
        public string Key => "a1b2c3d4-1111-2222-3333-444455556666";

        public string Description =>
            "Nettoyage nocturne des surfaces natives « À regarder ce soir » : retire le genre " +
            "« AI Tonight » de tous les items Emby ET vide la collection « AI Tonight » de ses " +
            "membres (la coquille reste, re-remplie au prochain run). Tourne quotidiennement à 3 h ; " +
            "les runs Tonight suivants reconstruisent les surfaces sur les recos toujours pertinentes. " +
            "Ne touche pas au genre « AI Suggestion » de la bibliothèque .strm.";

        public string Category => "LLM AI";

        public bool IsHidden => false;

        public bool IsEnabled => true;

        public bool IsLogged => true;

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            progress?.Report(10);
            try
            {
                // 1) Retrait du genre « AI Tonight » de tous les items.
                await AiGenreTagger.RemoveAllAsync(_library, _logger, AiGenreTagger.TonightGenre, cancellationToken)
                    .ConfigureAwait(false);

                progress?.Report(50);

                // 2) Vidage de la collection « AI Tonight » (best-effort : un
                //    échec collection ne doit pas masquer un nettoyage genre
                //    réussi, ni inversement). Tourne toujours pour nettoyer les
                //    membres restants même si la feature collection est désactivée.
                try
                {
                    await AiTonightCollectionManager.ClearAsync(_collections, _library, _logger, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] Tâche nettoyage collection AI Tonight : {0}", ex.Message);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] Tâche nettoyage AI Tonight : {0}", ex, ex.Message);
                throw;
            }
            finally
            {
                progress?.Report(100);
            }
        }

        /// <summary>
        /// Trigger par défaut : quotidien à 3 h du matin (le nettoyage se fait
        /// la nuit, hors des runs Tonight diurnes). L'utilisateur peut
        /// l'ajuster dans le planificateur Emby.
        /// </summary>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = "DailyTrigger",
                TimeOfDayTicks = new TimeSpan(3, 0, 0).Ticks
            };
        }
    }
}