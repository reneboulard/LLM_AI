using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;

namespace LLM_AI
{
    /// <summary>
    /// Outil <b>record_program</b> du chat externe (v1.13.23, human-in-the-loop
    /// à PIN) : l'usager demande un enregistrement, le LLM appelle l'action
    /// <c>record</c> — qui ne crée RIEN mais dépose une réservation dans le
    /// bucket serveur (<see cref="RecordingPendingStore"/>) avec un code à
    /// 4 chiffres généré côté serveur ; quand l'usager fournit ce code dans
    /// son message, l'agent appelle <c>confirm</c> et le timer est créé
    /// (<see cref="AutoProgrammer.ProgramOneAsync"/>, gardes incluses).
    /// <list type="bullet">
    /// <item><b>Le code n'est JAMAIS visible du LLM</b> : l'action
    ///     <c>record</c> répond sans le code ; l'endpoint le lit dans le
    ///     bucket et le joint au DTO de réponse (canal direct vers l'écran
    ///     de l'app compagnon). Un modèle ne peut pas se confirmer
    ///     lui-même.</item>
    /// <item><b>Droits vérifiés à chaque appel</b> (défense en profondeur) :
    ///     opt-in <c>ExternalChatRecordingsEnabled</c> + liste
    ///     <c>ExternalChatRecordingUsers</c> + droit natif
    ///     <see cref="PermissionGate.CanRecordLive"/> — fail-closed.</item>
    /// <item><b>Parental</b> : le programme EPG passe
    ///     <see cref="PermissionGate.IsEpgAllowed"/> — un refus ne révèle
    ///     rien de la raison.</item>
    /// <item><b>Verrou 3 essais</b> + quota/jour compté à l'effet seulement
    ///     (réservé avant création, remboursé si le timer n'est pas créé)
    ///     — voir <see cref="RecordingPendingStore"/>.</item>
    /// </list>
    /// Construit UNIQUEMENT quand l'usager porte les trois portes (le chat
    /// externe sans ce tool reste lecture seule). Ne lève jamais : erreur →
    /// JSON <c>{"error":"..."}</c>.
    /// </summary>
    public class RecordingChatTool : ILlmTool
    {
        private const int MaxPostPaddingMinutes = 30;

        private readonly PluginConfiguration _cfg;
        private readonly User _user;
        private readonly ILiveTvManager _liveTv;
        private readonly ILibraryManager _library;
        private readonly IServerApplicationHost _host;
        private readonly ISessionManager _sessions;
        private readonly ILogger _logger;

        public RecordingChatTool(PluginConfiguration cfg, User user, ILiveTvManager liveTv,
            ILibraryManager library, IServerApplicationHost host, ISessionManager sessions,
            ILogger logger)
        {
            _cfg = cfg; _user = user; _liveTv = liveTv; _library = library;
            _host = host; _sessions = sessions; _logger = logger;
        }

        public string Name => "record_program";

        public string Description =>
            "Programme l'enregistrement d'un programme TV en direct (DVR), avec " +
            "CONFIRMATION OBLIGATOIRE à deux phases : record dépose une réservation " +
            "et le serveur affiche un code à 4 chiffres à l'écran de l'app — tu ne " +
            "vois JAMAIS ce code. Tu demandes ensuite à l'usager de le saisir, puis " +
            "confirm crée l'enregistrement. Ne devine, n'invente et ne réinterprète " +
            "jamais un code. Résous d'abord le programme avec find/epg (source=epg). " +
            "Pour TOUTE question sur l'état de la réservation, appelle d'abord " +
            "status (lecture seule, ne révèle JAMAIS le code) — ne devine jamais " +
            "l'état du bucket d'après tes propres bulles.";

        public string ArgumentsSchema => @"{
  ""action"": ""record | confirm | status"",
  ""program_id"": ""(record) id du programme EPG (renvoyé par find/epg)"",
  ""title"": ""(record) titre du programme (pour le libellé)"",
  ""kind"": ""(record) movie | series (défaut movie)"",
  ""post_padding_minutes"": ""(record) marge de fin demandée par l'usager (plafonnée à 30)"",
  ""pin"": ""(confirm) code à 4 chiffres fourni PAR l'usager — jamais deviné. status ne prend aucun autre paramètre""
}";

        // ------------------------------------------------------------------
        //  Exécution
        // ------------------------------------------------------------------

