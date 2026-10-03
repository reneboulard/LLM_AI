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
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;

namespace LLM_AI
{
    /// <summary>
    /// Endpoint HTTP « Atelier de langues » (v1.17.0 T1c) : expose
    /// <c>GET /Plugins/LLMAI/I18nGenerate</c> au panneau « Langues
    /// d'interface » de la config (T1a). Gabarit exact du run détaché
    /// d'audit (<see cref="AuditApiService"/>, v1.14.2) : la requête démarre
    /// la campagne en tâche de fond (résiliente à la fermeture d'onglet,
    /// single-flight contre les clics en rafale) et répond immédiatement ;
    /// la page pole <c>?Status=true</c> toutes les 5 s.
    /// </summary>
    /// <remarks>
    /// <para><b>GET, et non POST</b> : la T1a a câblé le client sur le
    /// pattern d'audit détaché (GET state-changing, gabarit v1.14.2) — le
    /// gabarit est ce qui fait foi, la spec initiale POST est dépassée.</para>
    /// <para>Couche HTTP fine : l'endpoint résout l'usager (admin), résout
    /// les backends via un <see cref="LlmRunner"/> jetable (l'INSTANCE est
    /// construite pour satisfaire le constructeur — inutilisée au-delà de
    /// <see cref="LlmRunner.ResolveBackends"/>), donne la main à
    /// <see cref="I18nGenerator"/> puis publie ses jalons structurés
    /// (<see cref="I18nMilestone"/>) localisés dans <see cref="I18nGenState"/>
    /// (la langue d'AFFICHAGE du poller peut être autre que la cible — le
    /// moteur, lui, reste sans opinion). Le rapport est persisté par le
    /// MOTEUR (une seule écriture, au même endroit que la vraie production)
    /// ; l'endpoint ne fait que l'état.</para>
    /// </remarks>
    public class I18nGenerateApiService : BaseApiService
    {
        private readonly IJsonSerializer _json;
        private readonly ILiveTvManager _liveTv;

        public I18nGenerateApiService(IJsonSerializer json, ILiveTvManager liveTv)
        {
            _json = json;
            _liveTv = liveTv;
        }

        // ------------------------------------------------------------------
        //  DTO requête / réponse
        // ------------------------------------------------------------------

        /// <summary>
        /// Requête GET <c>/Plugins/LLMAI/I18nGenerate</c>.
        /// <para><c>Status</c> : lecture seule de l'état du run détaché
        /// (running/progression/outcome) + du dernier rapport persisté —
        /// l'appel de polling du panneau (et sa reprise au rechargement :
        /// la réponse est identique).</para>
        /// <para>Sinon : démarrage d'une génération pour <c>Lang</c>
        /// (code libre 2-3 lettres, normalisé par le moteur) et <c>Mode</c>
        /// (full | missing | skipped — défaut « missing » = le plus sûr,
        /// la complétion ; les modes portent l'usager, jamais l'inverse).</para>
        /// </summary>
        [Route("/Plugins/LLMAI/I18nGenerate", "GET")]
        public class I18nGenerateRequest : IReturn<object>
        {
            public string Lang { get; set; }
            public string Mode { get; set; }
            public bool Status { get; set; }
        }

        /// <summary>
        /// Réponse renvoyée au navigateur (contrat T1a — noms de champs
        /// consommés par <c>i18nApplyStatus</c> dans config.js). <c>Report</c>
        /// = dernier rapport RÉUSSI persisté (<see cref="I18nGenReportStore"/>) —
        /// porté par TOUTES les réponses (dont le polling : quand
        /// <c>Outcome</c> passe à « ok », le rapport frais vient d'être
        /// enregistré par le moteur, zéro appel supplémentaire côté page).
        /// <c>Error</c> = échec du dernier run OU refus d'accès : toujours avec
        /// <c>Running=false</c> pour un refus d'accès (le client l'affiche et
        /// sort), mais en tant que champ de la photo du run pour un échec de
        /// run (l'appelant pollant le voit au même endroit).
        /// </summary>
        public class I18nGenResponse
        {
            public bool Running { get; set; }
            public string Progress { get; set; }
            public string StartedAt { get; set; }
            public string FinishedAt { get; set; }
            public string Outcome { get; set; }
            public string Error { get; set; }
            public string Report { get; set; }
            public string LastGeneratedAt { get; set; }
        }

        // ------------------------------------------------------------------
        //  Handler GET
        // ------------------------------------------------------------------

        public object Get(I18nGenerateRequest req)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new I18nGenResponse { Error = I18n.SDisplay("err.noconfig", ApplicationHost) };

            // Réservé aux administrateurs : l'atelier écrit le fichier de
            // langue du serveur et consomme des appels LLM réels (coût, quota
            // cloud) — la génération n'est pas une opération usager.
            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new I18nGenResponse { Error = I18n.SDisplay("err.admin", ApplicationHost) };

            // ---- ?Status=true : polling du run détaché -------------------
            // État live + dernier rapport persisté : quand Outcome passe à
            // « ok », la réponse porte le rapport frais (moteur l'a déjà
            // persisté — un polling suffit, comme l'audit).
            if (req?.Status ?? false)
            {
                var st = I18nGenState.Snapshot();
                var last = I18nGenReportStore.Load();
                return StatusResponse(st, last);
            }

            // ---- Démarrage DETACHÉ d'une génération ----------------------
            string lang = (req?.Lang ?? string.Empty).Trim().ToLowerInvariant();
            string mode = req?.Mode;

            // Jalon initial LOCALISÉ : affiché pendant la collecte (natives +
            // glossaire), avant le premier jalon de dose.
            if (!I18nGenState.TryStart(I18n.SDisplay("i18n.gen.progress.collect", ApplicationHost)))
            {
                // Single-flight : un run est déjà en cours — on rend son
                // état, la page reprend simplement le polling (clics absorbés).
                return StatusResponse(I18nGenState.Snapshot(), I18nGenReportStore.Load());
            }

