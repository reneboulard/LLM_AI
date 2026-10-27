using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;
using Emby.Notifications;

namespace LLM_AI
{
    /// <summary>
    /// Toasts d'enregistrement des recommandations « AI Tonight » (v2.4.0) :
    /// branché sur <see cref="ILiveTvManager.RecordingStarted"/> et
    /// <see cref="ILiveTvManager.RecordingEnded"/> pour signaler à l'usager
    /// qu'un programme de ses recommandations <b>live</b> vient de passer à
    /// l'antenne (« ▶ ça commence ») puis d'être enregistré (« ✅ prêt à
    /// regarder »). Toast sur les sessions actives de l'usager
    /// (<see cref="ISessionManager.SendMessageCommand"/>, capacité
    /// <c>DisplayMessage</c>) + notification cloche persistante
    /// (<see cref="INotificationManager"/>) — même mécanique que
    /// <c>TonightLoginService</c>, éprouvée au login.
    /// <para>Pattern <see cref="IServerEntryPoint"/> découvert par scan
    /// d'assembly, abonnement dans <see cref="Run"/>, désabonnement dans
    /// <see cref="Dispose"/> — identique à <c>RecordingWatcher</c>, qui
    /// partage l'événement <see cref="ILiveTvManager.RecordingEnded"/> sans
    /// conflit (événements C# multicast).</para>
    /// </summary>
    /// <remarks>
    /// <para><b>Appariement</b> (forme des args éprouvée au champ par
    /// <c>RecordingWatcher</c> sur 4.10.1.0 : l'argument porte
    /// <c>ActiveRecordingInfo</c> directement — aucune résolution
    /// <c>GetActiveRecordingInfo</c> nécessaire) : <b>id d'abord</b>
    /// (v2.4.1) — l'InternalId du programme de l'événement contre l'id EPG
    /// enrichi de la reco (le titre du payload reste celui du LLM, parfois
    /// reformulé/faux — vécu 2026-10-10) ; sans id, repli titre normalisé
    /// (<see cref="LlmRunner.NormTitle"/>) corroboré par l'heure de début
    /// du guide (±15 min).</para>
    /// <para><b>Titre d'affichage</b> (v2.7.1) : le toast/cloche d'un
    /// épisode de série porte la série ET sa position —
    /// « Série — S02E05 « Titre d'épisode » » via
    /// <see cref="GetEmbyInfoTool.ProgramDisplayTitle"/> — car le
    /// <c>Name</c> du programme EPG est le titre d'ÉPISODE seul (vécu prod
    /// 2026-10-26 : « ▶ « Nelly » commence » pour un épisode d'« Alertes:
    /// Pelletier », aucune série ni numérotation). Le repli titre de
    /// l'appariement essaie le nom de série ET le titre d'épisode (le
    /// payload d'une reco live de série porte la série, pas l'épisode).</para>
    /// <para><b>Piège évité</b> : les recos <c>source="recording"</c>
    /// (« regarde l'épisode déjà enregistré ») ne sont JAMAIS appariées —
    /// sinon le timer quotidien d'une série consommée comme enregistrement
    /// (ex. 14:57 chaque jour) toasterait « commence » pour une reco qui ne
    /// parle pas de l'épisode en cours d'enregistrement.</para>
    /// <para><b>Anti-brisage</b> : résultat de plus de 48 h ignoré (une
    /// série quotidienne ne toasterait pas des jours sur une sélection
    /// périmée) ; comptes ignorés
    /// (<see cref="PluginConfiguration.TonightIgnoredUsers"/>) exclus ; 1
    /// événement = 1 toast ; seuls les enregistrements COMPLÉTÉS sont
    /// annoncés à la fin (statut <see cref="RecordingStatus.Completed"/>) ;
    /// fail-open silencieux sur toute défaillance — un échec n'affecte
    /// jamais l'enregistrement ni le serveur.</para>
    /// <para><b>Config</b> :
    /// <see cref="PluginConfiguration.TonightRecordingToast"/> (défaut
    /// true), gated par <see cref="PluginConfiguration.TonightEnabled"/>.
    /// Purement informatif — aucune action sur les timers ou la
    /// bibliothèque.</para>
    /// </remarks>
    public class TonightRecordingService : IServerEntryPoint
    {
        private readonly ILiveTvManager _liveTv;
        private readonly ISessionManager _sessions;
        private readonly IUserManager _users;
        private readonly INotificationManager _notifications;
        private readonly IServerApplicationHost _host;
        private readonly ILogger _logger;