        public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            try
            {
                // Portes cumulatives re-vérifiées à CHAQUE appel (défense en
                // profondeur — la construction du tool en amont n'est pas un
                // garde-fou, elle en est le premier anneau).
                if (_cfg == null || !_cfg.ExternalChatRecordingsEnabled)
                    return Json(new { error = "L'enregistrement via le chat externe n'est pas activé." });
                if (!UserListed(_cfg.ExternalChatRecordingUsers, _user.Name))
                    return Json(new { error = "Cet usager n'est pas autorisé à programmer des enregistrements." });
                if (!PermissionGate.CanRecordLive(_user))
                    return Json(new { error = "Cet usager n'a pas le droit d'enregistrer la TV en direct (policy Emby)." });

                string action = (Str(args, "action") ?? "").Trim().ToLowerInvariant();
                switch (action)
                {
                    case "record":
                        return await RecordAsync(args, ct).ConfigureAwait(false);
                    case "confirm":
                        string pinArg = Str(args, "pin") ?? TryGetPinNumber(args);
                        if (string.IsNullOrWhiteSpace(pinArg))
                            return Json(new { error = "Paramètre 'pin' requis (code à 4 chiffres fourni par l'usager)." });
                        return await ConfirmCoreAsync(pinArg.Trim(), ct).ConfigureAwait(false);
                    case "status":
                        return StatusAction();
                    default:
                        return Json(new { error = "Paramètre 'action' requis (record | confirm | status)." });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.ErrorException("[LLM_AI] [CHAT-EXT] record_program : échec : {0}", ex, ex.Message);
                return Json(new { error = "Demande non traitée : " + ex.Message });
            }
        }

        // ------------------------------------------------------------------
        //  record — réservation dans le bucket, AUCUN effet serveur
        // ------------------------------------------------------------------

        private async Task<string> RecordAsync(JsonElement args, CancellationToken ct)
        {
            string programId = (Str(args, "program_id") ?? "").Trim();
            string title = (Str(args, "title") ?? "").Trim();
            string kind = (Str(args, "kind") ?? "movie").Trim().ToLowerInvariant();
            if (programId.Length == 0 || title.Length == 0)
                return Json(new { error = "program_id et title sont requis (résous le programme avec find/epg d'abord)." });
            if (!string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(kind, "movie", StringComparison.OrdinalIgnoreCase))
                return Json(new { error = "kind attendu : movie | series." });
            int postPadding = TryGetInt(args, "post_padding_minutes");
            if (postPadding < 0) postPadding = 0;
            if (postPadding > MaxPostPaddingMinutes) postPadding = MaxPostPaddingMinutes;

            // Le programme est résolu POUR VALIDATION (existence + parental) ;
            // l'id conservé dans le pending reste la FORME D'ORIGINE projetée
            // par find-epg — AutoProgrammer (dedup + création) la consomme
            // telle quelle, comme sur le chemin chat admin.
            var program = ItemIdResolver.Resolve(_library, programId);
            if (program == null)
                return Json(new { error = "Programme introuvable (id invalide ou programme déjà diffusé) — résous-le de nouveau avec find/epg." });

            // Porte parentale EPG : fail-closed, la raison n'est pas révélée
            // (discipline PermissionGate : ne rien dévoiler, ne rien dire du
            // contenu refusé).
            bool unrecognized;
            var loc = _host?.TryResolve<MediaBrowser.Model.Globalization.ILocalizationManager>();
            var programTags = program.Tags ?? program.Genres ?? new string[0];
            if (PermissionGate.IsEpgAllowed(_user, loc, program.OfficialRating,
                    programTags.AsEnumerable(), out unrecognized)
                != PermissionGate.ParentalVerdict.Allowed)
            {
                _logger.Info("[LLM_AI] [CHAT-EXT] record_program refusé (parental) — usager {0}, programme « {1} ».",
                    _user.Name, title);
                return Json(new { error = "Cet enregistrement n'est pas autorisé pour cet usager." });
            }

            string pin, error;
            if (!RecordingPendingStore.TryReserve(_user.Name, programId,
                    title, kind, postPadding, out pin, out error))
                return Json(new { error });   // verrou (message prêt à rapporter)

            // Toast à l'écran du client : toute la famille voit la demande.
            string mode = string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase)
                ? "tous les nouveaux épisodes" : "à l'heure prévue";
            await ToastAsync(_sessions, _user, "🤖 À confirmer : « " + title + " » (code affiché dans l'app)", ct).ConfigureAwait(false);