            // Timeout dur de sécurité (25 min, gabarit audit) : sans lui, un
            // backend muet laisserait l'état « running » pour toujours et
            // bloquerait le single-flight. Le token traverse le moteur (chaque
            // appel LLM le porte) — les doses en cours sont jetées avec le run.
            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(25));

            // Backends résolus PAR L'APPELANT (T1c) : le moteur reçoit la
            // liste + les clés en ctor — zéro accès config à l'intérieur du
            // run (découplage testé au harnais, le moteur est un pur moteur).
            var runner = new LlmRunner(Logger, _json, LibraryManager, UserManager, _liveTv, ApplicationHost);
            var backends = runner.ResolveBackends(cfg);
            string ollamaKey = LlmRunner.ResolveKey(cfg.OllamaApiKey, "OLLAMA_API_KEY");
            string geminiKey = LlmRunner.ResolveKey(cfg.GeminiApiKey, "GEMINI_API_KEY");

            var genie = new I18nGenerator(cfg, backends, ollamaKey, geminiKey,
                _json, Logger, ApplicationHost, ProgressMilestone, cts.Token);

            var run = Task.Run(async () =>
            {
                try
                {
                    var res = await genie.RunAsync(lang, mode).ConfigureAwait(false);
                    if (res.Ok)
                        I18nGenState.FinishOk();      // rapport déjà persisté par le moteur
                    else
                        I18nGenState.FinishError(res.Error);
                }
                catch (OperationCanceledException)
                {
                    Logger?.Warn("[LLM_AI] I18n génération annulée (timeout 25 min ou arrêt) — le fichier de langue précédent reste en place (.bak au dernier write).");
                    I18nGenState.FinishError(I18n.SDisplay("i18n.gen.err.timeout", ApplicationHost));
                }
                catch (Exception ex)
                {
                    Logger?.ErrorException("[LLM_AI] Échec de la génération de langue : {0}", ex, ex.Message);
                    // Erreur AFFICHÉE : préfixe localisé + message d'exception
                    // brut (le message d'exception n'est pas le nôtre — pas
                    // localisable).
                    I18nGenState.FinishError(
                        I18n.SFormatDisplay("i18n.gen.err.fail", ApplicationHost, ex.Message));
                }
                finally
                {
                    cts.Dispose();
                }
            }, CancellationToken.None);

            // On n'attend PAS le run : réponse immédiate d'acquittement
            // (Running=true) — la page pole ?Status=true. Le wrapper attrape
            // tout : la tâche ne lève jamais ; run est une référence tenue
            // vivante (comme l'audit).
            _ = run;

            return StatusResponse(I18nGenState.Snapshot(), I18nGenReportStore.Load());
        }

        // ------------------------------------------------------------------
        //  Jalons : événements STRUCTURÉS du moteur → texte d'état localisé
        // ------------------------------------------------------------------

        /// <summary>Publier un jalon du moteur en texte d'affichage. kinds :
        /// collect (base+glossaire), dose (n/total — nom — clés), repair,
        /// write — miroir exact des chaînes <c>i18n.gen.progress.*</c>.
        /// Les exceptions d'un jalon ne tuent jamais le run (le delegate du
        /// moteur attrape déjà, ce guard est la seconde ceinture).</summary>
        private void ProgressMilestone(string kind, int doseIndex, int doseCount,
            string doseName, int keys)
        {
            try
            {
                string msg;
                if (kind == "dose")
                    msg = I18n.SFormatDisplay("i18n.gen.progress.dose", ApplicationHost,
                        doseIndex, doseCount, doseName, keys);
                else if (kind == "repair")
                    msg = I18n.SFormatDisplay("i18n.gen.progress.repair", ApplicationHost, keys);
                else if (kind == "write")
                    msg = I18n.SFormatDisplay("i18n.gen.progress.write", ApplicationHost);
                else
                    msg = I18n.SFormatDisplay("i18n.gen.progress.collect", ApplicationHost);
                I18nGenState.SetProgress(msg);
            }
            catch { /* un jalon ne doit jamais tuer le run */ }
        }

        // ------------------------------------------------------------------
        //  Photo d'état → réponse HTTP
        // ------------------------------------------------------------------

        /// <summary>Traduit une photo de <see cref="I18nGenState"/> (+ éventuel
        /// dernier rapport persisté) en réponse HTTP. Les dates partent en
        /// UTC ISO (« o »), null si absentes.</summary>
        private static I18nGenResponse StatusResponse(AuditRunSnapshot st, LastI18nGenReport last)
        {
            return new I18nGenResponse
            {
                Running = st.Running,
                Progress = st.Progress,
                StartedAt = st.StartedAt.HasValue
                    ? st.StartedAt.Value.ToString("o", CultureInfo.InvariantCulture) : null,
                FinishedAt = st.FinishedAt.HasValue
                    ? st.FinishedAt.Value.ToString("o", CultureInfo.InvariantCulture) : null,
                Outcome = st.Outcome,
                Error = st.Error,
                Report = last?.Report,
                LastGeneratedAt = last != null && last.GeneratedAt != default
                    ? last.GeneratedAt.ToString("o", CultureInfo.InvariantCulture) : null
            };
        }

        // ------------------------------------------------------------------
        //  Auth : résolution de l'administrateur appelant (pattern audit)
        // ------------------------------------------------------------------

        /// <summary>Résout l'usager à partir du token d'authentification
        /// (calque <see cref="AuditApiService.ResolveAdmin"/> : User du token
        /// d'abord, puis UserId). L'appelant vérifie IsAdministrator.</summary>
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