        /// <summary>Garde de fraîcheur du résultat Tonight apparié : le
        /// rapprochement ne considère que les résultats de moins de 48 h —
        /// sans elle, une série quotidienne dont un épisode avait été
        /// recommandé « live » toasterait des jours sur une sélection
        /// périmée.</summary>
        private static readonly TimeSpan s_resultMaxAge = TimeSpan.FromHours(48);

        /// <summary>Tolérance de l'heure de début (corroboration) : les
        /// timers démarent souvent en avance (« padding », ex. 14:57 pour
        /// 15:00) et l'EPG bouge de quelques minutes d'un run à l'autre.
        /// Le titre normalisé reste la clé primaire — l'heure ne fait
        /// qu'écarter un homonyme concurrent.</summary>
        private const int StartToleranceMinutes = 15;

        public TonightRecordingService(
            ILiveTvManager liveTv,
            ISessionManager sessions,
            IUserManager users,
            INotificationManager notifications,
            IServerApplicationHost host,
            ILogger logger)
        {
            _liveTv = liveTv;
            _sessions = sessions;
            _users = users;
            _notifications = notifications;
            _host = host;
            _logger = logger;
        }

        public void Run()
        {
            _liveTv.RecordingStarted += OnRecordingStarted;
            _liveTv.RecordingEnded += OnRecordingEnded;
            // Ligne d'observabilité : prouve l'abonnement au démarrage — si
            // 4.10.x ne TIRE pas ces événements, le service reste inert
            // (fail-open) et la PREMIÈRE réception (Info, ci-dessous) fait
            // foi du tirage réel sur ce serveur.
            _logger?.Info("[LLM_AI] TonightRecordingService démarré — abonné aux événements d'enregistrement Emby (toasts gated par TonightEnabled + TonightRecordingToast).");
        }

        public void Dispose()
        {
            _liveTv.RecordingStarted -= OnRecordingStarted;
            _liveTv.RecordingEnded -= OnRecordingEnded;
        }

        // ------------------------------------------------------------------
        //  Événements
        // ------------------------------------------------------------------

        // async void : le toast part après le retour du gestionnaire
        // d'événements du serveur, sans le bloquer (SendMessageCommand est
        // awaitable). Tout est catché — un échec n'affecte jamais
        // l'enregistrement ni le serveur.
        private async void OnRecordingStarted(object sender, GenericEventArgs<ActiveRecordingInfo> e)
        {
            try { await HandleAsync(e?.Argument, started: true).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] Toast « enregistrement commencé » échoué : {0}.", ex, ex.Message);
            }
        }

