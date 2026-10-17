using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Users;
using Emby.Notifications;

namespace LLM_AI
{
    /// <summary>
    /// Primitives de remédiation serveur (v1.13.30) : arrêt de lecture d'une
    /// session, déclenchement d'une tâche planifiée, message Emby à un
    /// usager. Code métier UNIQUE — deux appelants avec deux contrats :
    /// <list type="bullet">
    /// <item><see cref="SystemAuditTool"/> (remédiation directe de l'audit,
    ///   opt-in <c>AuditRemediationEnabled</c>) — formes JSON historiques
    ///   conservées.</item>
    /// <item>les tools d'action du chat admin (<c>stop_session</c>,
    ///   <c>trigger_task</c>, <c>send_message</c> dans <see cref="ChatActions"/>)
    ///   — deux phases : dépôt d'une proposition puis exécution à
    ///   l'approbation (la carte remplace le flag config).</item>
    /// </list>
    /// Aucune vérification d'autorisation ici : elle appartient à l'appelant
    /// (endpoint admin-only pour le chat, gate config pour l'audit).
    /// </summary>
    internal static class ServerRemediation
    {
        // ------------------------------------------------------------------
        //  Résultats typés (chaque appelant construit sa propre forme JSON)
        // ------------------------------------------------------------------

        /// <summary>Résultat d'un arrêt de session.</summary>
        public sealed class StopResult
        {
            /// <summary>Lecture arrêtée (vérifiée) — ou déjà absente.</summary>
            public bool Stopped;
            /// <summary>Le client a CONFIRMÉ l'arrêt par ses rapports (v2.6.4) —
            /// sinon l'arrêt n'est qu'envoyé, jamais prouvé.</summary>
            public bool Confirmed;
            /// <summary>Chemin de l'arrêt : null = commande directe,
            /// « gohome » = repli navigation (v2.6.4).</summary>
            public string Via;
            /// <summary>Id du contrôleur admin inscrit dans la commande (v2.6.3).</summary>
            public string Controller;
            public string SessionId;
            public string UserName;
            public string NowPlaying;
            /// <summary>Note d'état (« aucune lecture en cours », échec honnête…) — sinon null.</summary>
            public string Note;
            /// <summary>Message d'erreur (session introuvable…) — sinon null.</summary>
            public string Error;
        }

        /// <summary>Résultat d'un déclenchement de tâche planifiée.</summary>
        public sealed class TaskResult
        {
            public bool Queued;
            public string TaskId;
            public string Name;
            public string Key;
            /// <summary>Message d'erreur (tâche introuvable…) — sinon null.</summary>
            public string Error;
        }

        /// <summary>Résultat d'un envoi de message.</summary>
        public sealed class MessageResult
        {
            /// <summary>« notification » ou « osd ».</summary>
            public string Delivery;
            /// <summary>Nombre d'usagers destinataires résolus.</summary>
            public int Recipients;
            /// <summary>osd : sessions atteintes ; notification : envois réussis.</summary>
            public int Sent;
            /// <summary>Note d'avertissement (ex. aucune session active en osd) — sinon null.</summary>
            public string Note;
            /// <summary>Message d'erreur (usager introuvable, texte requis…) — sinon null.</summary>
            public string Error;
        }

        // ------------------------------------------------------------------
        //  stop_session : arrête la lecture d'une session active
        // ------------------------------------------------------------------

