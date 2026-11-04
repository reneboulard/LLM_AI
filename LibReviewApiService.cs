using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;

namespace LLM_AI
{
    /// <summary>
    /// Endpoints « Gestion bibliothèque » (v2.10.0) — évaluation LLM de la
    /// bibliothèque pour suggérer des suppressions, réservés aux
    /// administrateurs (la page de config est déjà admin-only ; on re-gate
    /// quand même, même pattern que AuditApiService/CrossKindApiService).
    ///
    /// <list type="bullet">
    /// <item><c>GET /Plugins/LLMAI/LibReview?Brief=…</c> : démarre un run
    ///   DÉTACHÉ (single-flight <see cref="LibReviewRunState"/>) et répond
    ///   immédiatement <c>Running=true</c>.</item>
    /// <item><c>GET …?Status=true</c> : polling — état du run +, quand il
    ///   termine « ok », le dernier rapport FILTRÉ (voir ?Last).</item>
    /// <item><c>GET …?Last=true</c> : dernier rapport persisté, zéro LLM,
    ///   <b>filtré/validé</b> : chaque id est re-résolu et exclu s'il
    ///   n'existe plus ou s'il est dans LibReviewKeptIds.</item>
    /// <item><c>POST /Plugins/LLMAI/LibReviewDelete {ItemId}</c> : re-résout
    ///   l'id AU MOMENT du POST puis délègue la suppression à Emby
    ///   (<c>DeleteItem(DeleteFileLocation=true)</c>) — le plugin ne supprime
    ///   jamais hors de ce clic admin explicite.</item>
    /// <item><c>POST /Plugins/LLMAI/LibReviewKeep {ItemId}</c> : inscrit l'id
    ///   dans LibReviewKeptIds (config) — exclu de l'affichage et des
    ///   prochains runs.</item>
    /// <item><c>GET …?Kept=true</c> (v2.13.5) : liste « Conserver » re-résolue
    ///   item par item ; les ids dont l'item n'existe plus sont purgés de la
    ///   config (purge lazy) et comptés dans <c>KeptPurged</c>.</item>
    /// <item><c>POST /Plugins/LLMAI/LibReviewUnkeep {ItemId}</c> (v2.13.5) :
    ///   retire un id de la liste « Conserver ».</item>
    /// <item><c>POST /Plugins/LLMAI/LibReviewResetKept</c> (v2.13.5) : vide
    ///   toute la liste « Conserver ».</item>
    /// </list>
    /// </summary>
    public class LibReviewApiService : BaseApiService
    {
        private readonly ILiveTvManager _liveTv;
        private readonly IJsonSerializer _json;
        private readonly INotificationManager _notifications;

        private readonly IUserDataManager _userData;

        public LibReviewApiService(ILiveTvManager liveTv, IJsonSerializer json,
            INotificationManager notifications, IUserDataManager userData)
        {
            _liveTv = liveTv;
            _json = json;
            _notifications = notifications;
            _userData = userData;
        }

        // ------------------------------------------------------------------
        //  DTO
        // ------------------------------------------------------------------

        [Route("/Plugins/LLMAI/LibReview", "GET")]
        public class LibReviewRequest : IReturn<object>
        {
            /// <summary>Consigne libre pour ce run (bouton manuel).</summary>
            public string Brief { get; set; }
            /// <summary>Lecture seule : dernier rapport filtré (zéro LLM).</summary>
            public bool Last { get; set; }
            /// <summary>Polling du run détaché.</summary>
            public bool Status { get; set; }
            /// <summary>v2.13.5 — lecture seule : liste « Conserver » re-résolue.</summary>
            public bool Kept { get; set; }
        }

