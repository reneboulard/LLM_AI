using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;

namespace LLM_AI
{
    /// <summary>
    /// Génération partagée de la section « À regarder ce soir » : construit le
    /// profil de goût de l'usager, les enregistrements récents non visionnés et
    /// la réserve bibliothèque, lance le run agent LLM (via
    /// <see cref="LlmRunner"/>), enrichit, et met en cache le résultat par
    /// usager. Centralise cette logique pour qu'elle soit réutilisée par
    /// l'endpoint HTTP (<c>TonightApiService</c>) ET par le déclencheur de login
    /// (<c>TonightLoginService</c>) — un seul run par usager par fenêtre de
    /// cache, même sur plusieurs appareils.
    /// <para>Le cache par usager est <b>statique</b> (partagé entre toutes les
    /// instances du service) : l'endpoint, le login et la page web voient le
    /// même cache. TTL = <see cref="PluginConfiguration.TonightCacheHours"/>.
    /// </para>
    /// </summary>
    internal class TonightService
    {
        private readonly IUserManager _users;
        private readonly ILibraryManager _library;
        private readonly ILiveTvManager _liveTv;
        private readonly IJsonSerializer _json;
        private readonly IServerApplicationHost _host;
        private readonly ILogger _logger;
        private readonly ICollectionManager _collections;
        private readonly IPlaylistManager _playlists;
        private readonly IUserDataManager _userData;

        public TonightService(IUserManager users, ILibraryManager library,
            ILiveTvManager liveTv, IJsonSerializer json, IServerApplicationHost host,
            ILogger logger, ICollectionManager collections,
            IPlaylistManager playlists, IUserDataManager userData)
        {
            _users = users;
            _library = library;
            _liveTv = liveTv;
            _json = json;
            _host = host;
            _logger = logger;
            _collections = collections;
            _playlists = playlists;
            _userData = userData;
        }

        // Workflow injecté dans le system prompt de l'agent (bloc
        // « WORKFLOW DE RECOMMANDATION ») pour le run « ce soir ».
        internal const string TONIGHT_WORKFLOW =
            "SECTION « CE SOIR » (personnalisée par usager) : tu croises TROIS sources pour " +
            "recommander ce que l'usager pourrait regarder CE SOIR :\n" +
            " 1) Son profil de goût (historique de visionnage récent) — fourni dans le message.\n" +
            " 2) L'EPG de ce soir : appelle get_emby_info avec action=\"epg_tonight\" (fenêtre " +
            "temporelle bornée par la config, séries ET films). Programmes à regarder EN DIRECT " +
            "ou à enregistrer.\n" +
            " 3) Ses enregistrements récents NON visionnés — fournis dans le message (films/épisodes " +
            "enregistrés ces derniers jours mais pas encore regardés). Ce sont des candidats de " +
            "choix immédiat : déjà enregistrés, prêts à regarder. Si l'usager suit une série et " +
            "qu'un nouvel épisode enregistré de cette série est non visionné, remonte-le en priorité.\n" +
            " 4) Les séries « prêtes à dévorer » — SI le message contient une section « SÉRIES " +
            "PRÊTES À DÉVORER » : ce sont des séries dont l'enregistrement est actif et dont " +
            "l'usager accumule volontairement les épisodes non visionnés avant de commencer. " +
            "Recommande-en AU PLUS UNE par run, comme « il est temps de commencer » : " +
            "source=\"recording\" si la série figure aussi dans les enregistrements non visionnés " +
            "(reprends alors l'id de cette liste), sinon source=\"library\" en reprenant l'id " +
            "fourni tel quel (l'UI proposera « Regarder ») ; mentionne le nombre d'épisodes en " +
            "attente dans la raison. C'est un rappel opportuniste, indépendant de la contrainte " +
            "de minimum — pas un remplissage de sélection.\n" +
            "Pour chaque recommandation, positionne kind=\"series\" ou kind=\"movie\" (series → timer " +
            "série, movie/one-off → timer unique) et priority high/medium/low. Ajoute un champ " +
            "source : \"live\" (programme EPG du soir — à regarder en direct ou à enregistrer) ou " +
            "\"recording\" (enregistrement disponible — à regarder maintenant, DÉJÀ enregistré, ne " +
            "pas re-programmer).\n" +
            "Les items epg_tonight portent is_series/is_movie : positionne kind en conséquence. " +
            "is_scheduled=true signifie qu'un timer existe déjà : recommande « à regarder en direct » " +
            "plutôt qu'un nouvel enregistrement. Les enregistrements non visionnés portent un champ " +
            "id (l'identifiant Emby de l'enregistrement) : pour source=\"recording\", reprends ce id " +
            "tel quel dans la recommandation (l'UI proposera « Regarder »).\n" +
            "GARANTIS AU MOINS le nombre minimum demandé de recommandations (voir le message). Si " +
            "l'EPG du soir + les enregistrements non visionnés produisent MOINS que ce minimum, " +
            "complète avec des titres de la RÉSERVE BIBLIOTHÈQUE (items de la bibliothèque de l'usager " +
            "non encore visionnés, listés dans le message) qui matchent son profil de goût, en " +
            "positionnant source=\"library\" et en reprenant leur id tel quel (l'UI proposera " +
            "« Regarder » — lecture depuis la bibliothèque). N'utilise cette réserve QUE pour " +
            "atteindre le minimum, pas pour gonfler la sélection au-delà.\n" +
            "Reprends title/channel/start tels quels depuis epg_tonight pour source=\"live\" (permet " +
            "la programmation). Tu peux enrichir via tmdb_lookup/web_search si c'est utile, mais " +
            "reste pratique et rapide — l'objectif est une courte sélection personnalisée pour ce " +
            "soir, pas un audit exhaustif. Retourne un tableau JSON " +
            "[{title, kind, reason, priority, source, channel, start, id, year, showbizz_match}] " +
            "(id pour source=\"recording\"/\"library\" ; channel/start pour source=\"live\" ; " +
            "year = année de production reprise du champ year d'epg_tonight/tmdb_lookup si " +
            "présent, ne l'invente jamais).";

        // ------------------------------------------------------------------
        //  Résultat de génération
        // ------------------------------------------------------------------

        /// <summary>
        /// Résultat d'un run « ce soir ». <see cref="Payload"/> est le tableau
        /// JSON de recommandations (chaîne, éventuellement vide). <see cref="Date"/>
        /// : date/heure (UTC ISO) de production. <see cref="FromCache"/> : true si
        /// servi depuis le cache (pas de run LLM). <see cref="Error"/> : message
        /// d'erreur (null si succès).
        /// </summary>
        public struct TonightResult
        {
            public string Payload;
            public string Date;
            public bool FromCache;
            public string Error;
            /// <summary>Origin « chat » : run déclenché par le tool
            /// <c>run_tonight_run</c> du chat (badge sur la page
            /// Recommandations).</summary>
            public bool ViaChat;
            /// <summary>Directives de session du run chat (éphémères —
            /// badge informatif). Vide pour un run normal.</summary>
            public string ChatDirectives;
            /// <summary>Note non fatale (v1.13.15.0) : policy parentale du
            /// compte restrictive (limite de cote / liste blanche de tags /
            /// BlockUnratedItems) et moins de recos que le minimum demandé —
            /// l'usager reçoit une explication au lieu d'une liste vide sans
            /// comprendre. Null si aucune note.</summary>
            public string Warning;
        }

        // Gate anti-spam « prêt à dévorer » en attente de persistance :
        // posé par BuildBingeReadySeries, consommé par GenerateTonightAsync
        // SEULEMENT si le run LLM réussit — le signalement a alors bien été
        // livré au modèle. Un run raté ne consomme pas le one-shot : la série
        // sera re-proposée au prochain run.
        private PluginConfiguration _pendingBingeCfg;
        private Dictionary<string, int> _pendingBingeMap;

        // ------------------------------------------------------------------
        //  Cache par usager (in-memory, statique, partagé ; TTL = TonightCacheHours)
        // ------------------------------------------------------------------

        private class CacheEntry
        {
            public string Items;
            public string Date;
            public DateTimeOffset ExpiresAt;
            /// <summary>Origin « chat » (badge) — cf. TonightResult.ViaChat.</summary>
            public bool ViaChat;
            public string ChatDirectives;
            /// <summary>Note parentale — cf. TonightResult.Warning.</summary>
            public string Warning;
        }

        private static readonly Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>();
        private static readonly object _cacheLock = new object();