            _logger.Info("[LLM_AI] [CHAT-EXT] record_program record — usager {0}, « {1} » (programId={2}, tampon={3} min).",
                _user.Name, title, programId, postPadding);
            return Json(new {
                ok = true, pending = true,
                libelle = "Enregistrement de « " + title + " »" + (mode == "tous les nouveaux épisodes"
                    ? " — tous les nouveaux épisodes de la série" : ""),
                code_envoye_ecran = true,
                note = "Un code à 4 chiffres s'affiche à l'écran de l'app : DEMANDE-le à l'usager. " +
                       "Ne le devine, ne l'invente et ne le réinterprète jamais." });
        }

        // ------------------------------------------------------------------
        //  confirm — le code de l'usager déclenche la création
        // ------------------------------------------------------------------

        private async Task<string> ConfirmAsync(JsonElement args, CancellationToken ct)
        {
            string pin = Str(args, "pin") ?? TryGetPinNumber(args);
            if (string.IsNullOrWhiteSpace(pin))
                return Json(new { error = "Paramètre 'pin' requis (code à 4 chiffres fourni par l'usager)." });
            return await ConfirmCoreAsync(pin.Trim(), ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Noyau de la confirmation, partagé entre le tool (chemin normal,
        /// l'agent appelle confirm) et l'interception endpoint
        /// (<see cref="TryConfirmFromMessageAsync"/>, anti-menteur).
        /// </summary>
        private async Task<string> ConfirmCoreAsync(string pin, CancellationToken ct)
        {

            RecordingPendingStore.Pending pending;
            string error;
            if (!RecordingPendingStore.TryConfirm(_user.Name, pin.Trim(), out pending, out error))
            {
                // Trace de chaque refus (audit des « petits menteurs » : la
                // trace prouve qu'AUCUN enregistrement n'a été créé à ce tour).
                _logger.Info("[LLM_AI] [CHAT-EXT] record_program confirm refusé — usager {0} : {1}",
                    _user.Name, error);
                return Json(new {
                    error,
                    // Marqueur explicite pour le modèle : ce tour n'a rien créé.
                    outcome = "refuse",
                    enregistrement_cree = false,
                    consigne = "Ce tour n'a créé AUCUN enregistrement. Rapporte l'erreur " +
                        "ci-dessus telle quelle à l'usager ; ne dis JAMAIS que " +
                        "l'enregistrement est fait, prévu ou programmé." });
            }

            // Quota dur réservé AVANT l'effet (remboursé si la création échoue).
            if (!RecordingPendingStore.TryConsumeCreation(_user.Name,
                    _cfg.ExternalChatRecordingMaxPerDay, out error))
            {
                // Le pending est consommé (code usagé) : re-réserver est nécessaire.
                RecordingPendingStore.SetTurnNotice(_user.Name, "⚠️ Quota du jour atteint — AUCUN enregistrement créé.");
                return Json(new { error });
            }

            var programIds = new HashSet<string>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ap = new AutoProgrammer(_liveTv, _library, _logger, _host);
            ap.BuildExistingTimerSets(programIds, names);

            var reco = new AutoProgrammer.Reco
            {
                Title = pending.Title,
                Id = pending.ProgramId,
                Kind = pending.Kind,
                Source = "live",
                Reason = "chat_external"
            };
            var outcome = await ap.ProgramOneAsync(reco, programIds, names, ct).ConfigureAwait(false);
            if (outcome != AutoProgrammer.OneOutcome.Created)
            {
                RecordingPendingStore.RefundCreation(_user.Name);  // pas un effet réel
                RecordingPendingStore.SetTurnNotice(_user.Name,
                    "⚠️ Enregistrement non programmé — AUCUN enregistrement créé.");
                return Json(new { error = "Enregistrement non programmé (" + outcome + "). "
                    + "Rapportez le motif honnêtement et proposez de re-demander l'enregistrement." });
            }

            await ToastAsync(_sessions, _user, "🤖 Enregistrement prévu : « " + pending.Title + " »", ct).ConfigureAwait(false);
            _logger.Info("[LLM_AI] [CHAT-EXT] record_program confirm — usager {0}, timer créé pour « {1} ».",
                _user.Name, pending.Title);
            return Json(new { ok = true, command = "confirm",
                detail = "Enregistrement programmé : " + pending.Title });
        }

        // ------------------------------------------------------------------
        //  Interception endpoint (anti-menteur) — la confirmation ne passe
        //  JAMAIS par le modèle
        // ------------------------------------------------------------------

        /// <summary>
        /// Interception DÉTERMINISTE de la confirmation par l'endpoint
        /// (v1.13.23, anti-menteur). Si une réservation est en attente pour
        /// l'usager et que le message contient exactement UN code à 4
        /// chiffres isolé (message court — le geste de l'usager), la
        /// confirmation est traitée ICI : le modèle n'a AUCUN rôle dans la
        /// transaction (il ne peut ni se confirmer lui-même, ni noyer un
        /// refus dans une réponse optimiste — les deux modes de mensonge
        /// constatés en test live). La réponse retournée est VÉRIDIQUE,
        /// composée côté serveur ; les refus réutilisent la notice du store
        /// (compteur d'essais inclus). Retourne null si rien n'est à
        /// intercepter (pas de réservation, message sans code isolé) — le
        /// chemin normal reprend alors, avec le tool <c>record_program</c>.
        /// </summary>
        public static async Task<string> TryConfirmFromMessageAsync(
            PluginConfiguration cfg, User user, ILiveTvManager liveTv,
            ILibraryManager library, IServerApplicationHost host,
            ISessionManager sessions, string message, ILogger logger,
            long turnStartTicks, CancellationToken ct)
        {
            // Portes cumulatives re-vérifiées — fail-closed, même anneau que
            // le tool ; si l'une manque, pas d'interception (chat normal).
            if (cfg == null || !cfg.ExternalChatRecordingsEnabled
                || !UserListed(cfg.ExternalChatRecordingUsers, user.Name)
                || !PermissionGate.CanRecordLive(user))
                return null;
            if (!RecordingPendingStore.HasPending(user.Name)) return null;

            string pin = ExtractIsolatedPin(message);
            if (pin == null) return null;   // pas de code isolé : chat normal

            var tool = new RecordingChatTool(cfg, user, liveTv, library, host, sessions, logger);

            RecordingPendingStore.Pending pending;
            string error;
            if (!RecordingPendingStore.TryConfirm(user.Name, pin, out pending, out error))
            {
                // Le store a posé sa notice VÉRIDIQUE (compteur d'essais,
                // verrou, expiration) — elle EST la réponse, indépendamment
                // de tout modèle.
                string notice = RecordingPendingStore.GetFreshNotice(user.Name, turnStartTicks);
                logger.Info("[LLM_AI] [CHAT-EXT] record_program confirm intercepté (refus) — usager {0} : {1}",
                    user.Name, notice ?? error);
                return notice ?? error;
            }

            if (!RecordingPendingStore.TryConsumeCreation(user.Name,
                    cfg.ExternalChatRecordingMaxPerDay, out error))
            {
                RecordingPendingStore.SetTurnNotice(user.Name,
                    "⚠️ Quota du jour atteint — AUCUN enregistrement créé.");
                return "⚠️ Quota du jour atteint — AUCUN enregistrement créé.";
            }

            var programIds = new HashSet<string>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ap = new AutoProgrammer(liveTv, library, logger, host);
            ap.BuildExistingTimerSets(programIds, names);
            var reco = new AutoProgrammer.Reco
            {
                Title = pending.Title,
                Id = pending.ProgramId,
                Kind = pending.Kind,
                Source = "live",
                Reason = "chat_external"
            };
            var outcome = await ap.ProgramOneAsync(reco, programIds, names, ct).ConfigureAwait(false);
            if (outcome != AutoProgrammer.OneOutcome.Created)
            {
                RecordingPendingStore.RefundCreation(user.Name);   // pas un effet réel
                logger.Info("[LLM_AI] [CHAT-EXT] record_program confirm intercepté (échec {0}) — usager {1}, « {2} ».",
                    outcome, user.Name, pending.Title);
                return "⚠️ Enregistrement non programmé — AUCUN enregistrement créé. "
                    + "Re-demandez l'enregistrement (un nouveau code s'affichera).";
            }

            await ToastAsync(sessions, user,
                "🤖 Enregistrement prévu : « " + pending.Title + " »", ct).ConfigureAwait(false);
            logger.Info("[LLM_AI] [CHAT-EXT] record_program confirm intercepté — usager {0}, timer créé pour « {1} ».",
                user.Name, pending.Title);
            return "✅ Enregistrement programmé : « " + pending.Title + " ».";
        }

        /// <summary>Un UNIQUE code à 4 chiffres isolé dans un message court
        /// (≤ 40 caractères — le geste de confirmation, pas une phrase de
        /// conversation). Plus d'un code, ou un long message : pas
        /// d'interception (le chemin normal reprend).</summary>
        private static readonly System.Text.RegularExpressions.Regex IsolatedPin
            = new System.Text.RegularExpressions.Regex(@"(?<!\d)\d{4}(?!\d)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string ExtractIsolatedPin(string message)
        {
            if (string.IsNullOrEmpty(message) || message.Length > 40) return null;
            var matches = IsolatedPin.Matches(message);
            return matches.Count == 1 ? matches[0].Value : null;
        }

        // ------------------------------------------------------------------
        //  status — lecture seule de l'état de la réservation (JAMAIS le code)
        // ------------------------------------------------------------------

        private string StatusAction()
        {
            int lockLeft = RecordingPendingStore.LockMinutesLeft(_user.Name);
            if (lockLeft > 0)
                return Json(new { pending = false, verrouille = true,
                    note = "Outil d'enregistrement verrouillé (trop de codes erronés) — réessayez dans "
                        + lockLeft + " minute(s). Rapporte-le tel quel ; ne suggère pas d'autres essais." });

            var p = RecordingPendingStore.DescribePending(_user.Name);
            if (p == null)
                return Json(new { pending = false,
                    note = "Aucun enregistrement en attente de confirmation. (Ne confonds pas : " +
                           "réservation en attente ≠ enregistrement créé.)" });

            DateTime expires = new DateTime(p.ExpiresUtc, DateTimeKind.Utc).ToLocalTime();
            return Json(new {
                pending = true,
                title = p.Title,
                kind = p.Kind,
                marge_fin_minutes = p.PostPaddingMin,
                code_envoye_ecran = true,
                expire_a = expires.ToString("HH:mm"),
                essais_ratees = p.FailedAttempts + "/" + RecordingPendingStore.MaxFailedAttempts,
                note = "Réservation EN ATTENTE du code (≠ enregistrement créé) : le code s'affiche à " +
                       "l'écran de l'app ; l'usager doit le fournir dans son message, jamais toi." });
        }

        // ------------------------------------------------------------------
        //  Toast à l'écran du client (cosmétique, jamais bloquant)
        // ------------------------------------------------------------------

        /// <summary>Toast à l'écran du client (cosmétique, jamais bloquant) —
        /// statique : partagé entre le tool et l'interception endpoint.</summary>
        private static async Task ToastAsync(ISessionManager sessions, User user,
            string text, CancellationToken ct)
        {
            try
            {
                var session = (sessions.Sessions ?? Enumerable.Empty<SessionInfo>())
                    .Where(s => s != null && SessionUserMatches(s, user))
                    .OrderByDescending(s => s.LastActivityDate)
                    .FirstOrDefault();
                if (session == null) return;
                bool hasDisplayMessage = (session.SupportedCommands ?? Array.Empty<string>())
                    .Any(c => string.Equals(c, "DisplayMessage", StringComparison.OrdinalIgnoreCase));
                if (!hasDisplayMessage) return;
                await sessions.SendMessageCommand(null, session.Id, new MessageCommand
                {
                    Header = string.Empty,
                    Text = text,
                    TimeoutMs = 6000,
                }, ct).ConfigureAwait(false);
            }
            catch { /* cosmétique — un échec de toast ne doit rien casser */ }
        }

        private static bool SessionUserMatches(SessionInfo s, User user)
        {
            var sid = s.UserId;
            if (string.IsNullOrWhiteSpace(sid)) return false;
            return string.Equals(sid, user.Id.ToString("N"), StringComparison.OrdinalIgnoreCase)
                || string.Equals(sid, user.Id.ToString(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(sid, user.InternalId.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool UserListed(List<string> list, string user)
        {
            return list != null && (list ?? Enumerable.Empty<string>())
                .Any(u => string.Equals((u ?? "").Trim(), user, StringComparison.OrdinalIgnoreCase));
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

        /// <summary>post_padding accepté en nombre ou en texte (le LLM varie).</summary>
        private static int TryGetInt(JsonElement args, string name)
        {
            try
            {
                if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v))
                    return 0;
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
                if (v.ValueKind == JsonValueKind.String
                    && int.TryParse(v.GetString(), out int parsed)) return parsed;
            }
            catch { }
            return 0;
        }

        /// <summary>PIN accepté en nombre (un modèle oublie les guillemets).</summary>
        private static string TryGetPinNumber(JsonElement args)
        {
            try
            {
                if (args.ValueKind == JsonValueKind.Object
                    && args.TryGetProperty("pin", out var v)
                    && v.ValueKind == JsonValueKind.Number)
                    return v.GetRawText();
            }
            catch { }
            return null;
        }

        private static string Json(object o)
        {
            return System.Text.Json.JsonSerializer.Serialize(o,
                new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
    }
}