        /// <summary>Une carte (DTO enrichi, validé à la volée).</summary>
        public class LibReviewCard
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Kind { get; set; }        // movie | episode | series | …
            public string Series { get; set; }      // nom de la série (épisode)
            // v2.12.0 — verdict du classement : delete | can_delete | keep
            // (absent = rapport < 2.12.0 → delete, proposition de suppression).
            public string Section { get; set; }
            public double SizeGb { get; set; }
            public string DateAdded { get; set; }
            public int EpisodeCount { get; set; }   // > 0 pour une série
            public string ImageUrl { get; set; }
            public string Reason { get; set; }
            // v2.11.2 — « jamais vu » (pool canards boîteux) + dernier visionnage.
            public bool Unwatched { get; set; }
            public string LastPlayed { get; set; }
            // v2.10.6 — mêmes motifs que emby-delete-series.sh (affichage carte).
            public double? CommunityRating { get; set; }
            public int? ProductionYear { get; set; }
            public string Status { get; set; }
            public string[] Genres { get; set; }
            public string Overview { get; set; }
        }

        public class LibReviewResponse
        {
            public bool Enabled { get; set; }
            public string Error { get; set; }
            public bool Running { get; set; }
            public string Progress { get; set; }
            public string StartedAt { get; set; }
            public string FinishedAt { get; set; }
            public string Outcome { get; set; }
            public string GeneratedAt { get; set; }
            public string Brief { get; set; }
            public int Candidates { get; set; }
            public List<LibReviewCard> Items { get; set; }
            public int KeptCount { get; set; }
            // v2.13.5 — renseigné uniquement quand Kept=true : liste
            // « Conserver » re-résolue (un item encore en bibliothèque).
            public List<LibReviewCard> KeptItems { get; set; }
            // v2.13.5 — ids purgés lors de ce ?Kept=true (item supprimé
            // depuis dans Emby) — retirés de LibReviewKeptIds + sauvegarde.
            public int KeptPurged { get; set; }
        }

        [Route("/Plugins/LLMAI/LibReviewDelete", "POST")]
        public class LibReviewDeleteRequest : IReturn<object>
        {
            public string ItemId { get; set; }
        }

        public class LibReviewDeleteResponse
        {
            /// <summary>ok | skipped (id inconnu/inexistant) | failed.</summary>
            public string Status { get; set; }
            public string Error { get; set; }
        }

        [Route("/Plugins/LLMAI/LibReviewKeep", "POST")]
        public class LibReviewKeepRequest : IReturn<object>
        {
            public string ItemId { get; set; }
        }

        public class LibReviewKeepResponse
        {
            public bool Added { get; set; }
            public string Error { get; set; }
        }

        [Route("/Plugins/LLMAI/LibReviewUnkeep", "POST")]
        public class LibReviewUnkeepRequest : IReturn<object>
        {
            public string ItemId { get; set; }
        }

        public class LibReviewUnkeepResponse
        {
            public bool Removed { get; set; }
            public int KeptCount { get; set; }
            public string Error { get; set; }
        }

        [Route("/Plugins/LLMAI/LibReviewResetKept", "POST")]
        public class LibReviewResetKeptRequest : IReturn<object>
        {
        }

        public class LibReviewResetKeptResponse
        {
            public bool Ok { get; set; }
            public int RemovedCount { get; set; }
            public string Error { get; set; }
        }

        // ------------------------------------------------------------------
        //  Auth (même pattern que CrossKindApiService)
        // ------------------------------------------------------------------

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

        private bool IsAdmin()
        {
            var user = ResolveAdmin();
            var policy = user?.Policy;
            return policy != null && policy.IsAdministrator;
        }

        // ------------------------------------------------------------------
        //  GET
        // ------------------------------------------------------------------

        public object Get(LibReviewRequest req)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new LibReviewResponse { Enabled = false, Error = I18n.SDisplay("err.noconfig", ApplicationHost) };

            if (!IsAdmin())
                return new LibReviewResponse { Enabled = true, Error = I18n.SDisplay("err.admin", ApplicationHost) };