        /// <summary>
        /// Arrête la lecture d'une session — au standard v2.6.3/v2.6.4 :
        /// (1) la commande <c>PlaystateCommand.Stop</c> porte
        /// <c>ControllingUserId</c> = premier admin (patron ComSkipper —
        /// sans contrôleur, l'app TV native prod ignore la commande) ;
        /// (2) l'arrêt est CONFIRMÉ PAR RELECTURE (session close ou plus de
        /// lecture, ~12 s max, sortie anticipée) — un ok d'envoi ne prouve
        /// rien (constat 2026-10-16/17 : des clients ignorent le stop) ;
        /// (3) repli <c>GoHome</c> si le stop direct n'est pas confirmé —
        /// la navigation FERME le lecteur du client web, qui signale
        /// l'arrêt lui-même (58 ms au banc 2026-10-17). N'agit pas sur la
        /// session elle-même (Emby n'expose pas de fermeture de session
        /// propre en in-process) : arrête le transcodage/lecture en cours —
        /// c'est l'action utile (« ce stream consomme trop »). Si le client
        /// ne confirme NI le direct NI le repli, <c>Stopped=false</c> :
        /// l'appelant ne doit jamais annoncer un arrêt.
        /// </summary>
        public static async Task<StopResult> StopSessionAsync(ISessionManager sessions,
            IUserManager users, string sessionId, CancellationToken ct)
        {
            var result = new StopResult { SessionId = sessionId };
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                result.Error = "paramètre 'session_id' requis.";
                return result;
            }

            var session = (sessions?.Sessions ?? Enumerable.Empty<SessionInfo>())
                .FirstOrDefault(s => s.Id == sessionId);
            if (session == null)
            {
                result.Error = "session introuvable : " + sessionId;
                return result;
            }

            result.UserName = session.UserName;
            result.NowPlaying = session.NowPlayingItem?.Name;
            if (session.NowPlayingItem == null)
            {
                // Rien à arrêter : état cible vérifié D'ENTRÉE — les
                // appelants ont leur branche « idle » dédiée (carte du
                // chat « rien à arrêter » depuis 2026-09-21).
                result.Stopped = true;
                result.Confirmed = true;
                result.Note = "aucune lecture en cours";
                return result;
            }

            // v2.6.3 — contrôleur identifié : premier admin (repli silencieux
            // sans contrôleur si la liste échoue — jamais l'ancien envoi nu).
            result.Controller = FirstAdminControllerId(users);
            await sessions.SendPlaystateCommand(null, sessionId,
                new PlaystateRequest { Command = PlaystateCommand.Stop,
                    ControllingUserId = result.Controller }, ct).ConfigureAwait(false);

            // v2.6.2/2.6.4 — CONFIRMATION PAR RELECTURE puis repli GoHome.
            if (await PlaybackGoneAsync(sessions, sessionId, ct).ConfigureAwait(false))
            {
                result.Stopped = true;
                result.Confirmed = true;
                return result;
            }

            // Repli GoHome (v2.6.4) : ferme le lecteur des clients qui
            // ignorent PlaystateCommand.Stop (client web — banc 2026-10-17).
            await sessions.SendGeneralCommand(null, sessionId,
                new GeneralCommand { Name = "GoHome" }, ct).ConfigureAwait(false);
            if (await PlaybackGoneAsync(sessions, sessionId, ct).ConfigureAwait(false))
            {
                result.Stopped = true;
                result.Confirmed = true;
                result.Via = "gohome";
                return result;
            }

            // Honnête : envoyé, NON confirmé — jamais d'arrêt fantôme.
            result.Stopped = false;
            result.Confirmed = false;
            result.Note = "le client n'a pas confirmé l'arrêt (ni direct, ni retour accueil) — la lecture poursuit probablement";
            return result;
        }

