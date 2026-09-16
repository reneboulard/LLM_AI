using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Session;

namespace LLM_AI
{
    /// <summary>
    /// Outil <b>client_command</b> du chat externe (v1.13.21) : le LLM envoie
    /// une commande NON DESTRUCTIVE au client Emby actif de l'usager du
    /// chat — projection de fiche, lecture, volume. Construit UNIQUEMENT
    /// quand <c>toolUser</c> est présent (chemin chat externe ; le chat
    /// admin n'a pas ce tool — un administrateur passe par la page Emby).
    /// <list type="bullet">
    /// <item><b>Allowlist stricte</b> : toute commande hors liste est rejetée
    ///     fail-closed avant tout effet (le LLM ne peut rien demander
    ///     d'autre). Exclusions permanentes : <c>SendKey</c> (poids
    ///     arbitraire), <c>TakeScreenshot</c> (intimité), <c>Restart</c>/
    ///     <c>Shutdown</c>/<c>Identify</c> (serveur).</item>
    /// <item><b>v1.13.22 — contrôle du visionnement</b> : <c>playback_status</c>
    ///     (position, durée, pistes), <c>seek</c> (relatif signé ou absolu,
    ///     clampé côté serveur au programme en cours — canal
    ///     <c>PlaystateRequest.Seek</c> validé live, jamais <c>SendKey</c>) et
    ///     <c>set_subtitle_track</c>/<c>set_audio_track</c> (langue, « off » ou
    ///     index, résolus sur les <c>MediaStreams</c> de l'item en cours).
    ///     Toasts SÉLECTIFS : un toast court s'affiche à l'écran UNIQUEMENT
    ///     après une bascule de piste réussie — rien pour seek/pause/volume,
    ///     l'image et l'OSD natif du client parlent déjà.
    ///     <c>playback_status</c> inclut aussi l'id de la série et les
    ///     numéros S/E de l'épisode en cours (v1.13.25.1) : « prochain
    ///     épisode » = <c>play_item(series_id)</c> en un seul tool, sans
    ///     recherche intermédiaire.</item>
    /// <item><b>Session bornée</b> : seules les sessions DONT l'usager est
    ///     celui de la requête sont ciblées (même règle que <c>Show</>,
    ///     forme « plus récente gagne ») — un usager ne pilote jamais
    ///     l'appareil d'un autre.</item>
    /// <item><b>Parental</b> : <c>display_item</c> et <c>play_item</c>
    ///     passent la policy parentale de l'usager
    ///     (<see cref="PermissionGate.IsParentallyAllowed"/>) — fail-closed,
    ///     la raison n'est pas révélée.</item>
    /// <item><b>Feuille jouable (v1.13.25)</b> : un <c>PlayNow</c> sur un id
    ///     de série/saison est ignoré par le client Android TV —
    ///     <c>play_item</c> étend une série/saison en son prochain épisode
    ///     non visionné (<see cref="NextUpResolver.ResolvePlayingEpisode"/>,
    ///     next up + repli), et refuse tout autre conteneur (collection,
    ///     playlist, personne) avec une erreur explicite au lieu d'un no-op
    ///     silencieux que le LLM habillerait d'une erreur inventée.</item>
    /// <item><b>play_next (v1.13.26)</b> : « passe au suivant » en pleine
    ///     lecture — le next up d'Emby retourne l'épisode EN COURS non
    ///     terminé, cette commande lance donc le premier non visionné
    ///     STRICTEMENT APRÈS le courant
    ///     (<see cref="NextUpResolver.FirstUnwatchedAfter"/>), dédupliqué
    ///     par (saison, épisode). Après l'envoi, elle RELIT l'item que le
    ///     client joue réellement (il peut substituer le jumeau principal
    ///     du (saison, épisode) demandé) et l'annonce — bascule non
    ///     confirmée en ~6 s = erreur explicite. Sans marquer le courant
    ///     comme vu (aucune écriture surprise).</item>
    /// <item><b>Lecture seule du serveur</b> : aucune écriture métadonnées,
    ///     aucun tool d'action — une commande ne touche qu'un flux client.</item>
    /// </list>
    /// Ne lève jamais : erreur → JSON <c>{"error":"..."}</c>.
    /// </summary>
    public class ClientCommandTool : ILlmTool
    {
        private readonly ISessionManager _sessions;
        private readonly ILibraryManager _library;
        private readonly IServerApplicationHost _host;
        private readonly User _user;
        private readonly ILogger _logger;

