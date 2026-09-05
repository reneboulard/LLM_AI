using System;
using System.Linq;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Observateur de télémétrie de lecture (mémoire réflexive, Phase A).
    /// Branché sur <see cref="ISessionManager.PlaybackStopped"/> (pattern
    /// <see cref="TonightLoginService"/> : <see cref="IServerEntryPoint"/>
    /// découvert par scan d'assembly, abonnement dans <see cref="Run"/>,
    /// désabonnement dans <see cref="Dispose"/>).
    /// <para>
    /// À chaque session terminée, une entrée est écrite dans
    /// <c>playback.json</c> (via <see cref="DecisionStore"/>) : item, usager,
    /// durée réelle, fraction lue (le signal central de la réflexion : rejet
    /// immédiat &lt; 5 %, abandon 5–50 %, partiel 50–80 %, validé &gt; 80 %),
    /// source (bibliothèque / .strm / direct), chaîne, client et appareil.
    /// </para>
    /// <para>Le % du direct n'est pas calculable ici (pas de durée d'œuvre
    /// naturelle sur un flux) : Pct reste null pour le direct — la Phase B
    /// (EpgSnapshot, runtime du programme) le dérivera à l'analyse.</para>
    /// <para>Opt-in <see cref="PluginConfiguration.PlaybackTelemetryEnabled"/>
    /// (défaut false) ; vérifié à CHAQUE événement (pas seulement à Run) —
    /// le flag peut être activé sans redémarrage. Best-effort : un échec
    /// n'affecte jamais la lecture.</para>
    /// </summary>
    public class PlaybackWatcher : IServerEntryPoint
    {
        private readonly ISessionManager _sessions;
        private readonly ILibraryManager _library;
        private readonly ILogger _logger;

        public PlaybackWatcher(ISessionManager sessions, ILibraryManager library, ILogger logger)
        {
            _sessions = sessions;
            _library = library;
            _logger = logger;
        }

        public void Run()
        {
            _sessions.PlaybackStopped += OnPlaybackStopped;
            if (Plugin.Instance?.Configuration?.PlaybackTelemetryEnabled ?? false)
                _logger?.Info("[LLM_AI] Télémétrie de lecture active (playback.json).");
        }

        public void Dispose()
        {
            _sessions.PlaybackStopped -= OnPlaybackStopped;
        }

        private void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                if (cfg == null || !cfg.PlaybackTelemetryEnabled) return;
                if (e == null || e.Item == null) return;
                if (e.IsAutomated) return; // lectures déclenchées par l'arrière-plan

                var item = e.Item;
                var date = DateTimeOffset.UtcNow;

                // Classification de la source : direct (chaîne LiveTV) / carte
                // .strm (recommandations à enregistrer) / bibliothèque. Les
                // cartes .strm sont étiquetées à part : une lecture de carte
                // d'enregistrement n'est pas un visionnage de bibliothèque
                // (l'analyse hebdo les exclut déjà, même règle).
                string src;
                string channel = null;
                if (item is LiveTvChannel)
                {
                    src = "livetv";
                    channel = e.MediaInfo?.ChannelName ?? item.Name;
                }
                else
                {
                    var strmRoot = StrmLibraryGenerator.ResolveLibraryRoot(
                        _library, cfg.StrmLibraryName, _logger);
                    src = TonightService.IsUnderPath(item.Path, strmRoot) ? "strm" : "library";
                }

                // Fraction lue : position ÷ durée d'œuvre. La durée vient de
                // l'item (BaseItem.RunTimeTicks) ou du MediaSource de session.
                // Le direct n'a ni l'un ni l'autre → Pct = null (Phase B).
                long runtimeTicks = item.RunTimeTicks
                    ?? e.MediaInfo?.RunTimeTicks
                    ?? e.MediaSource?.RunTimeTicks
                    ?? 0;
                double? pct = null;
                int? rtSec = null;
                if (runtimeTicks > 0)
                {
                    rtSec = (int)(runtimeTicks / TimeSpan.TicksPerSecond);
                    if (e.PlayedToCompletion)
                    {
                        pct = 1.0;
                    }
                    else if (e.PlaybackPositionTicks.HasValue)
                    {
                        pct = Math.Max(0.0, Math.Min(1.0,
                            (double)e.PlaybackPositionTicks.Value / runtimeTicks));
                    }
                }

                int durSec = e.PlaybackPositionTicks.HasValue
                    ? (int)Math.Max(0, e.PlaybackPositionTicks.Value / TimeSpan.TicksPerSecond)
                    : 0;

                var users = (e.Users ?? new System.Collections.Generic.List<User>())
                    .Where(u => u != null)
                    .ToList();

                if (users.Count == 0)
                {
                    // Session sans usager résolu (appareils certains) : entrée
                    // sans usager, utilisable pour les stats globales.
                    WriteEntry(item, null, date, durSec, pct, rtSec, src, channel, e);
                    return;
                }
                foreach (var user in users)
                    WriteEntry(item, user, date, durSec, pct, rtSec, src, channel, e);
            }
            catch (Exception ex)
            {
                _logger?.Debug("[LLM_AI] Télémétrie : traitement PlaybackStopped échoué : {0}", ex.Message);
            }
        }

        private void WriteEntry(BaseItem item, User user, DateTimeOffset date,
            int durSec, double? pct, int? rtSec, string src, string channel,
            PlaybackStopEventArgs e)
        {
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                DecisionStore.AppendPlayback(cfg, new PlaybackEntry
                {
                    ItemId = item.InternalId.ToString(),
                    User = user != null ? user.Id.ToString() : "",
                    Date = date,
                    DurSec = durSec,
                    Pct = pct,
                    RtSec = rtSec,
                    Src = src ?? "",
                    Channel = channel ?? "",
                    App = e.ClientName ?? e.Session?.Client ?? "",
                    Device = e.DeviceName ?? e.Session?.DeviceName ?? "",
                    Completed = e.PlayedToCompletion
                }, _logger);
            }
            catch (Exception ex)
            {
                _logger?.Debug("[LLM_AI] Télémétrie : écriture échouée : {0}", ex.Message);
            }
        }
    }
}