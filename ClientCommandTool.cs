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
    ///     <c>Shutdown</c>/<c>Identify</c> (serveur), <c>Seek</c>.</item>
    /// <item><b>Session bornée</b> : seules les sessions DONT l'usager est
    ///     celui de la requête sont ciblées (même règle que <c>Show</>,
    ///     forme « plus récente gagne ») — un usager ne pilote jamais
    ///     l'appareil d'un autre.</item>
    /// <item><b>Parental</b> : <c>display_item</c> et <c>play_item</c>
    ///     passent la policy parentale de l'usager
    ///     (<see cref="PermissionGate.IsParentallyAllowed"/>) — fail-closed,
    ///     la raison n'est pas révélée.</item>
    /// <item><b>Lecture seule du serveur</b> : aucune écriture métadonnées,
    ///     aucun tool d'action — une commande ne touche qu'un flux client.</item>
    /// </list>
    /// Ne lève jamais : erreur → JSON <c>{"error":"..."}</c>.
    /// </summary>
    public class ClientCommandTool : ILlmTool
    {
        private readonly ISessionManager _sessions;
        private readonly ILibraryManager _library;
        private readonly User _user;
        private readonly ILogger _logger;

        public ClientCommandTool(ISessionManager sessions, ILibraryManager library,
            User user, ILogger logger)
        {
            _sessions = sessions;
            _library = library;
            _user = user;
            _logger = logger;
        }

        public string Name => "client_command";

        public string Description =>
            "Envoie une commande non destructive au client Emby ACTIF de l'usager " +
            "(sa session à lui — jamais un autre appareil). Projection de fiche, " +
            "lecture/pause/arrêt, volume. Utilise-le quand l'usager demande " +
            "d'afficher ou de contrôler ce qui passe sur son écran.";

        public string ArgumentsSchema => @"{
  ""command"": ""display_item | play_item | go_home | pause | unpause | stop | set_volume | mute | unmute"",
  ""item_id"": ""(display_item / play_item) identifiant de l'item (id renvoyé par les outils)"",
  ""volume"": ""(set_volume) entier 0-100""
}";

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
                            + command + " » (autorisées : display_item, play_item, "
                            + "go_home, pause, unpause, stop, set_volume, mute, unmute)." });
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

            // play_item — démarrer la lecture maintenant (PlayNow).
            await _sessions.SendPlayCommand(null, session.Id, new PlayRequest
            {
                ItemIds = new[] { item.InternalId },
                PlayCommand = PlayCommand.PlayNow
            }, ct).ConfigureAwait(false);
            return Json(new { ok = true, command = "play_item",
                device = session.DeviceName, item = item.Name });
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