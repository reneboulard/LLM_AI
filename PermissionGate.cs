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