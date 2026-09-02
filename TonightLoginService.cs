using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Session;
using Emby.Notifications;

namespace LLM_AI
{
    /// <summary>
    /// Déclencheur de login : branche <see cref="ISessionManager.SessionStarted"/>
    /// pour produire, à la connexion d'un usager, le « À regarder ce soir »
    /// (via <see cref="TonightService"/>) et le lui signaler sur le client natif
    /// (Android / Android TV) par un <b>toast</b>
    /// (<see cref="ISessionManager.SendMessageCommand"/>), avec une
    /// <b>notification cloche</b> persistante en repli (deep-link) si la session
    /// s'est fermée avant la fin du run LLM.
    /// <para>Pattern confirmé par <c>Emby.ComSkipper/ServerEntryPoint.cs</c> :
    /// <see cref="IServerEntryPoint"/> découvert par scan d'assembly (aucune
    /// inscription manuelle), abonnement aux events session dans
    /// <see cref="Run"/>, désabonnement dans <see cref="Dispose"/>.</para>
    /// <para>Comportement configurable :
    /// <list type="bullet">
    /// <item><see cref="PluginConfiguration.LoginPopup"/> (défaut true) : active
    /// le toast + la cloche au login. Indépendant de l'auto-programmation.</item>
    /// <item><see cref="PluginConfiguration.AutoProgram"/> (défaut false, opt-in
    /// explicite) : si coché, crée les timers du record bucket après le run
    /// (cf. <see cref="AutoProgrammer"/>). Aucune programmation tant que
    /// décoché.</item>
    /// <item><see cref="PluginConfiguration.TonightEnabled"/> (défaut true) : si
    /// décoché, le service ne fait rien.</item>
    /// </list></para>
    /// <para>Throttling : un seul run Tonight par usager par fenêtre de cache
    /// (cf. <see cref="TonightService"/>) même sur plusieurs appareils. Le cache
    /// frais → toast immédiat (pas de run) ; le cache froid → run puis toast
    /// (~30–60 s). Un garde-fou <c>in-flight</c> évite deux runs parallèles pour
    /// le même usager (deux appareils qui se connectent à la fois).</para>
    /// </summary>
    public class TonightLoginService : IServerEntryPoint
    {
        private readonly ISessionManager _sessions;
        private readonly IUserManager _users;
        private readonly IJsonSerializer _json;
        private readonly ILiveTvManager _liveTv;
        private readonly ILibraryManager _library;
        private readonly IServerApplicationHost _host;
        private readonly INotificationManager _notifications;
        private readonly ILogger _logger;
        private readonly ICollectionManager _collections;

        public TonightLoginService(
            ISessionManager sessionManager,
            IUserManager userManager,
            IJsonSerializer json,
            ILiveTvManager liveTv,
            ILibraryManager library,
            IServerApplicationHost host,
            INotificationManager notifications,
            ILogger logger,
            ICollectionManager collections)
        {
            _sessions = sessionManager;
            _users = userManager;
            _json = json;
            _liveTv = liveTv;
            _library = library;
            _host = host;
            _notifications = notifications;
            _logger = logger;
            _collections = collections;
        }

        // Garde-fou anti-run-parallèle pour un même usager : si deux appareils
        // se connectent à la fois, seul le premier déclenche le run LLM ; le
        // second trouve le cache frais (ou est sauté) → pas de double run.
        private static readonly HashSet<string> _inFlight = new HashSet<string>(StringComparer.Ordinal);
        private static readonly object _inFlightLock = new object();

        public void Run()
        {
            _sessions.SessionStarted += OnSessionStarted;
            _logger?.Info("[LLM_AI] TonightLoginService démarré (popup au login activé par config LoginPopup).");
        }

        public void Dispose()
        {
            _sessions.SessionStarted -= OnSessionStarted;
        }

        // ------------------------------------------------------------------
        //  Handler de connexion
        // ------------------------------------------------------------------