            // ---- ?Status=true : polling du run détaché --------------------
            if (req?.Status ?? false)
            {
                var st0 = LibReviewRunState.Snapshot();
                var resp = new LibReviewResponse
                {
                    Enabled = cfg.LibReviewEnabled,
                    Running = st0.Running,
                    Progress = st0.Progress,
                    StartedAt = st0.StartedAt?.ToString("o", CultureInfo.InvariantCulture),
                    FinishedAt = st0.FinishedAt?.ToString("o", CultureInfo.InvariantCulture),
                    Outcome = st0.Outcome,
                    Error = st0.Error,
                    KeptCount = LibReviewEngine.ParseKeptIds(cfg.LibReviewKeptIds).Count
                };
                // Quand le run vient de finir « ok », la réponse porte le
                // rapport frais filtré (zéro appel supplémentaire côté page).
                if (st0.Outcome == "ok" && !st0.Running)
                    FillLast(resp, cfg);
                return resp;
            }

            // ---- ?Kept=true : liste « Conserver » re-résolue (v2.13.5) -----
            // Chaque id est re-résolu ; introuvable = purge lazy de la config
            // (l'item a été supprimé dans Emby entre-temps) + sauvegarde.
            if (req?.Kept ?? false)
            {
                var keptSet = LibReviewEngine.ParseKeptIds(cfg.LibReviewKeptIds);
                var resp = new LibReviewResponse
                {
                    Enabled = cfg.LibReviewEnabled,
                    KeptCount = keptSet.Count,
                    KeptItems = new List<LibReviewCard>()
                };
                int purged = 0;
                foreach (var id in keptSet.ToList())
                {
                    var item = ItemIdResolver.Resolve(LibraryManager, id);
                    if (item == null)
                    {
                        keptSet.Remove(id);
                        purged++;
                        continue;
                    }
                    resp.KeptItems.Add(ToCard(item, new LibReviewEntry { Id = id, Section = "keep", Reason = "" }));
                }
                if (purged > 0)
                {
                    cfg.LibReviewKeptIds = JsonSerializer.Serialize(new List<string>(keptSet));
                    try { Plugin.Instance?.SaveConfiguration(); }
                    catch (Exception ex)
                    {
                        Logger?.ErrorException("[LLM_AI] [LibReview] Purge LibReviewKeptIds échouée : {0}", ex, ex.Message);
                    }
                    resp.KeptPurged = purged;
                    resp.KeptCount = keptSet.Count;
                    Logger?.Info("[LLM_AI] [LibReview] Liste « Conserver » : {0} id(s) purgé(s) (item(s) supprimé(s) depuis), {1} restant(s).", purged, keptSet.Count);
                }
                resp.KeptItems.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                return resp;
            }

            // ---- ?Last=true : dernier rapport filtré (chargement page) ----
            if (req?.Last ?? false)
            {
                var resp = new LibReviewResponse
                {
                    Enabled = cfg.LibReviewEnabled,
                    KeptCount = LibReviewEngine.ParseKeptIds(cfg.LibReviewKeptIds).Count
                };
                var st1 = LibReviewRunState.Snapshot();
                resp.Running = st1.Running;
                resp.Progress = st1.Progress;
                resp.StartedAt = st1.StartedAt?.ToString("o", CultureInfo.InvariantCulture);
                FillLast(resp, cfg);
                return resp;
            }

            // ---- GET sans paramètre : démarre le run DÉTACHÉ ---------------
            if (!cfg.LibReviewEnabled)
                return new LibReviewResponse { Enabled = false, Error = I18n.SDisplay("librev.disabled", ApplicationHost) };

