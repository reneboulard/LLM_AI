using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace LLM_AI
{
    /// <summary>
    /// Endpoint HTTP plugin activant un enregistrement depuis une carte
    /// <c>.strm</c> de la bibliothèque dédiée « AI Suggestions ». Expose
    /// <c>GET /Plugins/LLMAI/Activate?programId=…&amp;kind=…&amp;t=…</c>.
    /// </summary>
    /// <remarks>
    /// <para>Mécanisme : la tâche planifiée (<c>StrmLibraryGenerator</c>) écrit
    /// une carte <c>.strm</c> par reco du record bucket dont l'URL pointe ici.
    /// Quand l'usager lit la carte, le lecteur média demande cette URL ; le
    /// plugin crée alors le timer Emby (série → SeriesTimer, film → Timer via
    /// <see cref="AutoProgrammer.ProgramOneAsync"/>) puis renvoie un court clip
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

        // Clip de confirmation embarqué (LLM_AI.recording_activated.mp4),
        // chargé une fois en mémoire statique.
        private static readonly byte[] s_clip = LoadClip();
        private const string ClipResource = "LLM_AI.recording_activated.mp4";

        public ActivateApiService(ILiveTvManager liveTv, ILibraryManager library)
        {
            _liveTv = liveTv;
            _library = library;
        }

        // ------------------------------------------------------------------
        //  DTO requête
        // ------------------------------------------------------------------

        /// <summary>
        /// Requête GET <c>/Plugins/LLMAI/Activate</c>.
        /// <c>ProgramId</c> : id de programme EPG (tel que repris depuis l'EPG
        /// par la reco). <c>Kind</c> : « series » ou « movie » (détermine
        /// SeriesTimer vs Timer). <c>T</c> : jeton de capacité
        /// (<see cref="PluginConfiguration.StrmSecret"/>) — la seule gate
        /// d'accès, le lecteur média ne transmettant pas l'auth Emby.
        /// </summary>
        [Route("/Plugins/LLMAI/Activate", "GET")]
        [Unauthenticated]
        public class ActivateRequest : IReturn<object>
        {
            public string ProgramId { get; set; }
            public string Kind { get; set; }
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

            // ---- Activation de l'enregistrement (best-effort) ----
            // Réutilise la même logique que la tâche planifiée : dedup contre
            // les timers existants, puis SeriesTimer (série) / Timer (film).
            // Idempotent : re-lire la carte renvoie le clip sans créer de
            // doublon (le dedup neutralise un second timer).
            try
            {
                var ct = Request?.CancellationToken ?? CancellationToken.None;
                var ap = new AutoProgrammer(_liveTv, _library, Logger);
                var programIds = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var names = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                ap.BuildExistingTimerSets(programIds, names);

                var reco = new AutoProgrammer.Reco
                {
                    Id = req.ProgramId,
                    Kind = req.Kind,
                    Title = req.ProgramId
                };
                var outcome = await ap.ProgramOneAsync(reco, programIds, names, ct).ConfigureAwait(false);
                Logger?.Info("[LLM_AI] Activate programId={0} kind={1} → {2}.", req.ProgramId, req.Kind, outcome);
            }
            catch (Exception ex)
            {
                // Un échec (programme déjà diffusé, conflit tuner…) est logué :
                // on renvoie quand même le clip — l'usager a cliqué, on confirme
                // visuellement, le détail est dans le journal.
                Logger?.Warn("[LLM_AI] Activate : échec création timer (programId={0}) : {1}", req.ProgramId, ex.Message);
            }

            // ---- Clip de confirmation (Range-aware) ----
            return ClipResponse();
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