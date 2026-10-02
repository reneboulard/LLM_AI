using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;

namespace LLM_AI
{
    /// <summary>
    /// Endpoint HTTP plugin « Audit santé » : expose
    /// <c>GET /Plugins/LLMAI/Audit</c> à la page de config pour produire un
    /// rapport de santé du serveur Emby à la demande. Couche HTTP fine,
    /// calquée sur <see cref="TonightApiService"/> : résout l'usager appelant
    /// (admin uniquement), construit le prompt d'audit (template config +
    /// focus optionnel) puis délègue le run agent à
    /// <see cref="LlmRunner.RunAuditAsync"/> (backends LLM partagés, outil
    /// <c>system_audit</c> dédié). Retourne le rapport Markdown brut.
    /// </summary>
    /// <remarks>
    /// Service ServiceStack découvert par scanning d'assembly : hérite
    /// <see cref="BaseApiService"/> (propriétés DI peuplées par l'hôte :
    /// Logger, UserManager, LibraryManager, ApplicationHost,
    /// AuthorizationContext, Request) et injecte via constructeur les
    /// services d'audit non exposés par la base :
    /// <see cref="ISessionManager"/>, <see cref="ITaskManager"/>,
    /// <see cref="INotificationManager"/>, <see cref="IJsonSerializer"/> et
    /// <see cref="ILiveTvManager"/> (ce dernier uniquement pour construire
    /// <see cref="LlmRunner"/> proprement — inutilisé sur le path d'audit).
    /// La route est portée par le DTO requête <see cref="AuditRequest"/> via
    /// <see cref="RouteAttribute"/>.
    /// </remarks>
    public class AuditApiService : BaseApiService
    {
        private readonly ISessionManager _sessions;
        private readonly ITaskManager _tasks;
        private readonly INotificationManager _notifications;
        private readonly IJsonSerializer _json;
        private readonly ILiveTvManager _liveTv;

        public AuditApiService(ISessionManager sessions, ITaskManager tasks,
            INotificationManager notifications, IJsonSerializer json,
            ILiveTvManager liveTv)
        {
            _sessions = sessions;
            _tasks = tasks;
            _notifications = notifications;
            _json = json;
            _liveTv = liveTv;
        }

        // ------------------------------------------------------------------
        //  DTO requête / réponse
        // ------------------------------------------------------------------

        /// <summary>
        /// Requête GET <c>/Plugins/LLMAI/Audit</c>.
        /// <c>Focus</c> : texte libre optionnel pour orienter l'audit (ex.
        /// « transcoding », « disk », ou une demande explicite de remédiation
        /// comme « arrête la session XYZ »). Appendé au template de prompt
        /// <see cref="PluginConfiguration.AuditPrompt"/>.
        /// <para><c>Last</c> : lecture seule — renvoie le dernier rapport
        /// persisté (<see cref="AuditReportStore"/>) SANS exécuter d'audit
        /// (zéro LLM) : c'est l'appel du chargement de la page. Null/absent =
        /// démarrage d'un nouvel audit (détaché, v1.14.2 — voir
        /// <see cref="AuditRunState"/>).</para>
        /// <para><c>Status</c> : lecture seule de l'état du run détaché
        /// (running/progression/outcome) + du dernier rapport persisté :
        /// c'est l'appel de polling de la page pendant un run.</para>
        /// </summary>
        [Route("/Plugins/LLMAI/Audit", "GET")]
        public class AuditRequest : IReturn<object>
        {
            public string Focus { get; set; }
            public bool Last { get; set; }
            public bool Status { get; set; }
        }

        /// <summary>
        /// Réponse renvoyée au navigateur. <c>Report</c> est le rapport
        /// Markdown brut produit par l'agent (rendu côté config.js via un
        /// mini-convertisseur Markdown→HTML sûr). <c>Date</c> : date/heure
        /// (UTC ISO) de production. <c>Enabled</c> : false si l'audit est
        /// désactivé en config. <c>Error</c> : message (ex. accès non-admin).
        /// <para><c>LastReport</c>/<c>LastGeneratedAt</c>/<c>LastMode</c> :
        /// le dernier rapport RÉUSSI persisté (<see cref="AuditReportStore"/>,
        /// v1.13.10) — renvoyés même sans exécution, pour l'affichage par
        /// défaut dans la page. Sur un run réussi ils reflètent le rapport
        /// courant (<c>Report</c>/<c>Date</c> restent les champs du run).
        /// Jamais peuplés pour un appelant non-admin : le rapport expose l'état
        /// du serveur.</para>
        /// <para><c>Running</c>/<c>Progress</c>/<c>StartedAt</c> : état du run
        /// <b>détaché</b> (v1.14.2) — le POST de départ répond immédiatement
        /// avec Running=true, la page pole <c>?Status=true</c>.
        /// <c>FinishedAt</c>/<c>Outcome</c> : le run détaché terminé (« ok » =
        /// un rapport vient d'être persisté → relire LastReport ; « error » =
        /// échec, <c>Error</c> porte le message).</para>
        /// </summary>
        public class AuditResponse
        {
            public bool Enabled { get; set; }
            public string Report { get; set; }
            public string Date { get; set; }
            public string Error { get; set; }
            public string LastReport { get; set; }
            public string LastGeneratedAt { get; set; }
            public string LastMode { get; set; }
            public bool Running { get; set; }
            public string Progress { get; set; }
            public string StartedAt { get; set; }
            public string FinishedAt { get; set; }
            public string Outcome { get; set; }
        }