        /// <summary>
        /// Relecture d'arrêt : la session a fermé ou ne rapporte plus de
        /// lecture (24 sondages × 500 ms = 12 s max, sortie anticipée) —
        /// le drapeau ne bascule qu'au prochain rapport du client
        /// (~5-10 s de cadence selon l'app).
        /// </summary>
        private static async Task<bool> PlaybackGoneAsync(ISessionManager sessions,
            string sessionId, CancellationToken ct)
        {
            for (int i = 0; i < 24; i++)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                var s = (sessions?.Sessions ?? Enumerable.Empty<SessionInfo>())
                    .FirstOrDefault(x => x.Id == sessionId);
                if (s == null || s.NowPlayingItem == null)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Id du PREMIER administrateur du serveur (patron ComSkipper,
        /// v2.6.3) — ou null si la liste échoue / aucun admin. Chemin
        /// partagé avec <c>ClientCommandTool.FirstAdminControllerId</c>,
        /// qui ajoute repli sur l'usager du chat + journalisation.
        /// </summary>
        internal static string FirstAdminControllerId(IUserManager users)
        {
            try
            {
                var admin = (users.GetUserList(
                    new UserQuery { IsAdministrator = true })
                    ?? Array.Empty<User>()).FirstOrDefault();
                return admin?.Id.ToString();
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------
        //  trigger_task : file une tâche planifiée
        // ------------------------------------------------------------------

        /// <summary>
        /// Déclenche une tâche planifiée (mise en file d'exécution via
        /// <see cref="ITaskManager.QueueScheduledTask(IScheduledTask, TaskOptions)"/>).
        /// La tâche est repérée par <c>taskId</c> (worker.Id) ou <c>taskKey</c>
        /// (ScheduledTask.Key). <paramref name="excludeHidden"/> exclut les
        /// tâches cachées (<see cref="IConfigurableScheduledTask.IsHidden"/>) —
        /// le chemin chat l'active (fail-closed), l'audit laisse MatchTask tel quel.
        /// </summary>
        public static TaskResult TriggerTask(ITaskManager tasks, string taskId, string taskKey, bool excludeHidden)
        {
            var result = new TaskResult();
            if (string.IsNullOrWhiteSpace(taskId) && string.IsNullOrWhiteSpace(taskKey))
            {
                result.Error = "paramètre 'task_id' ou 'task_key' requis.";
                return result;
            }

            var worker = MatchTask(tasks, taskId, taskKey, excludeHidden);
            if (worker == null)
            {
                result.Error = "tâche introuvable" + (excludeHidden ? " ou cachée" : "")
                    + " (task_id=" + (taskId ?? "") + ", task_key=" + (taskKey ?? "") + ").";
                return result;
            }

            tasks.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());
            result.Queued = true;
            result.TaskId = worker.Id;
            result.Name = worker.Name;
            result.Key = worker.ScheduledTask?.Key;
            return result;
        }

        // ------------------------------------------------------------------
        //  send_message : notification inbox ou toast OSD à un usager
        // ------------------------------------------------------------------

        /// <summary>
        /// Envoie un message à un usager. Deux modes de livraison :
        /// <list type="bullet">
        /// <item><c>notification</c> (défaut) : notification Emby inbox/cloche via
        ///   <see cref="INotificationManager.SendNotification"/> — fiable, livré
        ///   même sans session active (chemin éprouvé par <c>LlmScheduledTask</c>).</item>
        /// <item><c>osd</c> : toast à l'écran via
        ///   <see cref="ISessionManager.SendMessageCommand"/> sur chaque session
        ///   active de l'usager — requiert une session live.</item>
        /// </list>
        /// L'usager est résolu par Guid (<paramref name="recipient"/> = id) ou
        /// par nom (insensible casse).
        /// </summary>
        public static async Task<MessageResult> SendMessageAsync(ISessionManager sessions, IUserManager users,
            INotificationManager notifications, ILogger logger,
            string recipient, string header, string text, string delivery, int timeoutMs, CancellationToken ct)
        {
            var result = new MessageResult();
            if (string.IsNullOrWhiteSpace(recipient))
            {
                result.Error = "paramètre 'user_id' ou 'user_name' requis.";
                return result;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                result.Error = "paramètre 'text' requis (corps du message).";
                return result;
            }

            var resolved = ResolveUsers(users, logger, recipient);
            result.Recipients = resolved.Count;
            if (resolved.Count == 0)
            {
                result.Error = "usager introuvable : " + recipient;
                return result;
            }

            string safeHeader = string.IsNullOrWhiteSpace(header) ? "Message" : header;
            string mode = string.Equals(delivery, "osd", StringComparison.OrdinalIgnoreCase) ? "osd" : "notification";
            result.Delivery = mode;

            if (mode == "osd")
            {
                var live = (sessions?.Sessions ?? Enumerable.Empty<SessionInfo>()).ToList();
                foreach (var u in resolved)
                {
                    string uid = u.Id.ToString();
                    foreach (var s in live.Where(x => string.Equals(x.UserId, uid, StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            await sessions.SendMessageCommand(null, s.Id,
                                new MessageCommand { Header = safeHeader, Text = text, TimeoutMs = timeoutMs },
                                ct).ConfigureAwait(false);
                            result.Sent++;
                        }
                        catch (Exception ex)
                        {
                            logger?.Warn("[LLM_AI] remédiation send_message(osd) session {0} : {1}", s.Id, ex.Message);
                        }
                    }
                }
                if (result.Sent == 0)
                    result.Note = "Aucune session active — aucun toast envoyé. Utilise delivery=notification pour une livraison persistante.";
                return result;
            }

            // notification (défaut) — chemin inbox/cloche éprouvé.
            var now = DateTimeOffset.UtcNow;
            foreach (var u in resolved)
            {
                try
                {
                    var req = new NotificationRequest
                    {
                        Title = safeHeader,
                        Description = text,
                        Date = now,
                        Severity = LogSeverity.Info,
                        User = u
                    };
                    notifications.SendNotification(req);
                    result.Sent++;
                }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] remédiation send_message(notification) « {0} » : {1}", u.Name, ex.Message);
                }
            }
            return result;
        }

        // ------------------------------------------------------------------
        //  Helpers de résolution (partagés audit + chat)
        // ------------------------------------------------------------------

        /// <summary>Une tâche planifiée est cachée si elle implémente IConfigurableScheduledTask.IsHidden.</summary>
        internal static bool IsHidden(IScheduledTaskWorker w)
        {
            try { return (w.ScheduledTask as IConfigurableScheduledTask)?.IsHidden ?? false; }
            catch { return false; }
        }

        /// <summary>Repère une tâche par Id (worker.Id), Key (ScheduledTask.Key)
        /// — puis, en repli, par NOM affiché : le LLM (et l'humain) pense en
        /// noms (« Scan Media Library ») et l'audit n'expose même pas la Key
        /// interne (constat terrain 2026-09-21 : nom mis dans task_key →
        /// refus). Prépondérance Id &gt; Key &gt; nom ; les tâches cachées
        /// restent exclues sur toutes les passes (fail-closed).</summary>
        public static IScheduledTaskWorker MatchTask(ITaskManager tasks, string taskId, string taskKey, bool excludeHidden)
        {
            var workers = tasks?.ScheduledTasks ?? Array.Empty<IScheduledTaskWorker>();
            foreach (var w in workers)
            {
                if (excludeHidden && IsHidden(w)) continue;
                if (!string.IsNullOrWhiteSpace(taskId)
                    && string.Equals(w.Id, taskId, StringComparison.OrdinalIgnoreCase))
                    return w;
                if (!string.IsNullOrWhiteSpace(taskKey)
                    && string.Equals(w.ScheduledTask?.Key, taskKey, StringComparison.OrdinalIgnoreCase))
                    return w;
            }
            // Repli : nom affiché (le LLM met volontiers le nom dans
            // task_key — l'audit scheduled_tasks n'expose que id + name).
            if (string.IsNullOrWhiteSpace(taskKey)) return null;
            foreach (var w in workers)
            {
                if (excludeHidden && IsHidden(w)) continue;
                if (string.Equals(w.ScheduledTask?.Name, taskKey, StringComparison.OrdinalIgnoreCase))
                    return w;
            }
            return null;
        }

        /// <summary>
        /// Résout des usagers par identifiant (Guid) OU nom (insensible casse).
        /// On liste puis on match — robuste quel que soit le type de User.Id.
        /// </summary>
        public static List<User> ResolveUsers(IUserManager users, ILogger logger, string recipient)
        {
            var result = new List<User>();
            if (string.IsNullOrWhiteSpace(recipient)) return result;
            try
            {
                var all = users.GetUserList(new UserQuery()) ?? Array.Empty<User>();
                foreach (var u in all)
                {
                    if (u == null) continue;
                    if (string.Equals(u.Name, recipient, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(u.Id.ToString(), recipient, StringComparison.OrdinalIgnoreCase))
                        result.Add(u);
                }
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] remédiation ResolveUsers : {0}", ex.Message);
            }
            return result;
        }
    }
}