            string brief = (req?.Brief ?? "").Trim();
            string startMsg = I18n.SDisplay("librev.running", ApplicationHost) ?? "Évaluation en cours en arrière-plan…";
            if (!LibReviewRunState.TryStart(startMsg))
            {
                // Single-flight : un run tourne déjà — on renvoie l'état.
                var st0 = LibReviewRunState.Snapshot();
                return new LibReviewResponse
                {
                    Enabled = true,
                    Running = true,
                    Progress = st0.Progress,
                    StartedAt = st0.StartedAt?.ToString("o", CultureInfo.InvariantCulture)
                };
            }

            string capturedBrief = brief;
            _ = Task.Run(async () =>
            {
                try
                {
                    await LibReviewEngine.RunAsync(cfg, capturedBrief,
                        LibraryManager, UserManager, _userData, _liveTv, ApplicationHost, _json, _notifications,
                        Logger, LibReviewRunState.SetProgress, CancellationToken.None).ConfigureAwait(false);
                    LibReviewRunState.FinishOk();
                }
                catch (OperationCanceledException)
                {
                    LibReviewRunState.FinishError(I18n.SDisplay("librev.cancelled", ApplicationHost));
                }
                catch (Exception ex)
                {
                    Logger?.ErrorException("[LLM_AI] [LibReview] Run détaché en échec : {0}", ex, ex.Message);
                    LibReviewRunState.FinishError(ex.Message);
                }
            });