        // ------------------------------------------------------------------
        //  Handler GET
        // ------------------------------------------------------------------

        public object Get(AuditRequest req)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new AuditResponse { Enabled = false, Error = I18n.SDisplay("err.noconfig", ApplicationHost) };

            if (!cfg.AuditEnabled)
                return new AuditResponse { Enabled = false };

            // Réservé aux administrateurs : un audit santé expose l'état du
            // serveur (sessions, chemins, disques) et peut exécuter des actions
            // de remédiation — on ne laisse pas un usager ordinaire l'invoquer.
            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new AuditResponse { Enabled = true, Error = I18n.SDisplay("err.admin", ApplicationHost) };

            // ---- ?Status=true : polling du run détaché (v1.14.2) ---------
            // État live (running/progression/outcome) + dernier rapport
            // persisté : quand Outcome passe à « ok », la réponse elle-même
            // porte le rapport frais (zéro appel supplémentaire côté page).
            if (req?.Status ?? false)
            {
                var st = AuditRunState.Snapshot();
                var lastS = AuditReportStore.Load();
                return SnapshotResponse(st, lastS);
            }

            // ---- ?Last=true : lecture seule du dernier rapport -----------
            // (chargement de la page) : zéro LLM, aucune exécution. Absent =
            // LastReport null (la page ne montre que l'état « pas encore
            // d'audit persisté »). L'état du run détaché est aussi renvoyé :
            // un rechargement de page pendant un run reprend le polling.
            if (req?.Last ?? false)
            {
                var stL = AuditRunState.Snapshot();
                var last = AuditReportStore.Load();
                var respL = SnapshotResponse(stL, last);
                respL.LastReport = last?.Report;
                respL.LastGeneratedAt = last != null && last.GeneratedAt != default
                    ? last.GeneratedAt.ToString("o", CultureInfo.InvariantCulture)
                    : null;
                respL.LastMode = last?.Mode;
                return respL;
            }

            // ---- Démarrage DETACHÉ d'un nouvel audit (v1.14.2) -----------
            // Terrain 2026-10-01 : le run historique était attaché à la
            // requête HTTP — fermer/rafraîchir l'onglet annulait tout (« Audit
            // annulé » ×9, rapport perdu à la dose 7/7), et des clics en
            // rafale lançaient N audits concurrents. Nouveau contrat : la
            // requête démarre le run en tâche de fond et répond IMMÉDIATEMENT
            // (Running=true) ; la page pole ?Status=true ; le rapport se
            // persiste à la fin comme avant. TryStart = single-flight : un
            // clic pendant un run renvoie l'état en cours au lieu d'en
            // relancer un neuf.

            // Prompt = template config + focus optionnel (l'orientation ou la
            // demande explicite de remédiation de l'usager).
            string prompt = cfg.AuditPrompt ?? string.Empty;
            string focus = req?.Focus;
            if (!string.IsNullOrWhiteSpace(focus))
                prompt += "\n\n### Focus demandé\n" + focus.Trim();

            // Jalon initial LOCALISÉ (v1.15.0.5) : la fenêtre d'audit l'affiche
            // pendant toute la phase de collecte (en mode single, pendant tout
            // le run — la boucle agent n'a pas de jalons intermédiaires).
            if (!AuditRunState.TryStart(I18n.SDisplay("audit.progress.collect", ApplicationHost)))
            {
                // Single-flight : un run est déjà en cours — on rend son état,
                // la page reprend simplement le polling.
                var stBusy = AuditRunState.Snapshot();
                var lastB = AuditReportStore.Load();
                var respB = SnapshotResponse(stBusy, lastB);
                respB.LastReport = lastB?.Report;
                respB.LastGeneratedAt = lastB != null && lastB.GeneratedAt != default
                    ? lastB.GeneratedAt.ToString("o", CultureInfo.InvariantCulture)
                    : null;
                respB.LastMode = lastB?.Mode;
                return respB;
            }

            // Mode capturé AVANT le run : l'usager peut basculer le sélecteur
            // pendant que le run tourne.
            string mode = cfg.AuditMode;

            // Timeout dur de sécurité (25 min) : sans lui, un backend muet
            // laisserait l'état « running » pour toujours et bloquerait le
            // single-flight. Les timeouts HttpClient internes continuent de
            // piloter les replis backend comme avant.
            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(25));

            // LlmRunner construit avec les services de la base + liveTv (pour
            // satisfaire le constructeur ; inutilisé sur le path d'audit).
            var runner = new LlmRunner(Logger, _json, LibraryManager, UserManager, _liveTv, ApplicationHost);

