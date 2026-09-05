using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace LLM_AI
{
    /// <summary>
    /// Tâche planifiée de nettoyage nocturne des surfaces natives du watch bucket
    /// « À regarder ce soir » : (1) revert des favoris <b>posés par le plugin</b>
    /// (<see cref="AiTonightFavoritesManager.Revert"/> — set tracé dans le fichier
    /// d'état), (2) retire le genre
    /// <see cref="AiGenreTagger.TonightGenre"/> (« AI Tonight ») de tous les items
    /// Emby qui le portent, (3) <b>vide</b> la collection
    /// <see cref="AiTonightCollectionManager.CollectionName"/> (« AI Tonight ») de
    /// tous ses membres (la coquille BoxSet reste) et (4) <b>vide</b> la playlist
    /// <see cref="AiTonightPlaylistManager.PlaylistName"/> (entrées retirées, la
    /// coquille reste). Complète les surfaces posées par
    /// <see cref="TonightService"/> sur les recos du watch bucket — workflow :
    /// surface durant la journée (run Tonight), nettoyage à 3 h du matin.
    /// </summary>
    /// <remarks>
    /// <para><b>Tourne toujours</b> (non gated par les flags opt-in
    /// <see cref="PluginConfiguration.TonightGenreTagEnabled"/>,
    /// <see cref="PluginConfiguration.TonightCollectionEnabled"/>,
    /// <see cref="PluginConfiguration.TonightPlaylistEnabled"/> ni
    /// <see cref="PluginConfiguration.TonightFavoritesEnabled"/>) : nettoie les
    /// tags/membres/entrées/favoris restants même après désactivation des
    /// features. No-op s'il n'y a rien à nettoyer.</para>
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
        private readonly IServerApplicationHost _host;
        private readonly IPlaylistManager _playlists;
        private readonly IUserDataManager _userData;
        private readonly IUserManager _users;

        public AiTonightCleanupTask(ILibraryManager library, ILogger logger,
            ICollectionManager collections, IServerApplicationHost host,
            IPlaylistManager playlists, IUserDataManager userData, IUserManager users)
        {
            _library = library;
            _logger = logger;
            _collections = collections;
            _host = host;
            _playlists = playlists;
            _userData = userData;
            _users = users;
        }

        public string Name => I18n.S("task.cleanup.name", I18n.ResolveDisplayLangKey(_host));

        /// <summary>Identifiant stable de la tâche.</summary>
        public string Key => "a1b2c3d4-1111-2222-3333-444455556666";

        public string Description => I18n.S("task.cleanup.desc", I18n.ResolveDisplayLangKey(_host));

        public string Category => I18n.S("task.category", I18n.ResolveDisplayLangKey(_host));

        public bool IsHidden => false;

        public bool IsEnabled => true;

        public bool IsLogged => true;

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            progress?.Report(10);
            try
            {
                // 1) Revert des favoris posés par le plugin (set du fichier
                //    d'état) — best-effort : un échec favoris ne doit pas
                //    masquer les nettoyages suivants. Tourne toujours (fichier
                //    d'état restant après désactivation de la feature).
                try
                {
                    AiTonightFavoritesManager.Revert(_userData, _users, _library, _logger, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] Tâche nettoyage favoris AI Tonight : {0}", ex.Message);
                }

                progress?.Report(30);

                // 2) Retrait du genre « AI Tonight » de tous les items.
                await AiGenreTagger.RemoveAllAsync(_library, _logger, AiGenreTagger.TonightGenre, cancellationToken)
                    .ConfigureAwait(false);

                progress?.Report(50);

                // 3) Vidage de la collection « AI Tonight » (best-effort : un
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

                progress?.Report(80);

                // 4) Vidage de la playlist « AI Tonight » (entrées retirées,
                //    coquille conservée) — même sémantique best-effort.
                try
                {
                    await AiTonightPlaylistManager.ClearAsync(_playlists, _library, _logger, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] Tâche nettoyage playlist AI Tonight : {0}", ex.Message);
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