        public ClientCommandTool(ISessionManager sessions, ILibraryManager library,
            IServerApplicationHost host, User user, ILogger logger)
        {
            _sessions = sessions;
            _library = library;
            _host = host;
            _user = user;
            _logger = logger;
        }

        public string Name => "client_command";

        public string Description =>
            "Envoie une commande non destructive au client Emby ACTIF de l'usager " +
            "(sa session à lui — jamais un autre appareil). Projection de fiche, " +
            "lecture/pause/arrêt, épisode suivant, volume, ET contrôle du " +
            "visionnement en cours : état de lecture (playback_status), saut de " +
            "temps (seek), bascule des pistes de sous-titres et d'audio. " +
            "Utilise-le quand l'usager demande d'afficher ou de contrôler ce qui " +
            "passe sur son écran.";

        public string ArgumentsSchema => @"{
  ""command"": ""playback_status | seek | set_subtitle_track | set_audio_track | display_item | play_item | play_next | go_home | pause | unpause | stop | set_volume | mute | unmute"",
  ""offset_seconds"": ""(seek) décalage signé en secondes, ex. 30 ou -10 (|x| ≤ 1800)"",
  ""position_seconds"": ""(seek) position absolue dans le programme (0 = début)"",
  ""track"": ""(set_subtitle_track / set_audio_track) off | langue fr/fre | numéro de piste renvoyé par playback_status"",
  ""item_id"": ""(display_item / play_item) identifiant de l'item (id renvoyé par les outils) — play_item accepte un film, un épisode OU une série (jouée depuis son prochain épisode non visionné)"",
  ""volume"": ""(set_volume) entier 0-100""
}";

        // Bornes du contrôle du visionnement (fail-closed).
        private const long TicksPerSecond = 10_000_000;
        private const int MaxSeekOffsetSeconds = 1800;

        // ------------------------------------------------------------------
        //  Exécution
        // ------------------------------------------------------------------

        public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            try
            {
                string command = (Str(args, "command") ?? "").Trim().ToLowerInvariant();
                switch (command)
                {
                    case "display_item":
                    case "play_item":
                        return await ItemCommandAsync(command, Str(args, "item_id"), ct)
                            .ConfigureAwait(false);
                    case "play_next":
                        return await PlayNextAsync(ct).ConfigureAwait(false);
                    case "playback_status":
                        return Status();
                    case "seek":
                        return await SeekAsync(args, ct).ConfigureAwait(false);
                    case "set_subtitle_track":
                        return await SetTrackAsync(args, subtitles: true, ct).ConfigureAwait(false);
                    case "set_audio_track":
                        return await SetTrackAsync(args, subtitles: false, ct).ConfigureAwait(false);
                    case "go_home":
                    case "set_volume":
                    case "mute":
                    case "unmute":
                        return await GeneralCommandAsync(command, args, ct).ConfigureAwait(false);
                    case "pause":
                    case "unpause":
                    case "stop":
                        return await PlaystateAsync(command, ct).ConfigureAwait(false);
                    default:
                        // Fail-closed : le LLM ne décide pas de ce qui est
                        // envoyable — seule l'allowlist ci-dessus l'est.
                        return Json(new { error = "commande inconnue ou non autorisée : « "
                            + command + " » (autorisées : playback_status, seek, "
                            + "set_subtitle_track, set_audio_track, display_item, "
                            + "play_item, play_next, go_home, pause, unpause, stop, "
                            + "set_volume, mute, unmute)." });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.ErrorException("[LLM_AI] [CHAT-EXT] client_command : échec : {0}", ex, ex.Message);
                return Json(new { error = "Commande non exécutée : " + ex.Message });
            }
        }

        // ------------------------------------------------------------------
        //  display_item / play_item — item résolu + policy parentale
        // ------------------------------------------------------------------

