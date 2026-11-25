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
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Model.Querying;

using Emby.Notifications;

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
    /// indisponible (backend) → on ne touche pas (prudence). Une fiche
    /// MUETTE (overview vide — le juge n'aura jamais d'avis) sans preuve
    /// corroborante (titre ≠ égalité exacte ET année incompatible) → même
    /// chemin que le mismatch (v2.18.0) : l'id d'Emby n'est alors qu'une
    /// supposition non vérifiable.</item>
    /// <item><b>SB — ancrage bibliothèque</b> (dossier frère) : avant toute banque externe, un item
    /// homonyme de la bibliothèque (± « (année) », parenthèses et bruit EPG retirés) portant déjà
    /// une fiche la propose comme candidat n° 0 à la porte commune — sans ancrage, la chaîne peut
    /// DIVERGER de la fiche du dossier existant. Règle d'année dédiée (séries) : année item ≥ année
    /// fiche − 1 ; un frère homonyme d'une AUTRE époque sans synopsis à arbitrer est rejeté
    /// (reboot/homonyme).</item>
    /// <item><b>S0 — recherche native Emby</b> (option
    /// <c>OrphanEmbyFirstPass</c>) : <c>IProviderManager.GetRemoteSearchResults</c>
    /// (le moteur du dialogue « Identifier ») sur le titre EPG nettoyé.
    /// Candidats dans l'ordre d'Emby, porte renforcée (titre + année + juge).
    /// Accepté → fiche TMDB détaillée → application.</item>
    /// <item><b>S1 — recherche TMDB multilingue</b> (garde lexicale).
    /// <b>S2 — proposition LLM</b> (juge). <b>S3 — SearXNG → id IMDb</b>
    /// (juge).</item>
    /// </list>
    /// <para><b>Repli de type (series→movie)</b> : quand l'item est une série
    /// et que la chaîne échoue en kind series, la chaîne est rejouée en kind
    /// movie sur le même item. Emby type l'import DVR d'après le guide : un
    /// film/documentaire diffusé avec des métadonnées d'épisode atterrit en
    /// Series/Episode alors que l'œuvre est un film — la banque « tv » de
    /// TMDB ne la connaîtra jamais. Unidirectionnel : l'inverse (œuvre
    /// sérielle importée comme Movie) n'est pas observé et un vrai film
    /// homonyme d'une série risquerait un faux match. Le type d'item n'est
    /// jamais modifié.</para>
    /// <para><b>Porte d'acceptation</b> commune : année compatible (±1 an) +
    /// (garde lexicale sur les voies par titre) + juge LLM dès que les deux
    /// synopsis existent (sinon acceptation sur année+titre, comme la pratique
    /// EPG sans synopsis). Rejet = « on continue de chercher ». Toute
    /// acceptation passe ensuite la <b>garde « fiche déjà portée »</b>
    /// (<see cref="FicheConflictGateAsync"/>) : un autre item de bibliothèque
    /// portant déjà ce tmdb/tvdb/imdb au MÊME titre (± « (année) ») = même
    /// œuvre en deux dossiers (passage + log — la fusion manuelle reste à
    /// l'usager, le plugin ne touche jamais les fichiers) ; au titre
    /// divergent, le juge LLM arbitre (renommage/traduction → passage ;
    /// œuvres différentes, juge indisponible ou aucun synopsis → rejet par
    /// prudence).</para>
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

        internal sealed class CandidateVerdict
        {
            public string Stage;
            public TmdbMeta Meta;
            public string ProofLevel;
            public string ProofSource;
            public string ProofEvidence;
            public bool Accepted;
            public string Reason;
            public bool CrossKind;
        }

        internal sealed class ChatResolveReport
        {
            public string Status; // "resolvable", "already_identified", "needs_review", "not_found", "skipped"
            public BaseItem Item;
            public string Kind;
            public bool IsSeries;
            public bool CrossKind;
            public string WinningStage;
            public TmdbMeta Winner;
            public string WinningProofLevel;
            public string WinningProofSource;
            public string WinningProofEvidence;
            public string TruthSource;
            public int? TruthYear;
            public bool TruthHasOverview;
            public List<CandidateVerdict> Candidates = new List<CandidateVerdict>();
            public List<BaseItem> ExistingCarriers = new List<BaseItem>();
            public int? RuntimeMinutes;
        }

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
        /// En mode <see cref="CollectMode"/> (chat admin), enregistre chaque
        /// candidat évalué avec son verdict et sa preuve, sans rien écrire.
        /// </summary>
        internal sealed class PipelineTrace
        {
            public bool SawCandidates;
            public bool CollectMode;
            public bool IgnoreExistingItemIds;
            public bool CrossKind;
            public string WinningStage;
            public TmdbMeta Winner;
            public string WinningProofLevel;
            public string WinningProofSource;
            public string WinningProofEvidence;
            public readonly List<CandidateVerdict> Candidates = new List<CandidateVerdict>();

            internal void RecordCandidate(string stage, TmdbMeta meta, string proofLevel, string proofSource,
                string proofEvidence, bool accepted, string reason, bool crossKind)
            {
                if (!CollectMode || meta == null) return;
                Candidates.Add(new CandidateVerdict
                {
                    Stage = stage,
                    Meta = meta,
                    ProofLevel = proofLevel,
                    ProofSource = proofSource,
                    ProofEvidence = proofEvidence,
                    Accepted = accepted,
                    Reason = reason,
                    CrossKind = crossKind
                });
            }
        }

        /// <summary>
        /// Vérité EPG figée à la fin de l'enregistrement (RecordingWatcher) :
        /// programme du guide tel qu'il était AVANT toute identification. Tous
        /// les champs optionnels.
        /// </summary>
        internal sealed class EpgTruth
        {
            /// <summary>Titre de diffusion EPG (programmes sériés : souvent le
            /// titre d'ÉPISODE — « Multicolore », pas « Empathie »).</summary>
            public string Title;
            /// <summary>Titre SÉRIE du guide (SeriesName du programme ; vide pour les films et
            /// programmes creux). Ancre d'audit v2.19.2 : figé à la fin d'enregistrement, JAMAIS
            /// dérivé de l'item (son nom dérive de la fiche posée par Emby : validation
            /// circulaire).</summary>
            public string SeriesName;
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
            CancellationToken ct, EpgTruth deferredTruth = null)
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

            // Langue de la fiche S1/S2/S3 : langue préférée de la bibliothèque de l'item, sinon
            // langue du PROGRAMME (synopsis du guide, titre en repli), sinon langue usager
            // (comportement historique). TmdbLangPriority non vide : le CONTENU passe en tête et la
            // liste priorisée gouverne le synopsis (table translations, UNE lecture).
            string metaLang = ResolveItemMetaTmdb(item, truth, cfg, out var prio);
            string effTmdb = metaLang ?? userTmdb;
            if (metaLang != null && !string.Equals(metaLang, userTmdb, StringComparison.OrdinalIgnoreCase))
                _logger?.Info("[LLM_AI] OrphanIdentify : « {0} » — fiche recherchée en {1} (langue du contenu), langue usager {2}.",
                    ResolveEpgTitle(item, truth, deferredTruth) ?? itemName, metaLang, userTmdb);
            if (prio != null)
                _logger?.Info("[LLM_AI] OrphanIdentify : « {0} » — priorité synopsis TMDB active : {1} (contenu en tête de famille, dernier rang = repli).",
                    ResolveEpgTitle(item, truth, deferredTruth) ?? itemName, string.Join(", ", prio));

            // Audit des taggés « revenus avec ids » (opt-in OrphanAuditTaggedIds) : Emby peut
            // identifier un item APRÈS le tag, parfois à tort (homonyme) ; mismatch =
            // auto-remédiation. Passe 04 h uniquement : avec vérité EPG (RecordingWatcher), l'audit
            // à juge synopsis ci-dessous reste la voie supérieure.
            if (truth == null && !orphan && (taggedReview || taggedNotFound) && cfg.OrphanAuditTaggedIds)
                return await AuditTaggedIdsAsync(item, cfg, kind, isSeries, dry, effTmdb, verbose, ct, deferredTruth, prio).ConfigureAwait(false);

            if (tagged) return Status.Skipped;

            // --- Audit d'une identification Emby (truth fournie = RecordingWatcher).
            // La passe 04 h ne passe ici jamais : les non-orphelins y sont sautés.
            if (!orphan)
            {
                if (truth == null || taggedIdentified) return Status.Skipped;
                return await AuditEmbyIdsAsync(item, truth, cfg, kind, isSeries, dry, effTmdb, metaLang, ct, prio).ConfigureAwait(false);
            }

            // Même piège que l'entrée du pipeline (t="" = programme creux) : la coalescence doit
            // tolérer les espaces ; Skipped seulement si les DEUX titres sont vides.
            string epgTitle = ResolveEpgTitle(item, truth, deferredTruth);
            if (string.IsNullOrWhiteSpace(epgTitle)) return Status.Skipped;

            // Orphelin pur : S0 (Emby natif) puis S1→S2→S3. La vérité
            // différée y est transmise comme ANCRE de la porte d'acceptation
            // (v2.20.17) — jamais comme vérité de la passe série (v2.16.3).
            var langs = BuildLangCascade(metaLang, userTmdb, prio);
            bool allowS0 = cfg.OrphanEmbyFirstPass && _providers != null;
            var trace = new PipelineTrace();
            var meta = await ResolvePipelineAsync(item, truth, cfg, kind, isSeries,
                allowS0, langs, effTmdb, dry, trace, ct, deferredTruth: deferredTruth, overviewChain: prio).ConfigureAwait(false);

            // Repli de type « film importé comme série » : Emby type l'import DVR d'après le guide
            // ; la passe en kind series échoue alors PAR CONSTRUCTION (la banque « tv » de TMDB ne
            // connaît pas l'œuvre). On rejoue le pipeline en kind « movie » sur le même item — même
            // porte ; la fiche appliquée n'écrit que les ids/champs manquants et le type de l'item
            // n'est jamais modifié. Unidirectionnel : l'inverse risquerait un faux match
            // d'homonyme. Trace pré-ensemencée : le tag d'échec (needs-review vs introuvable)
            // fusionne les candidats vus par les DEUX passes.
            if (meta == null && isSeries)
            {
                // Vérité EPG différée (passe 04 h) : le watcher gèle l'entrée d'un épisode «
                // couvert par l'audit de sa série » ; sans relais, le repli film courrait sur un
                // item creux. Injectée ICI UNIQUEMENT : l'année EPG d'un épisode = année de SAISON,
                // l'injecter dans la passe série casserait la garde ±1 des séries longues ; dans la
                // passe film, l'année du guide = année de l'œuvre. Le Title de l'entrée différée
                // est null par construction (titre d'épisode ≠ titre de série) — repli item.Name.
                var movieTruth = deferredTruth ?? truth;
                if (deferredTruth != null && (deferredTruth.Year.HasValue || !string.IsNullOrWhiteSpace(deferredTruth.Overview)))
                    _logger?.Info(
                        "[LLM_AI] OrphanIdentify : « {0} » — repli film avec vérité EPG différée du store (année={1}, synopsis={2}) ; la passe série reste volontairement sans vérité (année d'épisode ≠ année de série).",
                        epgTitle,
                        deferredTruth.Year.HasValue ? deferredTruth.Year.Value.ToString(CultureInfo.InvariantCulture) : "?",
                        string.IsNullOrWhiteSpace(deferredTruth.Overview) ? "non" : "oui");
                var movieTrace = new PipelineTrace { SawCandidates = trace.SawCandidates };
                meta = await ResolvePipelineAsync(item, movieTruth, cfg, "movie", false,
                    allowS0, langs, effTmdb, dry, movieTrace, ct, crossKind: true, overviewChain: prio).ConfigureAwait(false);
                trace = movieTrace;
                if (meta != null)
                    _logger?.Info(
                        "[LLM_AI] OrphanIdentify : {0}« {1} » — fiche de type film appliquée sur un item série — à confirmer (type d'item conservé, Emby ne peut pas relire cette fiche au refresh).",
                        dry ? "(DRY-RUN) " : "", epgTitle);
                // Fiche croisée appliquée → needs-review (pas identified) :
                // l'audit OrphanAuditTaggedIds des passes suivantes relit la
                // fiche sous le type opposé et confirme (ou l'humain tranche).
                if (meta != null) return Status.NeedsReview;
            }

            if (meta == null) return trace.SawCandidates ? Status.NeedsReview : Status.NotFound;
            return Status.OrphanResolved;
        }

        // ------------------------------------------------------------------
        //  Pipeline de résolution d'orphelin : S0 (Emby natif, option) →
        //  S1 (TMDB multilingue) → S2 (LLM) → S3 (SearXNG). Applique le
        //  candidat retenu (ou pose needs-review). Retourne null si non résolu.
        // ------------------------------------------------------------------

        // ------------------------------------------------------------------
        //  Langue de la fiche S1/S2/S3 (détail dans le <summary> ci-dessous).
        // ------------------------------------------------------------------

        /// <summary>
        /// Résout la langue TMDB de la fiche pour un item :
        /// <list type="number">
        /// <item><b>Champ <see cref="PluginConfiguration.TmdbLangPriority"/> non vide : CONTENU
        /// d'abord</b> — langue du synopsis EPG (<paramref name="truth"/> figée, titre en repli)
        /// via <see cref="I18n.DetectContentLangKey"/> ; indéterminé → fail-open sur la langue
        /// préférée de la bibliothèque. La FAMILLE résolue met ses rangs en tête de la liste (la
        /// fiche revient dans la langue du contenu) et le SYNOPSIS est choisi dans la table
        /// <c>translations</c> par la liste priorisée (fr-CA avant fr-FR : sans elle fr-CA est
        /// inatteignable, <see cref="I18n.ToTmdbLang"/> ne connaît que fr-FR et TMDB ne fait aucun
        /// repli croisé). <paramref name="prio"/> reçoit la liste ordonnée (null si le champ est
        /// vide) ; elle alimente la cascade de recherche ET la sélection du synopsis. Aucune
        /// famille résolue → liste telle que tapée, langue = comportement historique
        /// (usager).</item>
        /// <item><b>Champ vide : comportement historique</b> : langue
        /// préférée de la bibliothèque de l'item
        /// (<c>PreferredMetadataLanguage</c> — l'usager la règle dans Emby,
        /// le plugin la suit, jamais codée en dur ; réflexion gardée :
        /// <see cref="ILibraryManager"/> est compilé contre 4.10 —
        /// <c>GetLibraryOptions(BaseItem)</c> peut manquer sur un serveur
        /// plus vieux), puis langue du programme (synopsis EPG d'abord,
        /// titre en repli). Null si les deux échouent.</item>
        /// </list>
        /// </summary>
        private string ResolveItemMetaTmdb(BaseItem item, EpgTruth truth, PluginConfiguration cfg,
            out IReadOnlyList<string> prio)
        {
            // Liste de priorité des langues du synopsis ; vide (défaut) = comportement historique.
            prio = null;
            var prioList = ParsePrioWithLog(cfg);
            if (prioList.Count > 0)
            {
                // Contenu d'abord : un enregistrement ANGLAIS reçoit une fiche anglaise même sur
                // une bibliothèque fr.
                string fam = I18n.DetectContentLangKey(
                    ResolveEpgTitle(item, truth) ?? item?.Name, truth?.Overview ?? item?.Overview);
                // Détection indéterminée → fail-open : famille de la langue de bibliothèque ; la
                // liste tranche la variante.
                if (fam == null) fam = TryLibraryLangKey(item);
                if (fam != null)
                {
                    var (ordered, head) = OrderTmdbPrioByFamily(prioList, fam);
                    prio = ordered;
                    return head;
                }
                // Aucune famille résolue : liste telle que tapée (l'oracle
                // translations reste actif dessus), langue = historique.
                prio = prioList;
                return null;
            }

            // --- Comportement historique (champ vide) : inchangé ---
            // 1) Config Emby : langue préférée de la bibliothèque de l'item.
            var libKey = TryLibraryLangKey(item);
            if (!string.IsNullOrEmpty(libKey))
                return I18n.ToTmdbLang(libKey);

            // 2) Langue du programme (synopsis du guide, titre en repli).
            var det = I18n.DetectContentLangKey(ResolveEpgTitle(item, truth) ?? item?.Name, truth?.Overview ?? item?.Overview);
            return det == null ? null : I18n.ToTmdbLang(det);
        }

        /// <summary>
        /// Ordonne la liste de priorité TMDB pour la famille de langue
        /// résolue d'un item : les rangs de la FAMILLE en tête
        /// (ordre de la liste conservé — fr-CA avant fr-FR), le reste
        /// ensuite. Famille absente de la liste → le code canonique de la
        /// famille en tête (le titre du contenu garde sa langue), liste
        /// ensuite. Retourne (liste ordonnée, langue de tête).
        /// </summary>
        internal static (List<string> ordered, string head) OrderTmdbPrioByFamily(IReadOnlyList<string> prio, string fam)
        {
            var headList = new List<string>();
            var tail = new List<string>();
            foreach (var l in prio)
            {
                if (string.Equals(I18n.TmdbLangFamily(l), fam, StringComparison.OrdinalIgnoreCase)) headList.Add(l);
                else tail.Add(l);
            }
            if (headList.Count > 0)
                return (headList.Concat(tail).ToList(), headList[0]);
            string canonical = I18n.ToTmdbLang(fam);
            return (new List<string> { canonical }.Concat(prio).ToList(), canonical);
        }

        /// <summary>Mémo anti-spam : la liste brute du dernier avertissement
        /// de parsing (une config fautive est dénoncée UNE fois, pas à chaque
        /// item d'une passe — course bénigne, pire cas un doublon de
        /// ligne).</summary>
        private static volatile string s_lastPrioWarnRaw;

        /// <summary>
        /// Parse <see cref="PluginConfiguration.TmdbLangPriority"/> avec
        /// journal des entrées invalides (une fois par valeur de champ —
        /// mémo ci-dessus). Liste vide si le champ est vide.
        /// </summary>
        private List<string> ParsePrioWithLog(PluginConfiguration cfg)
        {
            string raw = cfg?.TmdbLangPriority;
            var parsed = I18n.ParseTmdbLangPriority(raw, out var rejected);
            if (rejected.Length > 0 && s_lastPrioWarnRaw != raw)
            {
                s_lastPrioWarnRaw = raw;
                _logger?.Warn("[LLM_AI] Priorité TMDB : entrée(s) non valide(s) ignorée(s) : {0} — codes attendus : xx ou xx-XX (ex. fr-CA, fr, en-US, bg).",
                    string.Join(", ", rejected));
            }
            return parsed;
        }

        /// <summary>Langue préférée de la bibliothèque de l'item
        /// (<c>PreferredMetadataLanguage</c>) en clé 2 lettres, null si
        /// absente ou illisible. Réflexion gardée : <see cref="ILibraryManager"/>
        /// est compilé contre 4.10 — <c>GetLibraryOptions(BaseItem)</c> peut
        /// manquer sur un serveur plus vieux (un appel compilé lèverait
        /// MissingMethodException au JIT et tuerait toute la chaîne, le
        /// repli ne courrait jamais).</summary>
        private string TryLibraryLangKey(BaseItem item)
        {
            if (item == null || _library == null) return null;
            try
            {
                var m = _library.GetType().GetMethod("GetLibraryOptions", new[] { typeof(BaseItem) });
                var opts = m?.Invoke(_library, new object[] { item });
                var lang = opts?.GetType().GetProperty("PreferredMetadataLanguage")?.GetValue(opts) as string;
                return string.IsNullOrWhiteSpace(lang) ? null : I18n.LocaleToKey(lang);
            }
            catch (Exception ex)
            {
                _logger?.Info("[LLM_AI] OrphanIdentify : langue préférée de la bibliothèque illisible ({0}) — repli sur la langue du contenu.", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Cascade de langues de recherche S1 : la langue du contenu (quand elle
        /// est déterminée) en TÊTE — la fiche revient dans la langue du contenu et
        /// son titre devient comparable au titre du guide pour la porte
        /// lexicale. Indéterminé → ordre historique {en-US, fr-FR, usager}
        /// strictement inchangé. Priorité usager non vide : la liste
        /// ordonnée par famille REMPLACE la construction ad-hoc (fr-CA, fr-FR,
        /// en-US au lieu de {fr-FR, en-US} — la famille doit rester contiguë en
        /// tête) ; la langue usager historique reste un ultime rang de
        /// RECHERCHE (jamais de sélection du synopsis : le dernier rang de la
        /// LISTE est le repli).
        /// </summary>
        private static string[] BuildLangCascade(string metaLang, string userTmdb, IReadOnlyList<string> prio = null)
        {
            if (prio != null && prio.Count > 0)
                return prio.Concat(new[] { userTmdb }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return (metaLang == null
                ? new[] { "en-US", "fr-FR", userTmdb }
                : new[] { metaLang, string.Equals(metaLang, "en-US", StringComparison.OrdinalIgnoreCase) ? "fr-FR" : "en-US", userTmdb })
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>Bande de la porte de durée FILM (v2.23.2), en ratio durée fichier / durée fiche
        /// TMDB. Large À DESSEIN : la durée d'un enregistrement varie (pubs, version télé coupée,
        /// marges DVR) ; seuls les écarts grossiers — une autre œuvre — sont rejetés.</summary>
        internal const double MovieRuntimeMinRatio = 0.70;
        internal const double MovieRuntimeMaxRatio = 1.60;

        /// <summary>Porte de durée, FILMS seulement (jamais les séries : la durée
        /// d'un dossier série ne dit rien d'un épisode). Retourne true si la
        /// fiche doit être rejetée. Fail-open : durée du fichier ou de la fiche
        /// inconnue (≤ 0) → pas de rejet. Le rejet bascule le verdict accepté
        /// déjà consigné en trace (chat) et est toujours loggé.</summary>
        private bool RuntimeRejects(BaseItem item, TmdbMeta meta, string kind, bool isSeries,
            string stage, string epgTitle, PipelineTrace trace)
        {
            if (item == null || meta == null || isSeries) return false;
            if (!string.Equals(kind, "movie", StringComparison.OrdinalIgnoreCase)) return false;
            if (!item.RunTimeTicks.HasValue || item.RunTimeTicks.Value <= 0) return false;
            if (!meta.Runtime.HasValue || meta.Runtime.Value <= 0) return false;
            double fileMin = item.RunTimeTicks.Value / (double)TimeSpan.TicksPerMinute;
            double ficheMin = meta.Runtime.Value;
            double ratio = fileMin / ficheMin;
            if (ratio >= MovieRuntimeMinRatio && ratio <= MovieRuntimeMaxRatio) return false;

            string reason = "durée incompatible : fichier " + Math.Round(fileMin).ToString(CultureInfo.InvariantCulture)
                + " min vs fiche " + meta.Runtime.Value.ToString(CultureInfo.InvariantCulture)
                + " min (ratio " + ratio.ToString("0.00", CultureInfo.InvariantCulture)
                + ", bande admise " + MovieRuntimeMinRatio.ToString("0.00", CultureInfo.InvariantCulture)
                + "–" + MovieRuntimeMaxRatio.ToString("0.00", CultureInfo.InvariantCulture) + ")";
            _logger?.Info(
                "[LLM_AI] OrphanIdentify : « {0} » — candidat « {1} » ({2}, {3}) rejeté : {4}.",
                epgTitle, meta.Title,
                meta.Year.HasValue ? meta.Year.Value.ToString(CultureInfo.InvariantCulture) : "?",
                stage, reason);
            if (trace != null)
            {
                trace.SawCandidates = true; // candidat vu et rejeté = needs-review, pas introuvable
                bool flipped = false;
                foreach (var cv in trace.Candidates)
                    if (cv.Accepted && cv.Meta != null && cv.Meta.TmdbId == meta.TmdbId)
                    {
                        cv.Accepted = false;
                        cv.Reason = reason;
                        flipped = true;
                    }
                if (!flipped)
                    trace.RecordCandidate(stage, meta, "faible", stage + " — durée", null, false, reason, false);
            }
            return true;
        }

        private async Task<TmdbMeta> ResolvePipelineAsync(BaseItem item, EpgTruth truth,
            PluginConfiguration cfg, string kind, bool isSeries, bool allowS0,
            string[] langs, string userTmdb, bool dry, PipelineTrace trace, CancellationToken ct,
            bool crossKind = false, EpgTruth deferredTruth = null, IReadOnlyList<string> overviewChain = null)
        {
            // t="" = programme creux au RecordingEnded (état fréquent) : `??` ne rattrape pas la
            // chaîne vide, et un titre vide empoisonne la chaîne (requête web sur l'année seule,
            // bras « année corroborante »). Tolérant aux espaces, comme la tête de l'audit — repli
            // item.Name.
            string epgTitle = ResolveEpgTitle(item, truth, deferredTruth);
            // Année = ProductionYear UNIQUEMENT (date de DIFFUSION pour un DVR,
            // pas PremiereDate/DateCreated). La vérité EPG peut corriger ; si
            // les deux sont absentes, repli sur l'année (xxxx) du chemin selon
            // la convention Emby (jamais d'écrasement).
            int? year = truth?.Year ?? item.ProductionYear;
            if (!year.HasValue)
            {
                int? pathYear = ExtractEmbyPathYear(item?.Path, isSeries);
                if (pathYear.HasValue)
                {
                    year = pathYear;
                    _logger?.Info("[LLM_AI] OrphanIdentify : « {0} » — année ({1}) déduite du chemin (absente de l'item).",
                        epgTitle, pathYear.Value.ToString(CultureInfo.InvariantCulture));
                }
            }
            string overview = truth?.Overview ?? item.Overview;
            string cleanTitle = TmdbLookupTool.CleanEpgTitle(epgTitle);
            if (string.IsNullOrWhiteSpace(cleanTitle)) cleanTitle = epgTitle;

            // Année aberrante (imports gracenote : ProductionYear = 1…) : elle ferait partir
            // primary_release_year=1 (zéro résultat) et la garde d'année rejetterait TOUT. Traitée
            // comme année inconnue : recherche SANS filtre d'année, porte sans garde d'année (le
            // titre + le juge décident).
            bool bogusYear = year.HasValue && year.Value < 1900;
            if (bogusYear)
            {
                _logger?.Info("[LLM_AI] OrphanIdentify : « {0} » — année aberrante ({1}) traitée comme inconnue.",
                    epgTitle, year.Value);
                year = null;
            }

            // Date ISO ou horodaté underscore en fin de titre = marqueur
            // d'échec d'identification d'Emby (passe 04 h : truth nulle,
            // l'année vient de l'item). C'est une date de diffusion : l'année
            // qu'elle induit est « soft » — on ne filtre pas la recherche TMDB
            // dessus et la porte ne la rejette pas ; l'acceptation repose alors
            // sur le titre lexical + le juge synopsis.
            bool yearSoft = bogusYear
                || (truth == null && TmdbLookupTool.HasEmbyDateMarker(epgTitle));
            int? searchYear = yearSoft ? (int?)null : year;
            int? gateYear = yearSoft ? (int?)null : year;
            if (yearSoft)
                _logger?.Info("[LLM_AI] OrphanIdentify : « {0} » — date Emby en fin de titre : année {1} indicative (soft), titre nettoyé pour la recherche.",
                    epgTitle, year.HasValue ? year.Value.ToString(CultureInfo.InvariantCulture) : "?");

            // Ancre de prévention du retry 04 h (v2.20.17) : quand le store porte une entrée
            // vivante avec le titre-SÉRIE figé du guide (sn), l'acceptation d'une fiche exige la
            // CLÉ EXACTE de ce titre + une année compatible avec l'ancre ; contenance lexicale et
            // année d'item ne sont que des indices de recherche (l'année d'item peut être le résidu
            // de la fiche fautive posée à l'import). La passe série 04 h reste SANS vérité injectée
            // (v2.16.3 : année EPG d'un épisode = année de SAISON). Inactif si truth != null
            // (chemin watcher) ou sans entrée/sn.
            string retryAnchorTitle = (truth == null && !string.IsNullOrWhiteSpace(deferredTruth?.SeriesName))
                ? deferredTruth.SeriesName
                : null;
            int? retryAnchorYear = retryAnchorTitle != null ? deferredTruth.Year : null;

            TmdbMeta meta = null;
            string stage = null;
            if (trace != null) trace.CrossKind = crossKind;
            var webCtx = new WebEvidenceContext
            {
                CleanTitle = cleanTitle,
                GateYear = gateYear,
                Kind = kind,
                UserTmdb = userTmdb,
                OverviewChain = overviewChain
            };

            // SB : ancrage bibliothèque (dossier frère) — AVANT toute banque
            // externe : la curation de l'usager prime sur les catalogues.
            try
            {
                meta = await ResolveViaSiblingAsync(item, cfg, kind, epgTitle, cleanTitle,
                    overview, gateYear, userTmdb, trace, ct, overviewChain).ConfigureAwait(false);
                if (meta != null) stage = "SB";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger?.Info("[LLM_AI] OrphanIdentify : SB « {0} » échoué ({1}).", epgTitle, ex.Message); }

            if (meta != null && RuntimeRejects(item, meta, kind, isSeries, "SB", epgTitle, trace))
            {
                meta = null;
                stage = null;
            }

            // S0 : recherche native Emby (moteur du dialogue « Identifier »).
            if (allowS0 && (searchYear.HasValue || yearSoft))
            {
                try
                {
                    meta = await ResolveViaEmbyAsync(cleanTitle, kind, isSeries, searchYear,
                        epgTitle, overview, userTmdb, gateYear, item, trace, ct, overviewChain).ConfigureAwait(false);
                    if (meta != null) stage = "S0";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger?.Info("[LLM_AI] OrphanIdentify : S0 « {0} » échoué ({1}).", epgTitle, ex.Message); }
            }

            if (meta != null && RuntimeRejects(item, meta, kind, isSeries, "S0", epgTitle, trace))
            {
                meta = null;
                stage = null;
            }

            // S1 : recherche multilingue sur le titre du guide. Année fiable (ou soft) : garde
            // lexicale + garde d'année (homonymes d'époques différentes rejetés). SANS aucune année
            // : seule l'ÉGALITÉ EXACTE du titre du guide avec la fiche est une preuve (un homonyme
            // dont le titre diffère ne passe pas) ; une garde plus stricte envoyait l'item en S2/S3
            // où le LLM hallucine.
            bool exactOnlyS1 = !searchYear.HasValue && !yearSoft;
            bool exactKeyS1 = isSeries && !string.IsNullOrWhiteSpace(truth?.SeriesName);
            if (meta == null && (searchYear.HasValue || yearSoft || exactOnlyS1))
            {
                try
                {
                    var s1 = await _tmdb.LookupMetaMultiLangAsync(cleanTitle, kind, searchYear, langs, ct, overviewChain).ConfigureAwait(false);
                    // Cascade annuelle : l'année de référence est une date de diffusion, parfois
                    // éloignée de l'année de sortie — si primary_release_year écarte tous les
                    // résultats, rejouer SANS filtre (PickFirstResult privilégie déjà la bonne
                    // année).
                    if (s1 == null && searchYear.HasValue)
                        s1 = await _tmdb.LookupMetaMultiLangAsync(cleanTitle, kind, null, langs, ct, overviewChain).ConfigureAwait(false);
                    if (s1 != null) trace.SawCandidates = true; // une banque liste le titre
                    bool s1Ok = exactOnlyS1
                        ? NormalizeTitle(cleanTitle).Length > 0
                          && NormalizeTitle(cleanTitle) == NormalizeTitle(s1?.Title)
                        : exactKeyS1
                            ? RetryAnchorAccepts(truth.SeriesName, truth.Year, s1?.Title, s1?.Year)
                            : TitleMatches(cleanTitle, s1?.Title, gateYear, s1?.Year);
                    if (s1Ok)
                    {
                        // Garde « fiche déjà portée » : S1 n'appelle pas la porte commune, la garde
                        // y est appliquée directement (porteur au titre divergent → rejet par
                        // arbitrage juge / prudence).
                        if (await FicheConflictGateAsync(cfg, s1, item, epgTitle, gateYear,
                                overview, false, ct, trace: trace, stageLabel: "S1",
                                proofLevel: "forte",
                                proofSource: exactOnlyS1
                                    ? "S1 — titre exact (sans année)"
                                    : (exactKeyS1 ? "S1 — titre-SÉRIE exact + année compatible" : "S1 — recherche TMDB titre+année"),
                                proofEvidence: "fiche TMDB « " + s1.Title + " » (" + (s1.Year?.ToString(CultureInfo.InvariantCulture) ?? "?") + ")",
                                crossKind: crossKind).ConfigureAwait(false))
                        {
                            meta = s1;
                            stage = "S1";
                        }
                    }
                    else if (s1 != null)
                    {
                        // Rejet loggé : c'était le seul chemin de la chaîne sans trace. La raison
                        // distingue la porte sans année fiable (égalité exacte du titre seule) de
                        // la garde titre/année.
                        string s1Reason = exactOnlyS1
                            ? "sans année fiable, seule l'égalité exacte du titre du guide est une preuve"
                            : "garde titre/année non satisfaite";
                        trace.RecordCandidate("S1", s1, "faible", "S1 — recherche TMDB", null, false, s1Reason, crossKind);
                        _logger?.Info(
                            "[LLM_AI] OrphanIdentify : S1 « {0} » — candidat « {1} » ({2}) rejeté : {3}.",
                            epgTitle, s1.Title,
                            s1.Year.HasValue ? s1.Year.Value.ToString(CultureInfo.InvariantCulture) : "?",
                            s1Reason);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger?.Info("[LLM_AI] OrphanIdentify : S1 « {0} » échoué ({1}).", epgTitle, ex.Message); }
            }

            if (meta != null && RuntimeRejects(item, meta, kind, isSeries, "S1", epgTitle, trace))
            {
                meta = null;
                stage = null;
            }

            // S2 : proposition LLM validée par TMDB (juge). L'année reste
            // envoyée au LLM comme contexte indicatif ; la porte reçoit
            // gateYear (null si soft).
            if (meta == null)
            {
                try
                {
                    meta = await ResolveViaLlmAsync(cfg, epgTitle, cleanTitle, kind, year, gateYear,
                        overview, truth?.Channel, langs, userTmdb, item, trace, ct, overviewChain, webCtx).ConfigureAwait(false);
                    if (meta != null) stage = "S2";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger?.Info("[LLM_AI] OrphanIdentify : S2 « {0} » échoué ({1}).", epgTitle, ex.Message); }
            }

            if (meta != null && RuntimeRejects(item, meta, kind, isSeries, "S2", epgTitle, trace))
            {
                meta = null;
                stage = null;
            }

            // S3 : recherche web (SearXNG) → ids IMDb/TMDB → validation TMDB + juge.
            if (meta == null && cfg.OrphanSearXngEnabled)
            {
                try
                {
                    meta = await ResolveViaSearXngAsync(cfg, epgTitle, cleanTitle, kind, gateYear,
                        yearSoft, overview, userTmdb, item, trace, ct, overviewChain, webCtx).ConfigureAwait(false);
                    if (meta != null) stage = "S3";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger?.Info("[LLM_AI] OrphanIdentify : S3 « {0} » échoué ({1}).", epgTitle, ex.Message); }
            }

            if (meta != null && RuntimeRejects(item, meta, kind, isSeries, "S3", epgTitle, trace))
            {
                meta = null;
                stage = null;
            }

            // Point de contrôle du retry ancré : un rejet laisse l'item en needs-review (visible,
            // récupérable) et laisse le repli film de ResolveItemAsync tourner sur la vérité
            // différée — jamais une identification terminale erronée silencieuse. S1 accepte sans
            // juge : c'est ICI que la contrainte s'applique, couvrant SB/S0/S1/S2/S3.
            if (meta != null && !RetryAnchorAccepts(retryAnchorTitle, retryAnchorYear, meta.Title, meta.Year))
            {
                string anchorReason = "ancre store vivante sur « " + retryAnchorTitle + " » exige la clé exacte + année compatible";
                _logger?.Info(
                    "[LLM_AI] OrphanIdentify : « {0} » — candidat « {1} » ({2}, {3}) rejeté : une entrée store vivante ancre le titre-SÉRIE du guide sur « {4} » — clé exacte + année compatible avec l'ancre exigées (contenance et année d'item = indices de recherche seulement).",
                    epgTitle, meta.Title,
                    meta.Year.HasValue ? meta.Year.Value.ToString(CultureInfo.InvariantCulture) : "?",
                    stage, retryAnchorTitle);
                if (trace.CollectMode)
                {
                    foreach (var cv in trace.Candidates)
                        if (cv.Accepted && cv.Meta != null && cv.Meta.TmdbId == meta.TmdbId)
                        {
                            cv.Accepted = false;
                            cv.Reason = anchorReason;
                        }
                }
                trace.SawCandidates = true; // candidat vu et rejeté = needs-review, pas introuvable
                meta = null;
                stage = null;
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

            if (trace.CollectMode)
            {
                trace.Winner = meta;
                trace.WinningStage = stage;
                trace.CrossKind = crossKind;
                var winVerdict = trace.Candidates.LastOrDefault(c => c.Accepted && c.Meta != null && c.Meta.TmdbId == meta.TmdbId);
                trace.WinningProofLevel = winVerdict?.ProofLevel ?? "forte";
                trace.WinningProofSource = winVerdict?.ProofSource ?? stage;
                trace.WinningProofEvidence = winVerdict?.ProofEvidence;
            }

            if (dry)
            {
                _logger?.Info("[LLM_AI] OrphanIdentify (DRY-RUN) : « {0} » → tmdb={1} imdb={2} tvdb={3} ({4}) — aucune écriture.",
                    epgTitle, meta.TmdbId, meta.ImdbId ?? "—", meta.TvdbId ?? "—", stage);
                return meta;
            }

            await ApplyAsync(item, meta, kind, isSeries, ct, crossKind).ConfigureAwait(false);
            _logger?.Info("[LLM_AI] OrphanIdentify : « {0} » → tmdb={1} imdb={2} tvdb={3} ({4}) ; Name verrouillé, tag {5}.",
                epgTitle, meta.TmdbId, meta.ImdbId ?? "—", meta.TvdbId ?? "—", stage,
                crossKind ? OrphanIdentifyTask.TagNeedsReview : OrphanIdentifyTask.TagIdentified);
            return meta;
        }

        // ------------------------------------------------------------------
        //  Audit d'une identification Emby (chemin RecordingWatcher).
        // ------------------------------------------------------------------

        private async Task<Status> AuditEmbyIdsAsync(BaseItem item, EpgTruth truth,
            PluginConfiguration cfg, string kind, bool isSeries, bool dry,
            string userTmdb, string metaLang, CancellationToken ct,
            IReadOnlyList<string> overviewChain = null)
        {
            string epgTitle = ResolveEpgTitle(item, truth);
            string epgSynopsis = truth.Overview;

            if (string.IsNullOrWhiteSpace(epgSynopsis))
            {
                // Toujours loggué (v2.18.0) : ce motif ne court que depuis le RecordingWatcher ; un
                // « reporté » silencieux se lisait comme une validation.
                _logger?.Info("[LLM_AI] Recording : « {0} » identifié par Emby (tmdb={1}) — pas de synopsis EPG à comparer, audit reporté.",
                    epgTitle, item.GetProviderId("tmdb") ?? "—");
                return Status.Skipped;
            }

            // Relire la fiche TMDB de l'id posé par Emby (tmdb > imdb > tvdb).
            TmdbMeta meta = null;
            bool crossRead = false; // fiche lue sous le type opposé à l'item
            string tmdbRaw = item.GetProviderId("tmdb");
            int.TryParse(tmdbRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId);
            if (tmdbId > 0)
                meta = await _tmdb.LookupMetaByIdAsync(tmdbId, kind, userTmdb, ct, overviewChain).ConfigureAwait(false);
            if (meta == null && !string.IsNullOrWhiteSpace(item.GetProviderId("imdb")))
                meta = await _tmdb.FindByExternalIdAsync(item.GetProviderId("imdb").Trim(), "imdb_id", kind, userTmdb, ct, overviewChain).ConfigureAwait(false);
            if (meta == null && isSeries && !string.IsNullOrWhiteSpace(item.GetProviderId("tvdb")))
                meta = await _tmdb.FindByExternalIdAsync(item.GetProviderId("tvdb").Trim(), "tvdb_id", kind, userTmdb, ct, overviewChain).ConfigureAwait(false);

            // Fiche illisible sous le type de l'item → relire sous le type
            // opposé (fiche croisée : id de film écrit par Emby sur un item
            // série, ou fiche posée par le repli de type). tvdb n'a de sens
            // que dans l'espace série — pas de relecture croisée par tvdb.
            if (meta == null)
            {
                string otherKind = isSeries ? "movie" : "series";
                if (tmdbId > 0)
                    meta = await _tmdb.LookupMetaByIdAsync(tmdbId, otherKind, userTmdb, ct, overviewChain).ConfigureAwait(false);
                if (meta == null && !string.IsNullOrWhiteSpace(item.GetProviderId("imdb")))
                    meta = await _tmdb.FindByExternalIdAsync(item.GetProviderId("imdb").Trim(), "imdb_id", otherKind, userTmdb, ct, overviewChain).ConfigureAwait(false);
                if (meta != null)
                {
                    crossRead = true;
                    _logger?.Info("[LLM_AI] Recording : « {0} » — fiche illisible en type « {1} », relue en « {2} » (fiche croisée).",
                        epgTitle, kind, otherKind);
                }
            }

            if (meta == null)
            {
                _logger?.Info("[LLM_AI] Recording : « {0} » — fiche TMDB illisible pour l'id d'Emby, audit reporté.", epgTitle);
                return Status.Skipped;
            }

            // Repli synopsis en-US (parité avec TmdbLookupTool) : une fiche TMDB muette en fr-FR
            // porte souvent un overview en en-US ; sans repli, un faux match Emby resterait reporté
            // au lieu d'être réfuté par le juge. Priorité usager active = table translations DÉJÀ
            // scannée : re-fetcher en en-US réintroduirait un synopsis anglais sur un contenu
            // français — le repli ne court que sur le comportement historique.
            if (overviewChain == null
                && meta.TmdbId > 0 && string.IsNullOrWhiteSpace(meta.Overview)
                && !string.Equals(userTmdb, "en-US", StringComparison.OrdinalIgnoreCase))
            {
                string fetchKind = crossRead ? (isSeries ? "movie" : "series") : kind;
                var enMeta = await _tmdb.LookupMetaByIdAsync(meta.TmdbId, fetchKind, "en-US", ct).ConfigureAwait(false);
                if (enMeta != null && !string.IsNullOrWhiteSpace(enMeta.Overview))
                    meta.Overview = enMeta.Overview;
            }

            // v2.19.2 — classement AVANT le juge (séries uniquement, fiche du type de l'item : une
            // fiche croisée garde l'arbitrage complet). Le juge compare le synopsis d'un ÉPISODE à
            // l'overview d'une fiche de SÉRIE : cette différence de granularité donnait des faux
            // négatifs. Trois preuves déterministes, par ordre de force : (1) corroboration de la
            // série-sœur de l'usager (même clé de titre exacte, même fiche, ≥1 épisode requis
            // depuis v2.19.3) ; (2) garde lexicale sur le titre-SÉRIE du guide ; (3) le juge pour
            // les divergences. Risque résiduel assumé (doctrine S1) : l'homonyme parfait (même
            // titre, même année, autre œuvre) passe sans juge.
            //
            // Ancre série = titre-SÉRIE DU GUIDE uniquement. Vide (films, fiches croisées, entrées
            // figées avant v2.19.2) → juge, comportement historique.
            string seriesAnchor = isSeries && !crossRead
                && !string.IsNullOrWhiteSpace(truth.SeriesName) ? truth.SeriesName : null;

            if (seriesAnchor != null)
            {
                var (sister, sisterFiche) = await TryFindSeriesFicheAsync(
                    item, seriesAnchor, kind, userTmdb, ct).ConfigureAwait(false);
                if (sisterFiche != null)
                {
                    if (sisterFiche.TmdbId == meta.TmdbId)
                    {
                        // Garde d'année de l'ancrage série (Année EPG d'un
                        // épisode = année de SAISON : la fiche d'une série
                        // longue peut être bien antérieure — d'où
                        // AnchorSeriesYearCompatible, pas YearCompatible).
                        // Une année du guide ANTÉRIEURE à la fiche (un
                        // enregistrement qui précéderait sa série ?) reste
                        // inarbitrable ici → juge.
                        if (AnchorSeriesYearCompatible(truth.Year, meta.Year))
                        {
                            // Les tags de la sœur ne sont jamais une porte :
                            // un needs-review sur la série englobe ses
                            // épisodes (l'usager révisera de toute façon) —
                            // ils sont portés au journal pour le diagnostic.
                            string proof = string.Format(CultureInfo.InvariantCulture,
                                "corroboré par la série sœur « {0} » ({1}, tags [{2}])",
                                sister.Name, sister.Path, string.Join(",", sister.Tags ?? Array.Empty<string>()));
                            return await ApplyAuditedFicheAsync(item, meta, kind, isSeries,
                                epgTitle, dry, proof, ct).ConfigureAwait(false);
                        }
                        _logger?.Info(
                            "[LLM_AI] Recording : « {0} » — même fiche que la série sœur « {1} » ({2}) mais année du guide {3} antérieure à la fiche {4} (enregistrement qui précéderait sa série ?) → juge.",
                            epgTitle, sister.Name, sister.Path,
                            truth.Year.HasValue ? truth.Year.Value.ToString(CultureInfo.InvariantCulture) : "?",
                            meta.Year.HasValue ? meta.Year.Value.ToString(CultureInfo.InvariantCulture) : "?");
                    }
                    else
                    {
                        _logger?.Info(
                            "[LLM_AI] Recording : « {0} » — la série sœur « {1} » ({2}) porte tmdb={3} ≠ fiche d'Emby tmdb={4} (conflit de curation) → juge.",
                            epgTitle, sister.Name, sister.Path, sisterFiche.TmdbId, meta.TmdbId);
                    }
                }
            }

            // Garde lexicale : la fiche porte le titre-SÉRIE du GUIDE (clé
            // exacte, jamais la contenance) et une année compatible. Fiche
            // non-muette requise : une fiche sans synopsis ne donne aucune
            // matière à réfuter (elle garde l'échappement muet v2.18.0
            // ci-dessous, pas un tampon lexical).
            if (seriesAnchor != null
                && !string.IsNullOrWhiteSpace(meta.Overview)
                && SameTitleKey(seriesAnchor).Length > 0
                && SameTitleKey(seriesAnchor) == SameTitleKey(meta.Title)
                && AnchorSeriesYearCompatible(truth.Year, meta.Year))
            {
                string proof = string.Format(CultureInfo.InvariantCulture,
                    "corroboré lexicalement (titre-SÉRIE du guide « {0} » = titre de la fiche, année compatible)",
                    seriesAnchor);
                return await ApplyAuditedFicheAsync(item, meta, kind, isSeries,
                    epgTitle, dry, proof, ct).ConfigureAwait(false);
            }

            string judgeTitle = (isSeries && !string.IsNullOrWhiteSpace(seriesAnchor)
                && !string.IsNullOrWhiteSpace(truth.Title)
                && SameTitleKey(truth.Title) != SameTitleKey(seriesAnchor))
                ? seriesAnchor + " (" + truth.Title + ")"
                : epgTitle;
            var verdict = await _runner.JudgeSynopsisMatchAsync(cfg, judgeTitle, truth.Year, epgSynopsis,
                meta.Title, meta.Year, meta.Overview, ct).ConfigureAwait(false);

            if (!verdict.IsValid)
            {
                // v2.18.0 : deux « indisponibles » de nature différente. Une fiche MUETTE (overview
                // vide) ne donnera JAMAIS matière au juge — défaut STRUCTUREL, pas un échec
                // backend. Sans AUCUNE preuve corroborante (titre ≠ égalité exacte du guide ET
                // année incompatible), l'id posé par Emby n'est qu'une supposition non vérifiable →
                // même chemin que le mismatch : retour EPG + re-pipeline S1/S2/S3 (la passe 04 h
                // refera le repli film sur la vérité différée). Une fiche muette au titre égal ou à
                // l'année compatible reste « reportée » : on ne détruit pas une identification
                // plausible sans preuve (séries longues muettes correctement fichées par Emby).
                if (string.IsNullOrWhiteSpace(meta.Overview)
                    && !TitlesExactlyEqual(epgTitle, meta.Title)
                    && !TitlesExactlyEqual(seriesAnchor, meta.Title) // v2.19.2 : ancre série — une fiche muette au titre-SÉRIE du guide reste plausible (defer, jamais de churn)
                    && !YearCompatible(truth.Year, meta.Year))
                {
                    _logger?.Info(
                        "[LLM_AI] Recording : « {0} » — fiche « {1} » (tmdb={2}) sans synopsis, titre non égal, année {3} incompatible : identification d'Emby non corroborable → reversion EPG + re-identification.",
                        epgTitle, meta.Title, meta.TmdbId,
                        meta.Year.HasValue ? meta.Year.Value.ToString(CultureInfo.InvariantCulture) : "?");
                    if (dry)
                    {
                        _logger?.Info("[LLM_AI] Recording (DRY-RUN) : ids d'Emby rejetés pour « {0} » — aucune écriture (reversion + reprise S1/S2/S3 non exécutées).", epgTitle);
                        return Status.NeedsReview;
                    }
                    await RevertToEpgAsync(item, truth, epgTitle, ct).ConfigureAwait(false);
                    var dLangs = BuildLangCascade(metaLang, userTmdb, overviewChain);
                    // Même trace pré-ensemencée que le mismatch : une fiche
                    // TMDB réelle (muette) a été vue — l'échec du
                    // re-pipeline reste needs-review, jamais introuvable.
                    var dRetrace = new PipelineTrace { SawCandidates = true };
                    var dMeta = await ResolvePipelineAsync(item, truth, cfg, kind, isSeries,
                        false, dLangs, userTmdb, dry, dRetrace, ct, overviewChain: overviewChain).ConfigureAwait(false);
                    return dMeta != null ? Status.OrphanResolved : Status.NeedsReview;
                }

                // Juge indisponible (backend LLM, ou fiche muette plausible) :
                // ni confirmation ni réfutation — on ne détruit pas une
                // identification existante sur un échec backend.
                _logger?.Info("[LLM_AI] Recording : « {0} » — juge synopsis indisponible ({1}), audit reporté.",
                    epgTitle,
                    string.IsNullOrWhiteSpace(meta.Overview) ? "fiche sans synopsis" : "backend LLM");
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
                await ApplyAsync(item, meta, kind, isSeries, ct, crossKind: crossRead).ConfigureAwait(false);
                _logger?.Info("[LLM_AI] Recording : « {0} » = id d'Emby (tmdb={1}) validé par le juge — verrous posés, tag {2}{3}.",
                    epgTitle, meta.TmdbId,
                    crossRead ? OrphanIdentifyTask.TagNeedsReview : OrphanIdentifyTask.TagIdentified,
                    crossRead ? " (fiche croisée — à confirmer, type d'item conservé)" : "");
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
            var langs = BuildLangCascade(metaLang, userTmdb, overviewChain);
            // Trace pré-ensemencée : le juge vient de REJETER une fiche TMDB
            // réelle (celle de l'id d'Emby) — un candidat a été vu. L'échec du
            // re-pipeline reste donc needs-review, jamais introuvable.
            var retrace = new PipelineTrace { SawCandidates = true };
            var meta2 = await ResolvePipelineAsync(item, truth, cfg, kind, isSeries,
                false, langs, userTmdb, dry, retrace, ct, overviewChain: overviewChain).ConfigureAwait(false);
            return meta2 != null ? Status.OrphanResolved : Status.NeedsReview;
        }

        /// <summary>Applique une fiche d'Emby VALIDÉE sans juge (corroboration
        /// sœur ou garde lexicale, v2.19.2) : verrous + tag identifié + ligne
        /// de journal forte portant la preuve — jamais un defer silencieux.
        /// Miroir dry-run sans écriture. Ne lève jamais (annulation
        /// exceptée).</summary>
        private async Task<Status> ApplyAuditedFicheAsync(BaseItem item, TmdbMeta meta, string kind, bool isSeries,
            string epgTitle, bool dry, string proof, CancellationToken ct)
        {
            if (dry)
            {
                _logger?.Info("[LLM_AI] Recording (DRY-RUN) : « {0} » = id d'Emby (tmdb={1}) {2} — aucune écriture.",
                    epgTitle, meta.TmdbId, proof);
                return Status.OrphanResolved;
            }
            await ApplyAsync(item, meta, kind, isSeries, ct).ConfigureAwait(false);
            _logger?.Info("[LLM_AI] Recording : « {0} » = id d'Emby (tmdb={1}) {2} — validé sans juge, verrous posés, tag {3}.",
                epgTitle, meta.TmdbId, proof, OrphanIdentifyTask.TagIdentified);
            return Status.OrphanResolved;
        }

        /// <summary>Série-sœur de l'item dans la bibliothèque (corroboration
        /// d'audit v2.19.2, garde « vraie sœur » v2.19.3) : même clé de titre
        /// EXACTE (SameTitleKey — jamais la contenance), type Series
        /// uniquement (jamais un film), cartes .strm et l'item lui-même
        /// exclus, AU MOINS UN ÉPISODE requis (v2.19.3 : un jumeau de
        /// métadonnées sans vidéos n'est pas une curation, voir
        /// HasAnyEpisode), fiche lisible (cascade tmdb → imdb → tvdb dans le
        /// kind de l'item, miroir de SB). Première sœur lisible et pourvue
        /// d'épisodes ; (null, null) sinon. Best-effort : requête ou lecture
        /// échouée → null, l'audit poursuit par le juge.</summary>
        private async Task<(BaseItem sister, TmdbMeta fiche)> TryFindSeriesFicheAsync(
            BaseItem self, string seriesAnchor, string kind, string userTmdb, CancellationToken ct)
        {
            string key = SameTitleKey(seriesAnchor);
            if (key.Length == 0 || _library == null || string.IsNullOrWhiteSpace(seriesAnchor))
                return (null, null);

            // Requête miroir de SB : le nom CONTIENT le titre nettoyé (le
            // suffixe « (année) » de la sœur ne doit pas la cacher), puis
            // confirmation stricte en mémoire par la clé.
            string clean = TmdbLookupTool.CleanEpgTitle(seriesAnchor);
            if (string.IsNullOrWhiteSpace(clean)) clean = seriesAnchor;
            BaseItem[] siblings;
            try
            {
                siblings = _library.GetItemList(new InternalItemsQuery
                {
                    NameContains = clean,
                    IncludeItemTypes = new[] { "Series" },
                    Recursive = true,
                    Limit = 25,
                    EnableTotalRecordCount = false
                }) ?? Array.Empty<BaseItem>();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Info("[LLM_AI] Recording : quête de la série sœur « {0} » échouée ({1}) — arbitrage par le juge.",
                    seriesAnchor, ex.Message);
                return (null, null);
            }

            foreach (var sib in siblings)
            {
                ct.ThrowIfCancellationRequested();
                if (sib == null || self == null || sib.InternalId == self.InternalId) continue;
                if (IsStrmCard(sib)) continue;
                if (SameTitleKey(sib.Name) != key) continue; // confirmation stricte en mémoire
                // Sans fiche à offrir (ids provider), la sœur ne dit rien.
                if (!HasItemProviderId(sib, "tmdb") && !HasItemProviderId(sib, "imdb")
                    && !HasItemProviderId(sib, "tvdb")) continue;

                // Garde « vraie sœur » (v2.19.3) : un jumeau sans épisode n'est pas une curation de
                // l'usager — sa fiche est l'état DVR mirroré (NFO sans vidéos), une COPIE de la
                // fiche auditée, pas une preuve indépendante. Écartée avec trace ; une vraie sœur
                // plus loin dans la réponse qualifie seule.
                if (!HasAnyEpisode(sib, ct))
                {
                    _logger?.Info("[LLM_AI] Recording : série sœur candidate « {0} » ({1}) écartée — aucun épisode en bibliothèque (jumeau de métadonnées ?), pas une preuve de curation.",
                        sib.Name, sib.Path);
                    continue;
                }

                TmdbMeta fiche = null;
                int.TryParse(sib.GetProviderId("tmdb"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sibTmdb);
                if (sibTmdb > 0)
                    fiche = await _tmdb.LookupMetaByIdAsync(sibTmdb, kind, userTmdb, ct).ConfigureAwait(false);
                if (fiche == null && HasItemProviderId(sib, "imdb"))
                    fiche = await _tmdb.FindByExternalIdAsync(sib.GetProviderId("imdb").Trim(), "imdb_id",
                        kind, userTmdb, ct).ConfigureAwait(false);
                if (fiche == null && string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase)
                    && HasItemProviderId(sib, "tvdb"))
                    fiche = await _tmdb.FindByExternalIdAsync(sib.GetProviderId("tvdb").Trim(), "tvdb_id",
                        kind, userTmdb, ct).ConfigureAwait(false);
                if (fiche != null) return (sib, fiche);
            }
            return (null, null);
        }

        /// <summary>Une SÉRIE candidate porte-t-elle AU MOINS un épisode en bibliothèque ? Garde «
        /// vraie sœur » v2.19.3 : les dossiers jumeaux nés d'un miroir de métadonnées (NFO sans les
        /// vidéos) deviennent des items Série+Saison mais JAMAIS d'épisode — sans épisode, ce n'est
        /// pas une curation mais l'écho d'un enregistrement passé. Requête base
        /// (AncestorIds+Episode, miroir des compteurs LibReview), jamais de lecture disque. Échec
        /// de requête → false (fail-closed : l'incertitude retombe sur les couches lexicales/juge).
        /// NB : une fois les vidéos déposées, le jumeau redevient une sœur à part
        /// entière.</summary>
        private bool HasAnyEpisode(BaseItem series, CancellationToken ct)
        {
            if (series == null || _library == null) return false;
            try
            {
                var eps = _library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Episode" },
                    AncestorIds = new[] { series.InternalId },
                    Recursive = true,
                    EnableTotalRecordCount = false
                }) ?? Array.Empty<BaseItem>();
                return eps.Length > 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Info("[LLM_AI] : épisodes de la série « {0} » illisibles ({1}) — candidate non qualifiée (garde vraie sœur).",
                    series.Name, ex.Message);
                return false;
            }
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
            string kind, bool isSeries, bool dry, string userTmdb, bool verbose, CancellationToken ct,
            EpgTruth deferredTruth = null, IReadOnlyList<string> overviewChain = null)
        {
            string itemName = item.Name;
            string cleanName = TmdbLookupTool.CleanEpgTitle(itemName);
            if (string.IsNullOrWhiteSpace(cleanName)) cleanName = itemName;

            // Relire la fiche TMDB de l'id posé (tmdb > imdb > tvdb séries) —
            // même cascade que l'audit d'identification Emby.
            TmdbMeta meta = null;
            bool crossRead = false; // fiche lue sous le type opposé à l'item
            string tmdbRaw = item.GetProviderId("tmdb");
            int.TryParse(tmdbRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId);
            if (tmdbId > 0)
                meta = await _tmdb.LookupMetaByIdAsync(tmdbId, kind, userTmdb, ct, overviewChain).ConfigureAwait(false);
            if (meta == null && !string.IsNullOrWhiteSpace(item.GetProviderId("imdb")))
                meta = await _tmdb.FindByExternalIdAsync(item.GetProviderId("imdb").Trim(), "imdb_id", kind, userTmdb, ct, overviewChain).ConfigureAwait(false);
            if (meta == null && isSeries && !string.IsNullOrWhiteSpace(item.GetProviderId("tvdb")))
                meta = await _tmdb.FindByExternalIdAsync(item.GetProviderId("tvdb").Trim(), "tvdb_id", kind, userTmdb, ct, overviewChain).ConfigureAwait(false);

            // Fiche illisible sous le type de l'item → relire sous le type opposé (fiche croisée
            // posée par le repli de type du pipeline) : sans cette relecture croisée, l'audit
            // sauterait éternellement une fiche pourtant correcte. tvdb n'a de sens que dans
            // l'espace série.
            if (meta == null)
            {
                string otherKind = isSeries ? "movie" : "series";
                if (tmdbId > 0)
                    meta = await _tmdb.LookupMetaByIdAsync(tmdbId, otherKind, userTmdb, ct, overviewChain).ConfigureAwait(false);
                if (meta == null && !string.IsNullOrWhiteSpace(item.GetProviderId("imdb")))
                    meta = await _tmdb.FindByExternalIdAsync(item.GetProviderId("imdb").Trim(), "imdb_id", otherKind, userTmdb, ct, overviewChain).ConfigureAwait(false);
                if (meta != null)
                {
                    crossRead = true;
                    _logger?.Info("[LLM_AI] OrphanIdentify : audit taggé « {0} » — fiche illisible en type « {1} », relue en « {2} » (fiche croisée).",
                        itemName, kind, otherKind);
                }
            }

            if (meta == null)
            {
                _logger?.Info("[LLM_AI] OrphanIdentify : audit taggé « {0} » — fiche TMDB illisible pour l'id posé, item laissé tel quel.", itemName);
                return Status.Skipped;
            }

            if (AuditTaggedTitleAccepted(cleanName, deferredTruth?.SeriesName, deferredTruth?.Year,
                isSeries, crossRead, meta.Title, meta.Year))
            {
                if (dry)
                {
                    _logger?.Info("[LLM_AI] OrphanIdentify (DRY-RUN) : audit taggé « {0} » = « {1} » (tmdb={2}) → validation proposée — aucune écriture.",
                        itemName, meta.Title, meta.TmdbId);
                    return Status.OrphanResolved;
                }
                // Fiche croisée confirmée : le tag croisé (add-only) marque
                // l'item durablement — findable pour un éventuel replacement
                // du fichier dans la bibliothèque de son type.
                if (crossRead) AddTag(item, OrphanIdentifyTask.TagCrossKind);
                await ApplyAsync(item, meta, kind, isSeries, ct).ConfigureAwait(false);
                _logger?.Info("[LLM_AI] OrphanIdentify : audit taggé « {0} » = « {1} » (tmdb={2}) → validé, tag {3}.",
                    itemName, meta.Title, meta.TmdbId, OrphanIdentifyTask.TagIdentified);
                return Status.OrphanResolved;
            }

            // Mismatch : titre EPG ≠ fiche. Les ids sont CONSERVÉS — la garde lexicale est aveugle
            // aux titres traduits (« Erreur vitale » = « The Fatal Flaw ») et la reprise S1/S2/S3
            // échouerait de la même façon. On flague needs-review : décision humaine dans l'éditeur
            // Emby (retirer l'id si la fiche est fausse, puis re-tagger). Le not-found est migré :
            // l'item a des ids, il attend une confirmation.
            _logger?.Info("[LLM_AI] OrphanIdentify : audit taggé « {0} » ≠ fiche « {1} » (tmdb={2}) — titre EPG divergent (traduction ? homonyme ?).",
                itemName, meta.Title ?? "—", meta.TmdbId);
            if (dry)
            {
                _logger?.Info("[LLM_AI] OrphanIdentify (DRY-RUN) : « {0} » à flaguer {1} (ids conservés) — aucune écriture.",
                    itemName, OrphanIdentifyTask.TagNeedsReview);
                return Status.NeedsReview;
            }

            if (crossRead) AddTag(item, OrphanIdentifyTask.TagCrossKind); // fiche croisée présente, douteuse — findable
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
            // Effacer l'année posée par la fiche erronée d'Emby pour qu'elle ne reste pas sur
            // l'item après expiration de la rétention du store. Série : truth.Year est l'année de
            // diffusion de l'épisode (pas de la série) → null ; film : restaurer l'année du guide
            // si valide.
            bool isSeriesItem = item is Series
                || (item != null && item.GetType().Name.IndexOf("Series", StringComparison.OrdinalIgnoreCase) >= 0);
            item.ProductionYear = isSeriesItem
                ? (int?)null
                : (truth.Year.HasValue && truth.Year.Value >= 1900 ? truth.Year : (int?)null);

            string guideTitle = (isSeriesItem && !string.IsNullOrWhiteSpace(truth.SeriesName))
                ? truth.SeriesName
                : (!string.IsNullOrWhiteSpace(truth.Title) ? truth.Title : null);
            if (!string.IsNullOrWhiteSpace(guideTitle) && SameTitleKey(item.Name) != SameTitleKey(guideTitle))
            {
                _logger?.Info("[LLM_AI] Recording : nom de l'item « {0} » restauré vers le titre du guide « {1} » avant verrouillage.",
                    item.Name, guideTitle);
                item.Name = guideTitle;
            }

            AddLock(item, MetadataFields.Name);
            AddTag(item, OrphanIdentifyTask.TagNeedsReview);
            item.UpdateToRepository(ItemUpdateType.MetadataEdit);
            _logger?.Info("[LLM_AI] Recording : « {0} » retourné à l'état EPG (ids retirés, tag {1}) — reprise S1/S2/S3.",
                epgTitle, OrphanIdentifyTask.TagNeedsReview);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        // ------------------------------------------------------------------
        //  SB — ancrage bibliothèque (dossier frère). Emby type l'import DVR d'après le titre EPG :
        //  chaque variation de titre (suffixe « (année) », traduction, renommage du guide) enfante
        //  un NOUVEAU dossier, souvent le double d'une œuvre déjà identifiée, possiblement dans une
        //  autre bibliothèque. Sans ancrage, S1/S2/S3 ré-identifient depuis les banques et peuvent
        //  diverger de la fiche du dossier existant. Un dossier frère homonyme (± « (année) »,
        //  parenthèses et bruit EPG retirés) portant une fiche la propose comme candidat n° 0 — la
        //  porte commune décide (titre/année/juge) ; rejet → la chaîne S0→S1→S2→S3 suit son cours.
        //  Fiche du frère illisible dans le kind de la passe (fiche croisée) : pas d'ancrage, le
        //  repli de type s'en charge. Effet systémique : les fiches CONVERGENT (filtre natif «
        //  Doublons » d'Emby fiable) et le signalement « même œuvre en deux dossiers » tombe au log
        //  via la garde « fiche déjà portée » (FicheConflictGateAsync).
        // ------------------------------------------------------------------

        private async Task<TmdbMeta> ResolveViaSiblingAsync(BaseItem item, PluginConfiguration cfg,
            string kind, string epgTitle, string cleanTitle, string overview, int? gateYear,
            string userTmdb, PipelineTrace trace, CancellationToken ct,
            IReadOnlyList<string> overviewChain = null)
        {
            string key = SameTitleKey(cleanTitle);
            if (key.Length == 0 || _library == null || string.IsNullOrWhiteSpace(cleanTitle)) return null;

            // Frères candidats : même nom (au sens Emby : le nom de l'item
            // CONTIENT le titre nettoyé — le suffixe « (année) » du frère ne
            // doit pas le cacher à la requête), confirmation mémoire par la
            // clé stricte (égalité exacte après nettoyage/normalisation).
            BaseItem[] siblings;
            try
            {
                siblings = _library.GetItemList(new InternalItemsQuery
                {
                    NameContains = cleanTitle,
                    IncludeItemTypes = new[]
                        { string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase) ? "Series" : "Movie" },
                    Recursive = true,
                    Limit = 25,
                    EnableTotalRecordCount = false
                }) ?? Array.Empty<BaseItem>();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Info("[LLM_AI] OrphanIdentify : SB « {0} » — requête bibliothèque échouée ({1}) — poursuite S0/S1.",
                    epgTitle, ex.Message);
                return null;
            }

            foreach (var sib in siblings)
            {
                ct.ThrowIfCancellationRequested();
                if (sib == null || item == null || sib.InternalId == item.InternalId) continue;
                if (IsStrmCard(sib)) continue; // cartes .strm : hors périmètre
                if (SameTitleKey(sib.Name) != key) continue; // confirmation stricte en mémoire
                // Sans fiche à offrir (ids provider), le frère ne dit rien.
                if (!HasItemProviderId(sib, "tmdb") && !HasItemProviderId(sib, "imdb")
                    && !HasItemProviderId(sib, "tvdb")) continue;

                // Garde « vraie sœur » (v2.19.3), séries uniquement : un frère jumeau sans épisode
                // propose l'état DVR d'un événement passé (miroir NFO), pas une curation — son
                // fanion de preuve lexicale (siblingLexicalProof) serait sinon automatique pour
                // toute fiche d'homonyme. Écarté avec trace ; la chaîne S0→S1→S2→S3 suit son cours.
                if (string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase)
                    && !HasAnyEpisode(sib, ct))
                {
                    _logger?.Info("[LLM_AI] OrphanIdentify : frère « {0} » ({1}) écarté — aucun épisode en bibliothèque (jumeau de métadonnées ?), proposition non retenue.",
                        sib.Name, sib.Path);
                    continue;
                }

                // Relire la fiche du frère (cascade tmdb → imdb → tvdb,
                // miroir de S0). Illisible dans le kind de la passe → frère
                // suivant (pas d'ancrage croisé, voir bandeau).
                TmdbMeta meta = null;
                int.TryParse(sib.GetProviderId("tmdb"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sibTmdb);
                if (sibTmdb > 0)
                    meta = await _tmdb.LookupMetaByIdAsync(sibTmdb, kind, userTmdb, ct, overviewChain).ConfigureAwait(false);
                if (meta == null && HasItemProviderId(sib, "imdb"))
                    meta = await _tmdb.FindByExternalIdAsync(sib.GetProviderId("imdb").Trim(), "imdb_id",
                        kind, userTmdb, ct, overviewChain).ConfigureAwait(false);
                if (meta == null && string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase)
                    && HasItemProviderId(sib, "tvdb"))
                    meta = await _tmdb.FindByExternalIdAsync(sib.GetProviderId("tvdb").Trim(), "tvdb_id",
                        kind, userTmdb, ct, overviewChain).ConfigureAwait(false);
                if (meta == null) continue;

                trace.SawCandidates = true; // une fiche réelle surfacée (depuis la bibliothèque)
                _logger?.Info("[LLM_AI] OrphanIdentify : SB « {0} » — dossier frère « {1} » ({2}) porte tmdb={3} ; fiche proposée à la porte.",
                    epgTitle, sib.Name, sib.Path, meta.TmdbId);

                if (await AcceptCandidateAsync(cfg, meta, epgTitle, gateYear, overview, true, cleanTitle, ct,
                        item, corroborateNoSynopsis: false, gateYear: gateYear, siblingAnchor: true,
                        trace: trace, stageLabel: "SB").ConfigureAwait(false))
                    return meta;
            }
            return null;
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
            BaseItem item, PipelineTrace trace, CancellationToken ct,
            IReadOnlyList<string> overviewChain = null)
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
                return await PickEmbyCandidateAsync(results, kind, gateYear, epgTitle, epgOverview, userTmdb, cleanTitle, item, trace, ct, overviewChain).ConfigureAwait(false);
            }

            var mq = new RemoteSearchQuery<MovieInfo>
            {
                SearchInfo = new MovieInfo { Name = cleanTitle, Year = searchYear }
            };
            var mresults = await _providers.GetRemoteSearchResults<Movie, MovieInfo>(mq, ct).ConfigureAwait(false);
            return await PickEmbyCandidateAsync(mresults, kind, gateYear, epgTitle, epgOverview, userTmdb, cleanTitle, item, trace, ct, overviewChain).ConfigureAwait(false);
        }

        private async Task<TmdbMeta> PickEmbyCandidateAsync(
            IEnumerable<MediaBrowser.Model.Providers.RemoteSearchResult> results,
            string kind, int? gateYear, string epgTitle, string epgOverview,
            string userTmdb, string cleanTitle, BaseItem item, PipelineTrace trace, CancellationToken ct,
            IReadOnlyList<string> overviewChain = null)
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
                trace.SawCandidates = true; // une banque (Emby) a fait remonter un candidat identifié

                // Porte renforcée : titre lexical + année + juge synopsis.
                if (!await AcceptCandidateAsync(Plugin.Instance?.Configuration, meta, epgTitle,
                        gateYear, epgOverview, true, cleanTitle, ct, item,
                        trace: trace, stageLabel: "S0").ConfigureAwait(false))
                    continue;

                // Converti en fiche TMDB détaillée (genres, poster, statut) ;
                // relecture impossible → candidat suivant.
                TmdbMeta detail = null;
                if (meta.TmdbId > 0)
                    detail = await _tmdb.LookupMetaByIdAsync(meta.TmdbId, kind, userTmdb, ct, overviewChain).ConfigureAwait(false);
                if (detail == null && !string.IsNullOrWhiteSpace(meta.ImdbId))
                    detail = await _tmdb.FindByExternalIdAsync(meta.ImdbId, "imdb_id", kind, userTmdb, ct, overviewChain).ConfigureAwait(false);
                if (detail == null && kind == "series" && !string.IsNullOrWhiteSpace(meta.TvdbId))
                    detail = await _tmdb.FindByExternalIdAsync(meta.TvdbId, "tvdb_id", kind, userTmdb, ct, overviewChain).ConfigureAwait(false);
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
            BaseItem item, PipelineTrace trace, CancellationToken ct,
            IReadOnlyList<string> overviewChain = null, WebEvidenceContext webCtx = null)
        {
            var guess = await _runner.ResolveIdsAsync(cfg, epgTitle, kind, year, overview, channel, ct).ConfigureAwait(false);
            if (guess.IsEmpty) return null;

            // Année de référence (enregistrement) : préférence à l'année de la
            // porte (null si année soft), sinon à l'année proposée par le LLM.
            int? expectedYear = gateYear ?? guess.Year;

            // 1) id IMDb → TMDB /find.
            if (!string.IsNullOrWhiteSpace(guess.ImdbId))
            {
                var m = await _tmdb.FindByExternalIdAsync(guess.ImdbId.Trim(), "imdb_id", kind, userTmdb, ct, overviewChain).ConfigureAwait(false);
                if (m != null) trace.SawCandidates = true; // fiche réelle remontée, même rejetée
                if (await AcceptCandidateAsync(cfg, m, epgTitle, expectedYear, overview, false, null, ct, item, corroborateNoSynopsis: true, gateYear: gateYear, webCtx: webCtx, trace: trace, stageLabel: "S2").ConfigureAwait(false))
                    return m;
            }

            // 1b) id TVDB (séries) → TMDB /find tvdb_id : les séries québécoises
            // absentes de TMDB par titre existent souvent côté TVDB, et le LLM
            // connaît mieux cette banque pour les séries.
            if (kind == "series" && !string.IsNullOrWhiteSpace(guess.TvdbId))
            {
                var m = await _tmdb.FindByExternalIdAsync(guess.TvdbId.Trim(), "tvdb_id", kind, userTmdb, ct, overviewChain).ConfigureAwait(false);
                if (m != null) trace.SawCandidates = true;
                if (await AcceptCandidateAsync(cfg, m, epgTitle, expectedYear, overview, false, null, ct, item, corroborateNoSynopsis: true, gateYear: gateYear, webCtx: webCtx, trace: trace, stageLabel: "S2").ConfigureAwait(false))
                    return m;
            }

            // 2) id TMDB → relire la fiche (un id halluciné renvoie null).
            if (guess.TmdbId > 0)
            {
                var m = await _tmdb.LookupMetaByIdAsync(guess.TmdbId, kind, userTmdb, ct, overviewChain).ConfigureAwait(false);
                if (m != null) trace.SawCandidates = true;
                if (await AcceptCandidateAsync(cfg, m, epgTitle, expectedYear, overview, false, null, ct, item, corroborateNoSynopsis: true, gateYear: gateYear, webCtx: webCtx, trace: trace, stageLabel: "S2").ConfigureAwait(false))
                    return m;
            }

            // 3) titre original proposé → recherche S1 sur ce titre (garde lexicale).
            if (!string.IsNullOrWhiteSpace(guess.OriginalTitle))
            {
                string ot = TmdbLookupTool.CleanEpgTitle(guess.OriginalTitle);
                if (!string.IsNullOrWhiteSpace(ot))
                {
                    int? y = guess.Year ?? year;
                    var m = await _tmdb.LookupMetaMultiLangAsync(ot, kind, y, langs, ct, overviewChain).ConfigureAwait(false);
                    // Cascade annuelle (miroir de S1) : l'année proposée par le LLM peut être
                    // fausse pour une œuvre récente qu'il ne connaît pas — primary_release_year
                    // rend alors la vraie fiche invisible. Rejouer SANS filtre d'année : la porte
                    // d'acceptation reste chargée de valider.
                    if (m == null && y.HasValue)
                        m = await _tmdb.LookupMetaMultiLangAsync(ot, kind, null, langs, ct, overviewChain).ConfigureAwait(false);
                    if (m != null) trace.SawCandidates = true;
                    // Porte : l'année devinée par le LLM n'est indicative que quand l'item n'a pas
                    // d'année fiable (gateYear null). Titre EXACT ancré sur le TITRE DU GUIDE, pas
                    // sur la proposition (une fiche trouvée via une proposition la « confirme »
                    // toujours : égalité circulaire) = preuve plus forte que l'année devinée ; le
                    // tag needs-review + l'audit restent le filet. Garde d'année conservée pour les
                    // correspondances lâches, et un match lâche sans synopsis est rejeté (voir plus
                    // bas).
                    int? gateForCandidate = expectedYear;
                    if (!gateYear.HasValue && m != null)
                    {
                        string na = NormalizeTitle(cleanTitle);
                        if (na.Length > 0 && na == NormalizeTitle(m.Title))
                        {
                            // Titre du guide exact : l'année devinée est indicative.
                            gateForCandidate = null;
                        }
                        else if (string.IsNullOrWhiteSpace(overview))
                        {
                            // Titre lâche (inclusion/sous-séquence) + ni année fiable ni synopsis =
                            // aucune preuve décisive : une hallucination sémantique du LLM serait
                            // sinon appliquée par simple inclusion de titre.
                            trace?.RecordCandidate("S2", m, "faible", "S2 — proposition LLM",
                                "titre proposé « " + ot + " »", false,
                                "titre lâche sans année fiable ni synopsis comparable", trace?.CrossKind ?? false);
                            _logger?.Info("[LLM_AI] OrphanIdentify : candidat « {0} » (tmdb={1}) rejeté — titre lâche sans année fiable ni synopsis comparable (proposé « {2} »).",
                                m.Title, m.TmdbId, ot);
                            m = null;
                        }
                    }
                    if (await AcceptCandidateAsync(cfg, m, epgTitle, gateForCandidate, overview, true, ot, ct, item,
                            corroborateNoSynopsis: true, gateYear: gateYear, webCtx: webCtx, trace: trace, stageLabel: "S2").ConfigureAwait(false))
                        return m;
                }
            }

            return null;
        }

        // ------------------------------------------------------------------
        //  S3 : recherche web (SearXNG via WebSearchTool) → ids IMDb/TMDB
        //  extraits des résultats web (avec ancrage sur le titre du résultat)
        //  → validation TMDB + porte d'acceptation. Partagé via
        //  WebEvidenceContext avec le juge web de AcceptCandidateAsync.
        // ------------------------------------------------------------------

        internal static readonly Regex s_imdbUrlRe = new Regex(
            @"imdb\.com/(?:[a-z\-]+/)?title/(tt\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Ids TMDB dans les URLs de résultats web — capture aussi
        /// les URLs slug (<c>themoviedb.org/movie/385870-arthur-…</c>) : les
        /// fiches sans présence IMDb n'atterrissent que par cette voie.
        /// (partagé avec le renfort web du provider Identifier, miroir S3)</summary>
        internal static readonly Regex s_tmdbUrlRe = new Regex(
            @"themoviedb\.org/(?:movie|tv)/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal sealed class WebIdHit
        {
            public string Id;
            public string Source; // "imdb_id" ou "tmdb_id"
            public string ResultTitle;
            public bool TitleAnchored;
        }

        private sealed class WebAnchoredMeta
        {
            public TmdbMeta Meta;
            public string ResultTitle;
        }

        private sealed class WebEvidenceContext
        {
            public string CleanTitle;
            public int? GateYear;
            public string Kind;
            public string UserTmdb;
            public IReadOnlyList<string> OverviewChain;
            public bool Searched;
            public bool NoResponse;
            public string Error;
            public readonly List<WebIdHit> Hits = new List<WebIdHit>();
            public readonly Dictionary<string, TmdbMeta> ResolvedByKey = new Dictionary<string, TmdbMeta>(StringComparer.OrdinalIgnoreCase);
            public List<WebAnchoredMeta> AnchoredCompatibleMetas;
        }

        private async Task<TmdbMeta> ResolveViaSearXngAsync(PluginConfiguration cfg,
            string epgTitle, string cleanTitle, string kind, int? gateYear, bool yearSoft,
            string overview, string userTmdb, BaseItem item, PipelineTrace trace, CancellationToken ct,
            IReadOnlyList<string> overviewChain = null, WebEvidenceContext webCtx = null)
        {
            // Requête web sans titre = année seule : ses résultats sont du bruit d'année — annulée
            // (le filet de porte rejetterait de toute façon tout candidat sur titre vide).
            if (string.IsNullOrWhiteSpace(cleanTitle))
            {
                _logger?.Info("[LLM_AI] OrphanIdentify : S3 « {0} » — recherche web annulée : titre du guide vide.",
                    epgTitle);
                return null;
            }

            if (webCtx == null)
            {
                webCtx = new WebEvidenceContext
                {
                    CleanTitle = cleanTitle,
                    GateYear = gateYear,
                    Kind = kind,
                    UserTmdb = userTmdb,
                    OverviewChain = overviewChain
                };
            }

            await EnsureWebEvidenceAsync(webCtx, cfg, epgTitle, ct, resolveAnchored: false).ConfigureAwait(false);

            if (webCtx.NoResponse)
            {
                _logger?.Info("[LLM_AI] OrphanIdentify : S3 « {0} » — recherche web sans réponse.", epgTitle);
                return null;
            }
            if (!string.IsNullOrEmpty(webCtx.Error))
            {
                _logger?.Info("[LLM_AI] OrphanIdentify : S3 « {0} » — recherche web en erreur ({1}).",
                    epgTitle, TruncateLog(webCtx.Error, 120));
                return null;
            }

            if (webCtx.Hits.Count == 0)
            {
                // Des résultats web existent pour n'importe quelle requête
                // (définitions de dictionnaire…) — seuls les ids IMDb/TMDB
                // extraits comptent comme candidats (sémantique « introuvable »).
                _logger?.Info("[LLM_AI] OrphanIdentify : S3 « {0} » — aucun id IMDb/TMDB trouvé dans les résultats web.", epgTitle);
                return null;
            }

            // Logué : sans lui, une boucle by-id dont les /find renvoient
            // vide (id IMDb trop récent pour TMDB) est totalement invisible.
            trace.SawCandidates = true; // ids trouvés dans le web
            var imdbLog = webCtx.Hits.Where(h => h.Source == "imdb_id").Select(h => h.TitleAnchored ? h.Id + "*" : h.Id);
            var tmdbLog = webCtx.Hits.Where(h => h.Source == "tmdb_id").Select(h => h.TitleAnchored ? h.Id + "*" : h.Id);
            _logger?.Info("[LLM_AI] OrphanIdentify : S3 « {0} » — ids extraits des résultats web : imdb=[{1}] tmdb=[{2}].",
                epgTitle, string.Join(", ", imdbLog), string.Join(", ", tmdbLog));

            int? expectedYear = gateYear;

            // Priorité aux hits dont le titre du résultat web correspond au
            // titre local (*), puis hits non ancrés (ex. synopsis EPG à arbitrer).
            var orderedHits = webCtx.Hits
                .OrderByDescending(h => h.TitleAnchored)
                .ThenBy(h => h.Source == "imdb_id" ? 0 : 1);

            foreach (var hit in orderedHits)
            {
                var m = await ResolveWebHitAsync(webCtx, hit, ct).ConfigureAwait(false);
                if (await AcceptCandidateAsync(cfg, m, epgTitle, expectedYear, overview, false, null, ct, item,
                        corroborateNoSynopsis: true, gateYear: gateYear, webCtx: webCtx, trace: trace, stageLabel: "S3").ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(overview) || string.IsNullOrWhiteSpace(m.Overview))
                        _logger?.Info("[LLM_AI] OrphanIdentify : S3 « {0} » → candidat « {1} » ({2}={3}) accepté SANS vérif synopsis — à confirmer visuellement.",
                            epgTitle, m.Title, hit.Source == "imdb_id" ? "tt" : "tmdb", hit.Id);
                    return m;
                }
            }

            return null;
        }

        private async Task EnsureWebEvidenceAsync(WebEvidenceContext ctx, PluginConfiguration cfg,
            string epgTitle, CancellationToken ct, bool resolveAnchored)
        {
            if (ctx == null || cfg == null || !cfg.OrphanSearXngEnabled || string.IsNullOrWhiteSpace(ctx.CleanTitle) || _search == null)
                return;

            if (!ctx.Searched)
            {
                ctx.Searched = true;
                string query = ctx.GateYear.HasValue
                    ? ctx.CleanTitle + " " + ctx.GateYear.Value.ToString(CultureInfo.InvariantCulture)
                    : ctx.CleanTitle;

                await RunAndParseSearXngQueryAsync(ctx, query, epgTitle, ct).ConfigureAwait(false);

                // Résilience ±1 an (ex. sortie QC 2021 vs fiche TMDB/IMDb 2020) :
                // si la requête avec année ne retourne aucun hit ancré par le titre,
                // une relance sans année est tentée (sécurisée ensuite par
                // SameTitleKey exact + YearCompatible ±1).
                if (ctx.GateYear.HasValue
                    && !ctx.NoResponse
                    && string.IsNullOrEmpty(ctx.Error)
                    && !ctx.Hits.Any(h => h.TitleAnchored))
                {
                    await RunAndParseSearXngQueryAsync(ctx, ctx.CleanTitle, epgTitle, ct).ConfigureAwait(false);
                }
            }

            if (resolveAnchored && ctx.AnchoredCompatibleMetas == null)
            {
                ctx.AnchoredCompatibleMetas = new List<WebAnchoredMeta>();
                var seenTmdb = new HashSet<int>();
                foreach (var hit in ctx.Hits)
                {
                    if (!hit.TitleAnchored) continue;
                    var m = await ResolveWebHitAsync(ctx, hit, ct).ConfigureAwait(false);
                    if (m != null && m.TmdbId > 0 && YearCompatible(ctx.GateYear, m.Year) && seenTmdb.Add(m.TmdbId))
                    {
                        ctx.AnchoredCompatibleMetas.Add(new WebAnchoredMeta
                        {
                            Meta = m,
                            ResultTitle = hit.ResultTitle
                        });
                    }
                }
            }
        }

        private async Task RunAndParseSearXngQueryAsync(WebEvidenceContext ctx, string query, string epgTitle, CancellationToken ct)
        {
            string json;
            using (var doc = JsonDocument.Parse("{\"query\":\"" + JsonEscape(query) + "\"}"))
            {
                json = await _search.ExecuteAsync(doc.RootElement, ct).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                if (ctx.Hits.Count == 0) ctx.NoResponse = true;
                return;
            }

            ctx.NoResponse = false;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
                    {
                        if (ctx.Hits.Count == 0) ctx.Error = errEl.GetString();
                        return;
                    }
                    if (root.TryGetProperty("results", out var res) && res.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in res.EnumerateArray())
                        {
                            string rTitle = r.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String
                                ? tEl.GetString()
                                : string.Empty;
                            bool anchored = WebResultMatchesTitle(rTitle, ctx.CleanTitle);
                            CollectWebHits(r, "url", rTitle, anchored, ctx.Hits);
                            CollectWebHits(r, "content", rTitle, anchored, ctx.Hits);
                        }
                    }
                    if (root.TryGetProperty("infobox", out var ib) && ib.ValueKind == JsonValueKind.String)
                        CollectWebHitsFromText(ib.GetString(), string.Empty, false, ctx.Hits);
                    if (root.TryGetProperty("answers", out var ans) && ans.ValueKind == JsonValueKind.Array)
                        foreach (var a in ans.EnumerateArray())
                            if (a.ValueKind == JsonValueKind.String)
                                CollectWebHitsFromText(a.GetString(), string.Empty, false, ctx.Hits);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger?.Info("[LLM_AI] OrphanIdentify : S3 « {0} » — échec parsing résultats ({1}).", epgTitle, ex.Message); }
        }

        private async Task<TmdbMeta> ResolveWebHitAsync(WebEvidenceContext ctx, WebIdHit hit, CancellationToken ct)
        {
            if (ctx == null || hit == null || string.IsNullOrWhiteSpace(hit.Id)) return null;
            string key = hit.Source + ":" + hit.Id;
            if (ctx.ResolvedByKey.TryGetValue(key, out var cached)) return cached;

            TmdbMeta meta = null;
            if (string.Equals(hit.Source, "imdb_id", StringComparison.Ordinal))
            {
                meta = await _tmdb.FindByExternalIdAsync(hit.Id, "imdb_id", ctx.Kind, ctx.UserTmdb, ct, ctx.OverviewChain).ConfigureAwait(false);
            }
            else if (int.TryParse(hit.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId) && tmdbId > 0)
            {
                meta = await _tmdb.LookupMetaByIdAsync(tmdbId, ctx.Kind, ctx.UserTmdb, ct, ctx.OverviewChain).ConfigureAwait(false);
            }

            ctx.ResolvedByKey[key] = meta;
            return meta;
        }

        private sealed class WebJudgeOutcome
        {
            public bool Ok;
            public WebJudgeVerdict Verdict;
            public string Detail;
        }

        private async Task<WebJudgeOutcome> EvaluateWebJudgeAsync(WebEvidenceContext webCtx, PluginConfiguration cfg,
            TmdbMeta candidate, string epgTitle, CancellationToken ct)
        {
            if (cfg == null || !cfg.OrphanSearXngEnabled || webCtx == null)
            {
                _logger?.Info(
                    "[LLM_AI] OrphanIdentify : « {0} » — candidat « {1} » (tmdb={2}) rejeté : juge web désactivé ou indisponible sans synopsis ni titre commun (révision requise).",
                    epgTitle, candidate.Title, candidate.TmdbId);
                return new WebJudgeOutcome
                {
                    Ok = false,
                    Verdict = WebJudgeVerdict.Silence,
                    Detail = "juge web désactivé ou indisponible sans synopsis ni titre commun"
                };
            }

            await EnsureWebEvidenceAsync(webCtx, cfg, epgTitle, ct, resolveAnchored: true).ConfigureAwait(false);

            var anchoredList = webCtx.AnchoredCompatibleMetas;
            var anchoredIds = anchoredList != null
                ? anchoredList.Select(a => a.Meta.TmdbId).ToArray()
                : Array.Empty<int>();

            var verdict = EvaluateWebJudgeVerdict(candidate.TmdbId, anchoredIds, out int conflictingTmdbId);
            if (verdict == WebJudgeVerdict.Confirmed)
            {
                var match = anchoredList.FirstOrDefault(a => a.Meta.TmdbId == candidate.TmdbId);
                string rTitle = match?.ResultTitle ?? "—";
                _logger?.Info(
                    "[LLM_AI] OrphanIdentify : « {0} » — candidat « {1} » (tmdb={2}) sans synopsis ni titre commun CONFIRMÉ par le juge web (résultat « {3} »).",
                    epgTitle, candidate.Title, candidate.TmdbId, rTitle);
                return new WebJudgeOutcome
                {
                    Ok = true,
                    Verdict = WebJudgeVerdict.Confirmed,
                    Detail = "pont web « " + rTitle + " » → tmdb=" + candidate.TmdbId
                };
            }

            if (verdict == WebJudgeVerdict.Infirmed)
            {
                var conflict = anchoredList.FirstOrDefault(a => a.Meta.TmdbId == conflictingTmdbId);
                string detail = "le web désigne « " + (conflict?.Meta?.Title ?? "—") + " » (tmdb=" + conflictingTmdbId
                    + " via « " + (conflict?.ResultTitle ?? "—") + " »)";
                _logger?.Info(
                    "[LLM_AI] OrphanIdentify : « {0} » — candidat « {1} » (tmdb={2}) INFIRMÉ par le juge web ({3}) — rejeté.",
                    epgTitle, candidate.Title, candidate.TmdbId, detail);
                return new WebJudgeOutcome
                {
                    Ok = false,
                    Verdict = WebJudgeVerdict.Infirmed,
                    Detail = "infirmé par le juge web (" + detail + ")"
                };
            }

            _logger?.Info(
                "[LLM_AI] OrphanIdentify : « {0} » — candidat « {1} » (tmdb={2}) rejeté : juge web silencieux sans synopsis comparable ni titre commun (révision requise).",
                epgTitle, candidate.Title, candidate.TmdbId);
            return new WebJudgeOutcome
            {
                Ok = false,
                Verdict = WebJudgeVerdict.Silence,
                Detail = "juge web silencieux sans synopsis comparable ni titre commun"
            };
        }

        internal static void CollectWebHits(JsonElement parent, string prop, string resultTitle, bool anchored, List<WebIdHit> hits)
        {
            if (!parent.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.String) return;
            CollectWebHitsFromText(el.GetString(), resultTitle, anchored, hits);
        }

        internal static void CollectWebHitsFromText(string text, string resultTitle, bool anchored, List<WebIdHit> hits)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Match match in s_imdbUrlRe.Matches(text))
                AddOrUpgradeWebHit(hits, "imdb_id", match.Groups[1].Value, resultTitle, anchored);
            foreach (Match match in s_tmdbUrlRe.Matches(text))
                AddOrUpgradeWebHit(hits, "tmdb_id", match.Groups[1].Value, resultTitle, anchored);
        }

        internal static void AddOrUpgradeWebHit(List<WebIdHit> hits, string source, string id, string resultTitle, bool anchored)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            for (int i = 0; i < hits.Count; i++)
            {
                if (string.Equals(hits[i].Source, source, StringComparison.Ordinal)
                    && string.Equals(hits[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    if (anchored && !hits[i].TitleAnchored)
                    {
                        hits[i].TitleAnchored = true;
                        hits[i].ResultTitle = resultTitle;
                    }
                    return;
                }
            }
            hits.Add(new WebIdHit
            {
                Id = id,
                Source = source,
                ResultTitle = resultTitle,
                TitleAnchored = anchored
            });
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
        //  Porte d'acceptation d'un candidat TMDB : année + juge sémantique. Acceptation : année
        //  compatible ET (pas de synopsis à comparer, OU juge LLM confirme que les synopsis
        //  décrivent la même œuvre). Un candidat rejeté n'arrête jamais la chaîne. Optionnellement,
        //  garde de titre lexicale (voies par titre). Voies LLM/web (S2 par id, S2 par titre
        //  proposé, S3 web) sans synopsis comparable : corroboration exigée (corroborateNoSynopsis)
        //  ANCRÉE SUR LE TITRE DU GUIDE, pas sur la proposition du LLM (une fiche choisie PAR la
        //  proposition la « confirme » toujours : cercle vicieux). Sans année fiable (gateYear) ni
        //  synopsis : seule l'ÉGALITÉ EXACTE du titre du guide est une preuve. gateYear = année
        //  fiable de l'item (null si soft/absente).
        //  siblingAnchor = candidat du dossier frère (SB) : règle d'année dédiée séries (année item
        //  ≥ année fiche − 1 : les saisons d'une série longue se diffusent des années après la
        //  première), garde lexicale pure (l'année est arbitrée à part) et rejet des frères
        //  homonymes d'une AUTRE époque sans synopsis à arbitrer (reboot/homonyme). Toute
        //  acceptation passe ensuite la garde « fiche déjà portée » (FicheConflictGateAsync) — voir
        //  sa documentation.
        // ------------------------------------------------------------------

        private async Task<bool> AcceptCandidateAsync(PluginConfiguration cfg, TmdbMeta m,
            string epgTitle, int? epgYear, string epgSynopsis,
            bool requireTitleMatch, string matchTitle, CancellationToken ct,
            BaseItem item,
            bool corroborateNoSynopsis = false, int? gateYear = null, bool siblingAnchor = false,
            WebEvidenceContext webCtx = null, PipelineTrace trace = null, string stageLabel = null)
        {
            if (m == null || m.TmdbId <= 0) return false;
            string st = stageLabel ?? (siblingAnchor ? "SB" : "?");
            bool ck = trace?.CrossKind ?? false;

            // Titre du guide vide : AUCUNE corroboration possible (le bras « année corroborante »
            // matcherait n'importe quelle fiche de la même année). Un titre vide n'est jamais une
            // preuve — filet indépendant des entrées du pipeline (défense en profondeur).
            if (string.IsNullOrWhiteSpace(epgTitle))
            {
                trace?.RecordCandidate(st, m, "faible", st, null, false,
                    "titre du guide vide, aucune preuve possible", ck);
                _logger?.Info("[LLM_AI] OrphanIdentify : candidat « {0} » (tmdb={1}) rejeté — titre du guide vide, aucune preuve possible (programme creux ?).",
                    m.Title, m.TmdbId);
                return false;
            }

            bool seriesKind = string.Equals(m.Kind, "series", StringComparison.OrdinalIgnoreCase);

            // Année-thème : le guide peut porter l'année-SUJET d'un documentaire
            // (ProductionYear=1945 pour une œuvre de 2020 SUR l'année 1945) ; ≥ 1900, elle échappe
            // au garde « année aberrante » et ferait rejeter la VRAIE fiche. Un enregistrement DVR
            // ne peut pas PRÉCÉDER son œuvre : une fiche postérieure de plus d'un an à l'année de
            // l'item désigne une année-thème. L'année devient non fiable POUR CE CANDIDAT : garde
            // d'année passée, doctrine « sans année fiable » (égalité exacte du titre du guide, ou
            // juge synopsis). Un homonyme d'une autre époque reste rejeté par le titre. L'ancrage
            // frère (séries) garde sa règle dédiée.
            int? itemYear = gateYear ?? epgYear;
            if (!siblingAnchor && itemYear.HasValue && itemYear.Value >= 1900
                && m.Year.HasValue && m.Year.Value >= 1900
                && m.Year.Value > itemYear.Value + 1)
            {
                _logger?.Info(
                    "[LLM_AI] OrphanIdentify : « {0} » — année de l'item ({1}) antérieure de plus d'un an à la fiche ({2}) : année-thème probable, traitée comme non fiable (titre exact / juge décident).",
                    epgTitle, itemYear.Value.ToString(CultureInfo.InvariantCulture),
                    m.Year.Value.ToString(CultureInfo.InvariantCulture));
                epgYear = null;
                gateYear = null;
            }

            // Garde-fou année (deux œuvres d'époques différentes). Rejet logué : ce garde est
            // silencieux sinon, et un « 0 si inconnu » du LLM y a déjà caché une fiche correcte.
            // Ancrage frère (séries) : règle dédiée — un enregistrement ne peut pas PRÉCÉDER la
            // première diffusion de sa série, mais peut la suivre de longtemps.
            bool yearOk = siblingAnchor && seriesKind
                ? AnchorSeriesYearCompatible(epgYear, m.Year)
                : YearCompatible(epgYear, m.Year);
            if (!yearOk)
            {
                string yrReason = "année incompatible (attendue "
                    + (epgYear.HasValue ? epgYear.Value.ToString(CultureInfo.InvariantCulture) : "—")
                    + ", fiche "
                    + (m.Year.HasValue ? m.Year.Value.ToString(CultureInfo.InvariantCulture) : "—") + ")";
                trace?.RecordCandidate(st, m, "faible", st, null, false, yrReason, ck);
                _logger?.Info("[LLM_AI] OrphanIdentify : candidat « {0} » (tmdb={1}) rejeté — {2}.",
                    m.Title, m.TmdbId, yrReason);
                return false;
            }

            // Garde de titre lexicale (voies par titre uniquement). Ancrage :
            // lexicale PURE (années null) — l'année vient d'être arbitrée par
            // la règle dédiée ci-dessus, la revérifier ici rejetterait les
            // séries longues (enregistrement 2020, fiche 2011).
            if (requireTitleMatch && !TitleMatches(matchTitle, m.Title,
                    siblingAnchor ? null : epgYear, siblingAnchor ? null : m.Year))
            {
                trace?.RecordCandidate(st, m, "faible", st, null, false,
                    "garde de titre (proposé « " + (matchTitle ?? "—") + " »)", ck);
                _logger?.Info("[LLM_AI] OrphanIdentify : candidat « {0} » (tmdb={1}) rejeté — garde de titre (proposé « {2} »).",
                    m.Title, m.TmdbId, matchTitle ?? "—");
                return false;
            }

            // Pas de synopsis à comparer : acceptation sur année (+titre), comportement EPG
            // historique. Sur les voies LLM/web, un id ou un titre PROPOSÉ n'est pas une preuve (la
            // fiche trouvée via la proposition la valide toujours). Sans année fiable ni synopsis
            // comparable, seule l'égalité EXACTE du titre du guide décide ; avec une année fiable,
            // la garde lexicale (titre du guide) ou l'année corroborante suffit.
            if (string.IsNullOrWhiteSpace(epgSynopsis) || string.IsNullOrWhiteSpace(m.Overview))
            {
                // Ancrage frère (séries) sans synopsis : un écart d'époque
                // (> 1 an) est alors inarbitrable — reboot ou homonyme ? On
                // n'écrit rien (needs-review en fin de chaîne), la chaîne
                // continue de chercher.
                if (siblingAnchor && seriesKind
                    && epgYear.HasValue && m.Year.HasValue
                    && Math.Abs(epgYear.Value - m.Year.Value) > 1)
                {
                    string sibReason = "dossier frère homonyme d'une autre époque (item "
                        + epgYear.Value.ToString(CultureInfo.InvariantCulture)
                        + ", fiche " + m.Year.Value.ToString(CultureInfo.InvariantCulture) + "), aucun synopsis";
                    trace?.RecordCandidate(st, m, "faible", st, null, false, sibReason, ck);
                    _logger?.Info("[LLM_AI] OrphanIdentify : candidat « {0} » (tmdb={1}) rejeté — dossier frère homonyme d'une autre époque (item {2}, fiche {3}), aucun synopsis pour arbitrer (reboot/homonyme ?).",
                        m.Title, m.TmdbId,
                        epgYear.Value.ToString(CultureInfo.InvariantCulture),
                        m.Year.Value.ToString(CultureInfo.InvariantCulture));
                    return false;
                }
                if (!corroborateNoSynopsis)
                    return await FicheConflictGateAsync(cfg, m, item, epgTitle, epgYear, epgSynopsis,
                        false, ct, trace: trace, stageLabel: st,
                        proofLevel: "forte",
                        proofSource: siblingAnchor ? "SB — série sœur en bibliothèque" : (st + " — recherche Emby"),
                        proofEvidence: siblingAnchor ? "dossier frère homonyme avec ≥1 épisode" : "correspondance titre + année Emby",
                        crossKind: ck).ConfigureAwait(false);
                string epgClean = TmdbLookupTool.CleanEpgTitle(epgTitle);
                string a = NormalizeTitle(epgClean);
                bool exactGuide = a.Length > 0 && a == NormalizeTitle(m.Title);
                bool titleOk = TitleMatches(epgClean, m.Title, epgYear, m.Year);
                bool yearsOk = gateYear.HasValue && m.Year.HasValue; // année FIABLE — YearCompatible déjà passé plus haut
                if (gateYear.HasValue)
                {
                    if (titleOk)
                        return await FicheConflictGateAsync(cfg, m, item, epgTitle, epgYear, epgSynopsis,
                            false, ct, trace: trace, stageLabel: st,
                            proofLevel: "forte",
                            proofSource: st + " — titre + année",
                            proofEvidence: "correspondance lexicale « " + epgClean + " » ↔ « " + m.Title + " » (" + m.Year + ")",
                            crossKind: ck).ConfigureAwait(false);
                    if (yearsOk)
                    {
                        var webOutcome = await EvaluateWebJudgeAsync(webCtx, cfg, m, epgTitle, ct).ConfigureAwait(false);
                        if (webOutcome.Ok)
                            return await FicheConflictGateAsync(cfg, m, item, epgTitle, epgYear, epgSynopsis,
                                false, ct, trace: trace, stageLabel: st,
                                proofLevel: "intermédiaire",
                                proofSource: st + " — pont web ancré",
                                proofEvidence: webOutcome.Detail + " ; année " + gateYear.Value.ToString(CultureInfo.InvariantCulture) + " corroborée",
                                crossKind: ck).ConfigureAwait(false);
                        trace?.RecordCandidate(st, m, "faible", st + " — année seule",
                            "année " + gateYear.Value.ToString(CultureInfo.InvariantCulture) + " sans titre commun",
                            false, webOutcome.Detail, ck);
                        return false;
                    }
                }
                else if (exactGuide)
                {
                    return await FicheConflictGateAsync(cfg, m, item, epgTitle, epgYear, epgSynopsis,
                        false, ct, trace: trace, stageLabel: st,
                        proofLevel: "forte",
                        proofSource: st + " — titre exact",
                        proofEvidence: "égalité exacte du titre du guide « " + epgClean + " »",
                        crossKind: ck).ConfigureAwait(false);
                }
                string noSynReason = gateYear.HasValue
                    ? "titre du guide sans rapport lexical avec la fiche"
                    : "sans année fiable, seule l'égalité exacte du titre du guide est une preuve";
                trace?.RecordCandidate(st, m, "faible", st, null, false, noSynReason, ck);
                _logger?.Info("[LLM_AI] OrphanIdentify : candidat « {0} » (tmdb={1}) rejeté — voie LLM sans synopsis comparable : {2} (titre guide « {3} », fiche « {4} »).",
                    m.Title, m.TmdbId, noSynReason, epgClean, m.Title);
                return false;
            }

            // v2.19.2 — preuve lexicale de l'ancrage frère (séries) : la fiche est proposée PAR le
            // dossier frère de l'usager et porte la même clé de titre que le titre de recherche
            // (année déjà arbitrée par AnchorSeriesYearCompatible). Le juge comparerait un synopsis
            // d'ÉPISODE à l'overview de la SÉRIE (faux négatifs) ; contournement → porte « fiche
            // déjà portée » avec le fanion de preuve : la divergence de titre = épisode vs série,
            // pas un conflit d'œuvre.
            if (siblingAnchor && seriesKind
                && SameTitleKey(matchTitle).Length > 0
                && SameTitleKey(matchTitle) == SameTitleKey(m.Title))
            {
                _logger?.Info("[LLM_AI] OrphanIdentify : candidat « {0} » (tmdb={1}) — preuve lexicale de l'ancrage frère (clé de titre égale, année arbitrée) : juge contourné.",
                    m.Title, m.TmdbId);
                return await FicheConflictGateAsync(cfg, m, item, epgTitle, epgYear, epgSynopsis,
                    false, ct, siblingLexicalProof: true, trace: trace, stageLabel: st,
                    proofLevel: "forte",
                    proofSource: "SB — série sœur en bibliothèque",
                    proofEvidence: "clé de titre égale + année arbitrée",
                    crossKind: ck).ConfigureAwait(false);
            }

            var v = await _runner.JudgeSynopsisMatchAsync(cfg, epgTitle, epgYear, epgSynopsis,
                m.Title, m.Year, m.Overview, ct).ConfigureAwait(false);

            if (!v.IsValid)
            {
                trace?.RecordCandidate(st, m, "faible", st, null, false,
                    "juge synopsis indisponible", ck);
                _logger?.Info("[LLM_AI] OrphanIdentify : juge synopsis indisponible pour « {0} » — candidat « {1} » rejeté par prudence.",
                    epgTitle, m.Title);
                return false;
            }
            if (!v.Match)
            {
                trace?.RecordCandidate(st, m, "faible", st, null, false,
                    "juge synopsis : œuvres différentes (" + v.Reason + ")", ck);
                _logger?.Info("[LLM_AI] OrphanIdentify : juge synopsis : « {0} » ≠ candidat « {1} » ({2}) — rejet, on continue.",
                    epgTitle, m.Title, v.Reason);
                return false;
            }
            _logger?.Info("[LLM_AI] OrphanIdentify : juge synopsis : « {0} » = candidat « {1} » ({2}) — accepté.",
                epgTitle, m.Title, v.Reason);
            // Juge confirmé : la fiche décrit cet item — la garde « fiche déjà
            // portée » reste appliquée (signalement du porteur divergent) mais
            // ne rejette plus (l'écriture est sémantiquement fondée).
            return await FicheConflictGateAsync(cfg, m, item, epgTitle, epgYear, epgSynopsis,
                true, ct, trace: trace, stageLabel: st,
                proofLevel: "forte",
                proofSource: st + " — juge synopsis",
                proofEvidence: "juge sémantique : " + (v.Reason ?? "concordance"),
                crossKind: ck).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------
        //  Garde « fiche déjà portée » (fiche-conflict). Contrepartie ACTIVE du filtre natif «
        //  Doublons » d'Emby (passif, aveugle aux langues et aux fiches TVDB divergentes) : AVANT
        //  d'écrire un candidat, qui d'autre dans la bibliothèque porte DÉJÀ cette fiche
        //  (tmdb/tvdb/imdb, requête indexée AnyProviderIdEquals) ?
        //  <list type="bullet">
        //  <item>aucun autre porteur → passage ;</item>
        //  <item>porteur au MÊME titre (± « (année) », bruit EPG retiré — SameTitleKey) → passage +
        //  log « même œuvre en deux dossiers » ; la fusion manuelle reste à l'usager (le plugin ne
        //  touche jamais les fichiers) ;</item>
        //  <item>porteur au titre DIVERGENT → le juge LLM arbitre le synopsis EPG contre l'overview
        //  du porteur : même œuvre (renommage/traduction) → passage ; œuvres différentes, juge
        //  indisponible ou aucun synopsis à arbitrer → REJET par prudence (needs-review en fin de
        //  chaîne).</item>
        //  </list>
        //  judgeConfirmed=true (le juge de la porte vient de confirmer le synopsis EPG contre la
        //  fiche) : le porteur divergent est probablement un renommage (ou une fiche erronée SUR le
        //  porteur, problème préexistant) → passage + log. Garde déterministe préalable : la fiche
        //  contredit un id déjà porté par l'item → rejet (défensif : la reversion d'audit
        //  RevertToEpgAsync est best-effort).
        // ------------------------------------------------------------------

        private async Task<bool> FicheConflictGateAsync(PluginConfiguration cfg, TmdbMeta m,
            BaseItem item, string epgTitle, int? epgYear, string epgSynopsis,
            bool judgeConfirmed, CancellationToken ct, bool siblingLexicalProof = false,
            PipelineTrace trace = null, string stageLabel = null,
            string proofLevel = null, string proofSource = null, string proofEvidence = null,
            bool crossKind = false)
        {
            if (m == null || m.TmdbId <= 0) return true;
            string st = stageLabel ?? "?";

            // Garde déterministe : conflit id déjà porté par l'item (ignorée en
            // mode ré-identification chat pour permettre la correction d'un
            // faux positif).
            if (trace == null || !trace.IgnoreExistingItemIds)
            {
                string idConflict = FindItemIdConflict(item, m);
                if (idConflict != null)
                {
                    trace?.RecordCandidate(st, m, "faible", st, null, false,
                        "contredit un id déjà porté par l'item (" + idConflict + ")", crossKind);
                    _logger?.Info("[LLM_AI] OrphanIdentify : candidat « {0} » (tmdb={1}) rejeté — contredit un id déjà porté par l'item ({2}).",
                        m.Title, m.TmdbId, idConflict);
                    return false;
                }
            }

            if (_library == null)
            {
                trace?.RecordCandidate(st, m, proofLevel ?? "forte", proofSource ?? st, proofEvidence, true, "accepté", crossKind);
                return true; // pas de garde possible — comportement historique
            }
            var carriers = FindFicheCarriers(m, item);
            if (carriers.Count == 0)
            {
                trace?.RecordCandidate(st, m, proofLevel ?? "forte", proofSource ?? st, proofEvidence, true, "accepté", crossKind);
                return true;
            }

            foreach (var x in carriers)
            {
                if (x == null) continue;

                // Même titre (± année/bruit) : même œuvre en deux dossiers.
                if (SameTitleKey(epgTitle).Length > 0 && SameTitleKey(epgTitle) == SameTitleKey(x.Name))
                {
                    _logger?.Info("[LLM_AI] OrphanIdentify : fiche (tmdb={0}) déjà portée par « {1} » ({2}) — même œuvre en deux dossiers (fusion manuelle : décision de l'usager, jamais du plugin).",
                        m.TmdbId, x.Name, x.Path);
                    continue;
                }

                // Preuve lexicale d'ancrage frère (v2.19.2) : le titre du guide est un titre
                // d'ÉPISODE, le porteur est le frère qui a PROPOSÉ la fiche (ou son jumeau de
                // l'autre racine) — divergence de granularité épisode-vs-série, pas un conflit
                // d'œuvre. Pas de juge ici non plus : c'est cette comparaison qui produisait les
                // faux négatifs.
                if (siblingLexicalProof)
                {
                    _logger?.Info("[LLM_AI] OrphanIdentify : fiche (tmdb={0}) déjà portée par « {1} » ({2}) — preuve lexicale de l'ancrage frère : titre du guide = épisode, porteur = la série, même œuvre.",
                        m.TmdbId, x.Name, x.Path);
                    continue;
                }

                // Titre divergent : arbitrage.
                if (judgeConfirmed)
                {
                    _logger?.Info("[LLM_AI] OrphanIdentify : fiche (tmdb={0}) déjà portée par « {1} » ({2}) au titre différent — juge a confirmé la fiche pour cet item : renommage/traduction probable (ou fiche du porteur à vérifier).",
                        m.TmdbId, x.Name, x.Path);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(epgSynopsis) || string.IsNullOrWhiteSpace(x.Overview))
                {
                    string cReason = "fiche déjà portée par « " + x.Name + " » au titre divergent, aucun synopsis pour arbitrer";
                    trace?.RecordCandidate(st, m, "faible", st, null, false, cReason, crossKind);
                    _logger?.Info("[LLM_AI] OrphanIdentify : candidat « {0} » (tmdb={1}) rejeté — fiche déjà portée par « {2} » ({3}) au titre sans rapport, aucun synopsis pour arbitrer (à vérifier : doublon de dossiers ou fiche erronée sur le porteur).",
                        m.Title, m.TmdbId, x.Name, x.Path);
                    return false;
                }
                var v = await _runner.JudgeSynopsisMatchAsync(cfg, epgTitle, epgYear, epgSynopsis,
                    x.Name, x.ProductionYear > 1900 ? (int?)x.ProductionYear : null, x.Overview,
                    ct).ConfigureAwait(false);
                if (!v.IsValid)
                {
                    trace?.RecordCandidate(st, m, "faible", st, null, false,
                        "fiche déjà portée par « " + x.Name + " » et juge indisponible", crossKind);
                    _logger?.Info("[LLM_AI] OrphanIdentify : candidat « {0} » (tmdb={1}) rejeté — fiche déjà portée par « {2} » au titre divergent et juge indisponible (prudence).",
                        m.Title, m.TmdbId, x.Name);
                    return false;
                }
                if (!v.Match)
                {
                    trace?.RecordCandidate(st, m, "faible", st, null, false,
                        "fiche déjà portée par « " + x.Name + " » : œuvres différentes (" + v.Reason + ")", crossKind);
                    _logger?.Info("[LLM_AI] OrphanIdentify : candidat « {0} » (tmdb={1}) rejeté — fiche déjà portée par « {2} » ({3}) : œuvres différentes ({4}).",
                        m.Title, m.TmdbId, x.Name, x.Path, v.Reason);
                    return false;
                }
                _logger?.Info("[LLM_AI] OrphanIdentify : fiche (tmdb={0}) déjà portée par « {1} » ({2}) — le juge confirme la même œuvre (renommage/traduction).",
                    m.TmdbId, x.Name, x.Path);
            }
            trace?.RecordCandidate(st, m, proofLevel ?? "forte", proofSource ?? st, proofEvidence, true, "accepté", crossKind);
            return true;
        }

        /// <summary>Items de bibliothèque (Movie/Series, cartes .strm exclues,
        /// item lui-même exclu) portant déjà un des ids provider du candidat
        /// (tmdb/tvdb/imdb — requête indexée par id, miroir du repli IMDb
        /// d'<c>EnrichWithLibrary</c>). Dédupliqués par id interne. Le type
        /// d'item n'est PAS filtré par le kind du candidat : une fiche croisée
        /// (film sur un item série, posée par le repli de type) est un
        /// porteur légitime.</summary>
        private List<BaseItem> FindFicheCarriers(TmdbMeta m, BaseItem self)
        {
            var result = new List<BaseItem>();
            if (m == null || _library == null) return result;

            var keys = new List<KeyValuePair<string, string>>();
            if (m.TmdbId > 0)
                keys.Add(new KeyValuePair<string, string>("Tmdb",
                    m.TmdbId.ToString(CultureInfo.InvariantCulture)));
            if (!string.IsNullOrWhiteSpace(m.TvdbId))
                keys.Add(new KeyValuePair<string, string>("Tvdb", m.TvdbId.Trim()));
            if (!string.IsNullOrWhiteSpace(m.ImdbId))
                keys.Add(new KeyValuePair<string, string>("Imdb", m.ImdbId.Trim()));

            foreach (var kv in keys)
            {
                try
                {
                    var q = new InternalItemsQuery
                    {
                        AnyProviderIdEquals = new Dictionary<string, string> { { kv.Key, kv.Value } },
                        IncludeItemTypes = new[] { "Movie", "Series" },
                        Recursive = true,
                        Limit = 12,
                        EnableTotalRecordCount = false
                    };
                    foreach (var x in _library.GetItemList(q) ?? Array.Empty<BaseItem>())
                    {
                        if (x == null || self != null && x.InternalId == self.InternalId) continue;
                        if (IsStrmCard(x)) continue;
                        bool known = false;
                        foreach (var r in result)
                            if (r.InternalId == x.InternalId) { known = true; break; }
                        if (!known) result.Add(x);
                    }
                }
                catch { /* best-effort : une clé illisible ne bloque pas la garde */ }
            }
            return result;
        }

        /// <summary>Conflit déterministe : le candidat porte un id provider DIFFÉRENT de celui que
        /// l'item porte déjà (tmdb/imdb/tvdb présents des deux côtés, valeurs différentes).
        /// Retourne la description du conflit, null si aucun. Défensif : le pipeline ne traite en
        /// principe que des orphelins, mais la reversion d'audit (<c>RevertToEpgAsync</c>) est
        /// best-effort — un retrait d'id échoué laisserait l'ancienne fiche rejetée sur
        /// l'item.</summary>
        private static string FindItemIdConflict(BaseItem item, TmdbMeta m)
        {
            if (item == null || m == null) return null;

            string itemTmdb = SafeProviderId(item, "tmdb");
            string candTmdb = m.TmdbId > 0 ? m.TmdbId.ToString(CultureInfo.InvariantCulture) : null;
            if (!string.IsNullOrWhiteSpace(itemTmdb) && !string.IsNullOrWhiteSpace(candTmdb)
                && !string.Equals(itemTmdb.Trim(), candTmdb.Trim(), StringComparison.OrdinalIgnoreCase))
                return "tmdb " + itemTmdb + " ≠ " + candTmdb;

            string itemImdb = SafeProviderId(item, "imdb");
            if (!string.IsNullOrWhiteSpace(itemImdb) && !string.IsNullOrWhiteSpace(m.ImdbId)
                && !string.Equals(itemImdb.Trim(), m.ImdbId.Trim(), StringComparison.OrdinalIgnoreCase))
                return "imdb " + itemImdb + " ≠ " + m.ImdbId;

            string itemTvdb = SafeProviderId(item, "tvdb");
            if (!string.IsNullOrWhiteSpace(itemTvdb) && !string.IsNullOrWhiteSpace(m.TvdbId)
                && !string.Equals(itemTvdb.Trim(), m.TvdbId.Trim(), StringComparison.OrdinalIgnoreCase))
                return "tvdb " + itemTvdb + " ≠ " + m.TvdbId;

            return null;
        }

        private static string SafeProviderId(BaseItem item, string key)
        {
            if (item == null) return null;
            try { return item.GetProviderId(key); } catch { return null; }
        }

        // ------------------------------------------------------------------
        //  Application non destructive + verrouillage
        // ------------------------------------------------------------------

        internal async Task ApplyAsync(BaseItem item, TmdbMeta meta, string kind, bool isSeries, CancellationToken ct,
            bool crossKind = false, bool replaceIds = false, bool forceIdentifiedTag = false)
        {
            if (replaceIds && item.ProviderIds != null)
            {
                item.ProviderIds.Remove("tmdb");
                item.ProviderIds.Remove("imdb");
                item.ProviderIds.Remove("tvdb");
            }

            // --- Provider ids (uniquement ceux absents) ---
            if (meta.TmdbId > 0 && string.IsNullOrWhiteSpace(item.GetProviderId("tmdb")))
                item.SetProviderId("tmdb", meta.TmdbId.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(meta.ImdbId) && string.IsNullOrWhiteSpace(item.GetProviderId("imdb")))
                item.SetProviderId("imdb", meta.ImdbId.Trim());
            if ((isSeries || string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrWhiteSpace(meta.TvdbId) && string.IsNullOrWhiteSpace(item.GetProviderId("tvdb")))
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

            // --- ProductionYear : seulement si absente (v2.20.19). L'année posée est celle de
            // l'ŒUVRE portée par la fiche VALIDÉE (release_date/first_air_date TMDB), pas l'année
            // de diffusion EPG. Fill-if-empty strict : une année déjà présente (curation de
            // l'usager, année-thème du guide) n'est JAMAIS écrasée ; l'année d'item devient ainsi
            // l'année FIABLE pour les gardes des passes suivantes. PAS de verrou dédié : l'enum
            // MetadataFields du SDK Emby 4.10 n'a pas de membre ProductionYear ; la protection
            // vient du tag llmai-identified et des téléchargeurs natifs décochés sur la
            // bibliothèque des enregistrements.
            if ((!item.ProductionYear.HasValue || item.ProductionYear.Value <= 0)
                && meta.Year.HasValue && meta.Year.Value > 0)
            {
                item.ProductionYear = meta.Year.Value;
                _logger?.Info("[LLM_AI] OrphanIdentify : « {0} » — ProductionYear={1} posée (absente de l'item ; année de l'œuvre de la fiche validée).",
                    item.Name, meta.Year.Value);
            }

            // --- Poster Primary : seulement si manquant (best-effort) ---
            bool setImage = false;
            if (!item.HasImage(ImageType.Primary, 0) && !string.IsNullOrWhiteSpace(meta.PosterUrl))
            {
                setImage = await TrySavePosterAsync(item, meta.PosterUrl, ct).ConfigureAwait(false);
            }

            // --- Verrouillage (add-only, jamais de retrait) ---
            // NB ProductionYear : non verrouillable sur le SDK Emby 4.10
            // (MetadataFields n'a pas ce membre) — voir le commentaire du
            // bloc ProductionYear ci-dessus.
            AddLock(item, MetadataFields.Name);
            if (setOverview) AddLock(item, MetadataFields.Overview);
            if (setGenres) AddLock(item, MetadataFields.Genres);

            // --- Tag d'idempotence --- (fiche croisée : needs-review — l'item
            // reste sous le radar de l'audit OrphanAuditTaggedIds, qui confirme
            // via la relecture croisée ; une fiche du type de l'item est
            // identifiée directement, Emby sait la relire au refresh. Le tag
            // croisé est ADD-ONLY : il survit à la confirmation et sert à
            // retrouver ces items — le type de l'item n'est jamais modifié.)
            // Exception chat (forceIdentifiedTag) : l'approbation humaine
            // explicite vaut confirmation — pose llmai-identified même sur
            // une fiche croisée, tout en conservant llmai-cross-kind.
            RemoveTag(item, OrphanIdentifyTask.TagNeedsReview);
            RemoveTag(item, OrphanIdentifyTask.TagNotFound);
            AddTag(item, (crossKind && !forceIdentifiedTag)
                ? OrphanIdentifyTask.TagNeedsReview
                : OrphanIdentifyTask.TagIdentified);
            if (crossKind) AddTag(item, OrphanIdentifyTask.TagCrossKind);

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
                        // Même piège 4.10 que DefaultImageApplier :
                        // generatedFromItemIds déréférencé sans garde
                        // (ImageSaver.SaveImage → .Length) et libraryOptions
                        // déréférencé sans garde (IsSaveLocalImagesEnabled) —
                        // Array.Empty + garde null obligatoires.
                        var libOpts = _library.GetLibraryOptions(item)
                            ?? new MediaBrowser.Model.Configuration.LibraryOptions();
                        await _providers.SaveImage(item, libOpts, ms, mime.AsMemory(),
                            ImageType.Primary, null, Array.Empty<long>(), dirSvc, true, ct).ConfigureAwait(false);
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
        /// inclusion acceptée ; années > 1 an d'écart refusées). Deuxième
        /// voie : sous-séquence ordonnée de tokens — attrape les variantes à
        /// sous-titre inséré (« ADN business : la face cachée des tests grand
        /// public » vs « ADN, la face cachée des tests grand public »),
        /// invisibles à l'inclusion contiguë.</summary>
        internal static bool TitleMatches(string epgTitle, string tmdbTitle, int? epgYear, int? metaYear)
        {
            if (string.IsNullOrWhiteSpace(tmdbTitle)) return false;
            string a = NormalizeTitle(epgTitle);
            string b = NormalizeTitle(tmdbTitle);
            if (a.Length == 0 || b.Length == 0) return false;

            bool titleOk = a == b || a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)
                || TokenSubsequence(epgTitle, tmdbTitle);
            if (!titleOk) return false;

            // Même doctrine d'année que la porte (inconnue/aberrante = pas de
            // garde) : un « 0 si inconnu » qui filerait ici rejetterait à tort.
            return YearCompatible(epgYear, metaYear);
        }

        /// <summary>Garde par tokens : les mots (normalisés) du titre le plus
        /// court apparaissent <b>dans l'ordre</b> dans le plus long — le
        /// sous-titre inséré (« business ») n'empêche plus le match. Exige ≥ 2
        /// tokens côté titre court pour éviter le match sur un seul mot.</summary>
        internal static bool TokenSubsequence(string epgTitle, string tmdbTitle)
        {
            string[] a = Tokens(epgTitle);
            string[] b = Tokens(tmdbTitle);
            if (a.Length < 2 || b.Length < a.Length) return false;
            int j = 0;
            foreach (string t in b)
            {
                if (string.Equals(t, a[j], StringComparison.Ordinal))
                {
                    j++;
                    if (j == a.Length) return true;
                }
            }
            return j == a.Length;
        }

        /// <summary>Titre → tokens normalisés (mots alphanumériques, casse et
        /// accents ignorés — même normalisation que <see cref="NormalizeTitle"/>
        /// appliquée par mot).</summary>
        private static string[] Tokens(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
            var list = new List<string>();
            foreach (string part in Regex.Split(s.Trim(), "[^\\p{L}\\p{N}]+"))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                string n = NormalizeTitle(part);
                if (n.Length > 0) list.Add(n);
            }
            return list.ToArray();
        }

        /// <summary>Garde d'année : accepte si l'une des deux années est
        /// inconnue ou aberrante (&lt; 1900 — « 0 si inconnu » du LLM, gracenote
        /// Year=1 : doctrine « année aberrante = inconnue »), ou si elles
        /// diffèrent d'au plus un an.</summary>
        internal static bool YearCompatible(int? expected, int? actual)
        {
            if (!expected.HasValue || expected.Value < 1900) return true;
            if (!actual.HasValue || actual.Value < 1900) return true;
            return Math.Abs(expected.Value - actual.Value) <= 1;
        }

        /// <summary>Égalité EXACTE des titres normalisés (doctrine « sans
        /// preuve d'année, seule l'égalité exacte du guide↔fiche est une
        /// preuve ») — false si l'un des deux est vide. Sert au discriminant
        /// de la fiche muette dans l'audit : une égalité exacte est une
        /// preuve corroborante, une inclusion seule n'en est pas une.</summary>
        internal static bool TitlesExactlyEqual(string a, string b)
        {
            string na = NormalizeTitle(a);
            string nb = NormalizeTitle(b);
            return na.Length > 0 && string.Equals(na, nb, StringComparison.Ordinal);
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
            {
                // Ligature « œ » dépliée en « oe » : « Coup de cœur » (EPG/UI) et « Coup de coeur »
                // (NFO, noms de dossier) sont le même mot — sans pliage, les clés divergent et les
                // gardes de titre exacte ratent le match. Pliage à sens unique, variantes
                // d'orthographe seulement.
                if (c == 'œ') sb.Append("oe");
                else if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Clé de rapprochement « même œuvre, deux dossiers » :
        /// nettoyage EPG du titre (bruit, parenthèses — donc les suffixes
        /// « (année) » — et dates-marqueurs d'Emby supprimés) puis
        /// normalisation (casse/accents/ponctuation). « Le monde de
        /// Gabrielle Roy (2021) » ≡ « Le monde de Gabrielle Roy » ;
        /// « Les enquêtes de Vera (2011) » ≡ « Les enquêtes de Vera
        /// (2020) ». Sert à l'ancrage frère (SB) et à la garde « fiche
        /// déjà portée » (FicheConflictGateAsync).</summary>
        internal static string SameTitleKey(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            string cleaned = TmdbLookupTool.CleanEpgTitle(s);
            return NormalizeTitle(string.IsNullOrWhiteSpace(cleaned) ? s : cleaned);
        }

        /// <summary>Garde d'année de l'ancrage frère (séries) : un
        /// enregistrement ne peut pas PRÉCÉDER la première diffusion de sa
        /// série — les saisons se diffusent des années après (« Les enquêtes
        /// de Vera (2020) » = la série fichée 2011, « Sissi (2022) » = la
        /// série fichée 2021) — mais une année ANTÉRIEURE à la fiche désigne
        /// une autre œuvre (un enregistrement 1976 ne peut pas être la
        /// fiche 2021). Inconnue/aberrante (&lt; 1900) : pas de garde
        /// (doctrine commune). L'écart d'époque POSTÉRIEUR sans synopsis à
        /// arbitrer (reboot/homonyme) est rejeté séparément dans la porte
        /// d'acceptation.</summary>
        internal static bool AnchorSeriesYearCompatible(int? expected, int? actual)
        {
            if (!expected.HasValue || expected.Value < 1900) return true;
            if (!actual.HasValue || actual.Value < 1900) return true;
            return expected.Value >= actual.Value - 1;
        }

        /// <summary>Porte d'acceptation du retry 04 h ancré (v2.20.17, cas
        /// « De souche » — voir le point de contrôle de ResolvePipelineAsync).
        /// Sans ancre (titre nul/vide) la porte est INACTIVE : comportement
        /// historique inchangé. Avec ancre : clé de titre EXACTE (jamais la
        /// contenance) + année compatible avec l'ancre — une fiche peut dater
        /// d'avant la saison enregistrée (longues séries), mais une fiche
        /// POSTÉRIEURE à l'ancre désigne une autre œuvre. Un titre de fiche
        /// vide ne peut pas prouver la clé → rejet.</summary>
        internal static bool RetryAnchorAccepts(string anchorTitle, int? anchorYear, string ficheTitle, int? ficheYear)
        {
            if (string.IsNullOrWhiteSpace(anchorTitle)) return true;
            string key = SameTitleKey(anchorTitle);
            return key.Length > 0
                && key == SameTitleKey(ficheTitle)
                && AnchorSeriesYearCompatible(anchorYear, ficheYear);
        }

        /// <summary>Titre EPG effectif d'un item : pour une série, le
        /// titre-SÉRIE du guide (<c>SeriesName</c>) prime sur <c>Title</c>
        /// (souvent un titre d'épisode — « Épisode 1 », « Multicolore » —
        /// sur le relais épisode→série du RecordingWatcher), puis sur
        /// <c>item.Name</c>. Films / SeriesName absent : <c>truth.Title</c>
        /// puis <c>item.Name</c>.</summary>
        internal static string ResolveEpgTitle(BaseItem item, EpgTruth truth, EpgTruth deferredTruth = null)
        {
            bool isSeriesItem = item is Series
                || (item != null && item.GetType().Name.IndexOf("Series", StringComparison.OrdinalIgnoreCase) >= 0);
            if (isSeriesItem)
            {
                if (!string.IsNullOrWhiteSpace(truth?.SeriesName)) return truth.SeriesName;
                if (!string.IsNullOrWhiteSpace(deferredTruth?.SeriesName)) return deferredTruth.SeriesName;
            }
            if (!string.IsNullOrWhiteSpace(truth?.Title)) return truth.Title;
            return item?.Name;
        }

        /// <summary>Porte d'acceptation de l'audit des taggés revenus avec des
        /// ids Emby (<see cref="AuditTaggedIdsAsync"/>) : exige la clé de
        /// titre EXACTE (jamais la simple contenance, qui validait « Immigrants
        /// de souche » pour « De souche ») et respecte l'ancre store vivante
        /// (série : clé exacte + année-série compatible ; fiche croisée film :
        /// clé exacte + année compatible).</summary>
        internal static bool AuditTaggedTitleAccepted(string cleanItemName, string anchorSeriesName,
            int? anchorYear, bool isSeries, bool crossRead, string ficheTitle, int? ficheYear)
        {
            if (isSeries && !string.IsNullOrWhiteSpace(anchorSeriesName))
            {
                if (!crossRead)
                    return RetryAnchorAccepts(anchorSeriesName, anchorYear, ficheTitle, ficheYear);
                string aKey = SameTitleKey(anchorSeriesName);
                return aKey.Length > 0
                    && aKey == SameTitleKey(ficheTitle)
                    && YearCompatible(anchorYear, ficheYear);
            }
            string key = SameTitleKey(cleanItemName);
            return key.Length > 0 && key == SameTitleKey(ficheTitle);
        }

        private static readonly Regex s_webSiteSuffixRe = new Regex(
            @"\s*[-|—–]\s*(?:IMDb|The Movie Database(?:\s*\(TMDB\))?|TMDB|TheTVDB(?:\.com)?|TVDB|Wikip[ée]dia|Wikipedia|AlloCin[ée]|SensCritique|Cinoche(?:\.com)?|Mediafilm|Rotten Tomatoes|Letterboxd|TV Guide|Trakt(?:\.tv)?|JustWatch|Apple TV|Prime Video|Netflix|CRAVE|Illico\+?|ICI TOU\.TV|T[ée]l[ée]film|Film|S[ée]rie TV|TV Series|TV Movie).*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex s_webQualifierParenRe = new Regex(
            @"\s*\((?=[^)]*(?:\b(?:19|20)\d{2}\b|TV\s+Series|TV\s+Mini[- ]Series|TV\s+Movie|TV\s+Special|Video|Short|Film|S[ée]rie(?:\s+t[ée]l[ée]vis[ée]e)?|Mini-s[ée]rie|T[ée]l[ée]film|Documentaire))[^)]*\)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static string StripWebTitleDecoration(string webTitle)
        {
            if (string.IsNullOrWhiteSpace(webTitle)) return string.Empty;
            string s = webTitle.Trim();
            for (int i = 0; i < 3; i++)
            {
                string next = s_webSiteSuffixRe.Replace(s, string.Empty).Trim();
                next = s_webQualifierParenRe.Replace(next, string.Empty).Trim();
                if (string.Equals(next, s, StringComparison.Ordinal)) break;
                s = next;
            }
            return s;
        }

        /// <summary>Ancrage lexical strict d'un résultat de recherche web sur le
        /// titre local nettoyé : retire les suffixes de site (IMDb, TMDB…) et
        /// les qualificateurs entre parenthèses (année, type), puis exige la
        /// clé exacte (<see cref="SameTitleKey"/>) — jamais la contenance.</summary>
        internal static bool WebResultMatchesTitle(string webTitle, string cleanGuideTitle)
        {
            string a = SameTitleKey(cleanGuideTitle);
            if (a.Length == 0) return false;
            string b = SameTitleKey(StripWebTitleDecoration(webTitle));
            return b.Length > 0 && string.Equals(a, b, StringComparison.Ordinal);
        }

        internal enum WebJudgeVerdict
        {
            Silence = 0,
            Confirmed = 1,
            Infirmed = 2
        }

        /// <summary>Verdict pur du juge web sur un candidat sans synopsis ni
        /// titre commun : confirme si un résultat web ancré sur le titre local
        /// pointe sur le même tmdbId ; infirme si les résultats ancrés désignent
        /// un autre tmdbId ; silence sinon (révision requise).</summary>
        internal static WebJudgeVerdict EvaluateWebJudgeVerdict(
            int candidateTmdbId,
            IReadOnlyList<int> anchoredTmdbIds,
            out int conflictingTmdbId)
        {
            conflictingTmdbId = 0;
            if (candidateTmdbId <= 0 || anchoredTmdbIds == null || anchoredTmdbIds.Count == 0)
                return WebJudgeVerdict.Silence;

            for (int i = 0; i < anchoredTmdbIds.Count; i++)
            {
                if (anchoredTmdbIds[i] == candidateTmdbId)
                    return WebJudgeVerdict.Confirmed;
            }

            for (int i = 0; i < anchoredTmdbIds.Count; i++)
            {
                if (anchoredTmdbIds[i] > 0 && anchoredTmdbIds[i] != candidateTmdbId)
                {
                    conflictingTmdbId = anchoredTmdbIds[i];
                    return WebJudgeVerdict.Infirmed;
                }
            }
            return WebJudgeVerdict.Silence;
        }

        private static readonly Regex s_embyPathYearRe = new Regex(
            @"\((19\d{2}|20\d{2})\)", RegexOptions.Compiled);

        private static bool IsDiscFolderSegment(string seg) =>
            string.Equals(seg, "VIDEO_TS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(seg, "BDMV", StringComparison.OrdinalIgnoreCase)
            || string.Equals(seg, "AUDIO_TS", StringComparison.OrdinalIgnoreCase);

        private static int? ParseEmbySegmentYear(string segment, int maxYear)
        {
            if (string.IsNullOrWhiteSpace(segment) || segment.EndsWith(":", StringComparison.Ordinal))
                return null;
            var matches = s_embyPathYearRe.Matches(segment);
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                if (int.TryParse(matches[i].Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
                    && y >= 1900 && y <= maxYear)
                {
                    return y;
                }
            }
            return null;
        }

        /// <summary>Déduit l'année <c>(xxxx)</c> depuis le chemin selon les
        /// conventions officielles Emby (Movie-Naming / TV-Naming) : pour une
        /// série, seul le nom du dossier de la série est inspecté (jamais son
        /// parent) ; pour un film, le nom de fichier puis au plus le dossier
        /// parent immédiat (en sautant un dossier <c>VIDEO_TS</c>/<c>BDMV</c>
        /// éventuel, jamais plus haut). Prend la dernière occurrence valide
        /// dans <c>[1900 .. année courante + 2]</c> — gère multi-versions
        /// (<c> - 1080p</c>), stacking (<c>-cd1</c>), ISO (<c>.dvd.iso</c>),
        /// tags <c>[tmdbid=…]</c> et titres avec points (<c>Mr. Robot</c>).</summary>
        internal static int? ExtractEmbyPathYear(string path, bool isSeries)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string[] rawParts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (rawParts.Length == 0) return null;

            int maxYear = DateTime.UtcNow.Year + 2;
            int idx = rawParts.Length - 1;

            if (isSeries)
                return ParseEmbySegmentYear(rawParts[idx], maxYear);

            // Film : nom du fichier (ou dossier si rip disque) d'abord.
            string leaf = rawParts[idx];
            if (IsDiscFolderSegment(leaf))
            {
                idx--;
                if (idx >= 0) return ParseEmbySegmentYear(rawParts[idx], maxYear);
                return null;
            }

            int? fromLeaf = ParseEmbySegmentYear(leaf, maxYear);
            if (fromLeaf.HasValue) return fromLeaf;

            // Repli sur le dossier parent immédiat (et saut d'un sous-dossier
            // VIDEO_TS/BDMV si la feuille était VIDEO_TS.IFO), jamais plus haut.
            int parentIdx = idx - 1;
            if (parentIdx >= 0 && IsDiscFolderSegment(rawParts[parentIdx]))
                parentIdx--;
            if (parentIdx >= 0)
                return ParseEmbySegmentYear(rawParts[parentIdx], maxYear);

            return null;
        }

        private static string MimeFromUrl(string url) =>
            url.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";

        internal static void RemoveLock(BaseItem item, MetadataFields field)
        {
            var lf = item?.LockedFields;
            if (lf == null || lf.Length == 0) return;
            var list = new List<MetadataFields>(lf);
            if (!list.Remove(field)) return;
            item.LockedFields = list.ToArray();
        }

        internal static bool HasLock(BaseItem item, MetadataFields field)
        {
            var lf = item?.LockedFields;
            return lf != null && Array.IndexOf(lf, field) >= 0;
        }

        internal List<BaseItem> FindFicheCarriersPublic(TmdbMeta m, BaseItem self)
            => FindFicheCarriers(m, self);

        internal EpgTruth TryRecoverStoreTruth(BaseItem item, bool isSeries, out string truthSource)
        {
            truthSource = "aucune";
            if (item == null) return null;
            try
            {
                var entries = RecordingValidateStore.Parse();
                if (entries != null && entries.Count > 0 && !string.IsNullOrWhiteSpace(item.Path))
                {
                    if (isSeries)
                    {
                        var e = OrphanIdentifyTask.MostRecentDeferredTruth(entries, item.Path);
                        if (e != null)
                        {
                            truthSource = "recording_validate.json (série différée)";
                            return new EpgTruth
                            {
                                Title = null,
                                SeriesName = e.SeriesName,
                                Overview = e.Overview,
                                Year = e.Year,
                                Channel = e.Channel
                            };
                        }
                    }
                    else
                    {
                        var e = entries
                            .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Path)
                                && string.Equals(x.Path.TrimEnd('\\', '/'), item.Path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(x => x.Updated)
                            .FirstOrDefault();
                        if (e != null)
                        {
                            truthSource = "recording_validate.json";
                            return new EpgTruth
                            {
                                Title = e.Title,
                                SeriesName = e.SeriesName,
                                Overview = e.Overview,
                                Year = e.Year,
                                Channel = e.Channel
                            };
                        }
                    }
                }
            }
            catch { /* tolérant : repli sur l'item */ }

            if (!string.IsNullOrWhiteSpace(item.Overview))
            {
                bool epgOriginLikely = (HasLock(item, MetadataFields.Name) && !HasLock(item, MetadataFields.Overview))
                    || IsOrphanItem(item);
                truthSource = epgOriginLikely ? "item.Overview (EPG probable)" : "item.Overview (fiche existante)";
            }
            return null;
        }

        internal async Task<TmdbMeta> LookupMetaForChatAsync(int tmdbId, string kind, BaseItem item,
            PluginConfiguration cfg, string userTmdb, CancellationToken ct)
        {
            if (tmdbId <= 0) return null;
            string metaLang = ResolveItemMetaTmdb(item, null, cfg, out var prio);
            string effTmdb = metaLang ?? userTmdb;
            return await _tmdb.LookupMetaByIdAsync(tmdbId, kind, effTmdb, ct, prio).ConfigureAwait(false);
        }

        /// <summary>Indice saisi par l'admin dans le chat (v2.23.2) — jamais une
        /// preuve : la fiche référencée est relue sur TMDB et surfacée en tête
        /// comme candidat « indice admin » non corroboré (l'admin tranche sur la
        /// carte). Tous les champs sont optionnels.</summary>
        internal sealed class ChatHint
        {
            public int TmdbId;
            public string ImdbId;
            public string Title;
            public int? Year;
            public bool IsEmpty => TmdbId <= 0 && string.IsNullOrWhiteSpace(ImdbId) && string.IsNullOrWhiteSpace(Title);
        }

        private async Task<List<CandidateVerdict>> ResolveChatHintAsync(ChatHint hint, string kind,
            string[] langs, string effTmdb, IReadOnlyList<string> prio, CancellationToken ct)
        {
            var found = new List<CandidateVerdict>();
            if (hint == null || hint.IsEmpty) return found;
            string primary = langs != null && langs.Length > 0 ? langs[0] : effTmdb;
            string other = string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase) ? "movie" : "series";

            void Add(TmdbMeta m, string how, bool cross)
            {
                if (m == null || m.TmdbId <= 0) return;
                if (found.Any(x => x.Meta.TmdbId == m.TmdbId
                    && string.Equals(x.Meta.Kind, m.Kind, StringComparison.OrdinalIgnoreCase))) return;
                found.Add(new CandidateVerdict
                {
                    Stage = "indice admin",
                    Meta = m,
                    Accepted = false,
                    ProofLevel = "indice",
                    ProofSource = "indice admin (" + how + ")",
                    ProofEvidence = "fiche relue sur TMDB à partir de l'indice saisi par l'admin — non corroborée par la cascade",
                    Reason = "indice admin (" + how + ") non corroboré par la cascade — validation visuelle requise",
                    CrossKind = cross
                });
            }

            try
            {
                if (hint.TmdbId > 0)
                {
                    var m = await _tmdb.LookupMetaByIdAsync(hint.TmdbId, kind, primary, ct, prio).ConfigureAwait(false);
                    if (m != null) Add(m, "tmdb=" + hint.TmdbId, false);
                    else
                    {
                        var mo = await _tmdb.LookupMetaByIdAsync(hint.TmdbId, other, primary, ct, prio).ConfigureAwait(false);
                        Add(mo, "tmdb=" + hint.TmdbId, true);
                    }
                }
                string imdb = hint.ImdbId?.Trim();
                if (!string.IsNullOrWhiteSpace(imdb) && imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                {
                    var m = await _tmdb.FindByExternalIdAsync(imdb, "imdb_id", kind, primary, ct, prio).ConfigureAwait(false);
                    if (m != null) Add(m, imdb, false);
                    else
                    {
                        var mo = await _tmdb.FindByExternalIdAsync(imdb, "imdb_id", other, primary, ct, prio).ConfigureAwait(false);
                        Add(mo, imdb, true);
                    }
                }
                if (!string.IsNullOrWhiteSpace(hint.Title))
                {
                    var m = await _tmdb.LookupMetaMultiLangAsync(hint.Title.Trim(), kind, hint.Year, langs, ct, prio).ConfigureAwait(false);
                    Add(m, "titre « " + hint.Title.Trim() + " »" + (hint.Year.HasValue ? " " + hint.Year.Value : ""), false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] [CHAT] indice admin : lecture TMDB échouée — {0}", ex.ToString());
            }
            _logger?.Info("[LLM_AI] [CHAT] indice admin (tmdb={0}, imdb={1}, titre={2}) → {3} fiche(s) relue(s).",
                hint.TmdbId, hint.ImdbId ?? "—", hint.Title ?? "—", found.Count);
            return found;
        }

        internal async Task<ChatResolveReport> ResolveForChatAsync(BaseItem item, PluginConfiguration cfg,
            string userTmdb, CancellationToken ct, ChatHint hint = null)
        {
            var report = new ChatResolveReport { Item = item };
            if (item == null || IsStrmCard(item))
            {
                report.Status = "skipped";
                return report;
            }

            bool isSeries = item.GetType().Name.IndexOf("Series", StringComparison.OrdinalIgnoreCase) >= 0;
            string kind = isSeries ? "series" : "movie";
            report.IsSeries = isSeries;
            report.Kind = kind;
            if (item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0)
                report.RuntimeMinutes = (int)Math.Round(item.RunTimeTicks.Value / (double)TimeSpan.TicksPerMinute);

            var storeTruth = TryRecoverStoreTruth(item, isSeries, out string truthSource);
            report.TruthSource = truthSource;
            report.TruthYear = storeTruth?.Year;
            report.TruthHasOverview = !string.IsNullOrWhiteSpace(storeTruth?.Overview) || !string.IsNullOrWhiteSpace(item.Overview);

            // Vérité pour la passe principale : store direct pour film,
            // différée pour série (ancre v2.20.17 sur sn + repli film).
            EpgTruth primaryTruth = isSeries ? null : storeTruth;
            EpgTruth deferredTruth = isSeries ? storeTruth : null;

            string metaLang = ResolveItemMetaTmdb(item, primaryTruth, cfg, out var prio);
            string effTmdb = metaLang ?? userTmdb;
            var langs = BuildLangCascade(metaLang, userTmdb, prio);
            bool allowS0 = cfg.OrphanEmbyFirstPass && _providers != null;

            var trace = new PipelineTrace
            {
                CollectMode = true,
                IgnoreExistingItemIds = true
            };
            var meta = await ResolvePipelineAsync(item, primaryTruth, cfg, kind, isSeries,
                allowS0, langs, effTmdb, dry: true, trace, ct,
                deferredTruth: deferredTruth, overviewChain: prio).ConfigureAwait(false);

            var allCandidates = new List<CandidateVerdict>();
            // Indice admin en tête : il n'est jamais une preuve (Accepted=false) ;
            // si la cascade accepte la même fiche, son verdict accepté prime à la dédup.
            allCandidates.AddRange(await ResolveChatHintAsync(hint, kind, langs, effTmdb, prio, ct).ConfigureAwait(false));
            allCandidates.AddRange(trace.Candidates);

            if (meta == null && isSeries)
            {
                var movieTruth = deferredTruth ?? primaryTruth;
                var movieTrace = new PipelineTrace
                {
                    SawCandidates = trace.SawCandidates,
                    CollectMode = true,
                    IgnoreExistingItemIds = true
                };
                meta = await ResolvePipelineAsync(item, movieTruth, cfg, "movie", false,
                    allowS0, langs, effTmdb, dry: true, movieTrace, ct,
                    crossKind: true, overviewChain: prio).ConfigureAwait(false);
                allCandidates.AddRange(movieTrace.Candidates);
                trace = movieTrace;
            }

            // Dédupliquer les candidats par (tmdbId, kind) en conservant
            // prioritairement le verdict accepté puis le premier verdict vu.
            var dedup = new List<CandidateVerdict>();
            foreach (var c in allCandidates)
            {
                if (c?.Meta == null || c.Meta.TmdbId <= 0) continue;
                int existingIdx = dedup.FindIndex(x => x.Meta.TmdbId == c.Meta.TmdbId
                    && string.Equals(x.Meta.Kind, c.Meta.Kind, StringComparison.OrdinalIgnoreCase));
                if (existingIdx < 0)
                    dedup.Add(c);
                else if (c.Accepted && !dedup[existingIdx].Accepted)
                    dedup[existingIdx] = c;
            }
            report.Candidates = dedup.OrderByDescending(c => c.Accepted).ToList();

            if (meta != null && meta.TmdbId > 0)
            {
                report.Winner = meta;
                report.WinningStage = trace.WinningStage;
                report.CrossKind = trace.CrossKind;
                report.WinningProofLevel = trace.WinningProofLevel;
                report.WinningProofSource = trace.WinningProofSource;
                report.WinningProofEvidence = trace.WinningProofEvidence;
                report.ExistingCarriers = FindFicheCarriers(meta, item);

                int.TryParse(SafeProviderId(item, "tmdb"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int curTmdb);
                if (HasTag(item, OrphanIdentifyTask.TagIdentified) && curTmdb == meta.TmdbId && !trace.CrossKind)
                    report.Status = "already_identified";
                else
                    report.Status = "resolvable";
            }
            else
            {
                report.Status = report.Candidates.Count > 0 || trace.SawCandidates
                    ? "needs_review"
                    : "not_found";
            }
            return report;
        }

        internal async Task<string> UndoForChatAsync(BaseItem item, CancellationToken ct)
        {
            if (item == null) return "item introuvable";
            bool isSeries = item.GetType().Name.IndexOf("Series", StringComparison.OrdinalIgnoreCase) >= 0;
            var truth = TryRecoverStoreTruth(item, isSeries, out _);

            var removedIds = new List<string>();
            foreach (var key in new[] { "tmdb", "imdb", "tvdb" })
            {
                var v = SafeProviderId(item, key);
                if (!string.IsNullOrWhiteSpace(v))
                {
                    removedIds.Add(key + "=" + v);
                    item.ProviderIds?.Remove(key);
                }
            }

            var removedTags = new List<string>();
            foreach (var t in new[]
            {
                OrphanIdentifyTask.TagIdentified,
                OrphanIdentifyTask.TagNeedsReview,
                OrphanIdentifyTask.TagNotFound,
                OrphanIdentifyTask.TagCrossKind
            })
            {
                if (HasTag(item, t))
                {
                    removedTags.Add(t);
                    RemoveTag(item, t);
                }
            }

            RemoveLock(item, MetadataFields.Name);
            RemoveLock(item, MetadataFields.Overview);
            RemoveLock(item, MetadataFields.Genres);

            bool restoredEpg = false;
            if (truth != null)
            {
                if (!string.IsNullOrWhiteSpace(truth.Overview))
                {
                    item.Overview = truth.Overview;
                    restoredEpg = true;
                }
                if (!isSeries && truth.Year.HasValue && truth.Year.Value >= 1900)
                    item.ProductionYear = truth.Year;
                string guideTitle = (isSeries && !string.IsNullOrWhiteSpace(truth.SeriesName))
                    ? truth.SeriesName
                    : (!string.IsNullOrWhiteSpace(truth.Title) ? truth.Title : null);
                if (!string.IsNullOrWhiteSpace(guideTitle) && SameTitleKey(item.Name) != SameTitleKey(guideTitle))
                    item.Name = guideTitle;
            }

            item.UpdateToRepository(ItemUpdateType.MetadataEdit);
            await Task.CompletedTask.ConfigureAwait(false);
            return "ids=[" + (removedIds.Count > 0 ? string.Join(", ", removedIds) : "aucun")
                + "], tags=[" + (removedTags.Count > 0 ? string.Join(", ", removedTags) : "aucun") + "]"
                + (restoredEpg ? ", synopsis EPG restauré" : "");
        }

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

        /// <summary>L'item porte-t-il ce tag ? (snapshot avant/après résolution :
        /// le tag croisé est add-only, un gain = fiche croisée fraîchement
        /// appliquée.)</summary>
        internal static bool HasTag(BaseItem item, string tag)
        {
            var t = item?.Tags ?? Array.Empty<string>();
            return Array.IndexOf(t, tag) >= 0;
        }

        // ------------------------------------------------------------------
        //  Notification « fiche croisée » (même pattern que RecordingDiskManager.NotifyTagPass /
        //  LlmScheduledTask) : les items tagués llmai-cross-kind sont filtrables dans Emby ; le
        //  déplacement du fichier dans la bibliothèque de son type reste une action de l'usager (le
        //  plugin ne déplace jamais de média). Description courte et NOMINATIVE ; le lien de la
        //  cloche est un lien profond — fiche Emby de l'œuvre pour un item unique, file «
        //  cross-kind » de la page de configuration (section dépliée par &section=crosskind) sinon.
        // ------------------------------------------------------------------

        /// <summary>Notification cloche « fiche croisée » — nominative :
        /// l'œuvre (ou les œuvres) en cause est nommée, la file
        /// « cross-kind » de la page de configuration porte le reste
        /// (fiche relue, fichiers source, actions copie/conversion).</summary>
        internal static async Task NotifyCrossKindAsync(INotificationManager notifications, IUserManager users,
            PluginConfiguration cfg, IServerApplicationHost host, ILogger logger, IReadOnlyList<BaseItem> items)
        {
            if (notifications == null || users == null || items == null || items.Count == 0) return;

            string langKey = I18n.ResolveDisplayLangKey(host);
            string title = I18n.S("crosskind.notif.title", langKey);
            string tag = OrphanIdentifyTask.TagCrossKind;

            string desc;
            if (items.Count == 1)
            {
                // Item unique : l'œuvre est nommée avec le sens du
                // croisement (« fiche film sur un item série ») — la
                // fiche est du type OPPOSÉ à l'item, c'est le sens du tag.
                var it = items[0];
                bool itemIsSeries = it is Series;
                desc = string.Format(CultureInfo.InvariantCulture,
                    I18n.S("crosskind.notif.desc.one", langKey),
                    it?.Name ?? string.Empty,
                    I18n.S(itemIsSeries ? "rec.kind.movie" : "rec.kind.series", langKey),
                    I18n.S(itemIsSeries ? "rec.kind.series" : "rec.kind.movie", langKey),
                    tag);
            }
            else
            {
                // Plusieurs : jusqu'à 3 titres nommés, le surplus en
                // abrégé — la file « cross-kind » reste l'inventaire
                // complet (fiches, fichiers, actions).
                var names = new List<string>();
                for (int i = 0; i < items.Count && i < 3; i++)
                {
                    string n = items[i]?.Name;
                    if (!string.IsNullOrWhiteSpace(n)) names.Add("« " + n + " »");
                }
                string titleList = string.Join(", ", names);
                if (items.Count > 3)
                    titleList += string.Format(CultureInfo.InvariantCulture,
                        I18n.S("crosskind.notif.more", langKey), items.Count - 3);
                desc = string.Format(CultureInfo.InvariantCulture,
                    I18n.S("crosskind.notif.desc.many", langKey), items.Count, titleList, tag);
            }

            string url = await ResolveCrossKindUrlAsync(cfg, host, items, logger).ConfigureAwait(false);

            // Le lien voyage AUSSI dans la description : le consommateur du canal de lecture (ex.
            // Webhooks) rend Title+Description mais IGNORE le champ Url — une URL nue en fin de
            // description est affichée par tout canal qui rend la description. Le champ Url reste
            // posé pour les clients qui l'honorent. LF seul : les payloads webhook/JSON n'ont pas à
            // trimballer de CR.
            if (!string.IsNullOrWhiteSpace(url))
                desc = desc + "\n" + url;

            // Administrateurs uniquement : la file « cross-kind » vit sur la
            // page de configuration du plugin (route admin-only du routeur
            // web Emby) — un non-admin recevrait une cloche dont le guidage
            // ET le lien profond lui sont inaccessibles.
            List<MediaBrowser.Controller.Entities.User> list;
            try
            {
                list = users.GetUserList(new UserQuery())
                    .Where(u => u != null && (u.Policy?.IsAdministrator ?? false))
                    .ToList();
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] OrphanIdentify : lecture des usagers pour notification échouée ({0}).", ex.Message);
                return;
            }

            int sent = 0;
            foreach (var u in list)
            {
                try
                {
                    notifications.SendNotification(new NotificationRequest
                    {
                        Title = title,
                        Description = desc,
                        Url = url,
                        Date = DateTimeOffset.UtcNow,
                        Severity = LogSeverity.Info,
                        User = u
                    });
                    sent++;
                }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] OrphanIdentify : notification à « {0} » échouée ({1}).", u.Name, ex.Message);
                }
            }
            if (sent > 0)
            {
                logger?.Info("[LLM_AI] OrphanIdentify : notification fiche croisée ({0} œuvre(s)) envoyée à {1}/{2} administrateur(s) — lien : {3}.",
                    items.Count, sent, list.Count,
                    string.IsNullOrWhiteSpace(url) ? "(aucun)" : url);
            }
            else
            {
                logger?.Warn("[LLM_AI] OrphanIdentify : notification fiche croisée ({0} œuvre(s)) NON envoyée — aucun administrateur.",
                    items.Count);
            }
        }

        /// <summary>Lien profond de la cloche « fiche croisée » : fiche Emby
        /// de l'œuvre pour un item unique (id currency UI =
        /// <c>InternalId</c>, même gabarit que les liens du chat et les
        /// cartes .strm), sinon la file « cross-kind » de la page de
        /// configuration — le paramètre <c>section=crosskind</c> du hash y
        /// déplie la section (config.js). Fail-open : file de configuration
        /// si le <c>serverId</c> manque, URL de base seule si elle est
        /// vide (aucun lien plus fiable à fabriquer).</summary>
        private static async Task<string> ResolveCrossKindUrlAsync(
            PluginConfiguration cfg, IServerApplicationHost host, IReadOnlyList<BaseItem> items, ILogger logger)
        {
            // Base : URL publique configurée prioritaire (liens joignables
            // depuis les clients externes), sinon URL locale du serveur —
            // même priorité que LlmScheduledTask.ResolveEmbyUrl.
            string baseUrl = (cfg?.EmbyPublicUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                try { baseUrl = host == null ? string.Empty : (host.GetLocalHostApiUrl() ?? string.Empty); }
                catch { baseUrl = string.Empty; }
            }
            if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;
            baseUrl = baseUrl.TrimEnd('/');

            // serverId : requis par le routeur web de la fiche item (même
            // doctrine que StrmLibraryGenerator.EpgLink — jamais de lien
            // fabriqué sans lui). GetPublicSystemInfo est le chemin fiable
            // (GetSystemInfo lève une NRE sur certains hôtes Windows).
            string serverId = null;
            try
            {
                var pub = host == null
                    ? null
                    : await host.GetPublicSystemInfo(CancellationToken.None).ConfigureAwait(false);
                serverId = pub?.Id;
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] CrossKind : serverId indisponible ({0}) — repli sur la file de configuration.", ex.Message);
            }

            // Item unique → la fiche Emby de l'œuvre (le vidéo en cause).
            if (items.Count == 1 && items[0] != null && !string.IsNullOrWhiteSpace(serverId))
                return string.Format(CultureInfo.InvariantCulture,
                    "{0}/web/index.html#!/item?id={1}&serverId={2}",
                    baseUrl,
                    items[0].InternalId.ToString(CultureInfo.InvariantCulture),
                    Uri.EscapeDataString(serverId));

            // Sinon → la file « cross-kind » de la page de configuration.
            string url = baseUrl + "/web/index.html#!/configurationpage?name=LLMAIConfigPage&section=crosskind";
            if (!string.IsNullOrWhiteSpace(serverId))
                url += "&serverId=" + Uri.EscapeDataString(serverId);
            return url;
        }
    }
}