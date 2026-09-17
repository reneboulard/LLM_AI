using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

using MediaBrowser.Model.Serialization;

using MediaBrowser.Controller.LiveTv;

namespace LLM_AI
{
    /// <summary>
    /// Résolveur d'orphelins DVR <b>partagé</b> entre la tâche planifiée
    /// <see cref="OrphanIdentifyTask"/> (passe 04 h) et le
    /// <see cref="RecordingWatcher"/> (validation à la fin de l'enregistrement).
    /// Centralise la chaîne de résolution et la porte d'acceptation afin qu'il
    /// n'existe qu'un seul corps de règles.
    /// </summary>
    /// <remarks>
    /// <para><b>Chaîne de résolution</b> (par item) :</para>
    /// <list type="number">
    /// <item><b>Audit</b> (uniquement quand une <see cref="EpgTruth"/> est
    /// fournie — chemin RecordingWatcher) : si Emby a déjà posé des ids, on
    /// relit la fiche TMDB correspondante et le juge sémantique compare le
    /// synopsis EPG figé au synopsis TMDB. Match → validation (verrous + tag
    /// identifié). Mismatch → retrait des ids écrits par Emby, retour à l'état
    /// EPG, puis S1/S2/S3 immédiat. Sans synopsis à comparer ou juge
    /// indisponible → on ne touche pas (prudence).</para></item>
    /// <item><b>S0 — recherche native Emby</b> (option
    /// <c>OrphanEmbyFirstPass</c>) : <c>IProviderManager.GetRemoteSearchResults</c>
    /// (le moteur du dialogue « Identifier ») sur le titre EPG nettoyé.
    /// Candidats dans l'ordre d'Emby, porte renforcée (titre + année + juge).
    /// Accepté → fiche TMDB détaillée → application.</para></item>
    /// <item><b>S1 — recherche TMDB multilingue</b> (garde lexicale).
    /// <b>S2 — proposition LLM</b> (juge). <b>S3 — SearXNG → id IMDb</b>
    /// (juge).</para></item>
    /// </list>
    /// <para><b>Porte d'acceptation</b> commune : année compatible (±1 an) +
    /// (garde lexicale sur les voies par titre) + juge LLM dès que les deux
    /// synopsis existent (sinon acceptation sur année+titre, comme la pratique
    /// EPG sans synopsis). Rejet = « on continue de chercher ».</para>
    /// <para><b>Vérité EPG</b> : une <see cref="EpgTruth"/> fournie prime sur
    /// les champs de l'item (que l'identification — parfois fausse — de Emby
    /// peut avoir écrasés). Sans truth, comportement historique : champs de
    /// l'item.</para>
    /// <para>Application non destructive + verrouillage add-only ; Name EPG
    /// jamais modifié. Dry-run : aucune écriture, logs uniquement.
    /// Best-effort : un échec d'étape passe à la suivante.</para>
    /// </remarks>
    internal sealed class OrphanResolver
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _library;
        private readonly IServerApplicationHost _host;
        private readonly IProviderManager _providers;
        private readonly LlmRunner _runner;
        private readonly TmdbLookupTool _tmdb;
        private readonly WebSearchTool _search;

        /// <summary>HttpClient partagé pour le téléchargement des posters TMDB.</summary>
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        internal OrphanResolver(
            ILogger logger, IJsonSerializer json, ILibraryManager library,
            IUserManager users, ILiveTvManager liveTv,
            IServerApplicationHost host, IProviderManager providers)
        {
            _logger = logger;
            _library = library;
            _host = host;
            _providers = providers;
            _runner = new LlmRunner(logger, json, library, users, liveTv, host);
            _tmdb = new TmdbLookupTool(logger);
            _search = new WebSearchTool(logger);
        }

        internal enum Status { Skipped, OrphanResolved, NeedsReview, NotFound }

        /// <summary>
        /// Trace du passage d'un item dans le pipeline : sait si AU MOINS UN
        /// candidat a été <b>surfaced</b> (renvoyé par une banque, même rejeté
        /// ensuite par la porte). Distingue deux échecs de nature différente :
        /// aucun candidat vu à S0/S1/S2/S3 = le titre est absent de toutes les
        /// banques → <see cref="OrphanIdentifyTask.TagNotFound"/> (état
        /// terminal, rien à réviser) ; candidats vus mais rejetés =
        /// <see cref="OrphanIdentifyTask.TagNeedsReview"/> (une action humaine
        /// reste possible). Politique de retry nocturne : needs-review
        /// retraité si <c>OrphanRetryNeedsReview</c>, not-found jamais.
        /// </summary>
        internal sealed class PipelineTrace
        {
            public bool SawCandidates;
        }

        /// <summary>
        /// Vérité EPG figée à la fin de l'enregistrement (RecordingWatcher) :
        /// programme du guide tel qu'il était AVANT toute identification. Tous
        /// les champs optionnels.
        /// </summary>
        internal sealed class EpgTruth
        {
            /// <summary>Titre de diffusion EPG (séries : titre de la série).</summary>
            public string Title;
            /// <summary>Synopsis EPG figé.</summary>
            public string Overview;
            /// <summary>Année EPG (ProductionYear du programme du guide).</summary>
            public int? Year;
            /// <summary>Chaîne (contexte S2).</summary>
            public string Channel;
        }

        // ------------------------------------------------------------------
        //  Entrée unique : résolution d'un item
        // ------------------------------------------------------------------

