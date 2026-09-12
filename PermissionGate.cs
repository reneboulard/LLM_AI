using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace LLM_AI
{
    /// <summary>
    /// Gate de droits usager du plugin : résolution de l'usager appelant et
    /// lecture de sa policy Emby. Deux étages de personalisation coexistent
    /// (décision design 2026-09) : les <b>enregistrements</b> sont une décision
    /// foyer pilotée par le droit d'enregistrement Emby, l'<b>intérêt de
    /// visionnement</b> (Watch Tonight, reco) est par usager.
    /// </summary>
    /// <remarks>
    /// <para>La policy Emby est la seule source de vérité : elle est lue à
    /// chaque requête via <see cref="IUserManager"/>, jamais mise en cache —
    /// un droit retiré dans le dashboard prend effet immédiatement. Le plugin
    /// ne gère PAS de comptes propres ; il combine la policy (couche dure) et
    /// ses opt-ins de config (couche souple, ex. <c>ChatPromptsEnabled</c>).</para>
    /// <para>Deux voies de résolution selon le canal :</para>
    /// <list type="bullet">
    /// <item><c>FromAuthorization</c> — requête authentifiée standard (token
    /// usager) : priorité au <c>User</c> porté par le token, repli sur le
    /// <c>UserId</c> (Int64) du token. Pattern <c>RecosApiService.ResolveCaller</c>
    /// (extrait pour réutilisation).</item>
    /// <item><c>FromSession</c> — corrélation par session de lecture (les
    /// requêtes <c>.strm</c> ne portent PAS l'auth Emby) : <see cref="SessionInfo"/>
    /// du lecteur → usager. Pattern <c>TonightLoginService.ResolveUser</c>.</item>
    /// </list>
    /// </remarks>
    internal static class PermissionGate
    {
        // ------------------------------------------------------------------
        //  Résolution d'usager
        // ------------------------------------------------------------------

        /// <summary>
        /// Résout l'usager à partir du token d'authentification d'une requête
        /// plugin standard. Priorité au User du token, puis au UserId (Int64)
        /// du token. Null si non authentifié. Ne lève jamais.
        /// </summary>
        /// <param name="authCtx">Contexte d'authorization de l'hôte
        /// (<c>BaseApiService.AuthorizationContext</c>).</param>
        /// <param name="request">Requête en cours (<c>BaseApiService.Request</c>).</param>
        /// <param name="users"><c>BaseApiService.UserManager</c>.</param>
        public static User FromAuthorization(
            MediaBrowser.Controller.Net.IAuthorizationContext authCtx,
            IRequest request,
            IUserManager users)
        {
            try
            {
                var auth = authCtx?.GetAuthorizationInfo(request);
                var user = auth?.User;
                if (user == null && auth != null && auth.UserId != 0)
                    user = users.GetUserById(auth.UserId);
                return user;
            }
            catch { return null; }
        }

        /// <summary>
        /// Résout l'usager depuis une session de lecture. <see cref="SessionInfo.UserId"/>
        /// est le Guid string ; <see cref="IUserManager.GetUserById(string)"/> le
        /// résout. Repli sur le nom (UserInternalId non string). Null si pas
        /// d'usager (session anonyme). Ne lève jamais.
        /// </summary>
        /// <param name="session">Session de lecture (peut être null).</param>
        /// <param name="users">Gestionnaire d'usagers de l'hôte.</param>
        public static User FromSession(
            MediaBrowser.Controller.Session.SessionInfo session,
            IUserManager users)
        {
            try
            {
                string uid = session?.UserId;
                if (!string.IsNullOrWhiteSpace(uid))
                    return users.GetUserById(uid);
                if (!string.IsNullOrWhiteSpace(session?.UserName))
                    return users.GetUserByName(session.UserName);
            }
            catch { /* tolérant */ }
            return null;
        }

        // ------------------------------------------------------------------
        //  Lecture de policy
        // ------------------------------------------------------------------

        /// <summary>
        /// L'usager peut-il programmer des enregistrements ? Réplique fidèle du
        /// droit natif <c>EnableLiveTvManagement</c> (les administrateurs le
        /// portent par défaut). Les administrateurs passent aussi par ce test —
        /// un admin sans le droit (config exotique) est traité comme les autres.
        /// </summary>
        public static bool CanRecordLive(User user)
        {
            return user?.Policy?.EnableLiveTvManagement ?? false;
        }

        /// <summary>
        /// L'usager peut-il regarder la TV en direct ? Réplique fidèle du droit
        /// natif <c>EnableLiveTvAccess</c>. Utilisé par le run « Watch Tonight » :
        /// l'EPG (tools <c>epg_*</c> et snapshot de validation) n'est consulté
        /// que si ce droit est porté — un usager sans TV en direct n'a rien à
        /// regarder en direct, et l'EPG est une donnée qu'il n'a pas le droit de
        /// voir. Null → false (fail-closed, cohérent avec <see cref="CanRecordLive"/>).
        /// </summary>
        public static bool CanWatchLive(User user)
        {
            return user?.Policy?.EnableLiveTvAccess ?? false;
        }

        // ------------------------------------------------------------------
        //  Accès médiathèque (EnableAllFolders / EnabledFolders)
        // ------------------------------------------------------------------

        /// <summary>
        /// La policy de l'usager restreint-elle l'accès aux bibliothèques ?
        /// Vrai si la policy est illisible/null, <c>EnableAllFolders</c> est
        /// porté, ou la liste <c>EnabledFolders</c> est vide (fail-open —
        /// l'usager est traité comme non restreint et la passe de filtrage est
        /// sautée). Un résultat <c>false</c> engage le filtrage par
        /// <see cref="FilterAccessible"/>.
        /// </summary>
        public static bool HasUnrestrictedFolders(User user)
        {
            var p = user?.Policy;
            if (p == null) return true;                       // policy illisible : fail-open
            if (p.EnableAllFolders) return true;              // accès explicite à tout
            // Type de EnabledFolders variable selon le build (List<string> ou
            // string[]) : comptage via IEnumerable, valable pour les deux.
            if (p.EnabledFolders == null || !p.EnabledFolders.Cast<string>().Any()) return true;
            return false;
        }

        /// <summary>
        /// Résout les InternalId (long) des bibliothèques racine accessibles à
        /// l'usager : chaque entrée de <c>UserPolicy.EnabledFolders</c> est
        /// résolue en item via <see cref="ItemIdResolver"/> (InternalId OU Guid
        /// hérité — les deux formes selon le build du dashboard). Null si la
        /// résolution échoue (fail-open, Warn logué) ou si l'usager n'est pas
        /// restrictif (aucune résolution faite).
        /// </summary>
        public static long[] AccessibleFolderIds(User user, ILibraryManager library, ILogger logger)
        {
            try
            {
                if (HasUnrestrictedFolders(user)) return null;
                var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in user.Policy.EnabledFolders ?? Enumerable.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(s)) enabled.Add(s.Trim());
                if (enabled.Count == 0) return null;

                // EnabledFolders porte des ids d'items (chaînes) de bibliothèques
                // racine : résolus via <see cref="ItemIdResolver"/> (InternalId OU
                // Guid — les deux formes selon le build du dashboard). Pas
                // d'énumération du UserRootFolder (cette build n'expose ni
                // Folder.Children ni InternalItemsQuery.ParentId).
                var ids = new List<long>();
                foreach (var raw in enabled)
                {
                    var item = ItemIdResolver.Resolve(library, raw);
                    if (item == null) continue;
                    ids.Add(item.InternalId);
                }
                if (ids.Count == 0)
                {
                    // Aucune bibliothèque reconnue : soit les ids du dashboard
                    // n'ont pas la forme attendue, soit l'usager n'a accès à
                    // rien. Fail-open (null) plutôt que vider les recos sur une
                    // hypothèse.
                    logger?.Warn("[LLM_AI] Gate permission : aucune bibliothèque de EnabledFolders reconnue ({0} entrée(s)) — filtrage accès bibliothèque désactivé pour cet appel.", enabled.Count);
                    return null;
                }
                return ids.ToArray();
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Gate permission : résolution des bibliothèques accessibles échouée : {0}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Filtre des candidats bibliothèque sur les bibliothèques accessibles à
        /// l'usager (critère médiathèque du dashboard :
        /// <c>EnableAllFolders</c>/<c>EnabledFolders</c>). No-op si l'usager n'est
        /// pas restrictif ou si la résolution échoue (fail-open : les items sont
        /// conservés — une indisponibilité transitoire ne vide jamais les recos).
        /// Implementation : intersection en une requête
        /// (<c>ItemIds</c> = candidats, <c>AncestorIds</c> = bibliothèques
        /// accessibles) — les candidats passés sont ≤ 600 (pools des sondes
        /// Tonight), la requête reste bon marché.
        /// </summary>
        /// <param name="items">Candidats bruts (pools ≤ 600).</param>
        /// <returns>Sous-liste des items accessibles (même ordre).</returns>
        public static List<BaseItem> FilterAccessible(User user, ILibraryManager library,
            ILogger logger, System.Collections.Generic.IEnumerable<BaseItem> items)
        {
            if (user == null) return null;   // null = « pas de filtre applicable »
            var source = new List<BaseItem>(items ?? new BaseItem[0]);
            if (HasUnrestrictedFolders(user)) return source;
            long[] allowed = AccessibleFolderIds(user, library, logger);
            if (allowed == null) return source;   // fail-open

            try
            {
                var q = new InternalItemsQuery
                {
                    ItemIds = source.Select(i => i.InternalId).Distinct().ToArray(),
                    AncestorIds = allowed,
                    EnableTotalRecordCount = false
                };
                var accessible = new HashSet<long>(
                    (library.GetItemList(q) ?? new BaseItem[0]).Select(i => i.InternalId));
                var kept = source.Where(i => accessible.Contains(i.InternalId)).ToList();
                int dropped = source.Count - kept.Count;
                if (dropped > 0)
                    logger?.Info("[LLM_AI] Gate permission : {0} candidat(s) bibliothèque écarté(s) — bibliothèque(s) non accessible(s) pour l'usager.", dropped);
                return kept;
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Gate permission : filtrage accès bibliothèque échoué (fail-open) : {0}", ex.Message);
                return source;
            }
        }

        // ------------------------------------------------------------------
        //  Contrôle parental (MaxParentalRating / BlockUnratedItems / tags)
        // ------------------------------------------------------------------

        /// <summary>
        /// Verdict parental d'un item pour un usager. <see cref="Allowed"/>
        /// couvre aussi les cotes non reconnues par la table serveur
        /// (comportement natif : aveugle — jamais une décision forcée) ;
        /// <see cref="BlockedUnrated"/> distingue le blocage par
        /// <c>BlockUnratedItems</c> d'un dépassement de limite
        /// (<see cref="BlockedRating"/>) et d'un tag
        /// (<see cref="BlockedTag"/>).
        /// </summary>
        internal enum ParentalVerdict
        {
            /// <summary>Item visible pour l'usager (ou cote non reconnue —
            /// native-blind, conservé).</summary>
            Allowed,
            /// <summary>Cote héritée au-dessus de <c>MaxParentalRating</c>,
            /// ou item non autorisé en mode liste blanche stricte.</summary>
            BlockedRating,
            /// <summary>Un tag de la policy interdit (liste noire) ou
            /// aucun tag autorisé (liste blanche).</summary>
            BlockedTag,
            /// <summary>Item non coté (ou marqueur NR) et son type figure
            /// dans <c>BlockUnratedItems</c>.</summary>
            BlockedUnrated
        }

        /// <summary>
        /// Policy parentale d'un usager, extraite une fois pour un lot.
        /// <para><b>Sémantique validée empiriquement 2026-09-12</b> (Emby
        /// 4.10.0.40 — cf. mémo projet) : <c>BlockedTags</c> est une liste
        /// noire quand <c>IsTagBlockingModeInclusive</c> est false (les items
        /// portant un tag listé disparaissent), une liste blanche quand le
        /// flag est true (« exclure tous sauf le tag » — les tags autorisés
        /// sont écrits DANS <c>BlockedTags</c>, le champ <c>IncludeTags</c>
        /// étant un no-op). <c>AllowTagOrRating</c> change la sémantique de
        /// la liste blanche : true = visible si tag autorisé <b>OU</b> cote
        /// sous la limite (le tag autorisé contourne la limite) ; false =
        /// visible seulement si tag autorisé <b>ET</b> cote sous la limite.
        /// <c>BlockUnratedItems</c> liste les TYPES de contenu dont les items
        /// non cotés sont bloqués (énum <c>UnratedItem</c> : Movie, Series,
        /// LiveTvProgram…).</para>
        /// </summary>
        private sealed class ParentalPolicy
        {
            /// <summary>Une règle parentale existe au moins (limite, tags ou
            /// types non cotés bloqués) — false = aucun filtrage à faire.</summary>
            public bool Active;
            public int? MaxRating;
            public readonly HashSet<string> UnratedKinds = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public bool Inclusive;
            public bool AllowTagOrRating;

            /// <summary>
            /// Construit la policy depuis l'usager. Fail-open : policy
            /// illisible/null → Active=false (aucun filtrage — jamais vider
            /// les recos sur une hypothèse). Ne lève jamais.
            /// </summary>
            public static ParentalPolicy From(User user)
            {
                var pol = new ParentalPolicy();
                try
                {
                    var p = user?.Policy;
                    if (p == null) return pol;
                    pol.MaxRating = p.MaxParentalRating;
                    pol.Inclusive = p.IsTagBlockingModeInclusive;
                    pol.AllowTagOrRating = p.AllowTagOrRating;
                    foreach (var t in p.BlockedTags ?? Enumerable.Empty<string>())
                        if (!string.IsNullOrWhiteSpace(t)) pol.Tags.Add(t.Trim());
                    // Type de BlockUnratedItems variable selon le build
                    // (UnratedItem[] / List<UnratedItem> / string[]) :
                    // projection par Cast<object>+ToString, valable pour tous.
                    if (p.BlockUnratedItems != null)
                        foreach (var k in p.BlockUnratedItems.Cast<object>())
                            if (k != null) pol.UnratedKinds.Add(k.ToString());
                }
                catch { return new ParentalPolicy(); }   // fail-open
                pol.Active = pol.MaxRating.HasValue || pol.Tags.Count > 0 || pol.UnratedKinds.Count > 0;
                return pol;
            }
        }

        /// <summary>
        /// L'usager porte-t-il AU MOINS une règle parentale (limite
        /// <c>MaxParentalRating</c>, tags bloqués/autorisés ou
        /// <c>BlockUnratedItems</c>) ? False si policy absente/illisible
        /// (fail-open) — le filtrage parental est alors entièrement sauté.
        /// </summary>
        public static bool HasParentalRestrictions(User user)
        {
            return ParentalPolicy.From(user).Active;
        }

        /// <summary>
        /// Verdict parental d'un item BIBLIOTHÈQUE pour l'usager. Cote :
        /// valeur héritée native (<c>BaseItem.GetInheritedParentalRatingValue</c>
        /// — méthode, pas propriété, sur cette build) comparée à
        /// <c>MaxParentalRating</c> ; cote absente/marqueur NR → règle
        /// <c>BlockUnratedItems</c> ; cote présente mais hors table (pas de
        /// score natif) → <see cref="ParentalVerdict.Allowed"/> (blindage
        /// natif reproduit — l'audit <c>ratings_check</c> signale ces cas).
        /// Tags : item + série porteuse pour un épisode (les tags de série ne
        /// se propagent pas aux épisodes dans le DTO). Un item reconnu par
        /// AUCUNE règle n'est jamais bloqué (fail-open par règle, pas global).
        /// </summary>
        public static ParentalVerdict IsParentallyAllowed(User user, BaseItem item)
        {
            return Evaluate(ParentalPolicy.From(user), item);
        }

        /// <summary>
        /// Filtre des candidats bibliothèque sur la policy parentale de
        /// l'usager (limite <c>MaxParentalRating</c>, tags noirs/autorisés,
        /// <c>BlockUnratedItems</c>). No-op si l'usager ne porte aucune
        /// règle parentale — pattern <see cref="FilterAccessible"/>.
        /// Déterministe : contrairement au fail-open transitoire, une policy
        /// parentale lisible EST appliquée (un item caché par Emby ne doit
        /// jamais revenir en reco) ; seul un item dont la cote n'a pas de
        /// score natif est conservé (blindage natif, cf. <see cref="IsParentallyAllowed"/>).
        /// </summary>
        /// <param name="items">Candidats bruts (pools des sondes Tonight).</param>
        /// <returns>Sous-liste des items autorisés (même ordre) — jamais null.</returns>
        public static List<BaseItem> FilterParental(User user, IEnumerable<BaseItem> items, ILogger logger)
        {
            var source = new List<BaseItem>(items ?? new BaseItem[0]);
            var pol = ParentalPolicy.From(user);
            if (!pol.Active) return source;

            var kept = new List<BaseItem>(source.Count);
            int blockRating = 0, blockTag = 0, blockUnrated = 0;
            foreach (var it in source)
            {
                if (it == null) continue;
                switch (Evaluate(pol, it))
                {
                    case ParentalVerdict.Allowed: kept.Add(it); break;
                    case ParentalVerdict.BlockedRating: blockRating++; break;
                    case ParentalVerdict.BlockedTag: blockTag++; break;
                    case ParentalVerdict.BlockedUnrated: blockUnrated++; break;
                }
            }
            int dropped = blockRating + blockTag + blockUnrated;
            if (dropped > 0)
                logger?.Info("[LLM_AI] Gate permission : {0} candidat(s) écarté(s) par le contrôle parental (limite de cote : {1}, tags : {2}, non cotés bloqués : {3}).",
                    dropped, blockRating, blockTag, blockUnrated);
            return kept;
        }

        /// <summary>
        /// Verdict d'un item pour une policy déjà construite (un lot = une
        /// construction). Toute exception interne → <see cref="ParentalVerdict.Allowed"/>
        /// (fail-open : un item illisible ne vide jamais le pool).
        /// </summary>
        private static ParentalVerdict Evaluate(ParentalPolicy pol, BaseItem item)
        {
            try
            {
                if (!pol.Active || item == null) return ParentalVerdict.Allowed;

                // --- Cote (héritée) et type au sens UnratedItem --------------
                int? score = null;
                try { score = item.GetInheritedParentalRatingValue(); }
                catch { /* méthode absente ou échec : score inconnu */ }
                string text = item.OfficialRating;
                string kind = item is MediaBrowser.Controller.Entities.Movies.Movie
                    ? "Movie"
                    : (item is MediaBrowser.Controller.Entities.TV.Episode
                       || item is MediaBrowser.Controller.Entities.TV.Series) ? "Series" : "Other";

                // --- Tags (item + série porteuse pour un épisode) ------------
                bool hasAllowedTag = false;
                if (pol.Tags.Count > 0)
                {
                    var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var t in item.Tags ?? Enumerable.Empty<string>())
                        if (!string.IsNullOrWhiteSpace(t)) tags.Add(t.Trim());
                    if (tags.Count == 0)
                    {
                        var ep = item as MediaBrowser.Controller.Entities.TV.Episode;
                        try
                        {
                            var seriesTags = ep?.Series?.Tags;
                            foreach (var t in seriesTags ?? Enumerable.Empty<string>())
                                if (!string.IsNullOrWhiteSpace(t)) tags.Add(t.Trim());
                        }
                        catch { /* série non résoluble : tags d'item seuls */ }
                    }
                    hasAllowedTag = tags.Overlaps(pol.Tags);
                }

                if (!pol.Inclusive)
                {
                    // Liste noire (« Exclure le tag ») : tag ∈ liste → bloqué ;
                    // sinon règles de cote standard (cote non reconnue = native-blind).
                    if (hasAllowedTag) return ParentalVerdict.BlockedTag;
                    return RatingVerdict(pol, score, text, kind);
                }

                // Liste blanche (« Exclure tous sauf le tag ») — liste vide =
                // pas de liste blanche effective (le champ IncludeTags est un
                // no-op sur cette build : un BlockedTags vide ne cache rien,
                // validé 2026-09-12) → seules les règles de cote s'appliquent.
                if (pol.Tags.Count == 0)
                    return RatingVerdict(pol, score, text, kind);

                // Liste blanche effective : visible = tag autorisé (OU|ET) cote ok.
                bool ratingOk = RatingVerdict(pol, score, text, kind) == ParentalVerdict.Allowed;
                bool ok = pol.AllowTagOrRating ? (hasAllowedTag || ratingOk) : (hasAllowedTag && ratingOk);
                return ok ? ParentalVerdict.Allowed : ParentalVerdict.BlockedTag;
            }
            catch { return ParentalVerdict.Allowed; }   // fail-open par item
        }

        /// <summary>
        /// Verdict par la seule cote (limite + non coté + non reconnu) :
        /// score présent → comparaison à <c>MaxParentalRating</c> ;
        /// absent/marqueur NR → règle <c>BlockUnratedItems</c> du type ;
        /// cote textuelle hors table (pas de score natif) → conservé
        /// (blindage natif reproduit — design 2026-09-11 : jamais une
        /// décision forcée, l'audit <c>ratings_check</c> signale ces cotes).
        /// </summary>
        private static ParentalVerdict RatingVerdict(ParentalPolicy pol, int? score, string text, string kind)
        {
            if (score.HasValue)
                return (!pol.MaxRating.HasValue || score.Value <= pol.MaxRating.Value)
                    ? ParentalVerdict.Allowed : ParentalVerdict.BlockedRating;
            if (string.IsNullOrWhiteSpace(text) || UnratedMarkers.Contains(text.Trim()))
                return pol.UnratedKinds.Contains(kind) ? ParentalVerdict.BlockedUnrated : ParentalVerdict.Allowed;
            return ParentalVerdict.Allowed;
        }

        /// <summary>
        /// Marqueurs « non coté » légitimes (même convention que l'audit
        /// <c>ratings_check</c>) : hors table parentale mais pas du désordre —
        /// couverts par <c>BlockUnratedItems</c>, jamais par la limite.
        /// </summary>
        private static readonly HashSet<string> UnratedMarkers =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "NR", "Not Rated", "Unrated", "N/A" };

        /// <summary>
        /// Score parental d'une cote TEXTUELLE (programmes EPG — DTO sans
        /// méthode native) : normalisation Classification Mapper
        /// (<see cref="ClassificationMap.Normalize"/>, cotes CA- usager)
        /// PUIS table parentale du serveur (<c>ILocalizationManager.GetParentalRatings</c>
        /// — la même liste que le menu de limite du dashboard). Null si la
        /// cote est absente ou non reconnue (native-blind : la reco est
        /// conservée, comptée à part — jamais une décision forcée ;
        /// cf. census v1.13.14.0 : ~46 % des cotes EPG brutes hors table).
        /// </summary>
        public static int? EpgRatingScore(MediaBrowser.Model.Globalization.ILocalizationManager loc,
            string officialRating)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(officialRating)) return null;
                string canon = ClassificationMap.Normalize(officialRating);
                if (string.IsNullOrWhiteSpace(canon)) return null;
                foreach (var r in loc?.GetParentalRatings() ?? Array.Empty<MediaBrowser.Model.Entities.ParentalRating>())
                    if (r != null && string.Equals(r.Name?.Trim(), canon.Trim(), StringComparison.OrdinalIgnoreCase))
                        return r.Value;
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Verdict parental d'un programme EPG (DTO <c>BaseItemDto</c>, cote
        /// TEXTUELLE) pour l'usager : score résolu par
        /// <see cref="EpgRatingScore"/> (mapper CA- → table serveur), tags du
        /// programme (les programmes portent les tags plugin « AI Tonight » /
        /// « AI Delete » comme les items). Cote non reconnue → conservé
        /// (<paramref name="unrecognized"/> vrai, à compter à part).
        /// </summary>
        public static ParentalVerdict IsEpgAllowed(User user, MediaBrowser.Model.Globalization.ILocalizationManager loc,
            string officialRating, IEnumerable<string> tags, out bool unrecognized)
        {
            unrecognized = false;
            try
            {
                var pol = ParentalPolicy.From(user);
                if (!pol.Active) return ParentalVerdict.Allowed;

                var tagSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in tags ?? Enumerable.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(t)) tagSet.Add(t.Trim());
                bool hasAllowedTag = pol.Tags.Count > 0 && tagSet.Overlaps(pol.Tags);

                int? score = EpgRatingScore(loc, officialRating);
                bool textPresent = !string.IsNullOrWhiteSpace(officialRating)
                    && !UnratedMarkers.Contains(officialRating.Trim());
                if (score == null && textPresent) unrecognized = true;   // native-blind : conservé

                if (!pol.Inclusive)
                {
                    if (hasAllowedTag) return ParentalVerdict.BlockedTag;   // liste noire : tag ∈ liste
                    return RatingVerdict(pol, score, officialRating, "LiveTvProgram");
                }
                if (pol.Tags.Count == 0)
                    return RatingVerdict(pol, score, officialRating, "LiveTvProgram");

                bool ratingOk = RatingVerdict(pol, score, officialRating, "LiveTvProgram") == ParentalVerdict.Allowed;
                bool ok = pol.AllowTagOrRating ? (hasAllowedTag || ratingOk) : (hasAllowedTag && ratingOk);
                return ok ? ParentalVerdict.Allowed : ParentalVerdict.BlockedTag;
            }
            catch { return ParentalVerdict.Allowed; }
        }

        /// <summary>
        /// Bloc « CONTRAINTE PARENTALE » à injecter dans le prompt Watch
        /// Tonight quand l'usager porte une règle parentale — dit au LLM ce
        /// que le filtrage mécanique imposerait de toute façon (pattern
        /// v1.13.12.0 : le dire en amont, puis l'imposer en aval). Null si
        /// aucune restriction (aucun bloc injecté). Mode liste blanche
        /// stricte : le cas dégénéré (« rien n'est visible ») est signalé
        /// pour que le LLM réoriente au lieu de tâtonner.
        /// </summary>
        public static string DescribeForPrompt(User user)
        {
            var pol = ParentalPolicy.From(user);
            if (!pol.Active) return null;

            var sb = new System.Text.StringBuilder();
            sb.Append("\n\n### CONTRAINTE PARENTALE (droits de l'usager)\n");
            if (!pol.Inclusive)
            {
                if (pol.MaxRating.HasValue)
                    sb.Append($"L'usager a une limite parentale : ne recommande AUCUN item dont la cote dépasse le niveau {pol.MaxRating.Value} ")
                      .Append("(les cotes sont des scores Emby ; une cote non reconnue n'a pas de score — évite-la par prudence si tu en connais le contenu adulte).\n");
                if (pol.UnratedKinds.Count > 0)
                    sb.Append("Le contenu NON COTÉ (sans cote ou « NR ») est bloqué pour cet usager pour ses types : ")
                      .Append(string.Join(", ", pol.UnratedKinds)).Append(" — ne le recommande pas.\n");
                if (pol.Tags.Count > 0)
                    sb.Append("Tags interdits pour cet usager : ").Append(string.Join(", ", pol.Tags))
                      .Append(" — aucun item portant ces tags.\n");
            }
            else
            {
                sb.Append("Ce compte est en mode liste blanche de tags : il ne voit QUE les items portant un tag parmi ")
                  .Append(string.Join(", ", pol.Tags)).Append(". ");
                if (pol.AllowTagOrRating)
                    sb.Append("Les items sans ces tags restent visibles seulement si leur cote est sous la limite parentale. ");
                sb.Append("Recommande UNIQUEMENT du contenu visible pour cet usager — si rien n'est éligible, produis moins de recommandations plutôt que des recos qu'Emby lui cachera.\n");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  Enforcement .strm : annulation asynchrone (v1.13.11.0)
        // ------------------------------------------------------------------

        /// <summary>
        /// Annule les timers (single ET series) créés pour
        /// <paramref name="programId"/> qui ne figuraient PAS dans
        /// <paramref name="preExistingIds"/> — c'est-à-dire ceux créés par
        /// l'activation en cours. Un timer préexistant (créé avant l'activation,
        /// éventuellement par un autre usager autorisé) n'est JAMAIS touché.
        /// Best-effort, ne lève jamais : un échec de lecture des timers n'est
        /// que logué (l'enregistrement reste alors programmé — cas résiduel
        /// accepté, logué en audit). Synchrone (les annulations de cet hôte
        /// sont void) — appelé depuis le gate en arrière-plan.
        /// </summary>
        /// <param name="liveTv"><see cref="ILiveTvManager"/> de l'hôte.</param>
        /// <param name="programId">Id de programme EPG de la reco.</param>
        /// <param name="preExistingTimerIds">Ids de timers présents AVANT
        /// l'activation (capture immédiatement avant création).</param>
        /// <param name="logger">Logger (peut être null).</param>
        /// <remarks>Sur cet hôte, <c>CancelTimer</c>/<c>CancelSeriesTimer</c>
        /// sont void et <c>GetTimers</c>/<c>GetSeriesTimers</c> renvoient des
        /// DTO (<c>TimerInfoDto</c>/<c>SeriesTimerInfoDto</c>) — méthode
        /// synchrone, appelée depuis le gate en arrière-plan.</remarks>
        public static void CancelCreatedTimers(
            ILiveTvManager liveTv, string programId,
            System.Collections.Generic.HashSet<string> preExistingTimerIds,
            ILogger logger)
        {
            int cancelled = 0;
            try
            {
                // Single timers (films + timers ponctuels).
                var timers = liveTv.GetTimers(new MediaBrowser.Model.LiveTv.TimerQuery { IsScheduled = true })?.Items;
                foreach (var t in timers ?? new MediaBrowser.Model.LiveTv.TimerInfoDto[0])
                {
                    try
                    {
                        if (t == null || string.IsNullOrEmpty(t.Id)) continue;
                        if (!string.Equals(t.ProgramId, programId, StringComparison.OrdinalIgnoreCase)) continue;
                        if (preExistingTimerIds.Contains(t.Id)) continue;   // préexistant : pas le nôtre
                        liveTv.CancelTimer(t.Id);
                        cancelled++;
                        logger?.Info("[LLM_AI] Gate permission : timer « {0} » (id={1}) annulé — usager sans droit d'enregistrement.", t.ProgramId, t.Id);
                    }
                    catch (Exception ex) { logger?.Warn("[LLM_AI] Gate permission : annulation timer {0} échouée : {1}", t?.Id, ex.Message); }
                }

                // Series timers (enregistrements récurrents).
                var series = liveTv.GetSeriesTimers(new MediaBrowser.Model.LiveTv.SeriesTimerQuery())?.Items;
                foreach (var s in series ?? new MediaBrowser.Model.LiveTv.SeriesTimerInfoDto[0])
                {
                    try
                    {
                        if (s == null || string.IsNullOrEmpty(s.Id)) continue;
                        if (!string.Equals(s.ProgramId, programId, StringComparison.OrdinalIgnoreCase)) continue;
                        if (preExistingTimerIds.Contains(s.Id)) continue;
                        liveTv.CancelSeriesTimer(s.Id);
                        cancelled++;
                        logger?.Info("[LLM_AI] Gate permission : series timer « {0} » (id={1}) annulé — usager sans droit d'enregistrement.", s.ProgramId, s.Id);
                    }
                    catch (Exception ex) { logger?.Warn("[LLM_AI] Gate permission : annulation series timer {0} échouée : {1}", s?.Id, ex.Message); }
                }
            }
            catch (Exception ex)
            {
                // Cas résiduel : un échec ici laisse le timer en place. Logué
                // pour audit ; la passe de nettoyage nocturne ne le reprend pas
                // (il est légitime côté tuner) — le risque est assumé et visible.
                logger?.Warn("[LLM_AI] Gate permission : lecture des timers pour annulation échouée : {0}", ex.Message);
            }
            if (cancelled > 0)
                logger?.Info("[LLM_AI] Gate permission : {0} timer(s) annulé(s) pour programId={1}.", cancelled, programId);
        }
    }
}