        private async void OnRecordingEnded(object sender, GenericEventArgs<ActiveRecordingInfo> e)
        {
            try { await HandleAsync(e?.Argument, started: false).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] Toast « enregistrement complété » échoué : {0}.", ex, ex.Message);
            }
        }

        private async Task HandleAsync(ActiveRecordingInfo rec, bool started)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null) return;
            if (!cfg.TonightEnabled || !cfg.TonightRecordingToast) return;
            if (rec == null) return;

            // Titre : le programme du guide d'abord (nom EPG courant), le
            // timer en repli (son nom est figé à la création — un renommage
            // EPG tardif le rend périmé).
            string title = rec.Program?.Name ?? rec.Timer?.Name;
            if (string.IsNullOrWhiteSpace(title))
            {
                _logger?.Info("[LLM_AI] Enregistrement {0} (sans programme résolu) — silence.", started ? "démarré" : "terminé");
                return;
            }
            title = title.Trim();

            if (!started)
            {
                // À la fin, seuls les enregistrements COMPLÉTÉS sont
                // annoncés — un enregistrement annulé/conflit ne propose
                // rien à regarder.
                if (rec.RecordingStatus != RecordingStatus.Completed)
                {
                    _logger?.Info("[LLM_AI] Enregistrement « {0} » terminé (statut {1}) — seuls les complétés sont annoncés.",
                        title, rec.RecordingStatus);
                    return;
                }
            }

            // Heure de début du guide (corroboration) + chaîne (affichage) +
            // id EPG du programme (appariement primaire v2.4.1 — le titre du
            // payload peut être celui, fautif, du LLM).
            DateTimeOffset? start = rec.Program?.StartDate ?? rec.Timer?.StartDate;
            string channel = rec.Channel?.Name;
            long? programId = rec.Program?.InternalId;

            // Titre d'affichage (v2.7.1) : le Name de l'entité programme est
            // le titre d'ÉPISODE quand l'EPG en porte un (« Nelly ») — la
            // série vit dans SeriesName, la numérotation dans
            // ParentIndexNumber/IndexNumber. L'appariement, lui, reste sur
            // les identités brutes (titre + SeriesName ci-dessous).
            string display = GetEmbyInfoTool.ProgramDisplayTitle(title, rec.Program?.SeriesName,
                rec.Program?.ParentIndexNumber, rec.Program?.IndexNumber);
            if (string.IsNullOrWhiteSpace(display)) display = "« " + title + " »";

            var matches = MatchAgainstTonight(cfg, title, rec.Program?.SeriesName, start, programId);
            if (matches.Count == 0)
            {
                _logger?.Info("[LLM_AI] Enregistrement « {0} » {1} — absent des recommandations live (< 48 h), silence.",
                    title, started ? "démarré" : "complété");
                return;
            }

            _logger?.Info("[LLM_AI] Enregistrement « {0} » {1} — reco live de {2} usager(s) : toast + cloche.",
                title, started ? "démarré" : "complété", matches.Count);
            foreach (var (userId, reason) in matches)
                await AnnounceAsync(userId, display, channel, start, reason, started).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------
        //  Appariement contre les derniers résultats Tonight
        // ------------------------------------------------------------------

        /// <summary>
        /// Apparie le programme enregistré contre les recos
        /// <c>source="live"</c> du dernier résultat Tonight de chaque usager
        /// en cache (&lt; 48 h, comptes ignorés exclus). Appariement par
        /// <b>id d'abord</b> (v2.4.1) : l'enrichissement live rattache l'id
        /// EPG (InternalId du programme) mais ne réécrit PAS le titre — le
        /// payload garde le titre du LLM, parfois reformulé ou fautif (vécu
        /// 2026-10-10 : « Miss Scarlet, détectives privé » vs le titre EPG
        /// « Miss Scarlet, détective privée », id 774657 rattaché par
        /// chaîne+heure) ; un appariement par titre seul raterait
        /// l'enregistrement d'une reco enrichie. Sans id (ou sans match
        /// d'id), repli titre normalisé + corroboration d'horaire : le
        /// titre du payload de la reco live d'une SÉRIE est le nom de
        /// série (convention <c>SeriesName ?: Name</c> de l'enrichissement)
        /// alors que le programme EPG porte le titre d'ÉPISODE en
        /// <c>Name</c> — le repli compare donc le payload aux DEUX (nom de
        /// série et titre d'épisode, v2.7.1 : avant, seule l'id rattrapait
        /// les épisodes).
        /// Retourne (userId, raison LLM de la reco appariée) — une reco
        /// appariée par usager suffit. Best-effort : payload illisible →
        /// usager sauté.
        /// </summary>
        private List<(string UserId, string Reason)> MatchAgainstTonight(
            PluginConfiguration cfg, string title, string seriesName, DateTimeOffset? start, long? programId)
        {
            var matches = new List<(string, string)>();
            string norm = LlmRunner.NormTitle(title ?? string.Empty);
            string seriesNorm = LlmRunner.NormTitle(seriesName ?? string.Empty);
            // NB : pas de retour anticipé sur norm vide (v2.4.1) — un titre
            // dégénéré (tout-punctuation) normaliserait vide alors que l'id
            // reste exploitable ; le chemin TITRE est neutralisé dans la
            // boucle dans ce cas.

            var ignored = TonightService.IgnoredUserKeys(_users, cfg, _logger);
            foreach (var (userId, items) in TonightService.GetRecentResults(s_resultMaxAge))
            {
                if (ignored.Count > 0)
                {
                    var u = ResolveUser(userId);
                    if (u == null || ignored.Contains(TonightService.NormUserName(u.Name))) continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(items);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        if (el.ValueKind != JsonValueKind.Object) continue;

                        // source=live SEULEMENT : les recos « recording »
                        // (« regarde l'épisode déjà enregistré ») ne sont
                        // jamais appariées — le piège du timer quotidien.
                        if (!el.TryGetProperty("source", out var src) ||
                            src.ValueKind != JsonValueKind.String ||
                            !string.Equals(src.GetString(), "live", StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Appariement par ID d'abord (v2.4.1) : l'id enrichi
                        // EST le programme du guide (même InternalId que le
                        // Program de l'événement) → égalité infaillible, même
                        // si le titre du payload a été reformulé par le LLM.
                        // Un id non-numérique ou absent retombe sur le titre.
                        bool idHit = false;
                        if (programId.HasValue && el.TryGetProperty("id", out var idn))
                        {
                            long recoId = 0;
                            if (idn.ValueKind == JsonValueKind.String)
                                long.TryParse(idn.GetString(), out recoId);
                            else if (idn.ValueKind == JsonValueKind.Number)
                                recoId = idn.GetInt64();
                            idHit = recoId != 0 && recoId == programId.Value;
                        }

                        if (!idHit)
                        {
                            // Pas de titre exploitable des DEUX côtés → id seul.
                            if (norm.Length == 0 && seriesNorm.Length == 0) continue;
                            if (!el.TryGetProperty("title", out var t) || t.ValueKind != JsonValueKind.String) continue;
                            string payloadNorm = LlmRunner.NormTitle(t.GetString() ?? string.Empty);
                            if (payloadNorm.Length == 0) continue;
                            // Le payload d'une reco live de série porte le
                            // NOM DE SÉRIE, le programme EPG le titre
                            // d'ÉPISODE — les deux clés sont essayées.
                            if (!string.Equals(payloadNorm, norm, StringComparison.Ordinal) &&
                                !string.Equals(payloadNorm, seriesNorm, StringComparison.Ordinal))
                                continue;

                            // Corroboration d'horaire (±15 min) quand les DEUX
                            // heures sont connues — sinon le titre seul fait foi
                            // (échelle du foyer : peu d'homonymes concurrents).
                            if (start.HasValue &&
                                el.TryGetProperty("start", out var st) && st.ValueKind == JsonValueKind.String &&
                                DateTimeOffset.TryParse(st.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var recoStart) &&
                                Math.Abs((recoStart - start.Value).TotalMinutes) > StartToleranceMinutes)
                                continue;
                        }

                        string reason = null;
                        if (el.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String)
                            reason = rs.GetString();
                        matches.Add((userId, reason));
                        break; // une reco appariée par usager suffit
                    }
                }
                catch { /* payload illisible : usager sauté (fail-open) */ }
            }
            return matches;
        }

        // ------------------------------------------------------------------
        //  Annonce : toast (sessions actives) + cloche persistante
        // ------------------------------------------------------------------

        private async Task AnnounceAsync(string userId, string title, string channel,
            DateTimeOffset? start, string reason, bool started)
        {
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(channel)) meta.Add(channel);
            if (start.HasValue)
                meta.Add(start.Value.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture));
            string suffix = meta.Count > 0 ? " (" + string.Join(" · ", meta) + ")" : string.Empty;

            string toast = started
                ? string.Format(CultureInfo.InvariantCulture,
                    "▶ {0} commence — il est dans vos recommandations de ce soir{1}", title, suffix)
                : string.Format(CultureInfo.InvariantCulture,
                    "✅ {0} est enregistré — prêt à regarder{1}", title, suffix);

            User user = ResolveUser(userId);

            // 1) Toast : toutes les sessions ACTIVES de l'usager capables de
            //    DisplayMessage. Contrairement au login, pas de sonde
            //    d'attente : à l'heure d'un enregistrement les sessions sont
            //    établies depuis longtemps (capacités déjà déclarées).
            int sent = 0;
            try
            {
                var targets = (_sessions.Sessions ?? Enumerable.Empty<SessionInfo>())
                    .Where(s => s != null && string.Equals(s.UserId, userId, StringComparison.Ordinal) && SupportsDisplay(s))
                    .ToList();
                foreach (var s in targets)
                {
                    try
                    {
                        await _sessions.SendMessageCommand(s.Id, s.Id, new MessageCommand
                        {
                            Header = string.Empty, // non rendu par les clients (vécu login) — tout dans Text
                            Text = "🤖 " + toast,
                            TimeoutMs = TonightLoginService.PopupDurationMs(),
                        }, CancellationToken.None).ConfigureAwait(false);
                        sent++;
                    }
                    catch { /* session fermée entre-temps : suivante */ }
                }
            }
            catch { /* énumération tolérante */ }

            // 2) Cloche persistante : TOUJOURS — l'usager n'a peut-être pas
            //    de session active à l'heure de l'enregistrement, et elle
            //    survit à la fermeture (notifiers ex. SMTP).
            SendBell(user, toast, reason);

            _logger?.Info("[LLM_AI] Annonce {0} « {1} » à « {2} » : {3} toast(s){4}.",
                started ? "début" : "complétion", title, user?.Name ?? userId, sent,
                user == null ? "" : ", cloche envoyée");
        }

        /// <summary>La session déclare <c>DisplayMessage</c> (capacités
        /// déclarées, repli champ legacy) — même sonde que
        /// <c>TonightLoginService.SessionSupportsDisplay</c>, mais sur
        /// l'instance directement.</summary>
        private static bool SupportsDisplay(SessionInfo s)
        {
            try
            {
                var cmds = s.Capabilities?.SupportedCommands;
                if (cmds != null && cmds.Contains("DisplayMessage", StringComparer.OrdinalIgnoreCase)) return true;
                var legacy = s.SupportedCommands;
                return legacy != null && legacy.Contains("DisplayMessage", StringComparer.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// Cloche persistante (notification Emby) — même mécanique que
        /// <c>TonightLoginService.SendBellNotification</c>. La raison LLM
        /// de la reco a sa place ici (multi-lignes, courriels des
        /// notifiers) et jamais dans un toast.
        /// </summary>
        private void SendBell(User user, string toast, string reason)
        {
            if (_notifications == null || user == null) return;
            try
            {
                var desc = toast;
                if (!string.IsNullOrWhiteSpace(reason))
                    desc += "\n🤖 " + reason.Trim();
                string url = ResolveEmbyUrl();
                // Lien aussi en description (v2.4.5 : les canaux de lecture —
                // webhooks, courriel — ignorent le champ Url d'Emby).
                if (!string.IsNullOrWhiteSpace(url))
                    desc += "\n" + url;
                var req = new NotificationRequest
                {
                    Title = "🤖 LLM AI — Enregistrement recommandé",
                    Description = desc,
                    Url = url,
                    Date = DateTimeOffset.UtcNow,
                    Severity = LogSeverity.Info,
                    User = user,
                };
                _notifications.SendNotification(req);
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] Cloche enregistrement à « {0} » échouée : {1}.", ex, user.Name, ex.Message);
            }
        }

        // ------------------------------------------------------------------
        //  Utilitaires
        // ------------------------------------------------------------------

        private User ResolveUser(string userId)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(userId))
                    return _users.GetUserById(userId);
            }
            catch { /* tolérant */ }
            return null;
        }

        /// <summary>Même logique que
        /// <c>LlmScheduledTask.ResolveEmbyUrl</c> : URL publique configurée
        /// prioritaire, sinon URL locale du serveur.</summary>
        private string ResolveEmbyUrl()
        {
            string publicUrl = (Plugin.Instance?.Configuration?.EmbyPublicUrl ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(publicUrl)) return publicUrl;
            try
            {
                string local = _host?.GetLocalHostApiUrl();
                if (!string.IsNullOrWhiteSpace(local)) return local;
            }
            catch { /* tolérant */ }
            return string.Empty;
        }
    }
}