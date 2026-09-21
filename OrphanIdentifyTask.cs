using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Tâche planifiée quotidienne (04 h) qui <b>identifie les enregistrements
    /// DVR orphelins</b> : ceux qu'Emby n'a pas réussi à identifier (aucun id
    /// IMDb/TMDB/TVDB — souvent des titres québécois absents du catalogue
    /// TMDB/TVDB, qui utilise plutôt les titres de France). L'usager corrige
    /// aujourd'hui ces cas à la main (recherche web → id IMDb) puis verrouille
    /// les champs ; cette tâche automatise la démarche.
    /// </summary>
    /// <remarks>
    /// <para>La logique de résolution par item vit dans
    /// <see cref="OrphanResolver"/> (partagée avec le
    /// <see cref="RecordingWatcher"/>, qui l'appelle à la fin de chaque
    /// enregistrement avec la vérité EPG figée) : S0 recherche native Emby
    /// (option <c>OrphanEmbyFirstPass</c>), S1 nettoyage + recherche TMDB
    /// multilingue, S2 proposition LLM validée par TMDB, S3 recherche web
    /// SearXNG → id IMDb. Porte d'acceptation commune : année compatible +
    /// (garde lexicale sur les voies par titre) + juge sémantique LLM dès que
    /// les deux synopsis existent. Repli de type : une série dont la chaîne
    /// échoue est rejouée en kind movie (film importé comme série par
    /// Emby — la fiche est alors appliquée à l'item, dont le type est
    /// conservé).</para>
    /// <para><b>Application non destructive</b> : on ne remplit que les ids
    /// provider absents, un <c>Overview</c> vide, des <c>Genres</c> vides, et un
    /// poster <c>Primary</c> manquant. <b>Le <c>Name</c> EPG n'est jamais
    /// modifié</b> — il est verrouillé (<c>MetadataFields.Name</c>) pour
    /// préserver le titre d'origine (réutilisé plus tard pour scanner l'EPG à la
    /// recherche de nouveaux programmes). Les champs qu'on remplit sont
    /// également verrouillés (add-only — on ne retire jamais un verrou
    /// existant), reflétant la pratique manuelle de l'usager.</para>
    /// <para><b>Idempotence</b> via tags : <c>llmai-identified</c> (résolu) ou
    /// <c>llmai-needs-review</c> (irrésolu) — les items déjà tagués sont
    /// ignorés au passage suivant. <b>Dry-run</b> : <c>OrphanIdentifyDryRun</c>
    /// = aucune mutation, log détaillé de la résolution proposée. Best-effort :
    /// un item en erreur n'interrompt jamais le passage.</para>
    /// <para><b>Périmètre</b> : items de bibliothèque (films et séries)
    /// issus d'enregistrements DVR — une fois l'enregistrement terminé, Emby
    /// place l'item dans une bibliothèque, où il vit comme un <c>Movie</c>/<c>Series</c>
    /// normal. Les cartes <c>.strm</c> sont exclues. Découverte via
    /// <see cref="ILibraryManager.GetItemList(InternalItemsQuery)"/>.</para>
    /// </remarks>
    public class OrphanIdentifyTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly IServerApplicationHost _host;
        private readonly ILibraryManager _library;
        private readonly OrphanResolver _resolver;

        /// <summary>Tag posé sur un orphelin résolu (id écrit + champs verrouillés).</summary>
        public const string TagIdentified = "llmai-identified";
        /// <summary>Tag posé sur un orphelin qu'aucun stage n'a pu résoudre ALORS
        /// QUE des candidats existaient (rejetés par la porte) — à revérifier à
        /// la main ; retraitable par le passe nocturne si le retry est activé.</summary>
        public const string TagNeedsReview = "llmai-needs-review";
        /// <summary>Tag posé sur un orphelin dont le titre est absent de TOUTES
        /// les banques (aucun candidat vu à S0/S1/S2/S3) — état terminal : rien
        /// à réviser, la fiche EPG est la meilleure métadonnée disponible.
        /// GELÉ pour la passe nocturne ; réactivable en retirant le tag.</summary>
        public const string TagNotFound = "llmai-not-found";
        /// <summary>Tag posé (add-only) quand une fiche du type opposé à
        /// l'item a été acceptée — film sur un item série, ou l'inverse. Emby
        /// type l'import DVR d'après le guide : le type de l'item n'est jamais
        /// modifié, seule la fiche (ids/métadonnées/poster) porte l'œuvre
        /// correcte. Ce tag SURVIT à la confirmation du besoin-revue
        /// (contrairement à <see cref="TagNeedsReview"/>) : il sert à
        /// retrouver ces items pour replacer éventuellement le fichier dans
        /// la bibliothèque de son type (déplacement manuel — Emby ré-importe
        /// alors l'œuvre avec le bon type et le plugin la ré-identifie).</summary>
        public const string TagCrossKind = "llmai-cross-kind";

        public OrphanIdentifyTask(
            ILogger logger,
            IJsonSerializer jsonSerializer,
            ILibraryManager library,
            IUserManager users,
            ILiveTvManager liveTv,
            IServerApplicationHost host,
            IProviderManager providers)
        {
            _logger = logger;
            _host = host;
            _library = library;
            _resolver = new OrphanResolver(logger, jsonSerializer, library, users, liveTv, host, providers);
        }

        public string Name => I18n.S("task.orphan.name", I18n.ResolveDisplayLangKey(_host));

        /// <summary>Identifiant stable de la tâche (GUID dédié).</summary>
        public string Key => "f4a1c2b3-9900-4a8e-bb12-0a1b2c3d4e5f";

        public string Description => I18n.S("task.orphan.desc", I18n.ResolveDisplayLangKey(_host));

        public string Category => I18n.S("task.category", I18n.ResolveDisplayLangKey(_host));

        public bool IsHidden => false;

        public bool IsEnabled => true;

        public bool IsLogged => true;

        /// <summary>Déclencheur par défaut : quotidien à 04 h (après le nettoyage 03 h).</summary>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = "DailyTrigger",
                TimeOfDayTicks = new TimeSpan(4, 0, 0).Ticks
            };
        }

        // ------------------------------------------------------------------
        //  Exécution
        // ------------------------------------------------------------------

        public async Task Execute(CancellationToken ct, IProgress<double> progress)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null || !cfg.OrphanIdentifyEnabled)
            {
                _logger?.Info("[LLM_AI] OrphanIdentify : tâche désactivée (OrphanIdentifyEnabled=false) — passage ignoré.");
                return;
            }

            bool dry = cfg.OrphanIdentifyDryRun;
            if (string.IsNullOrWhiteSpace(cfg.TmdbApiKey))
            {
                _logger?.Warn("[LLM_AI] OrphanIdentify : clé API TMDB absente — S1/S2 impossibles, passage annulé.");
                return;
            }

            // Langue TMDB de l'usager (ResponseLanguage → langue d'affichage → legacy → en-US).
            string userTmdb = I18n.ToTmdbLang(I18n.ResolveMetaLangKey(cfg, _host));

            bool verbose = cfg.DebugVerbose;
            _logger?.Info("[LLM_AI] OrphanIdentify : démarrage ({0}{1}).",
                dry ? "DRY-RUN" : "application", verbose ? ", verbose" : "");

            // Les enregistrements DVR terminés sont importés par Emby dans une
            // bibliothèque (Movies/Series) — or GetRecordings ne retourne que les
            // enregistrements actifs/à venir, PAS les enregistrements complétés (qui
            // deviennent des items bibliothèque normaux). On scanne donc les items
            // Movie/Series de la bibliothèque et on retient les orphelins (aucun id
            // provider IMDb/TMDB/TVDB = identification Emby échouée). Les cartes
            // .strm de la bibliothèque ai_suggestions sont exclues (extension .strm).
            BaseItem[] items;
            try
            {
                items = _library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Movie", "Series" },
                    Recursive = true,
                    EnableTotalRecordCount = false
                }) ?? Array.Empty<BaseItem>();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] OrphanIdentify : GetItemList a échoué — passage annulé.", ex, ex.Message);
                return;
            }

            int scanned = 0, orphans = 0, resolved = 0, review = 0, notFound = 0, skipped = 0, errors = 0;
            int crossKind = 0;
            int total = items.Length;
            int idx = 0;

            _logger?.Info("[LLM_AI] OrphanIdentify : {0} item(s) Movie/Series en bibliothèque.", total);

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                idx++;
                try { progress?.Report((double)idx / total * 100.0); } catch { /* best-effort */ }
                scanned++;

                // Snapshot du tag croisé AVANT traitement : le tag est
                // add-only — un gain = fiche croisée fraîchement appliquée
                // (la notification ne compte que les NOUVEAUX, jamais les
                // déjà taggés retraités).
                bool hadCross = OrphanResolver.HasTag(item, OrphanIdentifyTask.TagCrossKind);

                OrphanResolver.Status st;
                try
                {
                    // Passe nocturne : vérité EPG absente (comportement historique).
                    st = await _resolver.ResolveItemAsync(item, null, cfg, dry, userTmdb, verbose, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    errors++;
                    _logger?.Warn("[LLM_AI] OrphanIdentify : erreur sur « {0} » ({1}) — item ignoré.",
                        item?.Name, ex.Message);
                    continue;
                }

                if (!hadCross && OrphanResolver.HasTag(item, OrphanIdentifyTask.TagCrossKind)) crossKind++;

                switch (st)
                {
                    case OrphanResolver.Status.Skipped: skipped++; break;
                    // Un item résolu, en needs-review ou introuvable était par
                    // construction un orphelin.
                    case OrphanResolver.Status.OrphanResolved: orphans++; resolved++; break;
                    case OrphanResolver.Status.NeedsReview: orphans++; review++; break;
                    case OrphanResolver.Status.NotFound: orphans++; notFound++; break;
                }
            }

            _logger?.Info(
                "[LLM_AI] OrphanIdentify : terminé. items={0} orphelins={1} résolus={2} needs-review={3} introuvables={4} ignorés(non-orphelin/.strm/déjà taggé)={5} erreurs={6} fiches-croisées-nouvelles={7} ({8}).",
                scanned, orphans, resolved, review, notFound, skipped, errors, crossKind, dry ? "DRY-RUN" : "application");

            // Notification (pattern des tags disque) : uniquement les fiches
            // croisées NOUVELLES de cette passe — le compte est nul sinon.
            if (crossKind > 0)
            {
                OrphanResolver.NotifyCrossKind(
                    _host.TryResolve<INotificationManager>(),
                    _host.TryResolve<IUserManager>(),
                    cfg, _host, _logger, crossKind);
            }
        }
    }
}