        // async void : on ne peut pas bloquer l'event session (le run LLM
        // peut prendre 30–60 s). Tout est catché pour ne jamais faire planter
        // le gestionnaire de sessions. Le CancellationToken vient de la session
        // si disponible, sinon None.
        private async void OnSessionStarted(object sender, SessionEventArgs e)
        {
            try
            {
                var session = e?.SessionInfo;
                if (session == null) return;

                var cfg = Plugin.Instance?.Configuration;
                if (cfg == null) return;
                if (!cfg.TonightEnabled || !cfg.LoginPopup) return;

                // Résout l'usager (UserId string = Guid). Null si la session n'a
                // pas d'usager (ex. lecture sans login) → on ignore.
                User user = ResolveUser(session);
                if (user == null) return;

                string sid = session.Id;

                // 1) Cache frais → toast immédiat (pas de run LLM, pas
                //    d'auto-programmation : les timers ont déjà été créés au
                //    premier run). Le toast seul suffit ; pas de cloche (le run
                //    est déjà ancien, l'usager l'a déjà vue).
                string cached = TonightService.TryGetCached(user.Id.ToString());
                if (!string.IsNullOrEmpty(cached))
                {
                    _logger?.Info("[LLM_AI] Login « {0} » : cache Tonight frais → toast immédiat.", user.Name);
                    await SendToastAsync(user, sid, cached, programmed: -1, ct: CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                // 2) Cache froid → run LLM. Garde-fou in-flight : si un run est
                //    déjà en cours pour cet usager (autre appareil), on ne lance
                //    pas un second run ; on saute ce login (le premier popup
                //    arrivera, et la cloche persistante couvre le cas).
                lock (_inFlightLock)
                {
                    if (!_inFlight.Add(user.Id.ToString()))
                    {
                        _logger?.Info("[LLM_AI] Login « {0} » : run Tonight déjà en cours (autre appareil ?) → ignoré.", user.Name);
                        return;
                    }
                }

                try
                {
                    _logger?.Info("[LLM_AI] Login « {0} » : cache Tonight froid → run LLM puis toast.", user.Name);
                    var ct = CancellationToken.None; // le run survit à la requête login
                    var svc = new TonightService(_users, _library, _liveTv, _json, _host, _logger, _collections);
                    var res = await svc.GenerateTonightAsync(user, cfg, refresh: false, ct).ConfigureAwait(false);
                    if (res.Error != null || string.IsNullOrEmpty(res.Payload))
                    {
                        _logger?.Info("[LLM_AI] Login « {0} » : run Tonight sans résultat ({1}).", user.Name, res.Error ?? "vide");
                        return;
                    }

                    // 3) Auto-programmation (record bucket) — GATING ABSOLU :
                    //    aucun timer tant que cfg.AutoProgram == false.
                    int programmed = -1;
                    if (cfg.AutoProgram)
                    {
                        try
                        {
                            var ap = new AutoProgrammer(_liveTv, _library, _logger);
                            var stats = await ap.Program(res.Payload, user, cfg, ct).ConfigureAwait(false);
                            programmed = stats.Programmed;
                        }
                        catch (Exception ex)
                        {
                            _logger?.ErrorException("[LLM_AI] Auto-program (login « {0} ») : {1}", ex, user.Name, ex.Message);
                        }
                    }

                    // 4) Toast sur la session qui vient de se connecter + cloche
                    //    persistante (repli si la session a fermé avant la fin).
                    await SendToastAsync(user, sid, res.Payload, programmed, ct).ConfigureAwait(false);
                }
                finally
                {
                    lock (_inFlightLock) { _inFlight.Remove(user.Id.ToString()); }
                }
            }
            catch (Exception ex)
            {
                // Ne jamais propager hors d'un async-void event handler.
                _logger?.ErrorException("[LLM_AI] TonightLoginService.OnSessionStarted : {0}", ex, ex.Message);
            }
        }

        // ------------------------------------------------------------------
        //  Résolution usager
        // ------------------------------------------------------------------

        /// <summary>
        /// Résout l'usager depuis la session. <see cref="SessionInfo.UserId"/>
        /// est le Guid string ; <see cref="IUserManager.GetUserById(string)"/>
        /// le résout. Null si pas d'usager (session anonyme).
        /// </summary>
        private User ResolveUser(SessionInfo session)
        {
            try
            {
                string uid = session.UserId;
                if (!string.IsNullOrWhiteSpace(uid))
                    return _users.GetUserById(uid);
                // Repli sur le nom (UserInternalId non string).
                if (!string.IsNullOrWhiteSpace(session.UserName))
                    return _users.GetUserByName(session.UserName);
            }
            catch { /* tolérant */ }
            return null;
        }

        // ------------------------------------------------------------------
        //  Toast (SendMessageCommand) + cloche (notification)
        // ------------------------------------------------------------------

        /// <summary>
        /// Envoie le toast au client qui vient de se connecter (gated
        /// <c>DisplayMessage</c> dans <see cref="SessionInfo.SupportedCommands"/>),
        /// PUIS une notification cloche persistante (deep-link) en repli — elle
        /// reste même si la session a fermé avant la fin du run et fonctionne
        /// sur tous les clients. Le toast est texte seul (pas de bouton), d'où
        /// la cloche en complément.
        /// </summary>
        /// <param name="programmed">Nombre de timers créés par AutoProgrammer.
        /// -1 = cache frais (auto-programmation déjà faite au premier run : ne
        /// pas mentionner « N programmé(s) » dans le toast).</param>
        // Délai de « plongée » après détection de la capacité DisplayMessage :
        // au moment de SessionStarted, le client vient à peine de s'authentifier
        // et n'a pas fini sa navigation post-login (le dashboard met ~1–3 s à se
        // rendre). ComSkipper, lui, envoie pendant la lecture — client pleinement
        // prêt. Ici on doit laisser le client se stabiliser avant d'envoyer le
        // toast, sinon le DisplayMessage part dans une UI non rendue et est
        // droppé côté client (toast jamais affiché, alors que le serveur l'a
        // bien envoyé). Sans ce délai, le toast partait la même milliseconde
        // que SessionStarted — avant même le POST /Sessions/Capabilities/Full.
        private const int DisplaySettleMs = 3000;

        private async Task SendToastAsync(User user, string sessionId, string payload, int programmed, CancellationToken ct)
        {
            string toast = BuildToastText(payload, programmed);
            // Cloche : texte multi-lignes propre (une reco par ligne + raison),
            // PAS le texte toast « • »-joint — les \n passent dans le courriel.
            string bell = BuildBellText(payload, programmed);
            if (string.IsNullOrEmpty(toast)) { SendBellNotification(user, bell); return; }

            // Attend que le client déclare DisplayMessage (au login, les
            // capacités ne sont pas encore postées — elles arrivent ~15 ms
            // APRÈS SessionStarted). Timeout ~10 s ; au-delà, cloche seule.
            bool supportsDisplay = await WaitForDisplayAsync(sessionId, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

            if (supportsDisplay)
            {
                // Settle : laisser le dashboard post-login se rendre avant
                // d'envoyer, sinon le toast est rendu puis effacé par la
                // navigation. (Sur le chemin cache froid, le run LLM a déjà
                // pris 30–60 s, donc le client est prêt — ce délai est ici
                // surtout pour le chemin cache frais, instantané au login.)
                try { await Task.Delay(DisplaySettleMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* tombé → la cloche prend le relais */ }

                try
                {
                    await SendToastSequenceAsync(sessionId, payload, programmed, ct).ConfigureAwait(false);
                    _logger?.Info("[LLM_AI] Toast(s) envoyé(s) à la session « {0} » (DisplayMessage).", user.Name);
                }
                catch (Exception ex)
                {
                    // Session fermée avant/après la fin du run → la cloche prend
                    // le relais. On ne logue qu'en info.
                    _logger?.Info("[LLM_AI] Toast échoué pour « {0} » (session fermée ?) : {1}", user.Name, ex.Message);
                }
            }
            else
            {
                _logger?.Info("[LLM_AI] Session « {0} » sans DisplayMessage (sous 10 s) → cloche seule.", user.Name);
            }

            // Cloche (deep-link) — toujours, même si le toast a réussi :
            // livrée via les notifiers configurés par l'usager (ex. SMTP —
            // test 2026-09-02 : courriel multi-lignes OK ; le client web
            // standard n'a PAS de boîte de réception intégrée, la cloche
            // dépend donc d'un notifier).
            SendBellNotification(user, bell);
        }

        // ------------------------------------------------------------------
        //  Séquence de toasts : une popup PAR recommandation
        // ------------------------------------------------------------------

        // Plafond d'items de la séquence : 5 popups ≈ 40 s au login (à 8 s
        // par popup), assez pour les recos utiles sans spammer l'usager.
        private const int ToastSequenceMax = 5;

        // Rythme du client WEB : son toast ignore TimeoutMs (test 2026-09-02 —
        // popup TEST à TimeoutMs=60000 sur Chrome Windows : fondu ~3 s) et
        // vit sur une animation CSS fixe (~3,3 s). On envoie donc le suivant
        // à 4 s : le toast vit ses ~3 s, le suivant arrive juste après — pas
        // de temps mort, pas de chevauchement.
        private const int WebPopupGapMs = 4000;

        /// <summary>
        /// Détermine si la session est un client web (toast à durée fixe —
        /// cf. <see cref="WebPopupGapMs"/>). Les autres clients (Android TV,
        /// qui honore TimeoutMs — vécu ComSkipper) utilisent
        /// <see cref="PopupDurationMs"/> comme durée ET intervalle.
        /// </summary>
        private bool IsWebClient(string sessionId)
        {
            try
            {
                var s = _sessions.Sessions?.FirstOrDefault(x => x.Id == sessionId);
                return s != null && (s.Client ?? string.Empty)
                    .IndexOf("web", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Envoie le « À regarder ce soir » au client : UNE popup PAR
        /// suggestion du payload Tonight (enregistrements, bibliothèque et
        /// programmes EPG live — la sélection complète de la section), en
        /// séquence (une seule popup avec toutes les recos s'est révélée
        /// illisible — texte dense, clients qui collapse les séparateurs).
        /// <para>Vécu 2026-09-02 (test web + Android) :
        /// <see cref="MessageCommand.Header"/> n'est PAS rendu par ces
        /// clients — tout (🤖, libellé, compteur i/n) vit donc dans
        /// <see cref="MessageCommand.Text"/> :
        /// « 🤖 À regarder ce soir (i/n) — Titre (chaîne · heure · type) ».</para>
        /// <para>Chaque popup reste à l'écran sa durée complète puis est
        /// remplacé par le suivant — au rythme ADAPTÉ au client (test
        /// 2026-09-02 : le web ignore TimeoutMs, toast à durée fixe ~3 s ;
        /// Android TV honore TimeoutMs) : web → <see cref="WebPopupGapMs"/>,
        /// autres → <see cref="PopupDurationMs"/> (= LoginPopupSeconds,
        /// défaut 8 s, réglable). Le bilan « N programmé(s) » est accolé au
        /// dernier toast.
        /// 0 ou 1 item → toast unique (texte résumé). Une session fermée en
        /// cours de séquence jette <see cref="ISessionManager.SendMessageCommand"/>
        /// → interrompt la boucle, la cloche (résumé complet) prend le relais.</para>
        /// </summary>
        private async Task SendToastSequenceAsync(string sessionId, string payload, int programmed, CancellationToken ct)
        {
            var watch = ExtractWatchItems(payload);
            int total = Math.Min(watch.Count, ToastSequenceMax);
            // Rythme adapté au client : web → 4 s (toast à durée fixe ~3 s,
            // pas de temps mort) ; Android TV & autres → LoginPopupSeconds
            // (défaut 8 s) comme durée demandée ET intervalle.
            int popupMs = IsWebClient(sessionId) ? WebPopupGapMs : PopupDurationMs();

            if (total <= 1)
            {
                // Une seule reco (ou aucune) : un toast unique, texte résumé
                // (préfixé 🤖 — le Header n'est pas rendu par les clients).
                var single = new MessageCommand
                {
                    Header = string.Empty,
                    Text = "🤖 " + BuildToastText(payload, programmed),
                    TimeoutMs = popupMs,
                };
                await _sessions.SendMessageCommand(sessionId, sessionId, single, ct).ConfigureAwait(false);
                return;
            }

            for (int i = 0; i < total; i++)
            {
                bool last = (i == total - 1);
                string text = string.Format(CultureInfo.InvariantCulture,
                    "🤖 À regarder ce soir ({0}/{1}) — {2}", i + 1, total, BuildItemToastText(watch[i]));
                if (last && programmed > 0)
                {
                    text += string.Format(CultureInfo.InvariantCulture, " • {0} {1}",
                        programmed, programmed > 1 ? "enregistrements programmés" : "enregistrement programmé");
                }
                if (last && watch.Count > total)
                    text += " …";

                var msg = new MessageCommand
                {
                    // Header vide : les clients web/Android ne le rendent pas
                    // (vécu 2026-09-02) — tout est dans le Text.
                    Header = string.Empty,
                    Text = text,
                    // Chaque popup vit sa durée complète ; le suivant le
                    // remplace juste au moment où il expire.
                    TimeoutMs = popupMs,
                };
                await _sessions.SendMessageCommand(sessionId, sessionId, msg, ct).ConfigureAwait(false);

                if (!last)
                {
                    try { await Task.Delay(popupMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        /// <summary>Durée (ms) de CHAQUE popup de la séquence (et du toast
        /// unique) : <see cref="PluginConfiguration.LoginPopupSeconds"/>,
        /// plancher 4 s (en dessous, pas le temps de lire titre + méta ;
        /// défaut 8 s ≈ 24 s de séquence pour 3 recos).</summary>
        private static int PopupDurationMs() =>
            (int)(Math.Max(4, Plugin.Instance?.Configuration?.LoginPopupSeconds ?? 8) * 1000);

        /// <summary>
        /// Texte d'un toast D'UNE reco : titre + méta entre parenthèses
        /// (chaîne · heure de début · type). Court — une popup, une reco.
        /// Partagé avec <see cref="BuildToastText"/> (même rendu par item).
        /// </summary>
        private static string BuildItemToastText(WatchItem it)
        {
            string s = it.Title ?? "";
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(it.Channel)) meta.Add(it.Channel);
            if (it.Start.HasValue) meta.Add(it.Start.Value.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(it.Kind))
            {
                string k = string.Equals(it.Kind, "series", StringComparison.OrdinalIgnoreCase) ? "série"
                          : string.Equals(it.Kind, "movie", StringComparison.OrdinalIgnoreCase) ? "film" : it.Kind;
                meta.Add(k);
            }
            if (meta.Count > 0) s += " (" + string.Join(" · ", meta) + ")";
            return s;
        }

        /// <summary>
        /// Sondage des capacités de la session : attend que le client déclare
        /// <c>DisplayMessage</c> dans <see cref="SessionInfo.Capabilities"/>
        /// (<see cref="ClientCapabilities.SupportedCommands"/>). Repli sur le
        /// champ legacy <see cref="SessionInfo.SupportedCommands"/>. Retourne
        /// faux au bout de <paramref name="timeout"/> (→ cloche seule).
        /// </summary>
        private async Task<bool> WaitForDisplayAsync(string sessionId, TimeSpan timeout, CancellationToken ct)
        {
            if (SessionSupportsDisplay(sessionId)) return true;
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                try { await Task.Delay(500, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }
                if (SessionSupportsDisplay(sessionId)) return true;
            }
            return false;
        }

        private bool SessionSupportsDisplay(string sessionId)
        {
            try
            {
                var s = _sessions.Sessions?.FirstOrDefault(x => x.Id == sessionId);
                if (s == null) return false;
                var cmds = s.Capabilities?.SupportedCommands;
                if (cmds != null && cmds.Contains("DisplayMessage", StringComparer.OrdinalIgnoreCase)) return true;
                // Repli : champ legacy directement sur SessionInfo.
                var legacy = s.SupportedCommands;
                return legacy != null && legacy.Contains("DisplayMessage", StringComparer.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// Construit le texte RÉSUMÉ (toast unique + cloche persistante) :
        /// titres de TOUTES les suggestions Tonight + bilan
        /// d'auto-programmation. « À regarder ce soir : X, Y, Z. » +
        /// « N enregistrement(s) programmé(s). » (seulement si
        /// <paramref name="programmed"/> ≥ 0).
        /// </summary>
        private static string BuildToastText(string payload, int programmed)
        {
            var watch = ExtractWatchItems(payload);
            var sb = new System.Text.StringBuilder();
            if (watch.Count > 0)
            {
                sb.Append("À regarder ce soir : ");
                // Plafond à 4 titres pour rester lisible dans un résumé.
                var parts = new List<string>();
                foreach (var it in watch.Take(4))
                    parts.Add(BuildItemToastText(it));
                // Séparateur « • » : reste lisible même si le client collapse
                // les newlines. Le toast est texte seul (pas d'image, pas de
                // HTML : le client web HTML-encode Header/Text avant rendu).
                sb.Append(string.Join(" • ", parts));
                if (watch.Count > 4) sb.Append(" …");
                sb.Append(".");
            }
            else
            {
                sb.Append("Suggestions LLM AI prêtes.");
            }
            if (programmed >= 0)
            {
                sb.Append(" ");
                sb.Append(programmed);
                sb.Append(programmed > 1 ? " enregistrements programmés." : " enregistrement programmé.");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  Cloche persistante (notification Emby)
        // ------------------------------------------------------------------

        /// <summary>
        /// Envoie une notification cloche à l'usager (deep-link vers la racine
        /// Emby — la page « Recommandations LLM AI » est atteignable via le menu).
        /// Repli durable au toast : reste même si la session a fermé. Pattern
        /// identique à <c>LlmScheduledTask.SendRecommendationNotification</c>
        /// mais ciblant un seul usager.
        /// </summary>
        private void SendBellNotification(User user, string text)
        {
            if (_notifications == null || user == null) return;
            try
            {
                var req = new NotificationRequest
                {
                    // 🤖 en préfixe du titre : même marque que les popups
                    // (la cloche est le canal principal sur les clients TV).
                    Title = "🤖 LLM AI — À regarder ce soir",
                    Description = text,
                    Url = ResolveEmbyUrl(),
                    Date = DateTimeOffset.UtcNow,
                    Severity = LogSeverity.Info,
                    User = user,
                };
                _notifications.SendNotification(req);
                _logger?.Info("[LLM_AI] Cloche envoyée à « {0} ».", user.Name);
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] Cloche à « {0} » échouée : {1}", ex, user.Name, ex.Message);
            }
        }

        /// <summary>
        /// Construit la description MULTI-LIGNE de la cloche (notification
        /// Emby → notifiers configurés, ex. SMTP — test 2026-09-02 : les
        /// retours à la ligne passent tels quels dans le courriel). Contraire-
        /// ment au toast (texte souvent collapsé → séparateur « • » sur une
        /// ligne), chaque suggestion a SES lignes : méta, puis la raison 🤖
        /// en retrait — le courriel est l'endroit où la raison LLM complète
        /// a sa place (jamais dans un toast).
        /// </summary>
        private static string BuildBellText(string payload, int programmed)
        {
            var watch = ExtractWatchItems(payload);
            var sb = new System.Text.StringBuilder();
            if (watch.Count > 0)
            {
                sb.Append("À regarder ce soir :");
                foreach (var it in watch)
                {
                    sb.Append("\n• ").Append(BuildItemToastText(it));
                    if (!string.IsNullOrWhiteSpace(it.Reason))
                        sb.Append("\n    🤖 ").Append(it.Reason);
                }
                if (programmed > 0)
                    sb.Append("\n").Append(programmed)
                      .Append(programmed > 1 ? " enregistrements programmés." : " enregistrement programmé.");
            }
            else
            {
                sb.Append("Suggestions LLM AI prêtes.");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Résout l'URL de base d'Emby pour le deep-link de la notification
        /// (aucune IP codée). Repli sur <see cref="PluginConfiguration.EmbyPublicUrl"/>.
        /// Identique à <c>LlmScheduledTask.ResolveEmbyUrl</c>.
        /// </summary>
        private string ResolveEmbyUrl()
        {
            try
            {
                if (_host != null)
                {
                    string local = _host.GetLocalHostApiUrl();
                    if (!string.IsNullOrWhiteSpace(local)) return local;
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] ResolveEmbyUrl : GetLocalHostApiUrl a échoué ({0}) — repli sur EmbyPublicUrl.", ex.Message);
            }
            return (Plugin.Instance?.Configuration?.EmbyPublicUrl ?? string.Empty).Trim();
        }

        // ------------------------------------------------------------------
        //  Extraction des suggestions « À regarder ce soir » (pour les popups)
        // ------------------------------------------------------------------

        private struct WatchItem
        {
            public string Title;
            public string Channel;
            public DateTimeOffset? Start;
            public string Kind;
            public string Reason;
        }

        /// <summary>
        /// Extrait TOUTES les suggestions du payload Tonight — enregistrements
        /// non visionnés, items de bibliothèque ET programmes EPG live (vécu
        /// 2026-09-02 : un run 100 % live → 0 item « watchable now » → popup
        /// unique générique « Suggestions prêtes », alors que la sélection
        /// avait 3 recos à annoncer). Le popup publie la sélection complète
        /// « À regarder ce soir », comme la section homonyme de la page.
        /// Récupère titre + chaîne + heure de début + kind pour enrichir le
        /// toast (texte seul : pas d'image, pas de HTML — le client web
        /// HTML-encode Header/Text avant rendu).
        /// </summary>
        private static List<WatchItem> ExtractWatchItems(string payload)
        {
            var items = new List<WatchItem>();
            if (string.IsNullOrWhiteSpace(payload)) return items;
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(payload))
                {
                    if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return items;
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        if (el.ValueKind != System.Text.Json.JsonValueKind.Object) continue;

                        var it = new WatchItem();
                        if (el.TryGetProperty("title", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String)
                            it.Title = t.GetString()?.Trim();
                        if (el.TryGetProperty("channel", out var ch) && ch.ValueKind == System.Text.Json.JsonValueKind.String)
                            it.Channel = ch.GetString();
                        if (el.TryGetProperty("start", out var st) && st.ValueKind == System.Text.Json.JsonValueKind.String
                            && DateTimeOffset.TryParse(st.GetString(), out var dto))
                            it.Start = dto;
                        if (el.TryGetProperty("kind", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.String)
                            it.Kind = k.GetString();
                        // Raison LLM : pas pour le toast (trop long), mais pour
                        // la cloche multi-lignes (courriel) où elle a sa place.
                        if (el.TryGetProperty("reason", out var rs) && rs.ValueKind == System.Text.Json.JsonValueKind.String)
                            it.Reason = rs.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(it.Title)) items.Add(it);
                        if (items.Count >= 8) break;
                    }
                }
            }
            catch { /* payload non parsable : toast générique */ }
            return items;
        }
    }
}