        internal async Task<Status> ResolveItemAsync(BaseItem item, EpgTruth truth,
            PluginConfiguration cfg, bool dry, string userTmdb, bool verbose,
            CancellationToken ct)
        {
            bool isSeries = item.GetType().Name.IndexOf("Series", StringComparison.OrdinalIgnoreCase) >= 0;
            string kind = isSeries ? "series" : "movie";

            var itemTags = item.Tags ?? Array.Empty<string>();
            bool taggedIdentified = Array.IndexOf(itemTags, OrphanIdentifyTask.TagIdentified) >= 0;
            bool taggedReview = Array.IndexOf(itemTags, OrphanIdentifyTask.TagNeedsReview) >= 0;
            bool taggedNotFound = Array.IndexOf(itemTags, OrphanIdentifyTask.TagNotFound) >= 0;
            bool retryReview = cfg.OrphanRetryNeedsReview;
            // Introuvable (aucune banque) = état terminal : JAMAIS retraité.
            bool tagged = taggedIdentified || taggedNotFound || (taggedReview && !retryReview);
            bool strmCard = IsStrmCard(item);
            bool orphan = IsOrphanItem(item);
            string itemName = item.Name;

            if (verbose)
            {
                string reason;
                if (strmCard) reason = "carte .strm (bibliothèque ai_suggestions)";
                else if (taggedIdentified) reason = "déjà taggé " + OrphanIdentifyTask.TagIdentified;
                else if (!orphan && (taggedReview || taggedNotFound) && cfg.OrphanAuditTaggedIds) reason = "taggé échec + ids reçus entre-temps → audit";
                else if (taggedNotFound) reason = "déjà taggé " + OrphanIdentifyTask.TagNotFound + " (introuvable — gelé, réactivable en retirant le tag)";
                else if (taggedReview) reason = retryReview ? ("needs-review (retry ON) → à retraiter") : ("déjà taggé " + OrphanIdentifyTask.TagNeedsReview);
                else if (!orphan) reason = truth != null ? "a des ids provider → audit" : "a déjà un id provider (non-orphelin)";
                else if (string.IsNullOrWhiteSpace(itemName)) reason = "titre vide";
                else reason = "ORPHELIN → à traiter";
                _logger?.Info("[LLM_AI] OrphanIdentify : id={0} « {1} » kind={2} imdb={3} tmdb={4} tvdb={5} tags=[{6}] → {7}.",
                    item.Id, itemName, kind,
                    HasItemProviderId(item, "imdb") ? "oui" : "non",
                    HasItemProviderId(item, "tmdb") ? "oui" : "non",
                    HasItemProviderId(item, "tvdb") ? "oui" : "non",
                    string.Join(",", itemTags), reason);
            }

            if (strmCard) return Status.Skipped;

            // Audit des taggés « revenus avec ids » : Emby identifie parfois un
            // item APRÈS qu'il a été taggé introuvable/besoin-revue — parfois
            // à tort (homonyme). Option OrphanAuditTaggedIds (opt-in) : la
            // fiche de l'id posé est confrontée au titre de l'item ; mismatch
            // = auto-remédiation (ids retirés + reprise S1→S2→S3). Passe 04 h
            // uniquement : avec vérité EPG (RecordingWatcher), l'audit Emby à
            // juge synopsis ci-dessous reste la voie supérieure.
            if (truth == null && !orphan && (taggedReview || taggedNotFound) && cfg.OrphanAuditTaggedIds)
                return await AuditTaggedIdsAsync(item, cfg, kind, isSeries, dry, userTmdb, verbose, ct).ConfigureAwait(false);

            if (tagged) return Status.Skipped;

            // --- Audit d'une identification Emby (truth fournie = RecordingWatcher).
            // La passe 04 h ne passe ici jamais : les non-orphelins y sont sautés.
            if (!orphan)
            {
                if (truth == null || taggedIdentified) return Status.Skipped;
                return await AuditEmbyIdsAsync(item, truth, cfg, kind, isSeries, dry, userTmdb, verbose, ct).ConfigureAwait(false);
            }

            string epgTitle = truth?.Title ?? itemName;
            if (string.IsNullOrWhiteSpace(epgTitle)) return Status.Skipped;

            // Orphelin pur : S0 (Emby natif) puis S1→S2→S3.
            var langs = new[] { "en-US", "fr-FR", userTmdb }
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            bool allowS0 = cfg.OrphanEmbyFirstPass && _providers != null;
            var trace = new PipelineTrace();
            var meta = await ResolvePipelineAsync(item, truth, cfg, kind, isSeries,
                allowS0, langs, userTmdb, dry, trace, ct).ConfigureAwait(false);
            if (meta == null) return trace.SawCandidates ? Status.NeedsReview : Status.NotFound;
            return Status.OrphanResolved;
        }

        // ------------------------------------------------------------------
        //  Pipeline de résolution d'orphelin : S0 (Emby natif, option) →
        //  S1 (TMDB multilingue) → S2 (LLM) → S3 (SearXNG). Applique le
        //  candidat retenu (ou pose needs-review). Retourne null si non résolu.
        // ------------------------------------------------------------------