        private async Task<string> ItemCommandAsync(string command, string itemId, CancellationToken ct)
        {
            var item = ItemIdResolver.Resolve(_library, (itemId ?? "").Trim());
            if (item == null)
                return Json(new { error = "Item introuvable." });

            // Double porte parentale (identique à Show) : un item refusé
            // n'est ni projeté ni lancé, et la raison n'est pas révélée.
            if (PermissionGate.IsParentallyAllowed(_user, item) != PermissionGate.ParentalVerdict.Allowed)
            {
                _logger.Info("[LLM_AI] [CHAT-EXT] client_command {0} refusé (parental) — usager {1}.",
                    command, _user.Name);
                return Json(new { error = "Cet item n'est pas autorisé pour cet usager." });
            }

            var session = ResolveSession();
            if (session == null)
                return Json(new { error =
                    "Aucune session Emby active pour cet usager (ouvrir l'app Emby sur l'appareil)." });

            if (command == "display_item")
            {
                bool hasDisplayContent = (session.SupportedCommands ?? Array.Empty<string>())
                    .Any(c => string.Equals(c, "DisplayContent", StringComparison.OrdinalIgnoreCase));
                if (hasDisplayContent)
                {
                    await _sessions.SendGeneralCommand(null, session.Id, new GeneralCommand
                    {
                        Name = "DisplayContent",
                        Arguments = new Dictionary<string, string>
                            { { "ItemId", item.InternalId.ToString() } }
                    }, ct).ConfigureAwait(false);
                }
                else
                {
                    // Repli toast (client qui ne déclare pas DisplayContent).
                    await _sessions.SendMessageCommand(null, session.Id, new MessageCommand
                    {
                        Header = string.Empty,
                        Text = "🤖 " + (item.Name ?? "Fiche demandée"),
                        TimeoutMs = 8000,
                    }, ct).ConfigureAwait(false);
                    return Json(new { ok = true, command = "display_item_toast",
                        device = session.DeviceName, item = item.Name,
                        detail = "Le client n'expose pas DisplayContent — titre envoyé en notification." });
                }
                return Json(new { ok = true, command = "display_item",
                    device = session.DeviceName, item = item.Name });
            }

            // play_item — toujours une FEUILLE jouable (v1.13.25) :
            // un PlayNow portant un id de série/saison est ACCEPTÉ par le
            // serveur (204) mais IGNORÉ par le client Android TV (vérifié
            // 2026-09-16 sur BRAVIA, Emby for Android 3.5.55 : un id épisode
            // joue, un id série reste sans effet — et le LLM, croyant à un
            // échec, inventait alors une erreur client). Une série/saison
            // devient donc son prochain épisode non visionné de l'usager
            // (next up + repli — NextUpResolver, même résolution que les
            // playlists AI Tonight) ; tout autre conteneur non jouable
            // (collection, playlist, personne) est refusé explicitement au
            // lieu d'un no-op silencieux.
            var playTarget = item;
            if (item is MediaBrowser.Controller.Entities.TV.Series
                || item is MediaBrowser.Controller.Entities.TV.Season)
            {
                playTarget = NextUpResolver.ResolvePlayingEpisode(
                    _library, _host, _user, item, _logger,
                    "[CHAT-EXT] client_command play_item — ");
                if (playTarget == null)
                    return Json(new { error = "Aucun épisode non visionné à jouer pour « "
                        + (item.Name ?? "cette série") + " » (série déjà entièrement vue ?)." });
                _logger.Info("[LLM_AI] [CHAT-EXT] client_command play_item : série « {0} » → épisode « {1} » (id={2}).",
                    item.Name, playTarget.Name, playTarget.InternalId);
            }
            else if (item is Folder)
            {
                return Json(new { error = "Type non jouable directement ("
                    + item.GetType().Name
                    + ") — attendu : film, épisode, ou série (développée en son prochain épisode)." });
            }

            // L'épisode étendu passe la MÊME porte parentale que la série
            // (fail-closed, la raison n'est pas révélée).
            if (!ReferenceEquals(playTarget, item)
                && PermissionGate.IsParentallyAllowed(_user, playTarget) != PermissionGate.ParentalVerdict.Allowed)
            {
                _logger.Info("[LLM_AI] [CHAT-EXT] client_command {0} refusé (parental, épisode étendu) — usager {1}.",
                    command, _user.Name);
                return Json(new { error = "Cet item n'est pas autorisé pour cet usager." });
            }

            // play_item — démarrer la lecture maintenant (PlayNow).
            await _sessions.SendPlayCommand(null, session.Id, new PlayRequest
            {
                ItemIds = new[] { playTarget.InternalId },
                PlayCommand = PlayCommand.PlayNow
            }, ct).ConfigureAwait(false);
            return Json(new { ok = true, command = "play_item",
                device = session.DeviceName, item = playTarget.Name,
                series = ReferenceEquals(playTarget, item) ? null : item.Name });
        }