            var run = Task.Run(async () =>
            {
                string report = null;
                try
                {
                    report = await runner.RunAuditAsync(cfg, "AUDIT", prompt, _sessions, _tasks, _notifications, cts.Token)
                        .ConfigureAwait(false);

                    // Persistance du dernier rapport : UNIQUEMENT si le run a
                    // produit un vrai rapport (voir IsRealReport). Best-effort :
                    // un échec IO n'interrompt pas.
                    if (IsRealReport(report))
                    {
                        AuditReportStore.Save(new LastAuditReport
                        {
                            GeneratedAt = DateTimeOffset.UtcNow,
                            Mode = mode,
                            Focus = focus,
                            Report = report
                        }, Logger);
                        AuditRunState.FinishOk();
                    }
                    else
                    {
                        // « Aucun backend configuré… », « Échec de l'audit… »,
                        // vide : le run a terminé SANS rapport — l'ancien
                        // rapport persisté reste le dernier valide.
                        // (report est déjà localisé par LlmRunner ; le repli
                        // « aucun rapport produit » suit la langue
                        // d'interface — v1.15.0.5.)
                        AuditRunState.FinishError(report ?? I18n.SDisplay("audit.err.noreport", ApplicationHost));
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger?.Warn("[LLM_AI] Audit détaché annulé (timeout 25 min ou arrêt) — le dernier rapport persisté reste affiché.");
                    AuditRunState.FinishError(I18n.SDisplay("audit.err.timeout", ApplicationHost));
                }
                catch (Exception ex)
                {
                    Logger?.ErrorException("[LLM_AI] Échec de l'audit détaché : {0}", ex, ex.Message);
                    // Erreur AFFICHÉE dans la fenêtre : préfixe localisé +
                    // message d'exception brut (le message d'exception n'est
                    // pas le nôtre — pas localisable).
                    AuditRunState.FinishError(string.Format(I18n.SDisplay("audit.err.fail", ApplicationHost), ex.Message));
                }
                finally
                {
                    cts.Dispose();
                }
            }, CancellationToken.None);

            // On n'attend PAS le run : réponse immédiate d'acquittement. La
            // tâche de fond vit sa vie (le log du run reste « [AUDIT] Rapport
            // d'audit » comme avant) et elle ne lève jamais (le wrapper
            // attrape tout) — run est juste une référence tenue vivante.
            _ = run;

            var stStart = AuditRunState.Snapshot();
            return SnapshotResponse(stStart, null);
        }

        // ------------------------------------------------------------------
        //  Helpers du run détaché
        // ------------------------------------------------------------------

        /// <summary>
        /// Traduit une photo de <see cref="AuditRunState"/> (+ éventuel
        /// dernier rapport persisté) en réponse HTTP. Les dates partent en
        /// UTC ISO (« o »), null si absentes.
        /// </summary>
        private static AuditResponse SnapshotResponse(AuditRunSnapshot st, LastAuditReport last)
        {
            return new AuditResponse
            {
                Enabled = true,
                Running = st.Running,
                Progress = st.Progress,
                StartedAt = st.StartedAt.HasValue
                    ? st.StartedAt.Value.ToString("o", CultureInfo.InvariantCulture) : null,
                FinishedAt = st.FinishedAt.HasValue
                    ? st.FinishedAt.Value.ToString("o", CultureInfo.InvariantCulture) : null,
                Outcome = st.Outcome,
                Error = st.Error,
                LastReport = last?.Report,
                LastGeneratedAt = last != null && last.GeneratedAt != default
                    ? last.GeneratedAt.ToString("o", CultureInfo.InvariantCulture) : null,
                LastMode = last?.Mode
            };
        }

        /// <summary>
        /// Un vrai rapport d'audit (persistable) : non vide et ne commençant
        /// ni par une phrase d'erreur de <c>RunAuditAsync</c> (« Aucun
        /// backend… », « Échec de l'audit… »), ni par autre chose que du
        /// Markdown de rapport. Ces messages d'échec ne doivent JAMAIS
        /// écraser le dernier vrai rapport persisté.
        /// </summary>
        internal static bool IsRealReport(string report)
        {
            return !string.IsNullOrWhiteSpace(report) &&
                !report.StartsWith("Aucun backend", StringComparison.OrdinalIgnoreCase) &&
                !report.StartsWith("Échec de l'audit", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        //  Auth : résolution de l'administrateur appelant
        // ------------------------------------------------------------------

        /// <summary>
        /// Résout l'usager à partir du token d'authentification. Calqué sur
        /// <c>TonightApiService.ResolveUser</c> : priorité au User du token,
        /// puis au UserId (Int64) du token. Retourne null si non authentifié.
        /// L'appelant vérifie ensuite <see cref="User.Policy"/>'s IsAdministrator.
        /// </summary>
        private User ResolveAdmin()
        {
            try
            {
                var auth = AuthorizationContext?.GetAuthorizationInfo(Request);
                var user = auth?.User;
                if (user == null && auth != null && auth.UserId != 0)
                    user = UserManager.GetUserById(auth.UserId);
                return user;
            }
            catch { return null; }
        }
    }
}