        private async Task<TmdbMeta> ResolvePipelineAsync(BaseItem item, EpgTruth truth,
            PluginConfiguration cfg, string kind, bool isSeries, bool allowS0,
            string[] langs, string userTmdb, bool dry, PipelineTrace trace, CancellationToken ct)
        {
            string epgTitle = truth?.Title ?? item.Name;
            // Année = ProductionYear UNIQUEMENT (date de DIFFUSION pour un DVR,
            // pas PremiereDate/DateCreated). La vérité EPG peut corriger.
            int? year = truth?.Year ?? item.ProductionYear;
            string overview = truth?.Overview ?? item.Overview;
            string cleanTitle = TmdbLookupTool.CleanEpgTitle(epgTitle);
            if (string.IsNullOrWhiteSpace(cleanTitle)) cleanTitle = epgTitle;

            // Date ISO en fin de titre = marqueur d'échec d'identification
            // d'Emby (passe 04 h : truth nulle, l'année vient de l'item).
            // C'est une date de diffusion : l'année qu'elle induit est
            // « soft » — on ne filtre pas la recherche TMDB dessus et la
            // porte ne la rejette pas ; l'acceptation repose alors sur le
            // titre lexicale + le juge synopsis.
            bool yearSoft = truth == null && TmdbLookupTool.HasEmbyDateMarker(epgTitle);
            int? searchYear = yearSoft ? (int?)null : year;
            int? gateYear = yearSoft ? (int?)null : year;
            if (yearSoft)
                _logger?.Info("[LLM_AI] OrphanIdentify : « {0} » — date Emby en fin de titre : année {1} indicative (soft), titre nettoyé pour la recherche.",
                    epgTitle, year.HasValue ? year.Value.ToString(CultureInfo.InvariantCulture) : "?");

            TmdbMeta meta = null;
            string stage = null;

            // S0 : recherche native Emby (moteur du dialogue « Identifier »).
            if (allowS0 && (searchYear.HasValue || yearSoft))
            {
                try
                {
                    meta = await ResolveViaEmbyAsync(cleanTitle, kind, isSeries, searchYear,
                        epgTitle, overview, userTmdb, gateYear, trace, ct).ConfigureAwait(false);
                    if (meta != null) stage = "S0";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger?.Info("[LLM_AI] OrphanIdentify : S0 « {0} » échoué ({1}).", epgTitle, ex.Message); }
            }

            // S1 : recherche multilingue. Exige une année fiable : sans année,
            // la garde lexicale (sans juge) peut accepter un faux homonyme ;
            // les items sans année restent à S2/S3 (juge sémantique) — sauf
            // année soft (marqueur Emby), où la recherche part SANS filtre
            // d'année et la porte retombe sur titre lexicale + juge.
            if (meta == null && (searchYear.HasValue || yearSoft))
            {
                try
                {
                    var s1 = await _tmdb.LookupMetaMultiLangAsync(cleanTitle, kind, searchYear, langs, ct).ConfigureAwait(false);
                    // Cascade annuelle : l'année de référence est une date de
                    // diffusion, parfois éloignée de l'année de sortie — si le
                    // filtre primary_release_year écarte tous les résultats,
                    // rejouer SANS filtre (PickFirstResult privilégie déjà le
                    // résultat à la bonne année).
                    if (s1 == null && searchYear.HasValue)
                        s1 = await _tmdb.LookupMetaMultiLangAsync(cleanTitle, kind, null, langs, ct).ConfigureAwait(false);
                    if (s1 != null) trace.SawCandidates = true; // une banque liste le titre
                    if (s1 != null && TitleMatches(cleanTitle, s1.Title, gateYear, s1.Year))
                    {
                        meta = s1;
                        stage = "S1";
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger?.Info("[LLM_AI] OrphanIdentify : S1 « {0} » échoué ({1}).", epgTitle, ex.Message); }
            }

            // S2 : proposition LLM validée par TMDB (juge). L'année reste
            // envoyée au LLM comme contexte indicatif ; la porte reçoit
            // gateYear (null si soft).
            if (meta == null)
            {
                try
                {
                    meta = await ResolveViaLlmAsync(cfg, epgTitle, cleanTitle, kind, year, gateYear,
                        overview, truth?.Channel, langs, userTmdb, trace, ct).ConfigureAwait(false);
                    if (meta != null) stage = "S2";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger?.Info("[LLM_AI] OrphanIdentify : S2 « {0} » échoué ({1}).", epgTitle, ex.Message); }
            }

            // S3 : recherche web (SearXNG) → ids IMDb/TMDB → validation TMDB + juge.
            if (meta == null && cfg.OrphanSearXngEnabled)
            {
                try
                {
                    meta = await ResolveViaSearXngAsync(cfg, epgTitle, cleanTitle, kind, gateYear,
                        yearSoft, overview, userTmdb, trace, ct).ConfigureAwait(false);
                    if (meta != null) stage = "S3";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger?.Info("[LLM_AI] OrphanIdentify : S3 « {0} » échoué ({1}).", epgTitle, ex.Message); }
            }

            if (meta == null || meta.TmdbId <= 0)
            {
                // Deux échecs de nature différente : aucun candidat vu dans
                // AUCUNE banque = introuvable (état terminal, tag not-found) ;
                // candidats vus mais rejetés par la porte = à réviser
                // (tag needs-review). Retrait de l'ancien tag d'échec avant
                // écriture : un item retraité bascule proprement de l'un à
                // l'autre (migration automatique du parc needs-review).
                string failureTag = trace.SawCandidates
                    ? OrphanIdentifyTask.TagNeedsReview
                    : OrphanIdentifyTask.TagNotFound;
                _logger?.Info("[LLM_AI] OrphanIdentify : « {0} » ({1}) — NON résolu ({2}).",
                    epgTitle, kind,
                    failureTag == OrphanIdentifyTask.TagNotFound ? "introuvable" : "needs-review");
                if (!dry && item != null)
                {
                    RemoveTag(item, OrphanIdentifyTask.TagNeedsReview);
                    RemoveTag(item, OrphanIdentifyTask.TagNotFound);
                    AddTag(item, failureTag);
                    item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                }
                return null;
            }

            if (dry)
            {
                _logger?.Info("[LLM_AI] OrphanIdentify (DRY-RUN) : « {0} » → tmdb={1} imdb={2} tvdb={3} ({4}) — aucune écriture.",
                    epgTitle, meta.TmdbId, meta.ImdbId ?? "—", meta.TvdbId ?? "—", stage);
                return meta;
            }

            await ApplyAsync(item, meta, kind, isSeries, ct).ConfigureAwait(false);
            _logger?.Info("[LLM_AI] OrphanIdentify : « {0} » → tmdb={1} imdb={2} tvdb={3} ({4}) ; Name verrouillé, tag {5}.",
                epgTitle, meta.TmdbId, meta.ImdbId ?? "—", meta.TvdbId ?? "—", stage, OrphanIdentifyTask.TagIdentified);
            return meta;
        }

        // ------------------------------------------------------------------
        //  Audit d'une identification Emby (chemin RecordingWatcher).
        // ------------------------------------------------------------------

        private async Task<Status> AuditEmbyIdsAsync(BaseItem item, EpgTruth truth,
            PluginConfiguration cfg, string kind, bool isSeries, bool dry,
            string userTmdb, bool verbose, CancellationToken ct)
        {
            string epgTitle = string.IsNullOrWhiteSpace(truth.Title) ? item.Name : truth.Title;
            string epgSynopsis = truth.Overview;

            if (string.IsNullOrWhiteSpace(epgSynopsis))
            {
                if (verbose)
                    _logger?.Info("[LLM_AI] Recording : « {0} » identifié par Emby (tmdb={1}) — pas de synopsis EPG à comparer, audit reporté.",
                        epgTitle, item.GetProviderId("tmdb") ?? "—");
                return Status.Skipped;
            }

            // Relire la fiche TMDB de l'id posé par Emby (tmdb > imdb > tvdb).
            TmdbMeta meta = null;
            string tmdbRaw = item.GetProviderId("tmdb");
            if (int.TryParse(tmdbRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId) && tmdbId > 0)
                meta = await _tmdb.LookupMetaByIdAsync(tmdbId, kind, userTmdb, ct).ConfigureAwait(false);
            if (meta == null && !string.IsNullOrWhiteSpace(item.GetProviderId("imdb")))
                meta = await _tmdb.FindByExternalIdAsync(item.GetProviderId("imdb").Trim(), "imdb_id", kind, userTmdb, ct).ConfigureAwait(false);
            if (meta == null && isSeries && !string.IsNullOrWhiteSpace(item.GetProviderId("tvdb")))
                meta = await _tmdb.FindByExternalIdAsync(item.GetProviderId("tvdb").Trim(), "tvdb_id", kind, userTmdb, ct).ConfigureAwait(false);

            if (meta == null)
            {
                _logger?.Info("[LLM_AI] Recording : « {0} » — fiche TMDB illisible pour l'id d'Emby, audit reporté.", epgTitle);
                return Status.Skipped;
            }

            var verdict = await _runner.JudgeSynopsisMatchAsync(cfg, epgTitle, truth.Year, epgSynopsis,
                meta.Title, meta.Year, meta.Overview, ct).ConfigureAwait(false);

            if (!verdict.IsValid)
            {
                // Juge indisponible : ni confirmation ni réfutation — on ne
                // détruit pas une identification existante sur un échec backend.
                _logger?.Info("[LLM_AI] Recording : « {0} » — juge synopsis indisponible, audit reporté.", epgTitle);
                return Status.Skipped;
            }

            if (verdict.Match)
            {
                if (dry)
                {
                    _logger?.Info("[LLM_AI] Recording (DRY-RUN) : « {0} » = id d'Emby (tmdb={1}) validé par le juge — aucune écriture.",
                        epgTitle, meta.TmdbId);
                    return Status.OrphanResolved;
                }
                await ApplyAsync(item, meta, kind, isSeries, ct).ConfigureAwait(false);
                _logger?.Info("[LLM_AI] Recording : « {0} » = id d'Emby (tmdb={1}) validé par le juge — verrous posés, tag {2}.",
                    epgTitle, meta.TmdbId, OrphanIdentifyTask.TagIdentified);
                return Status.OrphanResolved;
            }

            // Mismatch : identification Emby fausse. Retour à l'état EPG, puis
            // S1/S2/S3 immédiat (S0 inutile : c'est le moteur qui a produit le
            // faux match). Ne lève pas : RevertToEpgAsync est best-effort.
            _logger?.Info("[LLM_AI] Recording : « {0} » ≠ « {1} » (tmdb={2}) — identification Emby rejetée : {3}.",
                epgTitle, meta.Title, meta.TmdbId, verdict.Reason);
            if (dry)
            {
                _logger?.Info("[LLM_AI] Recording (DRY-RUN) : ids d'Emby rejetés pour « {0} » — aucune écriture (reversion + reprise S1/S2/S3 non exécutées).", epgTitle);
                return Status.NeedsReview;
            }

            await RevertToEpgAsync(item, truth, epgTitle, ct).ConfigureAwait(false);
            var langs = new[] { "en-US", "fr-FR", userTmdb }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // Trace pré-ensemencée : le juge vient de REJETER une fiche TMDB
            // réelle (celle de l'id d'Emby) — un candidat a été vu. L'échec du
            // re-pipeline reste donc needs-review, jamais introuvable.
            var retrace = new PipelineTrace { SawCandidates = true };
            var meta2 = await ResolvePipelineAsync(item, truth, cfg, kind, isSeries,
                false, langs, userTmdb, dry, retrace, ct).ConfigureAwait(false);
            return meta2 != null ? Status.OrphanResolved : Status.NeedsReview;
        }

        // ------------------------------------------------------------------
        //  Audit des taggés « revenus avec ids » (passe 04 h, option
        //  OrphanAuditTaggedIds). Un item taggé introuvable/besoin-revue peut
        //  avoir été identifié par Emby APRÈS le tag — parfois à tort
        //  (homonyme). Sans vérité EPG, le discriminateur est la garde
        //  lexicale titre↔fiche (SANS année : l'année posée vient d'Emby,
        //  pas du guide). Match → validation (verrous + tag identifié) ;
        //  mismatch → auto-remédiation : ids retirés, champs issus de la
        //  fausse fiche vidés, reprise immédiate S1→S2→S3. Respecte le
        //  dry-run. Best-effort.
        // ------------------------------------------------------------------

        private async Task<Status> AuditTaggedIdsAsync(BaseItem item, PluginConfiguration cfg,
            string kind, bool isSeries, bool dry, string userTmdb, bool verbose, CancellationToken ct)
        {
            string itemName = item.Name;
            string cleanName = TmdbLookupTool.CleanEpgTitle(itemName);
            if (string.IsNullOrWhiteSpace(cleanName)) cleanName = itemName;

            // Relire la fiche TMDB de l'id posé (tmdb > imdb > tvdb séries) —
            // même cascade que l'audit d'identification Emby.
            TmdbMeta meta = null;
            string tmdbRaw = item.GetProviderId("tmdb");
            if (int.TryParse(tmdbRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId) && tmdbId > 0)
                meta = await _tmdb.LookupMetaByIdAsync(tmdbId, kind, userTmdb, ct).ConfigureAwait(false);
            if (meta == null && !string.IsNullOrWhiteSpace(item.GetProviderId("imdb")))
                meta = await _tmdb.FindByExternalIdAsync(item.GetProviderId("imdb").Trim(), "imdb_id", kind, userTmdb, ct).ConfigureAwait(false);
            if (meta == null && isSeries && !string.IsNullOrWhiteSpace(item.GetProviderId("tvdb")))
                meta = await _tmdb.FindByExternalIdAsync(item.GetProviderId("tvdb").Trim(), "tvdb_id", kind, userTmdb, ct).ConfigureAwait(false);

            if (meta == null)
            {
                _logger?.Info("[LLM_AI] OrphanIdentify : audit taggé « {0} » — fiche TMDB illisible pour l'id posé, item laissé tel quel.", itemName);
                return Status.Skipped;
            }

            if (TitleMatches(cleanName, meta.Title, null, null))
            {
                if (dry)
                {
                    _logger?.Info("[LLM_AI] OrphanIdentify (DRY-RUN) : audit taggé « {0} » = « {1} » (tmdb={2}) → validation proposée — aucune écriture.",
                        itemName, meta.Title, meta.TmdbId);
                    return Status.OrphanResolved;
                }
                await ApplyAsync(item, meta, kind, isSeries, ct).ConfigureAwait(false);
                _logger?.Info("[LLM_AI] OrphanIdentify : audit taggé « {0} » = « {1} » (tmdb={2}) → validé, tag {3}.",
                    itemName, meta.Title, meta.TmdbId, OrphanIdentifyTask.TagIdentified);
                return Status.OrphanResolved;
            }

            // Mismatch : titre EPG ≠ fiche. Les ids sont CONSERVÉS — la garde
            // lexicale est aveugle aux titres traduits (dry-run 2026-09-17 :
            // « Erreur vitale » = « The Fatal Flaw », « Jungle en délire » =
            // « La Famille Delajungle » — fiches pourtant correctes) et la
            // reprise S1/S2/S3 échouerait de la même façon (même garde). On
            // flague needs-review : décision humaine dans l'éditeur Emby
            // (retirer l'id si la fiche est fausse, puis re-tagguer). Le
            // not-found est migré : l'item a des ids, il n'est plus
            // « introuvable », il attend une confirmation.
            _logger?.Info("[LLM_AI] OrphanIdentify : audit taggé « {0} » ≠ fiche « {1} » (tmdb={2}) — titre EPG divergent (traduction ? homonyme ?).",
                itemName, meta.Title ?? "—", meta.TmdbId);
            if (dry)
            {
                _logger?.Info("[LLM_AI] OrphanIdentify (DRY-RUN) : « {0} » à flaguer {1} (ids conservés) — aucune écriture.",
                    itemName, OrphanIdentifyTask.TagNeedsReview);
                return Status.NeedsReview;
            }

            RemoveTag(item, OrphanIdentifyTask.TagNotFound);
            AddTag(item, OrphanIdentifyTask.TagNeedsReview);
            item.UpdateToRepository(ItemUpdateType.MetadataEdit);
            _logger?.Info("[LLM_AI] OrphanIdentify : « {0} » tagué {1} — ids conservés, fiche à confirmer manuellement (éditeur Emby).",
                itemName, OrphanIdentifyTask.TagNeedsReview);
            return Status.NeedsReview;
        }

        /// <summary>
        /// Retrait des ids posés par Emby + retour à l'état EPG (overview/genres
        /// de la vérité, sinon vidés pour re-remplissage par la suite S1/S2/S3).
        /// Le Name EPG n'est jamais modifié (verrou posé comme pour une résolution).
        /// Best-effort : n'échoue jamais le pipeline.
        /// </summary>
        private async Task RevertToEpgAsync(BaseItem item, EpgTruth truth,
            string epgTitle, CancellationToken ct)
        {
            try
            {
                item.ProviderIds?.Remove("tmdb");
                item.ProviderIds?.Remove("imdb");
                item.ProviderIds?.Remove("tvdb");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Recording : retrait des ids d'Emby échoué ({0}) — poursuite.", ex.Message);
            }

            if (!string.IsNullOrWhiteSpace(truth.Overview))
                item.Overview = truth.Overview;
            else
                item.Overview = string.Empty; // synopsis d'Emby (faux) : vidé pour re-remplissage

            item.Genres = Array.Empty<string>(); // genres d'Emby (faux) : vidés

            AddLock(item, MetadataFields.Name);
            AddTag(item, OrphanIdentifyTask.TagNeedsReview);
            item.UpdateToRepository(ItemUpdateType.MetadataEdit);
            _logger?.Info("[LLM_AI] Recording : « {0} » retourné à l'état EPG (ids retirés, tag {1}) — reprise S1/S2/S3.",
                epgTitle, OrphanIdentifyTask.TagNeedsReview);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        // ------------------------------------------------------------------
        //  S0 : recherche native Emby (IProviderManager.GetRemoteSearchResults)
        //  — le moteur du dialogue « Identifier ». Candidats dans l'ordre
        //  d'Emby, porte renforcée (titre + année + juge quand les deux
        //  synopsis existent), conversion en fiche TMDB détaillée pour
        //  l'application. Aucun accepté → null.
        // ------------------------------------------------------------------

        private const int s_embyMaxCandidates = 8;

        private async Task<TmdbMeta> ResolveViaEmbyAsync(string cleanTitle, string kind, bool isSeries,
            int? searchYear, string epgTitle, string epgOverview, string userTmdb, int? gateYear,
            PipelineTrace trace, CancellationToken ct)
        {
            // La recherche native suit la langue configurée du serveur ; la
            // cascade multilingue de S1 reste le repli. searchYear null
            // (année soft) = recherche sans année.
            if (isSeries)
            {
                var query = new RemoteSearchQuery<SeriesInfo>
                {
                    SearchInfo = new SeriesInfo { Name = cleanTitle, Year = searchYear }
                };
                var results = await _providers.GetRemoteSearchResults<Series, SeriesInfo>(query, ct).ConfigureAwait(false);
                return await PickEmbyCandidateAsync(results, kind, gateYear, epgTitle, epgOverview, userTmdb, cleanTitle, trace, ct).ConfigureAwait(false);
            }

            var mq = new RemoteSearchQuery<MovieInfo>
            {
                SearchInfo = new MovieInfo { Name = cleanTitle, Year = searchYear }
            };
            var mresults = await _providers.GetRemoteSearchResults<Movie, MovieInfo>(mq, ct).ConfigureAwait(false);
            return await PickEmbyCandidateAsync(mresults, kind, gateYear, epgTitle, epgOverview, userTmdb, cleanTitle, trace, ct).ConfigureAwait(false);
        }

        private async Task<TmdbMeta> PickEmbyCandidateAsync(
            IEnumerable<MediaBrowser.Model.Providers.RemoteSearchResult> results,
            string kind, int? gateYear, string epgTitle, string epgOverview,
            string userTmdb, string cleanTitle, PipelineTrace trace, CancellationToken ct)
        {
            if (results == null) return null;
            int taken = 0;
            foreach (var cand in results)
            {
                ct.ThrowIfCancellationRequested();
                if (cand == null) continue;
                if (taken++ >= s_embyMaxCandidates) break;

                var meta = new TmdbMeta
                {
                    Kind = kind,
                    Title = cand.Name,
                    Overview = cand.Overview,
                    Year = cand.ProductionYear,
                    TmdbId = ParseProviderId(cand.ProviderIds, "tmdb"),
                    ImdbId = StrProviderId(cand.ProviderIds, "imdb"),
                    TvdbId = StrProviderId(cand.ProviderIds, "tvdb")
                };
                if (meta.TmdbId <= 0 && string.IsNullOrWhiteSpace(meta.ImdbId) && string.IsNullOrWhiteSpace(meta.TvdbId))
                    continue;
                trace.SawCandidates = true; // une banque (Emby) a surface un candidat identifié

                // Porte renforcée : titre lexicale + année + juge synopsis.
                if (!await AcceptCandidateAsync(Plugin.Instance?.Configuration, meta, epgTitle,
                        gateYear, epgOverview, true, cleanTitle, ct).ConfigureAwait(false))
                    continue;

                // Converti en fiche TMDB détaillée (genres, poster, statut) ;
                // relecture impossible → candidat suivant.
                TmdbMeta detail = null;
                if (meta.TmdbId > 0)
                    detail = await _tmdb.LookupMetaByIdAsync(meta.TmdbId, kind, userTmdb, ct).ConfigureAwait(false);
                if (detail == null && !string.IsNullOrWhiteSpace(meta.ImdbId))
                    detail = await _tmdb.FindByExternalIdAsync(meta.ImdbId, "imdb_id", kind, userTmdb, ct).ConfigureAwait(false);
                if (detail == null && kind == "series" && !string.IsNullOrWhiteSpace(meta.TvdbId))
                    detail = await _tmdb.FindByExternalIdAsync(meta.TvdbId, "tvdb_id", kind, userTmdb, ct).ConfigureAwait(false);
                if (detail == null)
                {
                    _logger?.Info("[LLM_AI] OrphanIdentify : S0 « {0} » — candidat « {1} » (tmdb={2}) sans fiche détaillée, candidat suivant.",
                        epgTitle, meta.Title ?? "—", meta.TmdbId);
                    continue;
                }
                return detail;
            }
            return null;
        }

        private static int ParseProviderId(ProviderIdDictionary dict, string key)
        {
            var raw = StrProviderId(dict, key);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
        }

        private static string StrProviderId(ProviderIdDictionary dict, string key)
        {
            if (dict == null || string.IsNullOrWhiteSpace(key)) return null;
            try
            {
                return dict.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------
        //  S2 : proposition LLM → validation TMDB
        // ------------------------------------------------------------------

        private async Task<TmdbMeta> ResolveViaLlmAsync(PluginConfiguration cfg,
            string epgTitle, string cleanTitle, string kind, int? year, int? gateYear,
            string overview, string channel, string[] langs, string userTmdb,
            PipelineTrace trace, CancellationToken ct)
        {
            var guess = await _runner.ResolveIdsAsync(cfg, epgTitle, kind, year, overview, channel, ct).ConfigureAwait(false);
            if (guess.IsEmpty) return null;

            // Année de référence (enregistrement) : préférence à l'année de la
            // porte (null si année soft), sinon à l'année proposée par le LLM.
            int? expectedYear = gateYear ?? guess.Year;

            // 1) id IMDb → TMDB /find.
            if (!string.IsNullOrWhiteSpace(guess.ImdbId))
            {
                var m = await _tmdb.FindByExternalIdAsync(guess.ImdbId.Trim(), "imdb_id", kind, userTmdb, ct).ConfigureAwait(false);
                if (m != null) trace.SawCandidates = true; // fiche réelle reléguée, même rejetée
                if (await AcceptCandidateAsync(cfg, m, epgTitle, expectedYear, overview, false, null, ct, corroborateNoSynopsis: true).ConfigureAwait(false))
                    return m;
            }

            // 1b) id TVDB (séries) → TMDB /find tvdb_id : les séries québécoises
            // absentes de TMDB par titre existent souvent côté TVDB, et le LLM
            // connaît mieux cette banque pour les séries.
            if (kind == "series" && !string.IsNullOrWhiteSpace(guess.TvdbId))
            {
                var m = await _tmdb.FindByExternalIdAsync(guess.TvdbId.Trim(), "tvdb_id", kind, userTmdb, ct).ConfigureAwait(false);
                if (m != null) trace.SawCandidates = true;
                if (await AcceptCandidateAsync(cfg, m, epgTitle, expectedYear, overview, false, null, ct, corroborateNoSynopsis: true).ConfigureAwait(false))
                    return m;
            }

            // 2) id TMDB → relire la fiche (un id halluciné renvoie null).
            if (guess.TmdbId > 0)
            {
                var m = await _tmdb.LookupMetaByIdAsync(guess.TmdbId, kind, userTmdb, ct).ConfigureAwait(false);
                if (m != null) trace.SawCandidates = true;
                if (await AcceptCandidateAsync(cfg, m, epgTitle, expectedYear, overview, false, null, ct, corroborateNoSynopsis: true).ConfigureAwait(false))
                    return m;
            }

            // 3) titre original proposé → recherche S1 sur ce titre (garde lexicale).
            if (!string.IsNullOrWhiteSpace(guess.OriginalTitle))
            {
                string ot = TmdbLookupTool.CleanEpgTitle(guess.OriginalTitle);
                if (!string.IsNullOrWhiteSpace(ot))
                {
                    int? y = guess.Year ?? year;
                    var m = await _tmdb.LookupMetaMultiLangAsync(ot, kind, y, langs, ct).ConfigureAwait(false);
                    if (m != null) trace.SawCandidates = true;
                    if (await AcceptCandidateAsync(cfg, m, epgTitle, expectedYear, overview, true, ot, ct).ConfigureAwait(false))
                        return m;
                }
            }

            return null;
        }

        // ------------------------------------------------------------------
        //  S3 : recherche web (SearXNG via WebSearchTool) → ids IMDb extraits
        //  des URLs de résultats → validation TMDB + porte d'acceptation.
        // ------------------------------------------------------------------

        private static readonly Regex s_imdbUrlRe = new Regex(
            @"imdb\.com/(?:[a-z\-]+/)?title/(tt\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Ids TMDB dans les URLs de résultats web — capture aussi
        /// les URLs slug (<c>themoviedb.org/movie/385870-arthur-…</c>) : les
        /// fiches sans présence IMDb n'atterrissent que par cette voie.</summary>
        private static readonly Regex s_tmdbUrlRe = new Regex(
            @"themoviedb\.org/(?:movie|tv)/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private async Task<TmdbMeta> ResolveViaSearXngAsync(PluginConfiguration cfg,
            string epgTitle, string cleanTitle, string kind, int? gateYear, bool yearSoft,
            string overview, string userTmdb, PipelineTrace trace, CancellationToken ct)
        {
            // Requête : titre NETTOYÉ (la date-marqueur Emby empoisonne la
            // recherche) + année fiable si connue (aide à lever l'ambiguïté ;
            // jamais l'année soft).
            string query = cleanTitle;
            if (gateYear.HasValue) query = cleanTitle + " " + gateYear.Value.ToString(CultureInfo.InvariantCulture);

            string json;
            using (var doc = JsonDocument.Parse("{\"query\":\"" + JsonEscape(query) + "\"}"))
            {
                json = await _search.ExecuteAsync(doc.RootElement, ct).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                _logger?.Info("[LLM_AI] OrphanIdentify : S3 « {0} » — recherche web sans réponse.", epgTitle);
                return null;
            }

            // Extraire les ids IMDb et TMDB (uniques, ordre d'apparition =
            // pertinence). Les fiches sans présence IMDb (docs québécois…)
            // n'atterrissent que par leur id TMDB dans les URLs de résultats.
            var imdbIds = new List<string>();
            var tmdbIds = new List<string>();
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
                    {
                        _logger?.Info("[LLM_AI] OrphanIdentify : S3 « {0} » — recherche web en erreur ({1}).",
                            epgTitle, TruncateLog(errEl.GetString(), 120));
                        return null;
                    }
                    if (root.TryGetProperty("results", out var res) && res.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in res.EnumerateArray())
                        {
                            CollectIds(r, "url", imdbIds, tmdbIds);
                            CollectIds(r, "content", imdbIds, tmdbIds);
                        }
                    }
                    if (root.TryGetProperty("infobox", out var ib) && ib.ValueKind == JsonValueKind.String)
                        CollectIdsFromText(ib.GetString(), imdbIds, tmdbIds);
                    if (root.TryGetProperty("answers", out var ans) && ans.ValueKind == JsonValueKind.Array)
                        foreach (var a in ans.EnumerateArray())
                            if (a.ValueKind == JsonValueKind.String) CollectIdsFromText(a.GetString(), imdbIds, tmdbIds);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger?.Info("[LLM_AI] OrphanIdentify : S3 « {0} » — échec parsing résultats ({1}).", epgTitle, ex.Message); }

            if (imdbIds.Count == 0 && tmdbIds.Count == 0)
            {
                // Des résultats web existent pour n'importe quelle requête
                // (définitions de dictionnaire…) — seuls les ids IMDb/TMDB
                // extraits comptent comme candidats (sémantique « introuvable »).
                _logger?.Info("[LLM_AI] OrphanIdentify : S3 « {0} » — aucun id IMDb/TMDB trouvé dans les résultats web.", epgTitle);
                return null;
            }
            trace.SawCandidates = true; // ids trouvés dans le web

            int? expectedYear = gateYear;

            foreach (string imdbId in imdbIds)
            {
                var m = await _tmdb.FindByExternalIdAsync(imdbId, "imdb_id", kind, userTmdb, ct).ConfigureAwait(false);
                if (await AcceptCandidateAsync(cfg, m, epgTitle, expectedYear, overview, false, null, ct).ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(overview) || string.IsNullOrWhiteSpace(m.Overview))
                        _logger?.Info("[LLM_AI] OrphanIdentify : S3 « {0} » → candidat « {1} » (tt={2}) accepté SANS vérif synopsis — à confirmer visuellement.",
                            epgTitle, m.Title, imdbId);
                    return m;
                }
            }

            foreach (string tmdbIdRaw in tmdbIds)
            {
                if (!int.TryParse(tmdbIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId) || tmdbId <= 0)
                    continue;
                var m = await _tmdb.LookupMetaByIdAsync(tmdbId, kind, userTmdb, ct).ConfigureAwait(false);
                if (await AcceptCandidateAsync(cfg, m, epgTitle, expectedYear, overview, false, null, ct).ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(overview) || string.IsNullOrWhiteSpace(m.Overview))
                        _logger?.Info("[LLM_AI] OrphanIdentify : S3 « {0} » → candidat « {1} » (tmdb={2}) accepté SANS vérif synopsis — à confirmer visuellement.",
                            epgTitle, m.Title, tmdbId);
                    return m;
                }
            }

            return null;
        }

        private void CollectIds(JsonElement parent, string prop, List<string> imdbIds, List<string> tmdbIds)
        {
            if (!parent.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.String) return;
            CollectIdsFromText(el.GetString(), imdbIds, tmdbIds);
        }

        private void CollectIdsFromText(string text, List<string> imdbIds, List<string> tmdbIds)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Match match in s_imdbUrlRe.Matches(text))
            {
                string id = match.Groups[1].Value;
                if (!imdbIds.Contains(id)) imdbIds.Add(id);
            }
            foreach (Match match in s_tmdbUrlRe.Matches(text))
            {
                string id = match.Groups[1].Value;
                if (!tmdbIds.Contains(id)) tmdbIds.Add(id);
            }
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static string TruncateLog(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");

        // ------------------------------------------------------------------
        //  Porte d'acceptation d'un candidat TMDB : année + juge sémantique.
        //  Acceptation : année compatible ET (pas de synopsis à comparer, OU
        //  juge LLM confirme que les synopsis décrivent la même œuvre).
        //  Un candidat rejeté n'arrête jamais la chaîne (« on continue »).
        //  Optionnellement, garde de titre lexicale (voies par titre).
        //  Voies par id (S2) sans synopsis comparable : corroboration exigée
        //  (corroborateNoSynopsis) — titre lexicale ou années des deux côtés.
        // ------------------------------------------------------------------

        private async Task<bool> AcceptCandidateAsync(PluginConfiguration cfg, TmdbMeta m,
            string epgTitle, int? epgYear, string epgSynopsis,
            bool requireTitleMatch, string matchTitle, CancellationToken ct,
            bool corroborateNoSynopsis = false)
        {
            if (m == null || m.TmdbId <= 0) return false;

            // Garde-fou année (deux œuvres d'époques différentes).
            if (!YearCompatible(epgYear, m.Year)) return false;

            // Garde de titre lexicale (voies par titre uniquement).
            if (requireTitleMatch && !TitleMatches(matchTitle, m.Title, epgYear, m.Year)) return false;

            // Pas de synopsis à comparer : acceptation sur année (+titre) —
            // conserve le comportement EPG sans synopsis. Sur les voies PAR ID
            // (S2 : id IMDb/TMDB proposé par le LLM), un id seul n'est pas une
            // preuve — un id halluciné qui tombe sur une fiche réelle mais
            // sans rapport serait accepté à tort ; exiger alors une
            // corroboration (titre lexicale OU années des deux côtés).
            if (string.IsNullOrWhiteSpace(epgSynopsis) || string.IsNullOrWhiteSpace(m.Overview))
            {
                if (!corroborateNoSynopsis) return true;
                bool titleOk = TitleMatches(TmdbLookupTool.CleanEpgTitle(epgTitle), m.Title, epgYear, m.Year);
                bool yearsOk = epgYear.HasValue && m.Year.HasValue; // YearCompatible déjà passé plus haut
                if (titleOk || yearsOk) return true;
                _logger?.Info("[LLM_AI] OrphanIdentify : candidat « {0} » (tmdb={1}) rejeté — voie par id sans synopsis comparable, preuve insuffisante (titre ou année).",
                    m.Title, m.TmdbId);
                return false;
            }

            var v = await _runner.JudgeSynopsisMatchAsync(cfg, epgTitle, epgYear, epgSynopsis,
                m.Title, m.Year, m.Overview, ct).ConfigureAwait(false);

            if (!v.IsValid)
            {
                _logger?.Info("[LLM_AI] OrphanIdentify : juge synopsis indisponible pour « {0} » — candidat « {1} » rejeté par prudence.",
                    epgTitle, m.Title);
                return false;
            }
            if (!v.Match)
            {
                _logger?.Info("[LLM_AI] OrphanIdentify : juge synopsis : « {0} » ≠ candidat « {1} » ({2}) — rejet, on continue.",
                    epgTitle, m.Title, v.Reason);
                return false;
            }
            _logger?.Info("[LLM_AI] OrphanIdentify : juge synopsis : « {0} » = candidat « {1} » ({2}) — accepté.",
                epgTitle, m.Title, v.Reason);
            return true;
        }

        // ------------------------------------------------------------------
        //  Application non destructive + verrouillage
        // ------------------------------------------------------------------

        internal async Task ApplyAsync(BaseItem item, TmdbMeta meta, string kind, bool isSeries, CancellationToken ct)
        {
            // --- Provider ids (uniquement ceux absents) ---
            if (meta.TmdbId > 0 && string.IsNullOrWhiteSpace(item.GetProviderId("tmdb")))
                item.SetProviderId("tmdb", meta.TmdbId.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(meta.ImdbId) && string.IsNullOrWhiteSpace(item.GetProviderId("imdb")))
                item.SetProviderId("imdb", meta.ImdbId.Trim());
            if (isSeries && !string.IsNullOrWhiteSpace(meta.TvdbId) && string.IsNullOrWhiteSpace(item.GetProviderId("tvdb")))
                item.SetProviderId("tvdb", meta.TvdbId.Trim());

            // --- Overview : seulement si vide ---
            bool setOverview = false;
            if (string.IsNullOrWhiteSpace(item.Overview) && !string.IsNullOrWhiteSpace(meta.Overview))
            {
                item.Overview = meta.Overview;
                setOverview = true;
            }

            // --- Genres : seulement si vides ---
            bool setGenres = false;
            if ((item.Genres == null || item.Genres.Length == 0) && meta.Genres != null && meta.Genres.Length > 0)
            {
                item.Genres = meta.Genres;
                setGenres = true;
            }

            // --- Poster Primary : seulement si manquant (best-effort) ---
            bool setImage = false;
            if (!item.HasImage(ImageType.Primary, 0) && !string.IsNullOrWhiteSpace(meta.PosterUrl))
            {
                setImage = await TrySavePosterAsync(item, meta.PosterUrl, ct).ConfigureAwait(false);
            }

            // --- Verrouillage (add-only, jamais de retrait) ---
            AddLock(item, MetadataFields.Name);
            if (setOverview) AddLock(item, MetadataFields.Overview);
            if (setGenres) AddLock(item, MetadataFields.Genres);

            // --- Tag d'idempotence ---
            RemoveTag(item, OrphanIdentifyTask.TagNeedsReview);
            RemoveTag(item, OrphanIdentifyTask.TagNotFound);
            AddTag(item, OrphanIdentifyTask.TagIdentified);

            var updateType = ItemUpdateType.MetadataEdit | (setImage ? ItemUpdateType.ImageUpdate : ItemUpdateType.None);
            item.UpdateToRepository(updateType);
        }

        private async Task<bool> TrySavePosterAsync(BaseItem item, string posterUrl, CancellationToken ct)
        {
            try
            {
                var fs = _host.TryResolve<IFileSystem>();
                if (_providers == null || fs == null) return false;

                using (var resp = await _http.GetAsync(posterUrl, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return false;
                    using (var ms = new MemoryStream())
                    {
                        await resp.Content.CopyToAsync(ms).ConfigureAwait(false);
                        if (ms.Length == 0) return false;
                        ms.Position = 0;

                        string mime = MimeFromUrl(posterUrl);
                        var dirSvc = new DirectoryService(fs);
                        var libOpts = _library.GetLibraryOptions(item);
                        await _providers.SaveImage(item, libOpts, ms, mime.AsMemory(),
                            ImageType.Primary, null, null, dirSvc, true, ct).ConfigureAwait(false);
                        return true;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Info("[LLM_AI] OrphanIdentify : poster « {0} » échoué ({1}) — ignoré.", item.Name, ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------
        //  Helpers (partagés avec la tâche)
        // ------------------------------------------------------------------

        /// <summary>Orphelin = aucun id provider IMDb/TMDB/TVDB présent.</summary>
        internal static bool IsOrphanItem(BaseItem item)
        {
            return !HasItemProviderId(item, "imdb")
                && !HasItemProviderId(item, "tmdb")
                && !HasItemProviderId(item, "tvdb");
        }

        internal static bool HasItemProviderId(BaseItem item, string key)
        {
            if (item == null) return false;
            try { return !string.IsNullOrWhiteSpace(item.GetProviderId(key)); }
            catch { return false; }
        }

        /// <summary>Carte .strm de la bibliothèque du plugin : hors périmètre.</summary>
        internal static bool IsStrmCard(BaseItem item)
        {
            var path = item?.Path;
            if (string.IsNullOrWhiteSpace(path)) return false;
            return path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Garde de titre lexicale (casse/accents/ponctuation ignorés,
        /// inclusion acceptée ; années > 1 an d'écart refusées).</summary>
        internal static bool TitleMatches(string epgTitle, string tmdbTitle, int? epgYear, int? metaYear)
        {
            if (string.IsNullOrWhiteSpace(tmdbTitle)) return false;
            string a = NormalizeTitle(epgTitle);
            string b = NormalizeTitle(tmdbTitle);
            if (a.Length == 0 || b.Length == 0) return false;

            bool titleOk = a == b || a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
            if (!titleOk) return false;

            if (epgYear.HasValue && metaYear.HasValue && Math.Abs(epgYear.Value - metaYear.Value) > 1)
                return false;

            return true;
        }

        /// <summary>Garde d'année : accepte si l'une des deux années est
        /// inconnue, ou si elles diffèrent d'au plus un an.</summary>
        internal static bool YearCompatible(int? expected, int? actual)
        {
            if (!expected.HasValue || !actual.HasValue) return true;
            return Math.Abs(expected.Value - actual.Value) <= 1;
        }

        internal static string NormalizeTitle(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (var c in s.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            var t = sb.ToString().ToLowerInvariant();
            sb.Clear();
            foreach (var c in t)
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        private static string MimeFromUrl(string url) =>
            url.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";

        internal static void AddLock(BaseItem item, MetadataFields field)
        {
            var lf = item.LockedFields;
            var list = lf == null ? new List<MetadataFields>() : new List<MetadataFields>(lf);
            if (list.Contains(field)) return;
            list.Add(field);
            item.LockedFields = list.ToArray();
        }

        internal static void AddTag(BaseItem item, string tag)
        {
            var t = item.Tags ?? Array.Empty<string>();
            if (Array.IndexOf(t, tag) >= 0) return;
            var list = new List<string>(t) { tag };
            item.Tags = list.ToArray();
        }

        internal static void RemoveTag(BaseItem item, string tag)
        {
            var t = item.Tags ?? Array.Empty<string>();
            int i = Array.IndexOf(t, tag);
            if (i < 0) return;
            var list = new List<string>(t);
            list.RemoveAt(i);
            item.Tags = list.ToArray();
        }
    }
}