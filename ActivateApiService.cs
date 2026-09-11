using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace LLM_AI
{
    /// <summary>
    /// Endpoint HTTP plugin activant un enregistrement depuis une carte
    /// <c>.strm</c> de la bibliothèque dédiée « AI Suggestions ». Expose
    /// <c>GET /Plugins/LLMAI/Activate?programId=…&amp;kind=…&amp;card=…&amp;t=…</c>.
    /// </summary>
    /// <remarks>
    /// <para>Mécanisme : la tâche planifiée (<c>StrmLibraryGenerator</c>) écrit
    /// une carte <c>.strm</c> par reco du record bucket dont l'URL pointe ici.
    /// Quand l'usager lit la carte, le lecteur média demande cette URL ; le
    /// plugin crée alors le timer Emby (série → SeriesTimer, film → Timer via
    /// <see cref="AutoProgrammer.ProgramOneAsync"/>), notifie le client par toast
    /// (<see cref="ActivateFeedback"/>, v1.12 — succès/échec/disque plein), fait
    /// supprimer la carte par Emby en cas de succès, puis renvoie un court clip
    /// de confirmation <c>recording_activated.mp4</c> (8 s, sans texte ni audio — universel).</para>
    /// <para><b>Auth</b> : l'URL <c>.strm</c> est demandée par le lecteur média
    /// (et par <c>ffprobe</c>/le transcodeur côté serveur) lors de la lecture,
    /// qui ne transmet PAS les en-têtes d'auth Emby. Le DTO requête
    /// <see cref="ActivateRequest"/> est donc décoré <c>[Unauthenticated]</c>
    /// pour désactiver l'auth Emby sur cette route — sinon le filtre d'auth
    /// renvoie 401 avant même que <see cref="Get"/> ne s'exécute (échec probe
    /// → « No compatible streams »). La seule gate d'accès reste le jeton de
    /// capacité <c>t</c> (voir <see cref="PluginConfiguration.StrmSecret"/>)
    /// embarqué dans l'URL et vérifié ici en comparaison à temps constant.
    /// Mismatch → corps vide + 404 (aucun timer créé).</para>
    /// <para>ServiceStack découvert par scanning d'assembly (comme
    /// <c>TonightApiService</c>) : hérite <see cref="BaseApiService"/> (Logger,
    /// Request peuplés par l'hôte) et injecte via constructeur les services non
    /// exposés par la base : <see cref="ILiveTvManager"/> et
    /// <see cref="ILibraryManager"/> (pour construire un
    /// <see cref="AutoProgrammer"/>). La route est portée par le DTO requête
    /// <see cref="ActivateRequest"/> via <see cref="RouteAttribute"/>.</para>
    /// <para><b>Streaming</b> : le clip (≈545 Ko, 8 s, 720p, sans piste audio)
    /// est renvoyé comme un <c>byte[]</c> ; les en-têtes (Content-Type,
    /// Accept-Ranges, Content-Range pour une requête <c>Range</c>) sont posés sur
    /// <c>Request.Response</c> (<c>IResponse</c>) avant le retour. L'API
    /// <c>IResponse</c> de cet hôte n'expose pas <c>OutputStream</c> : on délègue
    /// donc l'écriture du corps au framework en retournant le tableau d'octets
    /// (tranche pour une Range).</para>
    /// </remarks>
    public class ActivateApiService : BaseApiService
    {
        private readonly ILiveTvManager _liveTv;
        private readonly ILibraryManager _library;
        private readonly IServerApplicationHost _host;

        // Clip de confirmation embarqué (LLM_AI.recording_activated.mp4),
        // chargé une fois en mémoire statique.
        private static readonly byte[] s_clip = LoadClip();
        private const string ClipResource = "LLM_AI.recording_activated.mp4";

        public ActivateApiService(ILiveTvManager liveTv, ILibraryManager library,
            IServerApplicationHost host = null)
        {
            _liveTv = liveTv;
            _library = library;
            _host = host;
        }

        // ------------------------------------------------------------------
        //  DTO requête
        // ------------------------------------------------------------------

        /// <summary>
        /// Requête GET <c>/Plugins/LLMAI/Activate</c>.
        /// <c>ProgramId</c> : id de programme EPG (tel que repris depuis l'EPG
        /// par la reco). <c>Kind</c> : « series » ou « movie » (détermine
        /// SeriesTimer vs Timer). <c>Card</c> : nom du dossier de la carte
        /// (identité de la carte, écrit par <c>StrmLibraryGenerator</c> — permet
        /// le toast de confirmation et la suppression de la carte par Emby en
        /// cas de succès, voir <see cref="ActivateFeedback"/> ; absent sur les
        /// cartes d'avant v1.12 → feedback simplement ignoré). <c>T</c> : jeton
        /// de capacité (<see cref="PluginConfiguration.StrmSecret"/>) — gate
        /// d'accès, le lecteur média ne transmettant pas l'auth Emby ; complété
        /// par le gate de permission asynchrone (v1.13.11.0) : le lecteur
        /// identifié via sa session sans <c>EnableLiveTvManagement</c> voit les
        /// timers créés par sa lecture annulés et reçoit un toast dédié.
        /// </summary>
        [Route("/Plugins/LLMAI/Activate", "GET")]
        [Unauthenticated]
        public class ActivateRequest : IReturn<object>
        {
            public string ProgramId { get; set; }
            public string Kind { get; set; }
            public string Card { get; set; }
            public string T { get; set; }
        }

        // ------------------------------------------------------------------
        //  Handler GET
        // ------------------------------------------------------------------

        public async Task<object> Get(ActivateRequest req)
        {
            var cfg = Plugin.Instance?.Configuration;

            // Gate : feature désactivée OU jeton manquant/incorrect OU clip
            // absent → corps vide + 404. Aucun timer créé. La sécurité tient
            // même si le framework réécrit le status code : la gate est le jeton.
            if (cfg == null || !cfg.StrmLibraryEnabled ||
                !ConstantTimeEquals(req?.T, cfg.StrmSecret) ||
                s_clip == null || s_clip.Length == 0)
            {
                TrySetStatus(404);
                return Array.Empty<byte>();
            }

            // ---- Fraîcheur de l'activation (UNE décision par lecture, v1.13.11.0) ----
            // Une même lecture de carte génère plusieurs GET (sonde ffprobe,
            // requêtes Range du lecteur). La fraîcheur est décidée ICI, une fois
            // (TTL 5 min d'ActivateFeedback) : seul le premier GET de la lecture
            // tente la création de timer et déclenche le gate de permission ; les
            // suivants servent le clip sans réactivation (idempotence élargie :
            // la création n'est plus refaite à chaque GET — le dedup la
            // neutralisait de toute façon, et un timer annulé par le gate ne
            // serait pas recréé par un GET tardif de la même lecture).
            string strmPath = TryResolveCardPath(req?.Card);
            string key = strmPath ?? string.Join("|", req?.ProgramId ?? "?", req?.Kind ?? "?");
            if (!ActivateFeedback.TryMarkFresh(key))
            {
                Logger?.Info("[LLM_AI] Activate : activation récente (key={0}) — clip servi sans réactivation.", key);
                return ClipResponse();
            }

            // ---- Activation de l'enregistrement (best-effort) ----
            // Réutilise la même logique que la tâche planifiée : dedup contre
            // les timers existants, puis SeriesTimer (série) / Timer (film).
            var sessions = _host?.TryResolve<ISessionManager>();
            var users = _host?.TryResolve<MediaBrowser.Controller.Library.IUserManager>();
            var preTimerIds = new HashSet<string>(StringComparer.Ordinal);
            AutoProgrammer.OneOutcome? outcome = null;
            try
            {
                var ct = Request?.CancellationToken ?? CancellationToken.None;

                // Gate disque (même règle que AutoProgrammer.Program) : disque
                // d'enregistrements sous le seuil → aucun nouveau timer ; la
                // passe de tag « AI Delete » (opt-in) est déclenchée si activée.
                if (cfg?.RecordingDiskThresholdGb > 0
                    && RecordingDiskManager.TryResolveRecordingPath(_host, Logger, out string recPath)
                    && RecordingDiskManager.IsBelowThreshold(cfg, recPath, Logger, out long freeBytes, out long thresholdBytes))
                {
                    Logger?.Warn("[LLM_AI] Activate suspendu : {0} Go libre sur le volume d'enregistrements, sous le seuil ({1} Go) — aucun nouveau timer.",
                        (freeBytes / 1073741824.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                        (thresholdBytes / 1073741824.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
                    if (cfg.RecordingTaggingEnabled)
                    {
                        // Fire-and-forget : le passage de la carte ne doit pas
                        // attendre la passe de tag (best-effort, la passe ne
                        // lève jamais) — CancellationToken.None, la passe
                        // survit à la requête.
                        var host = _host;
                        var lib = _library;
                        _ = System.Threading.Tasks.Task.Run(() => RecordingDiskManager.RunTagPassAsync(
                            lib, recPath,
                            host?.TryResolve<MediaBrowser.Controller.Library.IUserManager>(),
                            host?.TryResolve<MediaBrowser.Controller.Notifications.INotificationManager>(),
                            host, Logger, cfg, CancellationToken.None));
                    }
                    DispatchFeedback(AutoProgrammer.OneOutcome.Failed, req, gateDisk: true);
                    return ClipResponse();
                }

                var ap = new AutoProgrammer(_liveTv, _library, Logger, _host);
                var programIds = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var names = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

                // Capture des ids de timers PRÉEXISTANTS (avant création) — le
                // gate permission (ci-dessous) n'annulera QUE les timers créés
                // par cette activation, jamais un timer antérieur légitime.
                try
                {
                    foreach (var t in _liveTv.GetTimers(new TimerQuery { IsScheduled = true })?.Items ?? new TimerInfoDto[0])
                        if (!string.IsNullOrEmpty(t?.Id)) preTimerIds.Add(t.Id);
                    foreach (var s in _liveTv.GetSeriesTimers(new SeriesTimerQuery())?.Items ?? new SeriesTimerInfoDto[0])
                        if (!string.IsNullOrEmpty(s?.Id)) preTimerIds.Add(s.Id);
                }
                catch (Exception ex)
                {
                    // Capture impossible : le gate n'annulera rien (fail-open
                    // total pour cette lecture) — cohérent avec le dedup
                    // tolérant d'AutoProgrammer.
                    Logger?.Warn("[LLM_AI] Activate : capture des timers préexistants échouée : {0}", ex.Message);
                }

                ap.BuildExistingTimerSets(programIds, names);

                var reco = new AutoProgrammer.Reco
                {
                    Id = req.ProgramId,
                    Kind = req.Kind,
                    Title = req.ProgramId
                };
                outcome = await ap.ProgramOneAsync(reco, programIds, names, ct).ConfigureAwait(false);
                Logger?.Info("[LLM_AI] Activate programId={0} kind={1} → {2}.", req.ProgramId, req.Kind, outcome);
            }
            catch (Exception ex)
            {
                // Un échec (programme déjà diffusé, conflit tuner…) est logué :
                // on renvoie quand même le clip — l'usager a cliqué, on confirme
                // visuellement, le détail est dans le journal.
                Logger?.Warn("[LLM_AI] Activate : échec création timer (programId={0}) : {1}", req.ProgramId, ex.Message);
            }

            // ---- Gate permission (v1.13.11.0) : contrôle ASYNCHRONE ----
            // Les requêtes .strm ne portent PAS l'auth Emby, et la session
            // lecteur n'est visible dans ISessionManager qu'APRÈS l'ouverture
            // du flux (c'est pour cela que le toast v1.12 poll en arrière-plan).
            // Un contrôle synchrone dans ce GET ne verrait donc jamais le
            // lecteur : le contrôle est reporté en arrière-plan (même finder que
            // le toast). Usager résolu sans EnableLiveTvManagement → annulation
            // des timers créés par CETTE activation + toast dédié ; la carte
            // reste en bibliothèque. Usager jamais résolu (probe serveur sans
            // session, api_key, cartes d'avant v1.12 sans chemin) → fail-open :
            // comportement inchangé.
            if (strmPath != null && sessions != null && users != null)
            {
                var liveTv = _liveTv;
                var lib = _library;
                var logger = Logger;
                var host = _host;
                var card = req;
                var outcomeSnapshot = outcome ?? AutoProgrammer.OneOutcome.Failed;
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        var session = await ActivateFeedback.FindPlayingSessionAsync(sessions, strmPath, logger).ConfigureAwait(false);
                        var reader = PermissionGate.FromSession(session, users);
                        if (reader != null && !PermissionGate.CanRecordLive(reader))
                        {
                            logger?.Warn("[LLM_AI] Activate : usager « {0} » sans droit d'enregistrement (programId={1}) — annulation des timers créés par cette activation.",
                                reader.Name, card?.ProgramId);
                            PermissionGate.CancelCreatedTimers(liveTv, card?.ProgramId, preTimerIds, logger);
                            string title = ActivateFeedback.ResolveProgramTitle(lib, card?.ProgramId);
                            string text = string.Format(
                                System.Globalization.CultureInfo.InvariantCulture,
                                I18n.S("activate.toast.unauthorized", I18n.ResolveDisplayLangKey(host)), title);
                            await ActivateFeedback.SendToastAsync(sessions, strmPath, text, logger).ConfigureAwait(false);
                            return;
                        }
                        // Comportement v1.12 inchangé : toast de statut + suppression
                        // carte par Emby en cas de succès.
                        DispatchFeedback(outcomeSnapshot, card);
                    }
                    catch (Exception ex)
                    {
                        logger?.Warn("[LLM_AI] Activate : gate permission échouée (ignorée) : {0}", ex.Message);
                        try { DispatchFeedback(outcomeSnapshot, card); } catch { /* best-effort */ }
                    }
                });
                return ClipResponse();
            }

            // Repli historique (carte sans param `card`, session manager ou
            // gestionnaire d'usagers indisponible) : feedback v1.12 immédiat,
            // pas de gate (l'usager n'est pas identifiable par le chemin).
            DispatchFeedback(outcome ?? AutoProgrammer.OneOutcome.Failed, req);

            // ---- Clip de confirmation (Range-aware) ----
            return ClipResponse();
        }

        // ------------------------------------------------------------------
        //  Retour visuel : toast + suppression carte (v1.12)
        // ------------------------------------------------------------------

        /// <summary>
        /// Déclenche le feedback d'une activation (<see cref="ActivateFeedback"/>,
        /// fire-and-forget) : toast Emby à la session qui lit la carte, et —
        /// en cas de succès (<see cref="AutoProgrammer.OneOutcome.Created"/> ou
        /// <see cref="AutoProgrammer.OneOutcome.Dedup"/>) — suppression de la
        /// carte PAR EMBY (item + fichier .strm, différée ~60 s). La fraîcheur
        /// (anti-doublon des GET répétés d'une même lecture) est désormais
        /// propriété du handler <see cref="Get"/> (v1.13.11.0) — ce feedback ne
        /// marque plus rien. Sans carte identifiable (cartes d'avant v1.12,
        /// bibliothèque introuvable) → feedback ignoré, logué.
        /// Ne lève jamais vers le handler.
        /// </summary>
        private void DispatchFeedback(AutoProgrammer.OneOutcome outcome, ActivateRequest req, bool gateDisk = false)
        {
            try
            {
                // Clé d'anti-doublon : le chemin .strm de la carte si connu,
                // sinon programId|kind (identique à la décision de fraîcheur du
                // handler — même clé, le TTL est consommé par Get).
                string strmPath = TryResolveCardPath(req?.Card);

                // Texte i18n selon l'issue. Succès = Created (timer créé) ou
                // Dedup (déjà couvert par un timer existant). Tout le reste
                // (Failed, owned/drop, NoId, gate disque…) = pas d'enregistrement.
                string textKey;
                bool success = false;
                if (gateDisk)
                {
                    textKey = "activate.toast.diskgate";
                }
                else
                {
                    switch (outcome)
                    {
                        case AutoProgrammer.OneOutcome.Created:
                            textKey = "activate.toast.programmed"; success = true; break;
                        case AutoProgrammer.OneOutcome.Dedup:
                            textKey = "activate.toast.duplicate"; success = true; break;
                        default:
                            textKey = "activate.toast.failed"; break;
                    }
                }

                var host = _host;
                var sessions = host?.TryResolve<MediaBrowser.Controller.Session.ISessionManager>();
                var lib = _library;
                string title = ActivateFeedback.ResolveProgramTitle(lib, req?.ProgramId);
                string toastText = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    I18n.S(textKey, I18n.ResolveDisplayLangKey(host)), title);

                _ = System.Threading.Tasks.Task.Run(() =>
                    ActivateFeedback.SendToastAsync(sessions, strmPath, toastText, Logger));

                if (success && strmPath != null)
                    _ = System.Threading.Tasks.Task.Run(() =>
                        ActivateFeedback.DeleteCardItemLaterAsync(lib, strmPath, Logger));
            }
            catch (Exception ex)
            {
                Logger?.Warn("[LLM_AI] Activate : dispatch feedback échoué (ignoré) : {0}", ex.Message);
            }
        }

        /// <summary>
        /// Reconstruit et valide le chemin du <c>.strm</c> de la carte depuis le
        /// param <c>card</c> (nom de dossier écrit par
        /// <see cref="StrmLibraryGenerator.WriteCard"/> — dossier ET fichier
        /// portent le même nom sane). Validation stricte : un seul segment
        /// (pas de séparateur ni <c>..</c>) et le chemin résolu doit rester sous
        /// la racine de la bibliothèque .strm configurée. Renvoie null si le
        /// param est absent (cartes d'avant v1.12) ou invalide.
        /// </summary>
        private string TryResolveCardPath(string card)
        {
            if (string.IsNullOrWhiteSpace(card)) return null;
            card = card.Trim();
            if (card.Contains("/") || card.Contains("\\") ||
                card == "." || card == "..") return null;

            var cfg = Plugin.Instance?.Configuration;
            string root = StrmLibraryGenerator.ResolveLibraryRoot(_library, cfg?.StrmLibraryName, Logger);
            if (string.IsNullOrWhiteSpace(root)) return null;

            try
            {
                string fullRoot = Path.GetFullPath(root);
                string full = Path.GetFullPath(Path.Combine(root, card, card + ".strm"));
                if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return null;
                return full;
            }
            catch (Exception ex)
            {
                Logger?.Warn("[LLM_AI] Activate : résolution du chemin de carte « {0} » échouée : {1}", card, ex.Message);
                return null;
            }
        }

        // ------------------------------------------------------------------
        //  Clip : en-têtes + tranche pour Range
        // ------------------------------------------------------------------

        /// <summary>
        /// Prépare les en-têtes sur <c>Request.Response</c> et renvoie le
        /// <c>byte[]</c> à écrire : corps complet (200) ou tranche (206) selon
        /// l'en-tête <c>Range</c>. L'écriture du corps est laissée au framework
        /// (<c>IResponse</c> n'expose pas <c>OutputStream</c> ici).
        /// </summary>
        private byte[] ClipResponse()
        {
            var resp = Request?.Response;
            if (resp == null) return s_clip;

            try { resp.ContentType = "video/mp4"; } catch { }
            try { resp.AddHeader("Accept-Ranges", "bytes"); } catch { }

            long start = 0, end = s_clip.Length - 1;
            bool ranged = false;
            try
            {
                string rangeHeader = Request.Headers?.Get("Range");
                if (!string.IsNullOrWhiteSpace(rangeHeader) &&
                    rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    var spec = rangeHeader.Substring(6).Split('-');
                    if (spec.Length >= 1 && long.TryParse(spec[0], out start))
                    {
                        // Suffixe explicite non vide -> end fourni ; sinon
                        // « bytes=N- » = jusqu'à EOF. NB : long.TryParse
                        // remet le out à 0 même en cas d'échec — il faut donc
                        // tester le suffixe avant d'appeler TryParse pour ne
                        // pas écraser end (sinon « bytes=0- » devient
                        // « bytes=0-0 » et renvoie 1 octet au lieu du clip
                        // entier, ce que ffprobe/ffmpeg interprètent comme une
                        // troncature -> « Input/output error »).
                        if (spec.Length == 2 && !string.IsNullOrEmpty(spec[1]) &&
                            long.TryParse(spec[1], out end))
                        {
                            // end explicite
                        }
                        else
                        {
                            end = s_clip.Length - 1; // ouvert : jusqu'à EOF
                        }
                        if (start < 0) start = 0;
                        if (end >= s_clip.Length) end = s_clip.Length - 1;
                        if (start <= end)
                        {
                            ranged = true;
                            TrySetStatus(206);
                            try { resp.AddHeader("Content-Range", $"bytes {start}-{end}/{s_clip.Length}"); } catch { }
                        }
                        else { start = 0; end = s_clip.Length - 1; }
                    }
                }
            }
            catch { /* tolérant : repli sur corps complet */ }

            int length = (int)(end - start + 1);
            if (!ranged) TrySetStatus(200);
            // Content-Length est laissé au framework : il le déduit du byte[]
            // renvoyé (corps complet ou tranche) — on évite un doublon d'en-tête.

            if (start == 0 && length == s_clip.Length) return s_clip;
            var slice = new byte[length];
            Array.Copy(s_clip, (int)start, slice, 0, length);
            return slice;
        }

        /// <summary>
        /// Pose un status code (int — <c>IResponse.StatusCode</c> est typé int
        /// sur cet hôte) de façon tolérante.
        /// </summary>
        private void TrySetStatus(int code)
        {
            try { Request.Response.StatusCode = code; } catch { }
        }

        // ------------------------------------------------------------------
        //  Utilitaires
        // ------------------------------------------------------------------

        /// <summary>
        /// Comparaison à temps constant (anti-orchestration de timing) entre deux
        /// chaînes ; renvoie false si l'une est nulle. Sécurise la gate
        /// <c>StrmSecret</c>.
        /// </summary>
        private static bool ConstantTimeEquals(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>
        /// Charge le clip embarqué en mémoire une fois (démarrage du service).
        /// Renvoie null si la ressource est absente (build sans l'asset).
        /// </summary>
        private static byte[] LoadClip()
        {
            try
            {
                using (var s = typeof(ActivateApiService).Assembly.GetManifestResourceStream(ClipResource))
                {
                    if (s == null) return null;
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch
            {
                return null;
            }
        }
    }
}