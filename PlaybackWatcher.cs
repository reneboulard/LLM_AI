using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
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
    /// <para>v2.14.0 — profil enrichi, tout FIGÉ au moment de la lecture
    /// (l'item DVR disparaît souvent avant l'analyse hebdo) : genres
    /// NORMALISÉS (<see cref="GenreCleanerMap"/> — même vocabulaire que le
    /// profil de goût et les surfaces epg_*), classification NORMALISÉE
    /// (<see cref="ClassificationMap"/>), année, type, position dans la
    /// série (id + saison + épisode), début de lecture (capté sur
    /// <see cref="ISessionManager.PlaybackStart"/>), méthode de lecture,
    /// langues audio/sous-titres, ids de session.</para>
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

        // v2.14.0 — début de lecture par (playSessionId|itemId) : posé sur
        // PlaybackStart, consommé (retiré) sur PlaybackStopped. Une clé par
        // ITEM : une même session de lecture peut enchaîner une file.
        // Anti-fuite : purgé des entrées > 12 h au-delà de 256 clés (start
        // sans stop : client mort, abandon silencieux…).
        private readonly ConcurrentDictionary<string, DateTimeOffset> _starts =
            new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        private const int StartMapPruneAt = 256;
        private static readonly TimeSpan StartMapTtl = TimeSpan.FromHours(12);

        public PlaybackWatcher(ISessionManager sessions, ILibraryManager library, ILogger logger)
        {
            _sessions = sessions;
            _library = library;
            _logger = logger;
        }

        public void Run()
        {
            _sessions.PlaybackStart += OnPlaybackStart;
            _sessions.PlaybackStopped += OnPlaybackStopped;
            if (Plugin.Instance?.Configuration?.PlaybackTelemetryEnabled ?? false)
                _logger?.Info("[LLM_AI] Télémétrie de lecture active (playback.json).");
        }

        public void Dispose()
        {
            _sessions.PlaybackStart -= OnPlaybackStart;
            _sessions.PlaybackStopped -= OnPlaybackStopped;
        }

        /// <summary>Clé d'appariement start/stop : playSessionId + item —
        /// les DEUX événements la portent ; repli sessionId si le
        /// playSessionId manque.</summary>
        private static string StartKey(PlaybackProgressEventArgs e)
        {
            if (e?.Item == null) return "";
            string ps = e.PlaySessionId;
            if (string.IsNullOrEmpty(ps)) ps = "n:" + (e.Session?.Id ?? "");
            return ps + "|" + e.Item.InternalId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            try
            {
                if (e?.Item == null) return;
                var cfg = Plugin.Instance?.Configuration;
                if (cfg == null || !cfg.PlaybackTelemetryEnabled) return;
                var key = StartKey(e);
                if (key.Length == 0) return;
                _starts[key] = DateTimeOffset.UtcNow;
                if (_starts.Count > StartMapPruneAt) PruneStarts();
            }
            catch { /* best-effort : jamais d'impact sur la lecture */ }
        }

        private void PruneStarts()
        {
            var cutoff = DateTimeOffset.UtcNow - StartMapTtl;
            foreach (var kv in _starts)
                if (kv.Value < cutoff) _starts.TryRemove(kv.Key, out _);
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

                // Début de lecture (v2.14.0) : consommé et retiré de la map.
                DateTimeOffset? start = null;
                var skey = StartKey(e);
                if (skey.Length > 0 && _starts.TryRemove(skey, out var s0))
                    start = s0;

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

                // ---------------------------------------------------------
                // v2.14.0 — profil enrichi, calculé UNE fois par session
                // (identique pour tous les usagers de la session), tout
                // FIGÉ au moment du stop : l'item peut disparaître avant
                // l'analyse hebdo (DVR éphémère du foyer).
                // ---------------------------------------------------------
                // Contexte séries/films pour GenreCleaner : les tables
                // diffèrent (« Kids »→« Enfant » séries vs « Children »→
                // « Familial » films — patron LibSeriesCtx de
                // GetEmbyInfoTool, non accessible ici : réplique locale).
                bool? seriesCtx =
                    item is Episode || item is Series ? true
                    : item is MediaBrowser.Controller.Entities.Movies.Movie ? false
                    : item is LiveTvProgram lp && !string.IsNullOrEmpty(lp.SeriesName) ? true
                    : (bool?)null;

                // Genres NORMALISÉS (vocabulaire curaté — même alignement
                // que le profil de goût et les surfaces epg_*) et
                // classification NORMALISÉE (« PG-13 »/« 13+ » → « CA-14A »)
                // : sans normalisation, les stats de consommation
                // mélangeraient deux vocabulaires et le LLM verrait
                // « Comedy » ici contre « Comédie » dans le profil.
                var genres = GenreCleanerMap.MapGenres(item.Genres, seriesCtx);
                string rating = ClassificationMap.Normalize(item.OfficialRating);

                long sid = item.SeriesId; // 0 = pas une série
                int? season = item.ParentIndexNumber;
                int? epNum = item.IndexNumber;

                // État de lecture de la session (méthode, langues) : dernier
                // état connu. PlaySession d'abord (état de CETTE lecture),
                // repli Session (appareils qui ne multiplient pas les play
                // sessions). Null-safe de bout en bout (best-effort).
                var playState = e.PlaySession?.PlayState ?? e.Session?.PlayState;
                string playMethod = null;
                if (playState != null && playState.PlayMethod.HasValue)
                    playMethod = playState.PlayMethod.Value.ToString();

                // Pistes de la session : MediaSource (in-process) puis
                // MediaInfo (DTO — un tableau, pas une liste). Les deux
                // formes existent selon le chemin qui a peuplé l'événement.
                System.Collections.Generic.IEnumerable<MediaStream> SessionStreams()
                {
                    if (e.MediaSource?.MediaStreams != null)
                        foreach (var s in e.MediaSource.MediaStreams) yield return s;
                    if (e.MediaInfo?.MediaStreams != null)
                        foreach (var s in e.MediaInfo.MediaStreams) yield return s;
                }
                string audio = null, subtitle = null;
                if (playState != null)
                {
                    var ai = playState.AudioStreamIndex;
                    audio = ai.HasValue && ai.Value >= 0
                        ? StreamLabel(SessionStreams(), ai.Value, MediaStreamType.Audio) ?? ("piste " + ai.Value)
                        : null;
                    var si = playState.SubtitleStreamIndex;
                    // null vs vide : si l'indice existe, l'état est CONNU —
                    // une piste non résolue dans MediaStreams (sous-titres
                    // externes : indice 100+) reste comptée comme active.
                    if (si.HasValue && si.Value >= 0)
                        subtitle = StreamLabel(SessionStreams(), si.Value, MediaStreamType.Subtitle) ?? ("piste " + si.Value);
                    else if (si.HasValue)
                        subtitle = ""; // état remonté, aucun sous-titre actif
                }

                var extras = new PlaybackExtras
                {
                    StartDate = start,
                    Genres = genres,
                    Rating = rating,
                    Year = item.ProductionYear,
                    Type = item.GetType().Name,
                    SeriesId = sid != 0 ? sid : (long?)null,
                    Season = season,
                    Episode = epNum,
                    PlayMethod = playMethod,
                    Audio = audio,
                    Subtitle = subtitle,
                    SessionId = e.Session?.Id ?? "",
                    PlaySessionId = e.PlaySessionId ?? ""
                };

                var users = (e.Users ?? new System.Collections.Generic.List<User>())
                    .Where(u => u != null)
                    .ToList();

                if (users.Count == 0)
                {
                    // Session sans usager résolu (appareils certains) : entrée
                    // sans usager, utilisable pour les stats globales.
                    WriteEntry(item, null, date, durSec, pct, rtSec, src, channel, e, extras);
                    return;
                }
                foreach (var user in users)
                    WriteEntry(item, user, date, durSec, pct, rtSec, src, channel, e, extras);
            }
            catch (Exception ex)
            {
                _logger?.Debug("[LLM_AI] Télémétrie : traitement PlaybackStopped échoué : {0}", ex.Message);
            }
        }

        /// <summary>Champs enrichis d'une session de lecture (v2.14.0),
        /// calculés une fois par stop dans <see cref="OnPlaybackStopped"/>
        /// et recopiés dans chaque entrée (usager).</summary>
        private sealed class PlaybackExtras
        {
            public DateTimeOffset? StartDate;
            public string[] Genres;
            public string Rating;
            public int? Year;
            public string Type;
            public long? SeriesId;
            public int? Season;
            public int? Episode;
            public string PlayMethod;
            public string Audio;
            public string Subtitle;
            public string SessionId;
            public string PlaySessionId;
        }

        /// <summary>Résout le label d'une piste (audio/sous-titres) par son
        /// indice dans les MediaStreams de la session : DisplayTitle (ex.
        /// « FRA - Stéréo », « Und(eng) »), sinon Title, sinon Language —
        /// le signal VF/VO du foyer, lisible par le LLM sans id de piste.
        /// Null si la piste n'est pas résoluble.</summary>
        private static string StreamLabel(System.Collections.Generic.IEnumerable<MediaStream> streams,
            int index, MediaStreamType type)
        {
            if (streams == null) return null;
            foreach (var s in streams)
            {
                if (s == null || s.Index != index || s.Type != type) continue;
                var label = s.DisplayTitle ?? s.Title;
                if (string.IsNullOrWhiteSpace(label)) label = s.Language;
                return string.IsNullOrWhiteSpace(label) ? null : label.Trim();
            }
            return null;
        }

        private void WriteEntry(BaseItem item, User user, DateTimeOffset date,
            int durSec, double? pct, int? rtSec, string src, string channel,
            PlaybackStopEventArgs e, PlaybackExtras extras)
        {
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                // v2.7.2 : titre + série FIGÉS dans l'entrée. L'id seul ne
                // peut pas rejoindre les décisions quand l'usager regarde
                // l'ENREGISTREMENT d'une reco live/record (item distinct du
                // programme EPG pointé, souvent déplacé hors du dossier DVR
                // avant l'analyse hebdo) ; le couple (titre, série) porte la
                // jointure de repli de MemoryTask. Une carte .strm porte le
                // titre de la reco — son activation devient rattachable.
                string frozenSeries = null;
                if (item is Episode ep) frozenSeries = ep.SeriesName;
                else if (item is LiveTvProgram ltp) frozenSeries = ltp.SeriesName;
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
                    Completed = e.PlayedToCompletion,
                    Title = item.Name ?? "",
                    SeriesName = frozenSeries ?? "",
                    // v2.14.0 — profil enrichi figé (cf. PlaybackExtras).
                    StartDate = extras?.StartDate,
                    Genres = extras?.Genres,
                    Rating = extras?.Rating,
                    Year = extras?.Year,
                    Type = extras?.Type,
                    SeriesId = extras?.SeriesId,
                    Season = extras?.Season,
                    Episode = extras?.Episode,
                    PlayMethod = extras?.PlayMethod,
                    Audio = extras?.Audio,
                    Subtitle = extras?.Subtitle,
                    SessionId = extras?.SessionId ?? "",
                    PlaySessionId = extras?.PlaySessionId ?? ""
                }, _logger);
            }
            catch (Exception ex)
            {
                _logger?.Debug("[LLM_AI] Télémétrie : écriture échouée : {0}", ex.Message);
            }
        }
    }
}