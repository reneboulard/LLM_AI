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

        public TonightService(IUserManager users, ILibraryManager library,
            ILiveTvManager liveTv, IJsonSerializer json, IServerApplicationHost host,
            ILogger logger, ICollectionManager collections)
        {
            _users = users;
            _library = library;
            _liveTv = liveTv;
            _json = json;
            _host = host;
            _logger = logger;
            _collections = collections;
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
            "[{title, kind, reason, priority, source, channel, start, id, showbizz_match}] " +
            "(id pour source=\"recording\"/\"library\" ; channel/start pour source=\"live\").";

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
        }

        // ------------------------------------------------------------------
        //  Cache par usager (in-memory, statique, partagé ; TTL = TonightCacheHours)
        // ------------------------------------------------------------------

        private class CacheEntry
        {
            public string Items;
            public string Date;
            public DateTimeOffset ExpiresAt;
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
            bool refresh, CancellationToken ct)
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

            // 3) Prompt personnalisé = template config + profil + enregistrements
            //    + réserve + contrainte de minimum dynamique.
            int minRec = Math.Max(0, cfg.TonightMinRecommendations);
            string prompt = (cfg.TonightPrompt ?? string.Empty).Trim()
                + "\n\n" + profile + recs + reserve
                + "\n\n### CONTRAINTE DE SÉLECTION\n"
                + $"Garantis AU MOINS {minRec} recommandation(s). Si l'EPG du soir + les "
                + "enregistrements non visionnés en produisent moins, complète avec la RÉSERVE "
                + "BIBLIOTHÈQUE ci-dessus (source=\"library\", reprends id). Ne dépasse pas le "
                + "minimum avec la réserve — l'EPG et les enregistrements restent prioritaires.";

            // 4) Run agent (boucle de tool-calling) — même logique que la tâche
            //    planifiée : backends, outils, enrichissement (match titres →
            //    id/channel_id/rating/image_url) gérés par LlmRunner.
            var (payload, ok) = await runner.RunAsync(cfg, "TONIGHT", prompt, TONIGHT_WORKFLOW, ct).ConfigureAwait(false);

            if (!ok || string.IsNullOrWhiteSpace(payload))
                return new TonightResult { Error = "Le run LLM n'a pas produit de recommandation." };

            // Enrichissement bibliothèque : pour les reco source="live" dont le
            // titre est déjà possédé, injecte library_id (bouton « Regarder »
            // depuis la bibliothèque + signal owned-guard pour AutoProgrammer).
            // S'exécute après l'enrichissement EPG.
            payload = runner.EnrichWithLibrary(payload);

            // Validation d'existence : on vérifie que chaque recommandation
            // pointe vers un item réel (EPG non expiré / item bibliothèque non
            // supprimé / id non halluciné). Drop les introuvables ; marque
            // « Diffusé » (aired=true) les programmes EPG déjà terminés (gardés,
            // mais sans actions obsolètes côté UI). Fail-open : une erreur de
            // requête transitoire ne vide jamais les recos.
            payload = ValidateAndFilter(payload, ct);
            if (string.IsNullOrWhiteSpace(payload))
                return new TonightResult { Error = "Toutes les recommandations pointaient vers des items introuvables (EPG expiré ou items supprimés)." };

            // Surface native des recos du watch bucket sur un run FRAIS (pas sur
            // cache) : deux mécanismes indépendants et opt-in, réutilisant les
            // mêmes ids collectés une fois ci-dessous (parseur/dedup
            // d'AutoProgrammer) :
            //  - étiquetage par genre « AI Tonight » (filtre par genre dans Emby) ;
            //  - collection Emby « AI Tonight » (collection navigable, non
            //    destructive — regroupe les items par référence).
            // Le nettoyage quotidien (AiTonightCleanupTask, 3 h) retire le genre
            // ET vide la collection ; les runs suivants reconstruisent l'un et/ou
            // l'autre selon les flags.
            HashSet<string> watchBucketIds = null;
            if (cfg.TonightGenreTagEnabled || cfg.TonightCollectionEnabled)
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
                    await AiGenreTagger.AddAsync(_library, _logger, watchBucketIds, AiGenreTagger.TonightGenre, ct)
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
                        ExpiresAt = DateTimeOffset.UtcNow.AddHours(cacheHours)
                    };
                }
            }

            return new TonightResult { Payload = payload, Date = date, FromCache = false };
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
        ///   Regarder en direct). Sinon on injecte <c>end</c> (date de fin
        ///   autoritaire). Id absent du snapshot → drop (EPG expiré ou id
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
        ///   parse JSON) renvoie le payload original inchangé — on ne vide
        ///   jamais les recos sur un échec de requête. Ne lève pas.
        /// </summary>
        private string ValidateAndFilter(string payload, CancellationToken ct)
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
                Dictionary<string, BaseItemDto> epg;
                try
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

                // Lookup bibliothèque (batché) pour source="recording"/"library".
                var libIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var node in arr)
                {
                    if (!(node is JsonObject obj)) continue;
                    string src = ObjStr(obj, "source");
                    if (!string.Equals(src, "recording", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(src, "library", StringComparison.OrdinalIgnoreCase)) continue;
                    string id = ObjStr(obj, "id");
                    if (!string.IsNullOrEmpty(id)) libIds.Add(id);
                }
                var libFound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                                if (it != null) libFound.Add(it.InternalId.ToString());
                        }
                        catch (Exception ex)
                        {
                            _logger?.Warn("[LLM_AI] Tonight validation : lookup bibliothèque indispo ({0}) — recos recording/library conservées non validées.", ex.Message);
                            foreach (var s in libIds) libFound.Add(s); // fail-open ciblé
                        }
                    }
                }

                int kept = 0, dropped = 0, epgExpired = 0, libMissing = 0;
                for (int i = arr.Count - 1; i >= 0; i--)
                {
                    if (!(arr[i] is JsonObject obj)) { kept++; continue; }
                    string src = ObjStr(obj, "source");
                    string id = ObjStr(obj, "id");

                    if (string.Equals(src, "live", StringComparison.OrdinalIgnoreCase))
                    {
                        if (epg == null) { kept++; continue; } // EPG indispo → fail-open
                        if (epg.TryGetValue(id ?? "", out var p))
                        {
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
                        if (libFound.Contains(id ?? "")) kept++;
                        else { arr.RemoveAt(i); dropped++; libMissing++; }
                    }
                    else
                    {
                        kept++; // source absente/autre : non concernée
                    }
                }

                _logger?.Info("[LLM_AI] Tonight validation : {0} gardée(s), {1} supprimée(s) (EPG expirés/hors-snapshot : {2}, items bibli. introuvables : {3}).",
                    kept, dropped, epgExpired, libMissing);

                return arr.ToJsonString();
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Tonight validation : échec global ({0}) — payload conservé.", ex.Message);
                return payload;
            }
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
        /// </summary>
        private static bool IsUnderPath(string itemPath, string root)
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
    }
}