        // ------------------------------------------------------------------
        //  play_next — épisode suivant EXPLICITE de ce qui joue
        // ------------------------------------------------------------------

        /// <summary>
        /// Passe à l'épisode suivant (v1.13.26) en lançant par
        /// <c>PlayNow</c> le premier épisode non visionné STRICTEMENT APRÈS
        /// l'épisode en cours (<see cref="NextUpResolver.FirstUnwatchedAfter"/>,
        /// dédupliqué par (saison, épisode) — la bibliothèque peut compter
        /// deux épisodes DIFFÉRENTS sous le même (saison, épisode), et sans
        /// garde le « suivant » était le jumeau du courant, rejoué).
        /// <b>Confirmation terrain (anti-menteur)</b> : le client peut
        /// substituer l'item demandé par le jumeau principal de son
        /// (saison, épisode) (vérifié 2026-09-16 sur BRAVIA : id 121711
        /// demandé → 121713 joué) ; après l'envoi, la commande RELIT donc
        /// ce que le client joue réellement (~6 s) et annonce CET item —
        /// pas la prédiction du résolveur. Pas de bascule confirmée =
        /// erreur explicite (le LLM ne peut pas annoncer un succès
        /// fantôme). Ne MARQUE PAS le courant comme vu (aucune écriture
        /// surprise).
        /// </summary>
        private async Task<string> PlayNextAsync(CancellationToken ct)
        {
            var session = ResolveSession();
            if (session == null)
                return Json(NoSessionError());

            var np = session.NowPlayingItem;
            if (np == null)
                return Json(new { error = "Rien n'est en lecture — play_next "
                    + "lance l'épisode suivant de ce qui joue (utilise play_item "
                    + "pour démarrer une série)." });
            if (!string.Equals(np.Type, "Episode", StringComparison.OrdinalIgnoreCase))
                return Json(new { error = "L'item en lecture n'est pas un épisode "
                    + "(« " + np.Name + " ») — play_next ne s'applique qu'aux séries." });

            var current = ItemIdResolver.Resolve(_library, (np.Id ?? "").Trim());
            // Série : depuis l'entité épisode si résolue, sinon depuis le
            // SeriesId du DTO (les deux formes passent ItemIdResolver).
            var series = (current as MediaBrowser.Controller.Entities.TV.Episode)?.Series
                ?? ItemIdResolver.Resolve(_library, (np.SeriesId ?? "").Trim());
            if (series == null)
                return Json(new { error = "Série de l'épisode en cours introuvable — "
                    + "impossible de déterminer le suivant." });

            MediaBrowser.Controller.Library.IUserDataManager userData = null;
            try { userData = _host?.TryResolve<MediaBrowser.Controller.Library.IUserDataManager>(); }
            catch { }

            var next = NextUpResolver.FirstUnwatchedAfter(
                _library, userData, _user, series, current, _logger,
                "[CHAT-EXT] client_command play_next — ");
            if (next == null)
                return Json(new { error = "Aucun épisode non visionné après « "
                    + (current?.Name ?? np.Name) + " » (fin de saison ou série terminée ?)." });

            // Même porte parentale que play_item (fail-closed, la raison
            // n'est pas révélée).
            if (PermissionGate.IsParentallyAllowed(_user, next) != PermissionGate.ParentalVerdict.Allowed)
            {
                _logger.Info("[LLM_AI] [CHAT-EXT] client_command play_next refusé (parental) — usager {0}.",
                    _user.Name);
                return Json(new { error = "Cet item n'est pas autorisé pour cet usager." });
            }

            _logger.Info("[LLM_AI] [CHAT-EXT] client_command play_next : épisode « {0} » → « {1} » (id={2}).",
                current?.Name ?? np.Name, next.Name, next.InternalId);

            var fromId = (np.Id ?? "").Trim();
            var fromName = current?.Name ?? np.Name;

            await _sessions.SendPlayCommand(null, session.Id, new PlayRequest
            {
                ItemIds = new[] { next.InternalId },
                PlayCommand = PlayCommand.PlayNow
            }, ct).ConfigureAwait(false);

            // Confirmation terrain : relire l'item que le client joue
            // VRAIMENT (il peut substituer le jumeau principal du (S,E)
            // demandé) et annoncer celui-là. ~6 s max, 500 ms/sondage.
            string playedName = null, playedId = null;
            int? playedSeason = null, playedEpisode = null;
            for (int i = 0; i < 12; i++)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                var np2 = ResolveSession()?.NowPlayingItem;
                if (np2 == null) continue; // bascule en cours (arrêt/relance)
                var id2 = (np2.Id ?? "").Trim();
                if (!string.IsNullOrEmpty(id2) && !string.Equals(id2, fromId, StringComparison.OrdinalIgnoreCase))
                {
                    playedName = np2.Name; playedId = id2;
                    playedSeason = np2.ParentIndexNumber; playedEpisode = np2.IndexNumber;
                    break;
                }
            }

