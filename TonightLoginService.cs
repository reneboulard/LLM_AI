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
            if (string.IsNullOrEmpty(toast)) { SendBellNotification(user, toast); return; }

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
                    var msg = new MessageCommand
                    {
                        Header = string.Empty,
                        Text = toast,
                        TimeoutMs = (long)(Math.Max(2, Plugin.Instance?.Configuration?.LoginPopupSeconds ?? 8) * 1000),
                    };
                    await _sessions.SendMessageCommand(sessionId, sessionId, msg, ct).ConfigureAwait(false);
                    _logger?.Info("[LLM_AI] Toast envoyé à la session « {0} » (DisplayMessage).", user.Name);
                }
                catch (Exception ex)
                {
                    // Session fermée avant la fin du run → la cloche prend le
                    // relais. On ne logue qu'en info.
                    _logger?.Info("[LLM_AI] Toast échoué pour « {0} » (session fermée ?) : {1}", user.Name, ex.Message);
                }
            }
            else
            {
                _logger?.Info("[LLM_AI] Session « {0} » sans DisplayMessage (sous 10 s) → cloche seule.", user.Name);
            }

            // Cloche persistante (deep-link) — toujours, même si le toast a
            // réussi : elle survive si l'usager ferme le toast, et deep-link
            // vers la page Recommandations (web). Sur la TV native, la cloche
            // est le canal principal (le toast texte seul n'est pas jouable).
            SendBellNotification(user, toast);
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
        /// Construit le texte du toast : titres du watch bucket (à regarder
        /// maintenant) + bilan d'auto-programmation. Court (toast) :
        /// « À regarder ce soir : X, Y, Z. » + « N enregistrement(s) programmé(s). »
        /// (seulement si <paramref name="programmed"/> &gt;= 0).
        /// </summary>
        private static string BuildToastText(string payload, int programmed)
        {
            var watch = ExtractWatchItems(payload);
            var sb = new System.Text.StringBuilder();
            if (watch.Count > 0)
            {
                sb.Append("À regarder ce soir : ");
                // Plafond à 4 titres pour rester lisible dans un toast.
                var parts = new List<string>();
                foreach (var it in watch.Take(4))
                {
                    string s = it.Title ?? "";
                    var meta = new List<string>();
                    if (!string.IsNullOrWhiteSpace(it.Channel)) meta.Add(it.Channel);
                    if (it.Start.HasValue) meta.Add(it.Start.Value.LocalDateTime.ToString("HH:mm"));
                    if (!string.IsNullOrWhiteSpace(it.Kind))
                    {
                        string k = string.Equals(it.Kind, "series", StringComparison.OrdinalIgnoreCase) ? "série"
                                  : string.Equals(it.Kind, "movie", StringComparison.OrdinalIgnoreCase) ? "film" : it.Kind;
                        meta.Add(k);
                    }
                    if (meta.Count > 0) s += " (" + string.Join(" · ", meta) + ")";
                    parts.Add(s);
                }
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
                    Title = "LLM AI — À regarder ce soir",
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
        //  Extraction des titres du watch bucket (pour le toast)
        // ------------------------------------------------------------------

        private struct WatchItem
        {
            public string Title;
            public string Channel;
            public DateTimeOffset? Start;
            public string Kind;
        }

        /// <summary>
        /// Extrait les items du watch bucket (source="recording" ou "library",
        /// ou live possédée via library_id) — ce que l'usager peut regarder
        /// maintenant. Récupère titre + chaîne + heure de début + kind pour
        /// enrichir le toast (le toast est texte seul : pas d'image, pas de
        /// HTML — le client web HTML-encode Header/Text avant rendu).
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
                        if (!el.TryGetProperty("source", out var s) || s.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                        string source = s.GetString();
                        bool watch = string.Equals(source, "recording", StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(source, "library", StringComparison.OrdinalIgnoreCase);
                        // Live possédée (library_id) = à regarder depuis la biblio.
                        if (!watch && string.Equals(source, "live", StringComparison.OrdinalIgnoreCase)
                            && el.TryGetProperty("library_id", out var lid) && lid.ValueKind == System.Text.Json.JsonValueKind.String
                            && !string.IsNullOrEmpty(lid.GetString()))
                            watch = true;
                        if (!watch) continue;

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