        /// <summary>
        /// Renvoie le résultat caché pour un usager S'il est encore frais (sans
        /// relancer le LLM). Retourne null si pas de cache ou cache expiré.
        /// Utilisé par le déclencheur de login pour distinguer le chemin
        /// « cache frais → toast immédiat, pas de run » du chemin
        /// « cache froid → run LLM puis toast ».
        /// </summary>
        internal static string TryGetCached(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return null;
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(userId, out var e) && e.ExpiresAt > DateTimeOffset.UtcNow)
                    return e.Items;
            }
            return null;
        }

        /// <summary>
        /// Variante complète de <see cref="TryGetCached"/> pour l'endpoint
        /// HTTP : renvoie le résultat caché avec ses métadonnées (date,
        /// origin chat — badge de la page Recommandations). Retourne null si
        /// pas de cache ou cache expiré.
        /// </summary>
        internal static TonightResult? TryGetCachedResult(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return null;
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(userId, out var e) && e.ExpiresAt > DateTimeOffset.UtcNow)
                    return new TonightResult
                    {
                        Payload = e.Items,
                        Date = e.Date,
                        FromCache = true,
                        ViaChat = e.ViaChat,
                        ChatDirectives = e.ChatDirectives,
                        Warning = e.Warning
                    };
            }
            return null;
        }

        /// <summary>
        /// Invalide le cache d'un usager (force le prochain appel à relancer le
        /// LLM). Utilisé par le login après auto-programmation si l'on veut
        /// rafraîchir. Optionnel — l'endpoint utilise <c>Refresh=1</c>.
        /// </summary>
        internal static void InvalidateUser(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return;
            lock (_cacheLock) { _cache.Remove(userId); }
        }

        // ------------------------------------------------------------------
        //  Génération (run LLM + cache)
        // ------------------------------------------------------------------

        /// <summary>
        /// Produit les recommandations « ce soir » pour un usager. Gère le cache
        /// (sauf <paramref name="refresh"/>) : un résultat frais est renvoyé
        /// immédiatement sans relancer le LLM. Sinon : construit le profil de
        /// goût + les enregistrements non visionnés + la réserve bibliothèque,
        /// lance le run agent, enrichit, met en cache. L'appelant décide quoi
        /// faire du payload (l'afficher, l'auto-programmer, envoyer un toast).
        /// Ne vérifie PAS <see cref="PluginConfiguration.TonightEnabled"/> —
        /// c'est à l'appelant (endpoint / login) de le faire selon son contexte.
        /// </summary>
        public async Task<TonightResult> GenerateTonightAsync(User user, PluginConfiguration cfg,
            bool refresh, CancellationToken ct,
            string sessionDirectives = null, bool fromChat = false)
        {
            if (cfg == null)
                return new TonightResult { Error = "Configuration du plugin indisponible." };
            if (user == null)
                return new TonightResult { Error = "Utilisateur non résolu." };

            // Cache par usager (sauf Refresh).
            int cacheHours = Math.Max(0, cfg.TonightCacheHours);
            string cacheKey = user.Id.ToString();
            if (!refresh && cacheHours > 0)
            {
                lock (_cacheLock)
                {
                    if (_cache.TryGetValue(cacheKey, out var e) && e.ExpiresAt > DateTimeOffset.UtcNow)
                        return new TonightResult { Payload = e.Items, Date = e.Date, FromCache = true };
                }
            }

            var runner = new LlmRunner(_logger, _json, _library, _users, _liveTv, _host);

            // Mode compact : si le backend LLM principal est un Ollama LOCAL
            // (modèle souvent petit, fenêtre de contexte limitée), on réduit les
            // données injectées (profil, enregistrements, réserve) pour éviter
            // de « noyer » le modèle. Sur cloud (ollama_cloud / gemini), on
            // garde toutes les données (qualité optimale).
            bool compact = IsLocalPrimary(cfg);
            if (compact) _logger?.Info("[LLM_AI] Tonight : backend local détecté — mode compact (données injectées réduites).");

            // Racine de la bibliothèque .strm (recommendations à enregistrer,
            // surface du record-bucket) à EXCLURE des sondes bibliothèque
            // envoyées au LLM (profil de goût + réserve). Sans cela, les cartes
            // .strm reviennent dans le profil (lu → marqué visionné) et dans la
            // réserve (non visionné → candidat source="library") : décision
            // circulaire (« recommande d'enregistrer X » → plus tard « recommande
            // de regarder X ce soir depuis la bibliothèque »). Null si la
            // bibliothèque .strm n'est pas configurée/trouvée → pas d'exclusion.
            string excludedStrmRoot = StrmLibraryGenerator.ResolveLibraryRoot(
                _library, Plugin.Instance?.Configuration?.StrmLibraryName, _logger);
            if (!string.IsNullOrWhiteSpace(excludedStrmRoot))
                _logger?.Info("[LLM_AI] Tonight : bibliothèque .strm exclue du LLM (anti-circulaire) : {0}", excludedStrmRoot);

            // 1) Profil de goût : historique de visionnage récent de l'usager.
            string profile = BuildTasteProfile(user, compact, excludedStrmRoot);

            // 2) Enregistrements récents non visionnés (candidats « à regarder
            //    ce soir » — déjà enregistrés, prêts à lire). Injectés comme le
            //    profil : l'usager est résolu ici (le tool get_emby_info, lui,
            //    est global/sans usager).
            string recs = BuildUnwatchedRecordings(user, cfg, ct, compact);

            // 2b) Réserve bibliothèque (items non visionnés) : utilisée par le
            //    LLM uniquement si EPG + enregistrements < TonightMinRecommendations.
            string reserve = BuildLibraryFallbackPool(user, compact, excludedStrmRoot);

            // 2c) Séries « prêtes à dévorer » (opt-in, gate anti-spam persistant) :
            //    séries dont l'enregistrement est actif et dont le stock d'épisodes
            //    non visionnés vient de franchir le seuil — signalées UNE fois par
            //    cycle d'accumulation (re-armées quand l'usager commence à regarder).
            string binge = cfg.TonightBingeEnabled
                ? BuildBingeReadySeries(user, cfg, compact, excludedStrmRoot)
                : string.Empty;

            // 2d) Watched-guard : index per-usager du contenu DÉJÀ VISIONNÉ
            //     (épisodes joués par série + films joués). Sert à MARQUER les
            //     recos live « déjà visionnées » (rediffusion EPG) pendant la
            //     validation — l'UI affiche la carte avec badge « Déjà visionné »
            //     et masque les actions, l'auto-programmation ne crée pas de
            //     timer, les popups la sautent. Null si indisponible (fail-open :
            //     recos non marquées, jamais droppées pour autant).
            WatchedIndex watchedIdx = BuildWatchedIndex(user, excludedStrmRoot);

            // 2e) Injection mémoire : la fiche mémoire réflexive (opt-in,
            //     Phase C) a priorité — quand elle existe, elle REMPLACE la
            //     directive de la boucle de rétroaction classique (repli
            //     fail-open sur cette dernière si la fiche est vide/absente).
            string feedback = MemoryCard.BuildInjectionBlock(cfg);
            if (string.IsNullOrEmpty(feedback) && cfg.RecoFeedbackEnabled)
                feedback = RecoFeedback.BuildTonightBlock(cfg, user.Id.ToString());

            // 3) Prompt personnalisé = template config + profil + enregistrements
            //    + réserve (+ binge + directive) + contrainte de minimum dynamique.
            int minRec = Math.Max(0, cfg.TonightMinRecommendations);
            string prompt = (cfg.TonightPrompt ?? string.Empty).Trim()
                + "\n\n" + profile + recs + reserve + binge + feedback;

            // Directives de session (chat, tool run_tonight_run) : one-shot,
            // injectées dans LE prompt de ce run uniquement — jamais
            // persistées dans la config. Plafonnées à 500 caractères par le
            // tool (ChatActions) ; garde-fou ici aussi (fail-safe).
            if (!string.IsNullOrWhiteSpace(sessionDirectives))
            {
                var sd = sessionDirectives.Trim();
                if (sd.Length > 500) sd = sd.Substring(0, 500);
                prompt += "\n\n### DIRECTIVES DE SESSION (chat — valables pour ce run uniquement)\n" + sd;
            }

            prompt += "\n\n### CONTRAINTE DE SÉLECTION\n"
                + $"Garantis AU MOINS {minRec} recommandation(s). Si l'EPG du soir + les "
                + "enregistrements non visionnés en produisent moins, complète avec la RÉSERVE "
                + "BIBLIOTHÈQUE ci-dessus (source=\"library\", reprends id). Ne dépasse pas le "
                + "minimum avec la réserve — l'EPG et les enregistrements restent prioritaires.";

            // Gate droit TV en direct (v1.13.12.0) : si l'usager ne porte pas
            // EnableLiveTvAccess, l'EPG n'est PAS consulté (tools epg_* → vide
            // légitime, snapshot de validation sauté) — dit au LLM en amont pour
            // qu'il réoriente sans tâtonner (filet double avec la note des tools).
            bool canLive = PermissionGate.CanWatchLive(user);
            if (!canLive)
            {
                prompt += "\n\n### CONTRAINTE D'ACCÈS (droits de l'usager)\n"
                    + "La TV en direct n'est pas accessible pour cet usager : l'EPG renverra vide "
                    + "(note « Live TV non accessible »). Ne recommande QUE depuis les enregistrements "
                    + "non visionnés (source=\"recording\") et la réserve bibliothèque "
                    + "(source=\"library\") — aucune reco source=\"live\".";
            }

            // Gate parental (v1.13.15.0) : si l'usager porte une limite
            // MaxParentalRating, des tags bloqués/autorisés ou
            // BlockUnratedItems, dit-le au LLM EN AMONT (pattern canLive :
            // le dire, puis l'imposer mécaniquement dans ValidateAndFilter).
            // Null si aucune règle parentale — aucun bloc injecté.
            string parentalPrompt = PermissionGate.DescribeForPrompt(user);
            if (parentalPrompt != null)
                prompt += parentalPrompt;

            // 4) Run agent (boucle de tool-calling) — même logique que la tâche
            //    planifiée : backends, outils, enrichissement (match titres →
            //    id/channel_id/rating/image_url) gérés par LlmRunner.
            // Mémoire réflexive (Phase A) : le runId relie les décisions
            // journalisées au pool de candidats capturé pendant le run
            // (epg_tonight par le tool get_emby_info, réserve + enregistrements
            // par les builders ci-dessus). Best-effort, opt-in.
            string runId = (fromChat ? "c" : "t") + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                + "-" + (user.Id.ToString("N").Length >= 6 ? user.Id.ToString("N").Substring(0, 6) : user.Id.ToString("N"));
            if (cfg.DecisionLogEnabled)
                DecisionStore.BeginRun(runId);
            var (payload, ok) = await runner.RunAsync(cfg, "TONIGHT", prompt, TONIGHT_WORKFLOW, ct, user).ConfigureAwait(false);

            if (!ok || string.IsNullOrWhiteSpace(payload))
            {
                if (cfg.DecisionLogEnabled) DecisionStore.EndRun(runId); // purge
                return new TonightResult { Error = "Le run LLM n'a pas produit de recommandation." };
            }

            // Le run a réussi : consomme le gate « prêt à dévorer » en attente
            // (le signalement a été livré au LLM). Sur échec (return ci-dessus),
            // le gate n'est PAS persisté — la série binge sera re-proposée au
            // prochain run au lieu d'avoir brûlé son one-shot anti-spam.
            if (_pendingBingeCfg != null && _pendingBingeMap != null)
            {
                PersistBingeNotified(_pendingBingeCfg, _pendingBingeMap);
                _pendingBingeCfg = null;
                _pendingBingeMap = null;
            }

            // Enrichissement bibliothèque : pour les reco source="live" dont le
            // titre est déjà possédé, injecte library_id (bouton « Regarder »
            // depuis la bibliothèque + signal owned-guard pour AutoProgrammer).
            // S'exécute après l'enrichissement EPG.
            payload = runner.EnrichWithLibrary(payload);

            // Validation d'existence : on vérifie que chaque recommandation
            // pointe vers un item réel (EPG non expiré / item bibliothèque non
            // supprimé / id non halluciné). Drop les introuvables ; marque
            // « Diffusé » (aired=true) les programmes EPG déjà terminés et
            // « Déjà visionné » (watched=true) les rediffusions que l'usager a
            // déjà vues (gardées, mais sans actions obsolètes côté UI).
            // Fail-open : une erreur de requête transitoire ne vide jamais les
            // recos.
            payload = ValidateAndFilter(payload, watchedIdx, ct, canLive, user);
            if (string.IsNullOrWhiteSpace(payload))
            {
                if (cfg.DecisionLogEnabled) DecisionStore.EndRun(runId); // purge
                return new TonightResult { Error = "Toutes les recommandations pointaient vers des items introuvables (EPG expiré ou items supprimés)." };
            }

            // Note « contrôle parental restrictif » (v1.13.15.0) : si la policy
            // parentale du compte est active et que la validation a laissé moins
            // de recos que le minimum demandé au LLM, l'usager reçoit une
            // EXPLICATION (TonightResult.Warning) au lieu d'une liste courte
            // sans comprendre — le cas dégénéré « liste blanche de tags » ne
            // peut littéralement rien recommander de visible (validé 2026-09-12 :
            // 3782 films → 1, guide EPG → 0).
            string warning = null;
            if (PermissionGate.HasParentalRestrictions(user))
            {
                int recCount = AutoProgrammer.ParseRecommendations(payload)
                    .Count(r => !string.IsNullOrWhiteSpace(r.Title));
                if (recCount < minRec)
                    warning = $"Contrôle parental : {recCount} recommandation(s) seulement (minimum demandé : {minRec}). "
                        + "La policy de ce compte (limite de cote, tags ou liste blanche de tags) limite le contenu visible — "
                        + "Emby cachera au compte tout ce qui est recommandé au-delà.";
            }

            // Surface native des recos du watch bucket sur un run FRAIS (pas sur
            // cache) : deux mécanismes indépendants et opt-in, réutilisant les
            // mêmes ids collectés une fois ci-dessous (parseur/dedup
            // d'AutoProgrammer) :
            //  - étiquetage par tag « AI Tonight » (filtre par tag dans Emby) ;
            //  - collection Emby « AI Tonight » (collection navigable, non
            //    destructive — regroupe les items par référence).
            // Le nettoyage quotidien (AiTonightCleanupTask, 3 h) retire le genre
            // ET vide la collection ; les runs suivants reconstruisent l'un et/ou
            // l'autre selon les flags.
            HashSet<string> watchBucketIds = null;
            if (cfg.TonightGenreTagEnabled || cfg.TonightCollectionEnabled ||
                cfg.TonightPlaylistEnabled || cfg.TonightFavoritesEnabled)
            {
                try
                {
                    var recos = AutoProgrammer.ParseRecommendations(payload);
                    watchBucketIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in recos)
                    {
                        if (AutoProgrammer.IsWatchBucket(r) && !string.IsNullOrEmpty(r.Id))
                            watchBucketIds.Add(r.Id);          // source=recording / library
                        if (!string.IsNullOrEmpty(r.LibraryId))
                            watchBucketIds.Add(r.LibraryId);   // owned item (live-but-owned aussi)
                    }
                }
                catch (Exception ex) { _logger?.Warn("[LLM_AI] Tonight surface : collecte ids échouée : {0}", ex.Message); }
            }

            if (cfg.TonightGenreTagEnabled && watchBucketIds != null)
            {
                try
                {
                    await AiTagger.AddAsync(_library, _logger, watchBucketIds, AiTagger.TonightTag, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) { _logger?.Warn("[LLM_AI] Tonight genre tag : {0}", ex.Message); }
            }

            if (cfg.TonightCollectionEnabled && watchBucketIds != null)
            {
                try
                {
                    await AiTonightCollectionManager.EnsureAsync(_collections, _library, _logger, _host, watchBucketIds, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) { _logger?.Warn("[LLM_AI] Tonight collection : {0}", ex.Message); }
            }

            // Surfaces « personnelles » (playlist + favoris éphémères) : opt-in,
            // mêmes ids du watch bucket, pour l'usager « Tonight » de la config.
            User tonightUser = null;
            if (cfg.TonightPlaylistEnabled || cfg.TonightFavoritesEnabled)
            {
                tonightUser = ResolveTonightUser(_users, cfg);
                if (tonightUser == null)
                    _logger?.Warn("[LLM_AI] Tonight surfaces personnelles : aucun usager résolu (TonightUserName={0}) — ignorées.",
                        cfg.TonightUserName);
            }

            if (cfg.TonightPlaylistEnabled && watchBucketIds != null && tonightUser != null)
            {
                try
                {
                    await AiTonightPlaylistManager.EnsureAsync(_playlists, _library, _logger, watchBucketIds, tonightUser, _host, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) { _logger?.Warn("[LLM_AI] Tonight playlist : {0}", ex.Message); }
            }

            if (cfg.TonightFavoritesEnabled && watchBucketIds != null && tonightUser != null)
            {
                try
                {
                    AiTonightFavoritesManager.Apply(_userData, _library, _logger, watchBucketIds, tonightUser, ct);
                }
                catch (Exception ex) { _logger?.Warn("[LLM_AI] Tonight favoris : {0}", ex.Message); }
            }

            string date = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            // Mise en cache du résultat.
            if (cacheHours > 0)
            {
                lock (_cacheLock)
                {
                    _cache[cacheKey] = new CacheEntry
                    {
                        Items = payload,
                        Date = date,
                        ExpiresAt = DateTimeOffset.UtcNow.AddHours(cacheHours),
                        ViaChat = fromChat,
                        ChatDirectives = fromChat ? (sessionDirectives ?? "").Trim() : null,
                        Warning = warning
                    };
                }
            }

            // Journalisation du run pour la boucle de rétroaction hebdo
            // (opt-in) : chaque reco devient une entrée du journal
            // (kind=tonight) que RecoAnalysisTask rapprochera des
            // visionnages réels de l'usager. Best-effort : un échec de
            // journalisation ne casse jamais le run.
            //
            // Mémoire réflexive (Phase A, opt-in DecisionLogEnabled) : en
            // PLUS du journal RecoLog (conservé pour l'analyse hebdo
            // actuelle), chaque reco est journalisée avec sa raison et sa
            // priorité (DecisionEntry), et le pool de candidats du run est
            // persisté (RunPool) — la matière première de la calibration
            // (reviser une croyance, pas un titre) et du diagnostic de
            // classement (écarté vs choisi). Double écriture transitoire :
            // la Phase C retire RecoLog.
            if (cfg.DecisionLogEnabled)
            {
                try
                {
                    // Ferme le run : drain des candidats capturés pendant le
                    // run (epg_tonight + réserve + enregistrements).
                    var poolCands = DecisionStore.EndRun(runId);

                    var decisions = new List<DecisionEntry>();
                    var recos = AutoProgrammer.ParseRecommendations(payload);
                    foreach (var r in recos)
                    {
                        if (string.IsNullOrWhiteSpace(r.Title)) continue;
                        var isLive = string.Equals(r.Source, "live", StringComparison.OrdinalIgnoreCase);
                        decisions.Add(new DecisionEntry
                        {
                            RunId = runId,
                            Kind = "tonight",
                            User = user.Id.ToString(),
                            Date = DateTimeOffset.UtcNow,
                            Title = r.Title,
                            // item : InternalId bibliothèque/enregistrement ;
                            // programme EPG pour un reco live (Guid, OK dans
                            // nos propres fichiers JSON — jamais en REST).
                            ItemId = isLive ? (r.LibraryId ?? "") : (r.LibraryId ?? r.Id ?? ""),
                            ProgramId = isLive ? (r.Id ?? "") : "",
                            Source = r.Source ?? "",
                            Reason = r.Reason ?? "",
                            Priority = r.Priority ?? "",
                            Mv = 0 // fiche mémoire : Phase C
                        });
                    }
                    if (decisions.Count > 0)
                        DecisionStore.AppendDecisions(cfg, decisions, _logger);
                    DecisionStore.SavePool(cfg, new RunPool
                    {
                        RunId = runId,
                        User = user.Id.ToString(),
                        Date = DateTimeOffset.UtcNow,
                        Mv = 0,
                        Candidates = poolCands
                    }, _logger);
                    _logger?.Info("[LLM_AI] Décisions : run {0} — {1} reco(s) journalisée(s), pool {2} candidat(s).",
                        runId, decisions.Count, poolCands.Count);
                }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] Décisions : journalisation du run échouée : {0}", ex.Message);
                    DecisionStore.EndRun(runId); // purge du contexte statique
                }
            }
            else if (DecisionStore.ActiveRunId != null)
            {
                DecisionStore.EndRun(runId); // run démarré avant un toggle off
            }

            if (cfg.RecoFeedbackEnabled)
            {
                try
                {
                    var entries = new List<RecoLogEntry>();
                    foreach (var r in AutoProgrammer.ParseRecommendations(payload))
                    {
                        if (string.IsNullOrWhiteSpace(r.Title)) continue;
                        entries.Add(new RecoLogEntry
                        {
                            User = user.Id.ToString(),
                            Kind = "tonight",
                            Title = r.Title,
                            Source = r.Source ?? "",
                            Id = r.Id ?? "",
                            Date = DateTimeOffset.UtcNow
                        });
                    }
                    if (entries.Count > 0)
                        RecoFeedback.AppendLog(cfg, entries, _logger);
                }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] Rétroaction : journalisation du run échouée : {0}", ex.Message);
                }
            }

            return new TonightResult
            {
                Payload = payload,
                Date = date,
                FromCache = false,
                ViaChat = fromChat,
                ChatDirectives = fromChat ? (sessionDirectives ?? "").Trim() : null,
                Warning = warning
            };
        }

        // ------------------------------------------------------------------
        //  Validation d'existence des recommandations
        //  (drop introuvables, marque « Diffusé » les programmes EPG terminés)
        // ------------------------------------------------------------------

        /// <summary>
        /// Valide que chaque recommandation du payload pointe vers un item réel,
        /// puis filtre/marque :
        /// <list type="bullet">
        /// <item><c>source="live"</c> : l'id (ProgramId EPG) doit figurer dans un
        ///   snapshot de l'EPG (programmes des dernières 24 h jusqu'à la fin de
        ///   la fenêtre « ce soir »). Si le programme a déjà fini
        ///   (<c>EndDate &lt;= now</c>) → on garde la reco mais on pose
        ///   <c>aired=true</c> (l'UI marque « Diffusé » et masque Programmer /
        ///   Regarder en direct). Si l'épisode/le film correspond à un contenu
        ///   que l'usager a déjà visionné (<paramref name="watchedIdx"/>) → on
        ///   pose aussi <c>watched=true</c> (rediffusion : badge « Déjà visionné »,
        ///   pas de timer, exclu des popups). Sinon on injecte <c>end</c> (date
        ///   de fin autoritaire). Id absent du snapshot → drop (EPG expiré ou id
        ///   halluciné).</item>
        /// <item><c>source="recording"/"library"</c> : l'id (InternalId Emby,
        ///   la forme DTO/REST — cf. <see cref="ItemIdResolver"/>) doit résoudre
        ///   un BaseItem via <see cref="ILibraryManager.GetItemList"/>. Un id
        ///   Guid hérité qui résout via <c>GetItemById</c> est normalisé en
        ///   InternalId avant le check (le Guid n'est pas consommable côté
        ///   REST/UI). Introuvable sous aucune forme → drop (item supprimé /
        ///   id halluciné).</item>
        /// <item>source absente/autre : gardé tel quel (recos hors flux « ce
        ///   soir », non concernées par la validation EPG).</item>
        /// </list>
        /// <b>Fail-open</b> : toute erreur transitoire (EPG/library indispo,
        ///   parse JSON, index watched indispo) renvoie le payload original
        ///   inchangé — on ne vide jamais les recos sur un échec de requête.
        ///   Ne lève pas.
        /// <para>Gate droit TV en direct (v1.13.12.0) : <paramref name="canLive"/>
        /// = <see cref="PermissionGate.CanWatchLive"/> de l'usager du run. Sans le
        /// droit, PAS de snapshot EPG (donnée à laquelle l'usager n'a pas droit) et
        /// les recos <c>source="live"</c> pures sont droppées (compteur
        /// <c>liveNotPermitted</c>) — conservées seulement si enrichies d'un
        /// <c>library_id</c> (watchables depuis la bibliothèque). Avec le droit, la
        /// logique fail-open existante est inchangée.</para>
        /// </summary>
        private string ValidateAndFilter(string payload, WatchedIndex watchedIdx, CancellationToken ct,
            bool canLive = true, User user = null)
        {
            if (string.IsNullOrWhiteSpace(payload)) return payload;

            JsonArray arr;
            try
            {
                if (JsonNode.Parse(payload) is JsonArray a) arr = a;
                else return payload; // Markdown libre / objet unique : rien à valider
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Tonight validation : parse JSON échoué ({0}) — payload conservé.", ex.Message);
                return payload;
            }

            try
            {
                var now = DateTimeOffset.Now;
                DateTimeOffset maxStart = TonightWindowEnd();

                // Snapshot EPG : programmes des dernières 24 h jusqu'à la fin de
                // la fenêtre « ce soir ». Couvre les programmes récemment
                // diffusés (détection « Diffusé », gère la fraîcheur du cache
                // jusqu'à ~24 h) + ceux à venir ce soir. Une seule requête.
                // Gate droit TV en direct (v1.13.12.0) : PAS de snapshot si
                // l'usager ne porte pas EnableLiveTvAccess — l'EPG est une donnée
                // à laquelle il n'a pas droit ; les recos live pures sont
                // droppées plus bas (liveNotPermitted), celles enrichies
                // library_id (watchables depuis la bibliothèque) sont conservées.
                Dictionary<string, BaseItemDto> epg;
                if (!canLive)
                {
                    _logger?.Info("[LLM_AI] Tonight validation : pas de droit TV en direct (usager) — pas de snapshot EPG, recos live pures droppées.");
                    epg = null;
                }
                else try
                {
                    epg = new Dictionary<string, BaseItemDto>(StringComparer.OrdinalIgnoreCase);
                    var q = new InternalItemsQuery
                    {
                        MinStartDate = now.AddDays(-1),
                        MaxStartDate = maxStart,
                        Limit = 2000
                    };
                    var programs = (_liveTv.GetPrograms(q)?.Items) ?? Array.Empty<BaseItemDto>();
                    if (programs.Length == 0)
                    {
                        // Même bug de build que epg_tonight (cf. GetEmbyInfoTool) :
                        // GetPrograms n'honore pas MinStartDate/MaxStartDate ici —
                        // la fenêtre retourne 0 programme, le snapshot serait VIDE
                        // et la validation dropperait TOUTES les recos « live »
                        // comme « hors-snapshot » (vécu 2026-09-01 : 4/4 enrichies
                        // puis 4/4 supprimées). Requête sans fenêtre — ni
                        // HasAired=false : le snapshot couvre aussi les 24
                        // dernières heures pour la détection « Diffusé » — puis
                        // filtre StartDate en C#.
                        var fq = new InternalItemsQuery { };
                        var pool = (_liveTv.GetPrograms(fq)?.Items) ?? Array.Empty<BaseItemDto>();
                        programs = pool
                            .Where(p => p.StartDate.HasValue
                                && p.StartDate >= now.AddDays(-1) && p.StartDate <= maxStart)
                            .OrderBy(p => p.StartDate)
                            .ToArray();
                        _logger?.Info("[LLM_AI] Tonight validation : fenêtre SQL 0 résultat → fallback mémoire {0} programme(s) dans la fenêtre (pool brut {1}).",
                            programs.Length, pool.Length);
                    }
                    foreach (var p in programs)
                    {
                        if (p == null || string.IsNullOrEmpty(p.Id)) continue;
                        epg[p.Id] = p;
                    }
                    _logger?.Info("[LLM_AI] Tonight validation : snapshot EPG {0} programme(s).", epg.Count);
                }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] Tonight validation : snapshot EPG indispo ({0}) — recos live conservées non validées.", ex.Message);
                    epg = null; // fail-open ciblé : on garde les live telles quelles
                }

                // Normalisation préalable des ids Guid hérités/déformés : un
                // reco dont l'id n'est pas un long mais résout quand même via
                // GetItemById(Guid) est réécrit en InternalId — le Guid n'est
                // pas consommable par la couche REST/UI (bouton « Regarder »)
                // ni par le batch ItemIds ci-dessous. Les ids non résolvables
                // restent tels quels : ils seront drop plus bas.
                foreach (var node in arr)
                {
                    if (!(node is JsonObject obj)) continue;
                    string src = ObjStr(obj, "source");
                    if (!string.Equals(src, "recording", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(src, "library", StringComparison.OrdinalIgnoreCase)) continue;
                    string id = ObjStr(obj, "id");
                    if (string.IsNullOrEmpty(id) || long.TryParse(id, out _)) continue;
                    var resolved = ItemIdResolver.Resolve(_library, id);
                    if (resolved != null)
                        obj["id"] = resolved.InternalId.ToString();
                }

                // Lookup bibliothèque (batché) pour source="recording"/"library"
                // ET les recos live enrichies d'un library_id (le contenu
                // réellement watchable est l'item bibliothèque — le verdict
                // parental porte sur LUI).
                var libIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var node in arr)
                {
                    if (!(node is JsonObject obj)) continue;
                    string src = ObjStr(obj, "source");
                    if (string.Equals(src, "recording", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(src, "library", StringComparison.OrdinalIgnoreCase))
                    {
                        string id = ObjStr(obj, "id");
                        if (!string.IsNullOrEmpty(id)) libIds.Add(id);
                    }
                    else if (string.Equals(src, "live", StringComparison.OrdinalIgnoreCase))
                    {
                        string libId = ObjStr(obj, "library_id");
                        if (!string.IsNullOrEmpty(libId)) libIds.Add(libId);
                    }
                }
                var libFound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Items réellement chargés (verdict parental : cote, tags) —
                // vide en fail-open (verdict parental alors sauté).
                var libById = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
                if (libIds.Count > 0)
                {
                    // Les id des recos recording/library sont des InternalId
                    // (long) sérialisés en chaîne — la forme DTO/REST d'Emby
                    // (BaseItem.Id, lui, est un Guid NON consommable par la
                    // couche REST ; cf. ItemIdResolver).
                    var longIds = new List<long>(libIds.Count);
                    foreach (var s in libIds)
                        if (long.TryParse(s, out var lid)) longIds.Add(lid);
                    if (longIds.Count > 0)
                    {
                        try
                        {
                            var lq = new InternalItemsQuery
                            {
                                ItemIds = longIds.ToArray(),
                                Limit = longIds.Count
                            };
                            var items = _library.GetItemList(lq) ?? Array.Empty<BaseItem>();
                            foreach (var it in items)
                            {
                                if (it == null) continue;
                                string key = it.InternalId.ToString();
                                libFound.Add(key);
                                libById[key] = it;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.Warn("[LLM_AI] Tonight validation : lookup bibliothèque indispo ({0}) — recos recording/library conservées non validées.", ex.Message);
                            foreach (var s in libIds) libFound.Add(s); // fail-open ciblé
                        }
                    }
                }

                // Gate parental (v1.13.15.0) : verdicts par reco (cote, tags,
                // BlockUnratedItems) — la policy est lue une fois par run ;
                // l'ILocalizationManager (cotes EPG textuelles) est résolu une
                // seule fois. Fail-open : policy illisible → aucun drop.
                var loc = _host?.TryResolve<MediaBrowser.Model.Globalization.ILocalizationManager>();
                bool parentalActive = PermissionGate.HasParentalRestrictions(user);

                int kept = 0, dropped = 0, epgExpired = 0, libMissing = 0, watchedMarked = 0, liveNotPermitted = 0,
                    parentalDropped = 0, epgUnrecognized = 0;
                for (int i = arr.Count - 1; i >= 0; i--)
                {
                    if (!(arr[i] is JsonObject obj)) { kept++; continue; }
                    string src = ObjStr(obj, "source");
                    string id = ObjStr(obj, "id");

                    if (string.Equals(src, "live", StringComparison.OrdinalIgnoreCase))
                    {
                        if (epg == null)
                        {
                            // Usager sans droit TV en direct (gate v1.13.12.0) :
                            // une reco live pure n'est pas watchable ni
                            // programmable pour lui → drop ; conservée seulement
                            // si enrichie d'un library_id (watchable depuis la
                            // bibliothèque). Hors gate : fail-open standard
                            // (EPG indispo → recos live conservées).
                            if (!canLive && string.IsNullOrWhiteSpace(ObjStr(obj, "library_id")))
                            { arr.RemoveAt(i); dropped++; liveNotPermitted++; continue; }
                            // Live-but-owned conservé via library_id : le contenu
                            // watchable est l'item bibliothèque → verdict parental
                            // sur LUI (le programme EPG n'est pas regardable).
                            if (parentalActive && !string.IsNullOrWhiteSpace(ObjStr(obj, "library_id"))
                                && libById.TryGetValue(ObjStr(obj, "library_id"), out var ownedItem))
                            {
                                var v = PermissionGate.IsParentallyAllowed(user, ownedItem);
                                if (v != PermissionGate.ParentalVerdict.Allowed)
                                { arr.RemoveAt(i); dropped++; parentalDropped++; continue; }
                            }
                            kept++; continue;
                        }
                        if (epg.TryGetValue(id ?? "", out var p))
                        {
                            // Gate parental (v1.13.15.0) : verdict du programme
                            // EPG (cote textuelle via mapper/table serveur,
                            // tags du programme). Cote non reconnue → conservé
                            // (native-blind, compté à part). Fail-open :
                            // policy illisible → aucune drop parentale.
                            if (parentalActive && p != null)
                            {
                                var v = PermissionGate.IsEpgAllowed(user, loc, p.OfficialRating,
                                    p.Tags ?? Array.Empty<string>(), out bool unrec);
                                if (unrec) epgUnrecognized++;
                                if (v == PermissionGate.ParentalVerdict.BlockedRating
                                    || v == PermissionGate.ParentalVerdict.BlockedTag
                                    || v == PermissionGate.ParentalVerdict.BlockedUnrated)
                                { arr.RemoveAt(i); dropped++; parentalDropped++; continue; }
                            }
                            // Watched-guard : rediffusion d'un épisode/film déjà
                            // visionné par l'usager → MARQUÉ, pas droppé (le
                            // marquage ne retire pas la reco de la sélection :
                            // le minimum de recos reste garanti ; c'est l'UI, les
                            // popups et l'auto-programmation qui la traitent
                            // comme non actionnable).
                            if (watchedIdx != null && IsWatchedProgram(p, watchedIdx))
                            {
                                obj["watched"] = true;
                                watchedMarked++;
                            }
                            if (p.EndDate.HasValue && p.EndDate.Value <= now)
                                obj["aired"] = true;   // Diffusé : gardé, marqué
                            else if (p.EndDate.HasValue)
                                obj["end"] = p.EndDate.Value.ToString("o", CultureInfo.InvariantCulture);
                            kept++;
                        }
                        else { arr.RemoveAt(i); dropped++; epgExpired++; }
                    }
                    else if (string.Equals(src, "recording", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(src, "library", StringComparison.OrdinalIgnoreCase))
                    {
                        if (libFound.Contains(id ?? ""))
                        {
                            // Gate parental (v1.13.15.0) : verdict de l'item
                            // bibliothèque (cote héritée, tags item/série,
                            // BlockUnratedItems). Item non chargé (fail-open
                            // du lookup) → verdict parental sauté.
                            if (parentalActive && libById.TryGetValue(id ?? "", out var item))
                            {
                                var v = PermissionGate.IsParentallyAllowed(user, item);
                                if (v != PermissionGate.ParentalVerdict.Allowed)
                                { arr.RemoveAt(i); dropped++; parentalDropped++; continue; }
                            }
                            kept++;
                        }
                        else { arr.RemoveAt(i); dropped++; libMissing++; }
                    }
                    else
                    {
                        kept++; // source absente/autre : non concernée
                    }
                }

                _logger?.Info("[LLM_AI] Tonight validation : {0} gardée(s), {1} supprimée(s) (EPG expirés/hors-snapshot : {2}, items bibli. introuvables : {3}, live sans droit TV : {4}, contrôle parental : {5}), rediffusions déjà visionnées marquées : {6}, cotes EPG non reconnues conservées : {7}.",
                    kept, dropped, epgExpired, libMissing, liveNotPermitted, parentalDropped, watchedMarked, epgUnrecognized);

                return arr.ToJsonString();
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Tonight validation : échec global ({0}) — payload conservé.", ex.Message);
                return payload;
            }
        }

        // ------------------------------------------------------------------
        //  Watched-guard (rediffusions déjà visionnées)
        // ------------------------------------------------------------------

        /// <summary>
        /// Index per-usager du contenu déjà visionné, pour marquer les recos
        /// live « déjà visionnées » (rediffusion EPG d'un épisode/film vu).
        /// Séries indexées par <see cref="LlmRunner.NormTitle"/> du nom de
        /// série — clés d'épisodes « s{S}e{E} » (même convention que
        /// <c>AiBadgeEnhancer</c>) + noms d'épisodes normalisés (repli quand
        /// l'EPG ne numérote pas la diffusion) ; films par titre normalisé.
        /// </summary>
        private class WatchedIndex
        {
            /// <summary>Clé série → clés « s{S}e{E} » des épisodes joués.</summary>
            public readonly Dictionary<string, HashSet<string>> SeriesEpisodes =
                new(StringComparer.OrdinalIgnoreCase);

            /// <summary>Clé série → noms d'épisodes joués normalisés.</summary>
            public readonly Dictionary<string, HashSet<string>> SeriesEpisodeTitles =
                new(StringComparer.OrdinalIgnoreCase);

            /// <summary>Titres de films joués normalisés.</summary>
            public readonly HashSet<string> MovieTitles = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Résout l'usager des surfaces Tonight « personnelles » (playlist +
        /// favoris) : nom de la config
        /// (<see cref="PluginConfiguration.TonightUserName"/>) via
        /// <see cref="IUserManager.GetUserByName"/> + repli insensible à la
        /// casse (pattern <c>GetEmbyInfoTool.ResolveUser</c>) ; nom vide →
        /// premier usager admin, sinon premier usager. Best-effort : null si
        /// aucun usager résoluble.
        /// </summary>
        internal static User ResolveTonightUser(IUserManager users, PluginConfiguration cfg)
        {
            try
            {
                var all = (users.GetUserList(new UserQuery()) ?? Array.Empty<User>())
                    .Where(u => u != null).ToArray();
                if (all.Length == 0) return null;

                var name = cfg?.TonightUserName;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var byName = users.GetUserByName(name);
                    if (byName != null) return byName;
                    var ci = all.FirstOrDefault(u =>
                        string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (ci != null) return ci;
                }

                return all.FirstOrDefault(u => u.Policy?.IsAdministrator ?? false)
                    ?? all.FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>
        /// Construit l'index du contenu déjà visionné de l'usager (épisodes ET
        /// films joués, bibliothèque .strm exclue — ses cartes de test sont
        /// « jouées » à l'activation et pollueraient l'index). Fenêtre limitée
        /// aux 300 épisodes + 200 films les plus récemment joués : le
        /// watched-guard n'a pas besoin de l'historique complet, seulement du
        /// passé assez frais pour qu'une rediffusion puisse y figurer.
        /// Échec de requête → null (fail-open : recos non marquées).
        /// </summary>
        private WatchedIndex BuildWatchedIndex(User user, string excludedRoot)
        {
            if (user == null) return null;
            var idx = new WatchedIndex();
            try
            {
                var eq = new InternalItemsQuery
                {
                    User = user,
                    IsPlayed = true,
                    Recursive = true,
                    IncludeItemTypes = new[] { "Episode" },
                    OrderBy = new[] { ("DatePlayed", SortOrder.Descending) },
                    Limit = 300,
                    EnableTotalRecordCount = false
                };
                foreach (var it in _library.GetItemList(eq) ?? Array.Empty<BaseItem>())
                {
                    // SeriesName n'existe que sur Episode (pas BaseItem) — même
                    // cast que le profil de goût / le builder binge.
                    var episode = it as MediaBrowser.Controller.Entities.TV.Episode;
                    if (episode == null) continue;
                    if (IsUnderPath(it.Path, excludedRoot)) continue;
                    string skey = LlmRunner.NormTitle(episode.SeriesName);
                    if (string.IsNullOrEmpty(skey)) continue;
                    if (it.ParentIndexNumber.HasValue && it.IndexNumber.HasValue)
                    {
                        if (!idx.SeriesEpisodes.TryGetValue(skey, out var ses))
                            idx.SeriesEpisodes[skey] = ses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        ses.Add("s" + it.ParentIndexNumber.Value + "e" + it.IndexNumber.Value);
                    }
                    var et = LlmRunner.NormTitle(it.Name);
                    if (!string.IsNullOrEmpty(et))
                    {
                        if (!idx.SeriesEpisodeTitles.TryGetValue(skey, out var ts))
                            idx.SeriesEpisodeTitles[skey] = ts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        ts.Add(et);
                    }
                }

                var mq = new InternalItemsQuery
                {
                    User = user,
                    IsPlayed = true,
                    Recursive = true,
                    IncludeItemTypes = new[] { "Movie" },
                    OrderBy = new[] { ("DatePlayed", SortOrder.Descending) },
                    Limit = 200,
                    EnableTotalRecordCount = false
                };
                foreach (var it in _library.GetItemList(mq) ?? Array.Empty<BaseItem>())
                {
                    if (it == null) continue;
                    if (IsUnderPath(it.Path, excludedRoot)) continue;
                    var mkey = LlmRunner.NormTitle(it.Name);
                    if (!string.IsNullOrEmpty(mkey)) idx.MovieTitles.Add(mkey);
                }

                _logger?.Info("[LLM_AI] Watched-guard : index de « {0} » — {1} série(s) visionnée(s), {2} film(s).",
                    user.Name, idx.SeriesEpisodes.Count, idx.MovieTitles.Count);
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Watched-guard : index indisponible ({0}) — recos live non marquées (fail-open).", ex.Message);
                return null;
            }
            return idx;
        }

        /// <summary>
        /// Vrai si le programme EPG <paramref name="p"/> correspond à un contenu
        /// que l'usager a déjà visionné (watched-guard) : film par titre
        /// normalisé, épisode par clé « s{S}e{E} » — repli sur le nom d'épisode
        /// normalisé quand l'EPG ne numérote pas la diffusion. Programme sans
        /// type connu (ni série ni film) ou sans correspondance → false
        /// (fail-open : jamais marqué sur un doute).
        /// </summary>
        private static bool IsWatchedProgram(BaseItemDto p, WatchedIndex idx)
        {
            if (idx == null || p == null) return false;
            if (p.IsMovie == true)
            {
                var mkey = LlmRunner.NormTitle(p.Name);
                return !string.IsNullOrEmpty(mkey) && idx.MovieTitles.Contains(mkey);
            }
            if (p.IsSeries == true)
            {
                string skey = LlmRunner.NormTitle(p.SeriesName);
                if (string.IsNullOrEmpty(skey)) return false;
                if (p.ParentIndexNumber.HasValue && p.IndexNumber.HasValue
                    && idx.SeriesEpisodes.TryGetValue(skey, out var ses)
                    && ses.Contains("s" + p.ParentIndexNumber.Value + "e" + p.IndexNumber.Value))
                    return true;
                var et = LlmRunner.NormTitle(p.EpisodeTitle);
                return !string.IsNullOrEmpty(et)
                    && idx.SeriesEpisodeTitles.TryGetValue(skey, out var ts)
                    && ts.Contains(et);
            }
            return false;
        }

        /// <summary>
        /// Fin de la fenêtre « ce soir » (heure locale), répliquée depuis
        /// <c>GetEmbyInfoTool.EpgTonight</c> : <see cref="PluginConfiguration.TonightWindowEnd"/>,
        /// défaut 23:59, reportée au lendemain si l'heure de fin est antérieure
        /// à maintenant. Borner le snapshot EPG de validation à « ce soir ».
        /// </summary>
        private DateTimeOffset TonightWindowEnd()
        {
            var now = DateTimeOffset.Now;
            var today = now.Date;
            var endStr = Plugin.Instance?.Configuration?.TonightWindowEnd;
            if (string.IsNullOrWhiteSpace(endStr)) endStr = "23:59";
            if (TryParseHHmm(endStr, out var et))
            {
                var d = today.Add(et);
                if (d < now.LocalDateTime) d = d.AddDays(1);
                return new DateTimeOffset(d, now.Offset);
            }
            return new DateTimeOffset(today.AddHours(23).AddMinutes(59), now.Offset);
        }

        /// <summary>Parse "HH:mm" (24 h) vers TimeSpan. Tolérant aux espaces.</summary>
        private static bool TryParseHHmm(string s, out TimeSpan value)
        {
            value = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var parts = s.Trim().Split(':');
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return false;
            if (h < 0 || h > 23 || m < 0 || m > 59) return false;
            value = new TimeSpan(h, m, 0);
            return true;
        }

        /// <summary>Lit une propriété string d'un JsonObject (null si absente/non-string).</summary>
        private static string ObjStr(JsonObject obj, string key)
        {
            if (obj.TryGetPropertyValue(key, out var v) && v is JsonValue jv
                && jv.TryGetValue<string>(out var s))
                return s;
            return null;
        }

        /// <summary>
        /// Indique si <paramref name="itemPath"/> est situé sous le dossier
        /// <paramref name="root"/> (comparaison insensible à la casse — Windows).
        /// Sert à exclure la bibliothèque .strm des sondes bibliothèque.
        /// Null/vide sur l'un ou l'autre → false (rien à exclure).
        /// Internal : partagé avec PlaybackWatcher (télémétrie).
        /// </summary>
        internal static bool IsUnderPath(string itemPath, string root)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(itemPath)) return false;
            string r = root.TrimEnd('/', '\\', System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            if (string.Equals(itemPath, r, StringComparison.OrdinalIgnoreCase)) return true;
            return itemPath.StartsWith(r + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || itemPath.StartsWith(r + System.IO.Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Détermine si le backend LLM de plus haute priorité est un Ollama
        /// <b>local</b> (modèle souvent petit, contexte limité). Sert à activer le
        /// mode compact (données injectées réduites). Reflète le filtrage de
        /// <see cref="LlmRunner.ResolveBackends"/> sans logger (appelé avant le
        /// run). En config legacy (LlmUrl sans LlmBackends), c'est local par
        /// construction.
        /// </summary>
        internal static bool IsLocalPrimary(PluginConfiguration cfg)
        {
            try
            {
                if (cfg == null) return false;
                if (cfg.LlmBackends == null || cfg.LlmBackends.Count == 0)
                    return !string.IsNullOrWhiteSpace(cfg.LlmUrl); // legacy = local
                var best = cfg.LlmBackends
                    .Where(b => b != null && b.Enabled)
                    .Where(b => !string.IsNullOrWhiteSpace(b.Url) || b.ProviderType != LlmProvider.OllamaLocal)
                    .OrderBy(b => b.Priority)
                    .FirstOrDefault();
                return best != null && best.ProviderType == LlmProvider.OllamaLocal;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        //  Profil de goût (historique récent)
        // ------------------------------------------------------------------

        /// <summary>
        /// Construit un profil de goût textuel depuis l'historique de visionnage
        /// récent de l'usager : titres récemment joués (tri <c>DatePlayed</c>
        /// descendant) + top genres. Injecté dans le prompt utilisateur pour que
        /// le LLM croise ce profil avec les programmes EPG de ce soir.
        /// <paramref name="compact"/> réduit le nombre de titres (modèle local).
        /// </summary>
        private string BuildTasteProfile(User user, bool compact, string excludedRoot)
        {
            int maxTitles = compact ? 10 : 25;
            var sb = new StringBuilder();
            try
            {
                var q = new InternalItemsQuery
                {
                    User = user,
                    IsPlayed = true,
                    Recursive = true,
                    IncludeItemTypes = new[] { "Movie", "Episode" },
                    OrderBy = new[] { ("DatePlayed", SortOrder.Descending) },
                    Limit = compact ? 25 : 60,
                    EnableTotalRecordCount = false
                };
                var items = _library.GetItemList(q) ?? Array.Empty<BaseItem>();

                var seen = new HashSet<string>(StringComparer.Ordinal);
                var titles = new List<string>();
                var genreFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var it in items)
                {
                    // Exclut la bibliothèque .strm (recommendations à enregistrer)
                    // : ses cartes ne sont pas du contenu réellement regardé.
                    if (IsUnderPath(it?.Path, excludedRoot)) continue;
                    var episode = it as MediaBrowser.Controller.Entities.TV.Episode;
                    bool isEpisode = episode != null;
                    string title = isEpisode ? episode.SeriesName : it.Name;
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    // Dédupliquer par titre (un épisode par série suffit pour le profil).
                    string key = LlmRunner.NormTitle(title);
                    if (string.IsNullOrEmpty(key) || !seen.Add(key)) continue;

                    titles.Add(isEpisode
                        ? $"- {title} (série)"
                        : $"- {title} (film)");

                    foreach (var g in it.Genres ?? Array.Empty<string>())
                    {
                        if (string.IsNullOrWhiteSpace(g)) continue;
                        if (genreFreq.TryGetValue(g, out var c)) genreFreq[g] = c + 1;
                        else genreFreq[g] = 1;
                    }

                    if (titles.Count >= maxTitles) break;
                }

                sb.AppendLine("### PROFIL DE GOÛT DE L'USAGER (historique de visionnage récent)");
                if (titles.Count > 0)
                {
                    sb.AppendLine("Titres récemment regardés (du plus récent au plus ancien) :");
                    foreach (var t in titles) sb.AppendLine(t);
                }
                else
                {
                    sb.AppendLine("(Aucun historique de visionnage récent — recommande sur la base " +
                                  "de la note/du genre des programmes du soir.)");
                }

                var topGenres = genreFreq
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key)
                    .Take(compact ? 5 : 8)
                    .Select(kv => kv.Key)
                    .ToList();
                if (topGenres.Count > 0)
                    sb.AppendLine("Genres les plus regardés : " + string.Join(", ", topGenres) + ".");
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] BuildTasteProfile : {0}", ex.Message);
                sb.AppendLine("(Profil de goût indisponible — recommande sur la base des programmes du soir.)");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  Enregistrements récents non visionnés
        // ------------------------------------------------------------------

        /// <summary>
        /// Construit la liste des enregistrements récents NON visionnés de
        /// l'usager (films/épisodes enregistrés ces
        /// <see cref="PluginConfiguration.TonightRecordingsDays"/> derniers jours
        /// mais pas encore regardés), formatée pour injection dans le prompt.
        /// Chaque entrée porte l'id Emby de l'enregistrement (Guid) pour que le
        /// LLM puisse le référencer (<c>source="recording"</c>, <c>id=…</c>) et
        /// que l'UI propose « Regarder ».
        /// <para>Résolu ici (et pas via un tool <c>get_emby_info</c>) car
        /// <c>IsPlayed</c> est par usager : le tool est global/sans usager.</para>
        /// </summary>
        private string BuildUnwatchedRecordings(User user, PluginConfiguration cfg, CancellationToken ct, bool compact)
        {
            var sb = new StringBuilder();
            try
            {
                int days = Math.Max(0, cfg?.TonightRecordingsDays ?? 7);
                var cutoff = DateTimeOffset.Now.AddDays(-days);

                var q = new InternalItemsQuery
                {
                    User = user,
                    IsPlayed = false,
                    Recursive = true,
                    OrderBy = new[] { ("DateCreated", SortOrder.Descending) },
                    Limit = compact ? 60 : 300,
                    EnableTotalRecordCount = false
                };
                var recs = (_liveTv.GetRecordings(q, ct)?.Items) ?? Array.Empty<BaseItemDto>();

                var lines = new List<string>();
                foreach (var r in recs)
                {
                    if (r == null) continue;
                    if ((r.DateCreated ?? DateTimeOffset.MinValue) < cutoff) continue;
                    if (r.UserData?.Played == true) continue;

                    bool isSeries = r.IsSeries == true;
                    bool isMovie = r.IsMovie == true;
                    string title = !string.IsNullOrEmpty(r.SeriesName) ? r.SeriesName : r.Name;
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    string kind = isSeries ? "series" : (isMovie ? "movie" : "other");
                    string date = (r.DateCreated ?? DateTimeOffset.MinValue).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    double? rating = r.CommunityRating;
                    var genres = r.Genres ?? Array.Empty<string>();

                    var line = new StringBuilder($"- id={r.Id} | title={title} | kind={kind} | recorded={date}");
                    if (rating.HasValue) line.Append($" | rating={rating.Value:0.0}");
                    if (genres.Length > 0) line.Append($" | genres=[{string.Join(", ", genres)}]");
                    if (isSeries && !string.IsNullOrEmpty(r.EpisodeTitle) && r.EpisodeTitle != title)
                        line.Append($" | episode={r.EpisodeTitle}");
                    lines.Add(line.ToString());

                    // Mémoire réflexive : les enregistrements listés font
                    // partie du menu soumis au LLM — capturés. No-op hors run.
                    if (!string.IsNullOrEmpty(DecisionStore.ActiveRunId))
                        DecisionStore.CaptureCandidate("recording", r.Id?.ToString(), title,
                            r.ChannelName, null, genres);

                    if (lines.Count >= (compact ? 12 : 40)) break;
                }

                sb.AppendLine("### ENREGISTREMENTS RÉCENTS NON VISIONNÉS (disponibles à regarder maintenant)");
                if (lines.Count > 0)
                {
                    sb.AppendLine($"{lines.Count} enregistrement(s) de ces {days} dernier(s) jour(s), non encore regardé(s) :");
                    foreach (var l in lines) sb.AppendLine(l);
                    sb.AppendLine("Ces éléments sont DÉJÀ enregistrés (ne PAS re-programmer). Pour un de " +
                                  "ces titres qui correspond aux goûts de l'usager, recommande-le avec " +
                                  "source=\"recording\" et reprends son id tel quel (l'UI proposera « Regarder »).");
                }
                else
                {
                    sb.AppendLine($"(Aucun enregistrement récent non visionné sur ces {days} dernier(s) jour(s).)");
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] BuildUnwatchedRecordings : {0}", ex.Message);
                sb.AppendLine("(Enregistrements récents indisponibles — base-toi sur l'EPG du soir et le profil.)");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  Réserve bibliothèque (fallback < min recommandations)
        // ------------------------------------------------------------------

        /// <summary>
        /// Construit une réserve d'items de la bibliothèque de l'usager NON
        /// visionnés (films + épisodes, triés par ajout récent puis note),
        /// injectée dans le prompt comme source de complément : le LLM ne l'utilise
        /// QUE si l'EPG du soir + les enregistrements non visionnés produisent
        /// moins de <see cref="PluginConfiguration.TonightMinRecommendations"/>
        /// recommandations. Chaque entrée porte l'id Emby (InternalId, la seule
        /// forme comprise par la couche REST/UI — voir
        /// <see cref="ItemIdResolver"/>) pour <c>source="library"</c> + bouton
        /// « Regarder » (lecture bibliothèque).
        /// </summary>
        private string BuildLibraryFallbackPool(User user, bool compact, string excludedRoot)
        {
            var sb = new StringBuilder();
            try
            {
                var q = new InternalItemsQuery
                {
                    User = user,
                    IsPlayed = false,
                    Recursive = true,
                    IncludeItemTypes = new[] { "Movie", "Episode" },
                    OrderBy = new[] { ("DateCreated", SortOrder.Descending) },
                    Limit = compact ? 15 : 30,
                    EnableTotalRecordCount = false
                };
                var items = _library.GetItemList(q) ?? Array.Empty<BaseItem>();

                // Gate accès médiathèque (v1.13.12.0) : les candidats de la réserve
                // restent dans les bibliothèques accessibles à l'usager
                // (EnableAllFolders/EnabledFolders) — no-op si non restrictif.
                var accessible = PermissionGate.FilterAccessible(user, _library, _logger, items);
                if (accessible != null) items = accessible.ToArray();

                // Gate parental (v1.13.15.0) : la réserve ne présente au LLM
                // que des items que la policy parentale laisse visibles
                // (MaxParentalRating / BlockUnratedItems / tags) — no-op si
                // aucune règle.
                var parentallyOk = PermissionGate.FilterParental(user, items, _logger);
                items = parentallyOk.ToArray();

                var seen = new HashSet<string>(StringComparer.Ordinal);
                var lines = new List<string>();
                foreach (var it in items)
                {
                    if (it == null) continue;
                    // Exclut la bibliothèque .strm (recommendations à enregistrer)
                    // pour éviter la décision circulaire : un item recommandé
                    // d'enregistrer ne doit pas revenir comme candidat « À
                    // regarder ce soir » depuis la bibliothèque.
                    if (IsUnderPath(it.Path, excludedRoot)) continue;
                    var episode = it as MediaBrowser.Controller.Entities.TV.Episode;
                    bool isEpisode = episode != null;
                    string title = isEpisode ? episode.SeriesName : it.Name;
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    string key = LlmRunner.NormTitle(title);
                    if (string.IsNullOrEmpty(key) || !seen.Add(key)) continue;

                    string kind = isEpisode ? "series" : "movie";
                    double? rating = it.CommunityRating;
                    int? year = it.ProductionYear;
                    var genres = it.Genres ?? Array.Empty<string>();

                    var line = new StringBuilder($"- id={it.InternalId} | title={title} | kind={kind}");
                    if (year.HasValue) line.Append($" | year={year.Value}");
                    if (rating.HasValue) line.Append($" | rating={rating.Value:0.0}");
                    if (genres.Length > 0) line.Append($" | genres=[{string.Join(", ", genres)}]");
                    if (isEpisode && !string.IsNullOrEmpty(it.Name) && it.Name != title)
                        line.Append($" | episode={it.Name}");
                    lines.Add(line.ToString());

                    // Mémoire réflexive : la réserve fait partie du menu soumis
                    // au LLM — capturée comme le reste du pool. No-op hors run.
                    if (!string.IsNullOrEmpty(DecisionStore.ActiveRunId))
                        DecisionStore.CaptureCandidate("library", it.InternalId.ToString(), title,
                            null, null, genres);

                    if (lines.Count >= (compact ? 8 : 20)) break;
                }

                sb.AppendLine("### RÉSERVE BIBLIOTHÈQUE (items NON visionnés — à utiliser SEULEMENT si < minimum)");
                if (lines.Count > 0)
                {
                    sb.AppendLine($"{lines.Count} titre(s) de la bibliothèque non encore regardés par l'usager :");
                    foreach (var l in lines) sb.AppendLine(l);
                    sb.AppendLine("À utiliser comme COMPLÉMENT pour atteindre le minimum de recommandations : " +
                                  "choisis ceux qui matchent le profil de goût, avec source=\"library\" et " +
                                  "l'id fourni (l'UI proposera « Regarder » — lecture depuis la bibliothèque).");
                }
                else
                {
                    sb.AppendLine("(Bibliothèque sans item non visionné — réserve vide.)");
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] BuildLibraryFallbackPool : {0}", ex.Message);
                sb.AppendLine("(Réserve bibliothèque indisponible.)");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  Séries « prêtes à dévorer » (binge-ready) + gate anti-spam
        // ------------------------------------------------------------------

        /// <summary>Agrégat d'une série candidate au signalement « prêt à dévorer ».</summary>
        private class BingeSeries
        {
            public string Key;         // clé normalisée (NormTitle du SeriesName)
            public string Name;        // nom d'affichage (SeriesName)
            public int Count;          // épisodes non visionnés observés
            public DateTimeOffset Newest; // arrivée la plus récente (DateCreated)
            public BaseItem First;     // premier épisode non visionné (ordre saison/épisode)
            public int FirstSeason;    // saison de First (int.MaxValue si inconnue)
            public int FirstEpisode;   // n° d'épisode de First (int.MaxValue si inconnu)
        }

        /// <summary>
        /// Construit le bloc « SÉRIES PRÊTES À DÉVORER » injecté dans le prompt
        /// « ce soir » (opt-in <see cref="PluginConfiguration.TonightBingeEnabled"/>) :
        /// séries dont l'usager accumule des épisodes non visionnés pendant
        /// l'enregistrement, dont <b>au moins un épisode est arrivé
        /// récemment</b> (<c>DateCreated</c> dans
        /// <see cref="PluginConfiguration.TonightBingeActiveDays"/> — le signal
        /// « enregistrement actif » qui distingue une accumulation d'une série
        /// dormante jamais commencée) et dont le stock vient de franchir
        /// <see cref="PluginConfiguration.TonightBingeThreshold"/>.
        /// <para><b>Gate anti-spam</b> (<see cref="PluginConfiguration.BingeNotified"/>) :
        /// une série signalée n'est plus proposée tant que son compte non
        /// visionné ne repasse pas sous le seuil — l'usager a commencé à
        /// regarder, ce qui ré-arme la suggestion pour le cycle d'accumulation
        /// suivant. Un seul signalement par cycle, jamais de répétition.</para>
        /// <para>Fail-open : toute erreur renvoie une chaîne vide (le run
        /// Tonight continue sans le bloc). Retourne aussi une chaîne vide quand
        /// il n'y a rien à signaler (pas de section vide dans le prompt).</para>
        /// </summary>
        private string BuildBingeReadySeries(User user, PluginConfiguration cfg, bool compact, string excludedRoot)
        {
            if (cfg == null || user == null) return string.Empty;
            int threshold = Math.Max(1, cfg.TonightBingeThreshold);
            int activeDays = Math.Max(1, cfg.TonightBingeActiveDays);
            var activeCutoff = DateTimeOffset.Now.AddDays(-activeDays);
            string userPrefix = user.Id.ToString() + "|";

            try
            {
                var q = new InternalItemsQuery
                {
                    User = user,
                    IsPlayed = false,
                    Recursive = true,
                    IncludeItemTypes = new[] { "Episode" },
                    OrderBy = new[] { ("DateCreated", SortOrder.Descending) },
                    // Pool large : le compte par série doit être fiable jusqu'au
                    // seuil. Les séries dormantes « pour un jour de pluie »
                    // gonflent le pool d'épisodes non visionnés — une fenêtre
                    // DateCreated côté requête raterait des stockpiles dont les
                    // épisodes anciens précèdent la coupure ; on filtre
                    // l'activité en C# après agrégation plutôt qu'en requête.
                    Limit = 600,
                    EnableTotalRecordCount = false
                };
                var items = _library.GetItemList(q) ?? Array.Empty<BaseItem>();

                // Gate accès médiathèque (v1.13.12.0) : le pool d'épisodes
                // agrégés reste dans les bibliothèques accessibles à l'usager
                // (EnableAllFolders/EnabledFolders) — no-op si non restrictif.
                var accessible = PermissionGate.FilterAccessible(user, _library, _logger, items);
                if (accessible != null) items = accessible.ToArray();

                // Gate parental (v1.13.15.0) : épisodes filtrés par la policy
                // parentale (cote héritée de la série, tags série/épisode,
                // BlockUnratedItems) avant l'agrégation par série — no-op si
                // aucune règle.
                var parentallyOk = PermissionGate.FilterParental(user, items, _logger);
                items = parentallyOk.ToArray();

                var series = new Dictionary<string, BingeSeries>(StringComparer.Ordinal);
                foreach (var it in items)
                {
                    if (it == null) continue;
                    // Exclut la bibliothèque .strm (recommendations à enregistrer) :
                    // ses cartes ne sont pas du contenu en attente de visionnage
                    // (garde anti-circulaire partagée avec les autres sondes).
                    if (IsUnderPath(it.Path, excludedRoot)) continue;
                    var episode = it as MediaBrowser.Controller.Entities.TV.Episode;
                    string name = episode?.SeriesName;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    string key = LlmRunner.NormTitle(name);
                    if (string.IsNullOrEmpty(key)) continue;

                    if (!series.TryGetValue(key, out var s))
                    {
                        s = new BingeSeries
                        {
                            Key = key,
                            Name = name,
                            Newest = DateTimeOffset.MinValue,
                            First = null,
                            FirstSeason = int.MaxValue,
                            FirstEpisode = int.MaxValue
                        };
                        series[key] = s;
                    }
                    s.Count++;
                    if (it.DateCreated > s.Newest) s.Newest = it.DateCreated;

                    // Premier épisode à regarder : ordre saison/épisode (repli :
                    // premier rencontré dans l'ordre DateCreated décroissant)
                    // pour que le bouton « Regarder » démarre au début du stock.
                    int season = it.ParentIndexNumber ?? int.MaxValue;
                    int number = it.IndexNumber ?? int.MaxValue;
                    if (s.First == null || season < s.FirstSeason
                        || (season == s.FirstSeason && number < s.FirstEpisode))
                    {
                        s.First = it; s.FirstSeason = season; s.FirstEpisode = number;
                    }
                }

                // Gate anti-spam : retire les entrées de CET usager dont le
                // compte observé repasse sous le seuil (série absente du pool =
                // tout est visionné → 0). Les autres usagers ne sont pas touchés.
                var notified = ParseBingeNotified(cfg);
                bool mapChanged = false;
                foreach (var entryKey in notified.Keys
                    .Where(k => k.StartsWith(userPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToList())
                {
                    int observed = series.TryGetValue(entryKey.Substring(userPrefix.Length), out var s0)
                        ? s0.Count : 0;
                    if (observed < threshold)
                    {
                        notified.Remove(entryKey); // ré-armement (l'usager regarde)
                        mapChanged = true;
                        _logger?.Info("[LLM_AI] Tonight binge : « {0} » ré-armée (compte {1} < seuil {2}).",
                            entryKey.Substring(userPrefix.Length), observed, threshold);
                    }
                }

                var candidates = series.Values
                    .Where(s => s.Count >= threshold && s.Newest >= activeCutoff)
                    .Where(s => !notified.ContainsKey(userPrefix + s.Key))
                    .OrderByDescending(s => s.Count)
                    .ThenByDescending(s => s.Newest)
                    .ToList();
                int cap = compact ? 1 : 2;
                var surfaced = candidates.Take(cap).ToList();
                if (surfaced.Count == 0)
                {
                    if (mapChanged) PersistBingeNotified(cfg, notified); // ré-armements à sauver
                    return string.Empty; // rien à signaler : pas de bloc (économie de tokens)
                }

                var sb = new StringBuilder();
                sb.AppendLine("### SÉRIES PRÊTES À DÉVORER (l'usager accumule des épisodes enregistrés non visionnés)");
                sb.AppendLine(surfaced.Count + " série(s) dont l'enregistrement est actif (nouvel épisode arrivé " +
                              "récemment) et dont le stock d'épisodes non visionnés a atteint le seuil — " +
                              "l'usager attend d'en avoir assez pour commencer. C'est le moment de le lui signaler :");
                foreach (var s in surfaced)
                {
                    var line = new StringBuilder("- id=" + s.First?.InternalId + " | title=" + s.Name +
                        " | unwatched=" + s.Count +
                        " | latest=" + s.Newest.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    if (s.FirstSeason != int.MaxValue || s.FirstEpisode != int.MaxValue)
                        line.Append(" | start=S" + (s.FirstSeason == int.MaxValue ? 0 : s.FirstSeason).ToString("00", CultureInfo.InvariantCulture)
                                  + "E" + (s.FirstEpisode == int.MaxValue ? 0 : s.FirstEpisode).ToString("00", CultureInfo.InvariantCulture));
                    if (!string.IsNullOrEmpty(s.First?.Name))
                        line.Append(" « " + s.First.Name + " »");
                    sb.AppendLine(line.ToString());
                    // Marque comme signalée : gate persistant (une fois par cycle
                    // d'accumulation, re-armée quand le compte repasse sous le seuil).
                    notified[userPrefix + s.Key] = s.Count;
                    mapChanged = true;
                }
                sb.AppendLine("Recommande AU PLUS UNE de ces séries (priorité au plus grand stock), kind=\"series\", " +
                              "source=\"recording\" si la série figure aussi dans les ENREGISTREMENTS NON VISIONNÉS " +
                              "ci-dessus (reprends alors l'id de cette liste), sinon source=\"library\" en reprenant " +
                              "l'id ci-dessus tel quel (l'UI proposera « Regarder »). Mentionne le nombre " +
                              "d'épisodes en attente dans la raison.");

                _logger?.Info("[LLM_AI] Tonight binge : {0} série(s) signalée(s) ({1} candidate(s), seuil {2}, fenêtre active {3} j, pool {4} épisode(s)).",
                    surfaced.Count, candidates.Count, threshold, activeDays, items.Length);

                // Ne persiste PAS ici : le signalement n'est consommé que si le
                // run LLM réussit (voir GenerateTonightAsync) — un run raté ne
                // « brûle » pas le one-shot anti-spam, la série sera re-proposée
                // au prochain run.
                if (mapChanged)
                {
                    _pendingBingeCfg = cfg;
                    _pendingBingeMap = notified;
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] BuildBingeReadySeries : {0}", ex.Message);
                return string.Empty; // fail-open : le run Tonight continue sans le bloc
            }
        }

        /// <summary>
        /// Parse <see cref="PluginConfiguration.BingeNotified"/> (tableau JSON
        /// <c>[{"key":"userId|serie","count":N}]</c>) en dictionnaire
        /// (insensible à la casse). Tolère un JSON mal formé (renvoie un
        /// dictionnaire vide) — même convention que
        /// <see cref="GetEmbyInfoTool.DroppedTitlesSet"/>.
        /// </summary>
        private static Dictionary<string, int> ParseBingeNotified(PluginConfiguration cfg)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var raw = cfg?.BingeNotified;
            if (string.IsNullOrWhiteSpace(raw)) return map;
            try
            {
                if (JsonNode.Parse(raw) is JsonArray arr)
                {
                    foreach (var node in arr)
                    {
                        if (!(node is JsonObject obj)) continue;
                        string key = ObjStr(obj, "key");
                        if (string.IsNullOrEmpty(key)) continue;
                        int count = 0;
                        if (obj["count"] is JsonValue cv && cv.TryGetValue<int>(out var n)) count = n;
                        map[key] = count;
                    }
                }
            }
            catch { /* JSON invalide : on ignore (map vide) */ }
            return map;
        }

        /// <summary>
        /// Sérialise le dictionnaire du gate anti-spam (clés triées : fichier
        /// déterministe d'un run à l'autre) et persiste la configuration.
        /// Best-effort : un échec de sauvegarde est logué sans casser le run
        /// (conséquence bénigne : le gate serait ré-évalué au prochain run).
        /// </summary>
        private void PersistBingeNotified(PluginConfiguration cfg, Dictionary<string, int> map)
        {
            try
            {
                var arr = new JsonArray();
                foreach (var kv in map.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var o = new JsonObject();
                    o["key"] = kv.Key;
                    o["count"] = kv.Value;
                    arr.Add(o);
                }
                cfg.BingeNotified = arr.ToJsonString();
                Plugin.Instance?.SaveConfiguration();
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Tonight binge : persistance du gate échouée ({0}) — ré-évalué au prochain run.", ex.Message);
            }
        }
    }
}