            if (playedName == null)
            {
                _logger.Info("[LLM_AI] [CHAT-EXT] client_command play_next : bascule NON confirmée par le client en 6 s (demandé « {0} », id={1}).",
                    next.Name, next.InternalId);
                return Json(new { error = "La commande a été envoyée mais le client n'a "
                    + "pas confirmé de bascule (l'épisode « " + fromName + " » joue "
                    + "toujours). N'annonce PAS le changement — vérifie "
                    + "playback_status ou l'écran." });
            }

            if (!string.Equals(playedId, next.InternalId.ToString(), StringComparison.OrdinalIgnoreCase))
                _logger.Info("[LLM_AI] [CHAT-EXT] client_command play_next : client a substitué l'item demandé (id={0}) — joue « {1} » (id={2}), jumeau du même (saison, épisode) dans la bibliothèque.",
                    next.InternalId, playedName, playedId);

            return Json(new { ok = true, command = "play_next",
                device = session.DeviceName, from = fromName, series = series.Name,
                item = playedName, season = playedSeason, episode = playedEpisode });
        }

        // ------------------------------------------------------------------
        //  go_home / set_volume / mute / unmute — GeneralCommand
        // ------------------------------------------------------------------

        private async Task<string> GeneralCommandAsync(string command, JsonElement args, CancellationToken ct)
        {
            var session = ResolveSession();
            if (session == null)
                return Json(new { error =
                    "Aucune session Emby active pour cet usager (ouvrir l'app Emby sur l'appareil)." });

            var cmd = new GeneralCommand();
            switch (command)
            {
                case "go_home":
                    cmd.Name = "GoHome";
                    break;
                case "set_volume":
                    if (!TryGetVolume(args, out int volume))
                        return Json(new { error = "Paramètre 'volume' requis (entier 0-100)." });
                    cmd.Name = "SetVolume";
                    cmd.Arguments = new Dictionary<string, string>
                        { { "Volume", volume.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
                    break;
                case "mute":
                    cmd.Name = "Mute";
                    break;
                default: // "unmute"
                    cmd.Name = "Unmute";
                    break;
            }

            await _sessions.SendGeneralCommand(null, session.Id, cmd, ct).ConfigureAwait(false);
            return Json(new { ok = true, command = command,
                device = session.DeviceName });
        }

        // ------------------------------------------------------------------
        //  pause / unpause / stop — PlaystateRequest
        // ------------------------------------------------------------------

        private async Task<string> PlaystateAsync(string command, CancellationToken ct)
        {
            var session = ResolveSession();
            if (session == null)
                return Json(new { error =
                    "Aucune session Emby active pour cet usager (ouvrir l'app Emby sur l'appareil)." });

            var state = command == "pause" ? PlaystateCommand.Pause
                : command == "unpause" ? PlaystateCommand.Unpause
                : PlaystateCommand.Stop;

            await _sessions.SendPlaystateCommand(null, session.Id,
                new PlaystateRequest { Command = state }, ct).ConfigureAwait(false);
            return Json(new { ok = true, command = command,
                device = session.DeviceName,
                now_playing = session.NowPlayingItem?.Name });
        }

        // ------------------------------------------------------------------
        //  playback_status — état de la lecture en cours (lecture seule)
        // ------------------------------------------------------------------

        private string Status()
        {
            var session = ResolveSession();
            if (session == null)
                return Json(NoSessionError());

            var np = session.NowPlayingItem;
            if (np == null)
            {
                // Lecture seule légitime : l'usager peut demander
                // « est-ce que ça joue ? » — pas une erreur.
                return Json(new { ok = true, playing = false,
                    device = session.DeviceName,
                    note = "Aucune lecture en cours sur cet appareil." });
            }

            var ps = session.PlayState;
            long pos = ps?.PositionTicks ?? 0;
            long runtime = np.RunTimeTicks ?? 0;
            var tracks = new List<object>();
            foreach (var m in np.MediaStreams ?? Array.Empty<MediaBrowser.Model.Entities.MediaStream>())
            {
                if (m.Type != MediaBrowser.Model.Entities.MediaStreamType.Audio
                    && m.Type != MediaBrowser.Model.Entities.MediaStreamType.Subtitle)
                    continue;
                tracks.Add(new {
                    type = m.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio ? "audio" : "subtitle",
                    index = m.Index, lang = m.Language, title = m.Title,
                    external = m.IsExternal });
            }

            _logger.Info("[LLM_AI] [CHAT-EXT] client_command playback_status — usager {0}, appareil {1}, item {2}, position {3} s.",
                _user.Name, session.DeviceName, np.Name, pos / TicksPerSecond);
            return Json(new {
                ok = true, playing = true,
                device = session.DeviceName, client = session.Client,
                item = new { id = np.Id, name = np.Name, type = np.Type,
                    series = np.SeriesName,
                    // Contexte de navigation (v1.13.25.1) : l'id de la série
                    // et les numéros S/E permettent au LLM d'enchaîner
                    // DIRECTEMENT play_item(series_id) — « prochain épisode »
                    // — sans le find intermédiaire qui faisait dériver les
                    // petits modèles (id recopié/déformé).
                    series_id = np.SeriesId,
                    season = np.ParentIndexNumber, episode = np.IndexNumber },
                position_seconds = pos / TicksPerSecond,
                runtime_seconds = runtime / TicksPerSecond,
                remaining_seconds = runtime > pos ? (runtime - pos) / TicksPerSecond : 0,
                paused = ps?.IsPaused ?? false,
                can_seek = ps?.CanSeek ?? false,
                play_method = ps?.PlayMethod,
                audio_track = ps?.AudioStreamIndex,
                subtitle_track = ps?.SubtitleStreamIndex,
                tracks = tracks });
        }

        // ------------------------------------------------------------------
        //  seek — saut de temps, cible calculée côté serveur (jamais SendKey)
        // ------------------------------------------------------------------

        private async Task<string> SeekAsync(JsonElement args, CancellationToken ct)
        {
            long? offsetSec = TryGetLong(args, "offset_seconds");
            long? absSec = TryGetLong(args, "position_seconds");
            if (offsetSec == null && absSec == null)
                return Json(new { error = "Paramètre requis : offset_seconds (relatif, signé) ou position_seconds (absolu)." });
            if (offsetSec != null && Math.Abs(offsetSec.Value) > MaxSeekOffsetSeconds)
                return Json(new { error = "offset_seconds est limité à ±" + MaxSeekOffsetSeconds + " secondes." });

            var session = ResolveSession();
            if (session == null)
                return Json(NoSessionError());
            var np = session.NowPlayingItem;
            if (np == null)
                return Json(new { error = "Aucune lecture en cours sur cet appareil." });

            var ps = session.PlayState;
            if (ps == null || !ps.CanSeek)
                return Json(new { error = "Le flux en cours ne permet pas de saut de temps." });

            // Clamp au programme en cours (runtime inconnu → relatif toléré
            // sans borne haute ; absolu non clampé, le client tranche).
            long pos = ps.PositionTicks ?? 0;
            long runtime = np.RunTimeTicks ?? 0;
            long target = absSec != null
                ? absSec.Value * TicksPerSecond
                : pos + offsetSec.Value * TicksPerSecond;
            target = Math.Max(0, target);
            if (runtime > 0) target = Math.Min(target, runtime);

            await _sessions.SendPlaystateCommand(null, session.Id,
                new PlaystateRequest { Command = PlaystateCommand.Seek,
                    SeekPositionTicks = target }, ct).ConfigureAwait(false);

            _logger.Info("[LLM_AI] [CHAT-EXT] client_command seek {0} -> position {1} s — usager {2}.",
                absSec != null ? "absolu" : ("relatif " + offsetSec + " s"),
                target / TicksPerSecond, _user.Name);
            // PAS de toast ni de relecture : l'image est le feedback, et la
            // position remonte avec quelques secondes de retard (gotcha).
            return Json(new { ok = true, command = "seek",
                device = session.DeviceName, item = np.Name,
                new_position_seconds = target / TicksPerSecond });
        }

        // ------------------------------------------------------------------
        //  set_subtitle_track / set_audio_track — langue, « off » ou index
        // ------------------------------------------------------------------

        private async Task<string> SetTrackAsync(JsonElement args, bool subtitles, CancellationToken ct)
        {
            string track = (Str(args, "track") ?? "").Trim();
            var session = ResolveSession();
            if (session == null)
                return Json(NoSessionError());
            var np = session.NowPlayingItem;
            if (np == null)
                return Json(new { error = "Aucune lecture en cours sur cet appareil." });

            var wantedType = subtitles
                ? MediaBrowser.Model.Entities.MediaStreamType.Subtitle
                : MediaBrowser.Model.Entities.MediaStreamType.Audio;
            var streams = (np.MediaStreams ?? Array.Empty<MediaBrowser.Model.Entities.MediaStream>())
                .Where(s => s.Type == wantedType).ToArray();

            int targetIndex;
            string lang = null;
            if (string.Equals(track, "off", StringComparison.OrdinalIgnoreCase))
            {
                // « off » = désactiver les sous-titres (Index -1, validé live) —
                // l'audio en cours est sélectionné, jamais coupé.
                if (!subtitles)
                    return Json(new { error = "« off » ne s'applique qu'aux sous-titres." });
                targetIndex = -1;
            }
            else if (int.TryParse(track, out int idx))
            {
                var match = streams.FirstOrDefault(s => s.Index == idx);
                if (match == null)
                    return Json(new { error = TrackError(streams, subtitles,
                        "la piste n° " + idx + " est introuvable ou n'est pas du bon type") });
                targetIndex = idx;
                lang = match.Language;
            }
            else if (track.Length > 0)
            {
                // Langue 2 ou 3 lettres (« fr » comme « fra »). Plusieurs
                // correspondances (incrustée vs externe) : la première gagne,
                // renvoyée dans la réponse.
                string code = track.ToLowerInvariant();
                var match = streams.FirstOrDefault(s => MatchLang(s.Language, code));
                if (match == null)
                    return Json(new { error = TrackError(streams, subtitles,
                        "aucune piste « " + track + " »") });
                targetIndex = match.Index;
                lang = code;
            }
            else
                return Json(new { error = "Paramètre 'track' requis (off | langue fr/fre | numéro de piste)." });

            var cmd = new GeneralCommand
            {
                Name = subtitles ? "SetSubtitleStreamIndex" : "SetAudioStreamIndex",
                Arguments = new Dictionary<string, string>
                    { { "Index", targetIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) } }
            };
            await _sessions.SendGeneralCommand(null, session.Id, cmd, ct).ConfigureAwait(false);

            // Toast sélectif : UNIQUEMENT pour les bascules de piste (après
            // succès) — court, 3-4 s, silencieux si le client ne déclare pas
            // DisplayMessage.
            string label = subtitles
                ? (targetIndex < 0 ? "Sous-titres coupés"
                    : "Sous-titres " + ShortLang(lang, targetIndex))
                : "Audio " + ShortLang(lang, targetIndex);
            await ToastAsync(session, "🤖 " + label, ct).ConfigureAwait(false);

            _logger.Info("[LLM_AI] [CHAT-EXT] client_command {0} -> index {1} ({2}) — usager {3}.",
                subtitles ? "set_subtitle_track" : "set_audio_track",
                targetIndex, lang ?? "off", _user.Name);
            return Json(new { ok = true,
                command = subtitles ? "set_subtitle_track" : "set_audio_track",
                device = session.DeviceName, item = np.Name,
                track_index = targetIndex, lang = lang });
        }

        private static bool MatchLang(string streamLang, string code)
        {
            if (string.IsNullOrWhiteSpace(streamLang)) return false;
            string l = streamLang.Trim().ToLowerInvariant();
            return code.Length == 2 ? l.Length >= 2 && l.Substring(0, 2) == code : l == code;
        }

        private static string ShortLang(string lang, int index)
        {
            if (string.IsNullOrWhiteSpace(lang)) return "n° " + index;
            lang = lang.Trim();
            return lang.Length <= 2 ? lang.ToUpperInvariant() : lang.Substring(0, 2).ToUpperInvariant();
        }

        private static string TrackError(IEnumerable<MediaBrowser.Model.Entities.MediaStream> streams,
            bool subtitles, string reason)
        {
            var avail = streams.Select(s => "n° " + s.Index + " ("
                + (s.Language ?? "sans langue") + (s.IsExternal ? ", externe" : "") + ")");
            return (subtitles ? "Sous-titres" : "Pistes audio") + " disponibles : "
                + string.Join(", ", avail) + " — " + reason + ".";
        }

        /// <summary>Toast cosmétique à l'écran du client : jamais bloquant
        /// (exception avalée), jamais envoyé si le client ne déclare pas
        /// <c>DisplayMessage</c>.</summary>
        private async System.Threading.Tasks.Task ToastAsync(SessionInfo session,
            string text, CancellationToken ct)
        {
            try
            {
                bool hasDisplayMessage = (session.SupportedCommands ?? Array.Empty<string>())
                    .Any(c => string.Equals(c, "DisplayMessage", StringComparison.OrdinalIgnoreCase));
                if (!hasDisplayMessage) return;
                await _sessions.SendMessageCommand(null, session.Id, new MessageCommand
                {
                    Header = string.Empty,
                    Text = text,
                    TimeoutMs = 3500,
                }, ct).ConfigureAwait(false);
            }
            catch { /* cosmétique — un échec de toast ne doit rien casser */ }
        }

        // ------------------------------------------------------------------
        //  Session bornée (même règle que Show : l'usager ne pilote que
        //  SES sessions ; plus récente LastActivityDate d'abord)
        // ------------------------------------------------------------------

        private SessionInfo ResolveSession()
        {
            return (_sessions.Sessions ?? Enumerable.Empty<SessionInfo>())
                .Where(s => s != null && SessionUserMatches(s, _user))
                .OrderByDescending(s => s.LastActivityDate)
                .FirstOrDefault();
        }

        private static bool SessionUserMatches(SessionInfo s, User user)
        {
            var sid = s.UserId;
            if (string.IsNullOrWhiteSpace(sid)) return false;
            return string.Equals(sid, user.Id.ToString("N"), StringComparison.OrdinalIgnoreCase)
                || string.Equals(sid, user.Id.ToString(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(sid, user.InternalId.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        //  Lectures tolérantes + sérialisation
        // ------------------------------------------------------------------

        private static string NoSessionError()
        {
            return "Aucune session Emby active pour cet usager (ouvrir l'app Emby sur l'appareil).";
        }

        private static long? TryGetLong(JsonElement args, string name)
        {
            try
            {
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
                {
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l))
                        return l;
                    if (v.ValueKind == JsonValueKind.String
                        && long.TryParse(v.GetString(), out long parsed))
                        return parsed;
                }
            }
            catch { }
            return null;
        }

        private static string Str(JsonElement args, string name)
        {
            try
            {
                if (args.ValueKind == JsonValueKind.Object
                    && args.TryGetProperty(name, out var v)
                    && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            }
            catch { }
            return null;
        }

        private static bool TryGetVolume(JsonElement args, out int volume)
        {
            volume = 0;
            try
            {
                if (args.ValueKind == JsonValueKind.Object
                    && args.TryGetProperty("volume", out var v))
                {
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out volume))
                        return volume >= 0 && volume <= 100;
                    if (v.ValueKind == JsonValueKind.String
                        && int.TryParse(v.GetString(), out volume))
                        return volume >= 0 && volume <= 100;
                }
            }
            catch { }
            return false;
        }

        private static string Json(object o)
        {
            return System.Text.Json.JsonSerializer.Serialize(o,
                new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
    }
}