            var st2 = LibReviewRunState.Snapshot();
            return new LibReviewResponse
            {
                Enabled = true,
                Running = true,
                Progress = st2.Progress,
                StartedAt = st2.StartedAt?.ToString("o", CultureInfo.InvariantCulture)
            };
        }

        // ------------------------------------------------------------------
        //  POST Delete / Keep
        // ------------------------------------------------------------------

        public object Post(LibReviewDeleteRequest req)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new LibReviewDeleteResponse { Status = "failed", Error = I18n.SDisplay("err.noconfig", ApplicationHost) };
            if (!IsAdmin())
                return new LibReviewDeleteResponse { Status = "failed", Error = I18n.SDisplay("err.admin", ApplicationHost) };

            string id = (req?.ItemId ?? "").Trim();
            if (string.IsNullOrEmpty(id))
                return new LibReviewDeleteResponse { Status = "failed", Error = I18n.SDisplay("librev.err.noid", ApplicationHost) };

            // Re-vérification AU MOMENT du POST : l'id doit exister encore.
            var item = ItemIdResolver.Resolve(LibraryManager, id);
            if (item == null)
            {
                Logger?.Info("[LLM_AI] [LibReview] Delete « {0} » : item introuvable (déjà supprimé ?) — skipped.", id);
                return new LibReviewDeleteResponse { Status = "skipped" };
            }

            try
            {
                // Délégation à Emby : le plugin ne supprime que via cet appel
                // explicite, exécuté par le pipeline Emby (item + fichier).
                // Pour une Series, Emby emporte les épisodes (cascade).
                LibraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true });
                Logger?.Info("[LLM_AI] [LibReview] « {0} » ({1}, id {2}) supprimé via Emby (admin clic).",
                    item.Name, item.GetType().Name, id);
                return new LibReviewDeleteResponse { Status = "ok" };
            }
            catch (Exception ex)
            {
                Logger?.ErrorException("[LLM_AI] [LibReview] Delete « {0} » (id {1}) échoué : {2}", ex, item.Name, id, ex.Message);
                return new LibReviewDeleteResponse { Status = "failed", Error = ex.Message };
            }
        }

        public object Post(LibReviewKeepRequest req)
        {
            var cfg = Plugin.Instance?.Configuration;
            var plugin = Plugin.Instance;
            if (cfg == null || plugin == null)
                return new LibReviewKeepResponse { Error = I18n.SDisplay("err.noconfig", ApplicationHost) };
            if (!IsAdmin())
                return new LibReviewKeepResponse { Error = I18n.SDisplay("err.admin", ApplicationHost) };

            string id = (req?.ItemId ?? "").Trim();
            if (string.IsNullOrEmpty(id))
                return new LibReviewKeepResponse { Error = I18n.SDisplay("librev.err.noid", ApplicationHost) };

            var kept = LibReviewEngine.ParseKeptIds(cfg.LibReviewKeptIds);
            if (!kept.Add(id))
                return new LibReviewKeepResponse { Added = false }; // déjà présent

            var arr = new List<string>(kept);
            cfg.LibReviewKeptIds = JsonSerializer.Serialize(arr);
            try { plugin.SaveConfiguration(); }
            catch (Exception ex)
            {
                Logger?.ErrorException("[LLM_AI] [LibReview] Sauvegarde LibReviewKeptIds échouée : {0}", ex, ex.Message);
                return new LibReviewKeepResponse { Error = ex.Message };
            }
            Logger?.Info("[LLM_AI] [LibReview] id « {0} » ajouté à la liste « Conserver » ({1} entrées).", id, arr.Count);
            return new LibReviewKeepResponse { Added = true };
        }

        // v2.13.5 — retire un id de la liste « Conserver » (bouton de la vue
        // « Liste Conserver » de la page). Miroir exact de LibReviewKeep.
        public object Post(LibReviewUnkeepRequest req)
        {
            var cfg = Plugin.Instance?.Configuration;
            var plugin = Plugin.Instance;
            if (cfg == null || plugin == null)
                return new LibReviewUnkeepResponse { Error = I18n.SDisplay("err.noconfig", ApplicationHost) };
            if (!IsAdmin())
                return new LibReviewUnkeepResponse { Error = I18n.SDisplay("err.admin", ApplicationHost) };

            string id = (req?.ItemId ?? "").Trim();
            if (string.IsNullOrEmpty(id))
                return new LibReviewUnkeepResponse { Error = I18n.SDisplay("librev.err.noid", ApplicationHost) };

            var kept = LibReviewEngine.ParseKeptIds(cfg.LibReviewKeptIds);
            if (!kept.Remove(id))
                return new LibReviewUnkeepResponse { Removed = false, KeptCount = kept.Count }; // absent

            cfg.LibReviewKeptIds = JsonSerializer.Serialize(new List<string>(kept));
            try { plugin.SaveConfiguration(); }
            catch (Exception ex)
            {
                Logger?.ErrorException("[LLM_AI] [LibReview] Sauvegarde LibReviewKeptIds (unkeep) échouée : {0}", ex, ex.Message);
                return new LibReviewUnkeepResponse { Error = ex.Message };
            }
            Logger?.Info("[LLM_AI] [LibReview] id « {0} » retiré de la liste « Conserver » ({1} entrées).", id, kept.Count);
            return new LibReviewUnkeepResponse { Removed = true, KeptCount = kept.Count };
        }

        // v2.13.5 — reset global de la liste « Conserver ».
        public object Post(LibReviewResetKeptRequest req)
        {
            var cfg = Plugin.Instance?.Configuration;
            var plugin = Plugin.Instance;
            if (cfg == null || plugin == null)
                return new LibReviewResetKeptResponse { Error = I18n.SDisplay("err.noconfig", ApplicationHost) };
            if (!IsAdmin())
                return new LibReviewResetKeptResponse { Error = I18n.SDisplay("err.admin", ApplicationHost) };

            var kept = LibReviewEngine.ParseKeptIds(cfg.LibReviewKeptIds);
            int removed = kept.Count;
            cfg.LibReviewKeptIds = "";
            try { plugin.SaveConfiguration(); }
            catch (Exception ex)
            {
                Logger?.ErrorException("[LLM_AI] [LibReview] Sauvegarde LibReviewKeptIds (reset) échouée : {0}", ex, ex.Message);
                return new LibReviewResetKeptResponse { Error = ex.Message };
            }
            Logger?.Info("[LLM_AI] [LibReview] Liste « Conserver » réinitialisée ({0} entrée(s) retirée(s)).", removed);
            return new LibReviewResetKeptResponse { Ok = true, RemovedCount = removed };
        }

        // ------------------------------------------------------------------
        //  Rapport filtré : re-résolution de chaque id + exclusion kept.
        //  C'est LA validation à l'affichage : un item effacé entre-temps
        //  (Emby, routine matinale, bouton Effacer) disparaît ici.
        // ------------------------------------------------------------------

        private void FillLast(LibReviewResponse resp, PluginConfiguration cfg)
        {
            var last = LibReviewReportStore.Load();
            if (last == null) return;
            resp.GeneratedAt = last.GeneratedAt.ToString("o", CultureInfo.InvariantCulture);
            resp.Brief = last.Brief;
            resp.Candidates = last.Candidates;

            var kept = LibReviewEngine.ParseKeptIds(cfg.LibReviewKeptIds);
            var cards = new List<LibReviewCard>();
            int skipped = 0;
            foreach (var e in last.Items)
            {
                if (kept.Contains(e.Id)) { skipped++; continue; }
                var item = ItemIdResolver.Resolve(LibraryManager, e.Id);
                if (item == null) { skipped++; continue; }  // plus en bibliothèque
                cards.Add(ToCard(item, e));
            }
            if (skipped > 0)
                Logger?.Info("[LLM_AI] [LibReview] Affichage : {0} entrée(s) du rapport écartée(s) (supprimée/conservée).", skipped);
            resp.Items = cards;
        }

        private LibReviewCard ToCard(BaseItem item, LibReviewEntry e)
        {
            // v2.11.2 — « jamais vu par personne » : aucun usager n'a de
            // UserData.Played sur l'item (même sémantique robuste que le pool).
            bool unwatched = !LibReviewEngine.IsPlayedByAny(item, UserManager, _userData);
            string lastPlayed = LibReviewEngine.LastPlayedDateByAny(item, UserManager, _userData);
            var card = new LibReviewCard
            {
                Id = item.InternalId.ToString(CultureInfo.InvariantCulture),
                Name = item.Name ?? "",
                Kind = item.GetType().Name.ToLowerInvariant(),
                Series = (item as Episode)?.SeriesName,
                Section = string.IsNullOrEmpty(e.Section) ? "delete" : e.Section,
                SizeGb = LibReviewEngine.FileSizeGb(item.Path, Logger),
                DateAdded = item.DateCreated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Reason = e.Reason ?? "",
                Unwatched = unwatched,
                LastPlayed = lastPlayed ?? "",
                CommunityRating = item.CommunityRating,
                ProductionYear = item.ProductionYear,
                Status = (item as Series)?.Status?.ToString(),
                Genres = item.Genres,
                Overview = LibReviewEngine.TruncateOverview(item.Overview)
            };
            // Pour une série : taille totale + nombre d'épisodes (affichage « 18 épisodes, ~42 Go »).
            if (item is Series series)
            {
                long total = 0;
                int eps = 0;
                try
                {
                    var list = LibraryManager.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { "Episode" },
                        AncestorIds = new[] { series.InternalId },
                        Recursive = true,
                        EnableTotalRecordCount = false
                    }) ?? Array.Empty<BaseItem>();
                    foreach (var ep in list)
                    {
                        eps++;
                        total += LibReviewEngine.FileSizeOrNull(ep.Path, Logger);
                    }
                }
                catch { /* best-effort */ }
                card.EpisodeCount = eps;
                card.SizeGb = total / (double)(1024L * 1024 * 1024);
            }
            card.ImageUrl = ImageUrl(item.InternalId);
            return card;
        }

        private string ImageUrl(long internalId)
        {
            var base_ = Plugin.Instance?.Configuration?.EmbyPublicUrl;
            return string.IsNullOrEmpty(base_) ? null : base_.TrimEnd('/') + "/Items/" + internalId + "/Images/Primary";
        }
    }
}
