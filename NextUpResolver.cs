using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;

namespace LLM_AI
{
    /// <summary>
    /// Résolution du <b>prochain épisode à jouer</b> d'une série/saison :
    /// next up natif (<see cref="ITVSeriesManager.GetNextUp"/>) avec repli
    /// « premier épisode non visionné ». Logique unique partagée par les
    /// playlists AI Tonight (normalisation du watch bucket en feuilles
    /// jouables), le tool <c>client_command play_item</c> du chat externe,
    /// et — pour la playlist publique foyer — la variante multi-usagers
    /// « le plus avancé » (<see cref="ResolveHouseholdEpisode"/>, v2.2.0).
    /// </summary>
    /// <remarks>
    /// <para><b>Pourquoi série→épisode</b> : un <c>PlayNow</c> (comme une
    /// insertion en playlist) portant un id de <see cref="Series"/>/
    /// <see cref="Season"/> n'est PAS développé par le client Android TV —
    /// le serveur répond 204 et l'app ne joue rien (vérifié 2026-09-16 sur
    /// une box BRAVIA, Emby for Android 3.5.55 : un id épisode joue, un id
    /// série reste sans effet). Toute couche qui LANCE une lecture doit donc
    /// émettre une feuille jouable (épisode).</para>
    /// <para><b>Empirisme du build</b> (Emby 4.10.0.40) :
    /// <c>GetNextUp</c> retourne VIDE pour une série jamais commencée
    /// (aucun épisode vu — vérifié aussi via le REST natif
    /// <c>/Shows/NextUp</c>) et peut renvoyer un épisode DÉJÀ VU. Le repli
    /// (<see cref="FirstUnwatchedEpisode"/>) calcule le prochain à la main :
    /// premier épisode non vu en ordre saison/épisode. L'état « vu » est
    /// relu par usager via <c>IUserDataManager</c> en C# (pas de filtre de
    /// requête) ; userData indisponible = « non vu » (fail-open : l'épisode
    /// next up est retenu tel quel).</para>
    /// </remarks>
    internal static class NextUpResolver
    {
        /// <summary>
        /// Retourne l'épisode à jouer pour <paramref name="seriesOrSeason"/>
        /// : next up de l'usager, repli premier épisode non visionné — ou
        /// null si introuvable (série sans série parente, tout vu, ou
        /// userData indisponible). Best-effort, ne lève jamais ; journalise
        /// les replis via <paramref name="logPrefix"/> (ex. « Playlist : »,
        /// « [CHAT-EXT] client_command play_item — »).
        /// </summary>
        internal static BaseItem ResolvePlayingEpisode(
            ILibraryManager library, IServerApplicationHost host,
            User user, BaseItem seriesOrSeason, ILogger logger, string logPrefix)
        {
            if (seriesOrSeason == null) return null;
            var series = seriesOrSeason as Series;
            var season = seriesOrSeason as Season;
            var seriesItem = series ?? season?.Series;
            if (seriesItem == null)
            {
                logger?.Warn("[LLM_AI] {0}série introuvable pour la saison « {1} ».",
                    logPrefix, seriesOrSeason.Name);
                return null;
            }

            ITVSeriesManager tv = null;
            IUserDataManager userData = null;
            try
            {
                tv = host?.TryResolve<ITVSeriesManager>();
                userData = host?.TryResolve<IUserDataManager>();
            }
            catch { /* résolution impossible → repli premier non visionné */ }

            BaseItem ep = null;
            if (tv != null)
            {
                try
                {
                    var next = tv.GetNextUp(
                        new NextUpQuery
                        {
                            SeriesId = seriesItem.InternalId,
                            UserId = user.InternalId,
                            Limit = 1,
                            EnableTotalRecordCount = false
                        },
                        user,
                        new DtoOptions());
                    ep = next?.Items != null && next.Items.Length > 0 ? next.Items[0] : null;
                }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] {0}next up échoué pour « {1} » : {2} — repli.",
                        logPrefix, seriesItem.Name, ex.Message);
                }
                // Un next up DÉJÀ VU ne vaut pas mieux que vide (le build
                // peut renvoyer un épisode visionné) : repli.
                if (ep != null && IsPlayedForUser(userData, user, ep))
                {
                    logger?.Info("[LLM_AI] {0}next up « {1} » de « {2} » déjà vu — repli.",
                        logPrefix, ep.Name, seriesItem.Name);
                    ep = null;
                }
            }
            else
            {
                logger?.Warn("[LLM_AI] {0}ITVSeriesManager indisponible — repli pour « {1} ».",
                    logPrefix, seriesItem.Name);
            }

            // Repli (série jamais commencée : GetNextUp est VIDE sur ce
            // build) : premier épisode NON VU en ordre saison/épisode.
            if (ep == null)
                ep = FirstUnwatchedEpisode(library, userData, user, seriesItem, logger, logPrefix);
            return ep;
        }

        /// <summary>
        /// Résolution <b>foyer</b> (playlist publique « AI Tonight », v2.2.0) :
        /// le prochain épisode <b>le plus avancé</b> parmi un ensemble
        /// d'usagers — <paramref name="householdUsers"/>, constitué par
        /// l'appelant (<c>TonightService.BuildHouseholdUsers</c> : le
        /// porteur Tonight + les comptes actifs, <b>moins les comptes
        /// ignorés</b> de la liste de config). Pour chaque usager, la
        /// résolution individuelle (<see cref="ResolvePlayingEpisode"/> :
        /// next up natif + repli premier non visionné) est calculée, puis
        /// l'épisode de position (saison, épisode) <b>maximale</b> est
        /// retenu — la playlist foyer ne pointe jamais un épisode que le
        /// foyer a déjà dépassé.
        /// <para><b>Pourquoi le max</b> (vécu 2026-10-08, prod) : résolue
        /// pour le seul porteur, la playlist publique pointait l'épisode 1
        /// de chaque série recommandée — le porteur est souvent un compte
        /// technique sans historique (champ usager Tonight vide → premier
        /// admin), dont le repli « premier non vu » vaut toujours S1E1. Le
        /// <b>min</b> (premier épisode non vu par TOUS) retomberait sur
        /// l'épisode 1 dès qu'un seul membre n'a rien vu ; le <b>max</b>
        /// rend un compte sans historique <b>neutre</b> : son S1E1 ne gagne
        /// que si personne n'a entamé la série — le bon résultat
        /// (« nouvelle série pour le foyer »).</para>
        /// <para><b>Égalités</b> : à position (saison, épisode) égale, le
        /// premier usager de l'ordre reçu l'emporte (déterminisme).
        /// Numéros absents = position basse (spéciaux non numérotés ≠
        /// « plus avancé »). Ensemble vide (foyer entier ignoré) ou tout
        /// vu pour tous = null (l'appelant saute la série). Chaque
        /// résolution individuelle est journalisée au nom de l'usager
        /// (préfixe « {paramref logPrefix} ({usager}) »), et un résumé
        /// une-ligne donne les positions du foyer et l'élu. Best-effort,
        /// ne lève jamais.</para>
        /// </summary>
        internal static BaseItem ResolveHouseholdEpisode(
            ILibraryManager library, IServerApplicationHost host,
            IEnumerable<User> householdUsers,
            BaseItem seriesOrSeason, ILogger logger, string logPrefix)
        {
            if (seriesOrSeason == null) return null;

            // Ensemble reçu DÉJÀ constitué et filtré par l'appelant
            // (TonightService : porteur + actifs, moins les ignorés) —
            // dédupliqué par id ici par précaution, ordre reçu conservé
            // (déterminisme des égalités).
            var users = new List<User>();
            foreach (var u in householdUsers ?? Array.Empty<User>())
                if (u != null && !users.Any(x => x.Id == u.Id))
                    users.Add(u);
            if (users.Count == 0) return null;

            BaseItem best = null;
            string bestUser = null;
            int bestSeason = int.MinValue, bestNumber = int.MinValue;
            var positions = new List<string>(users.Count);
            foreach (var u in users)
            {
                // Résolution individuelle (next up natif + repli) —
                // préfixe au nom de l'usager : chaque ligne de repli est
                // attribuable, le diagnostic foyer reste lisible.
                var ep = ResolvePlayingEpisode(library, host, u, seriesOrSeason, logger,
                    logPrefix + " (" + (u.Name ?? "?") + ") : ");
                if (ep == null)
                {
                    positions.Add((u.Name ?? "?") + "=aucun");
                    continue;
                }
                int season = ep.ParentIndexNumber ?? int.MinValue;
                int number = ep.IndexNumber ?? int.MinValue;
                positions.Add((u.Name ?? "?") + "=S" + (ep.ParentIndexNumber?.ToString() ?? "?")
                    + "E" + (ep.IndexNumber?.ToString() ?? "?"));
                if (best == null || season > bestSeason
                    || (season == bestSeason && number > bestNumber))
                {
                    best = ep; bestUser = u.Name; bestSeason = season; bestNumber = number;
                }
            }

            if (best != null)
                logger?.Info("[LLM_AI] {0} : « {1} » — {2} usager(s) considéré(s) [{3}] → retenu « {4} » (le plus avancé, usager « {5} »).",
                    logPrefix, seriesOrSeason.Name, users.Count,
                    string.Join(", ", positions), best.Name, bestUser ?? "?");
            return best;
        }

        /// <summary>État « vu » de l'item pour CET usager
        /// (<c>IUserDataManager</c> — <see cref="BaseItem"/> ne porte pas de
        /// UserData). userData indisponible = pas vu (fail-open).</summary>
        internal static bool IsPlayedForUser(
            IUserDataManager userData, User user, BaseItem item)
        {
            if (userData == null || user == null || item == null) return false;
            try { return userData.GetUserData(user, item)?.Played ?? false; }
            catch { return false; }
        }

        /// <summary>
        /// Repli « next up » : premier épisode <b>non vu</b> (pour l'usager)
        /// de la série, en ordre saison/épisode. Requête par le dossier de la
        /// série (<c>Folder.GetItemList</c>, chemin validé par le listing
        /// playlist) ; l'état « vu » est relu par usager via
        /// <c>IUserDataManager</c> en C# (pas de filtre de requête). userData
        /// indisponible ou tout vu = null. Best-effort, ne lève jamais.
        /// </summary>
        internal static BaseItem FirstUnwatchedEpisode(
            ILibraryManager library, IUserDataManager userData,
            User user, BaseItem seriesItem, ILogger logger, string logPrefix)
        {
            if (userData == null || user == null || seriesItem == null) return null;
            try
            {
                var items = (seriesItem as Folder)?.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = new[] { "Episode" },
                    EnableTotalRecordCount = false
                });
                if (items == null) return null;
                BaseItem best = null;
                int bestSeason = int.MaxValue, bestNumber = int.MaxValue;
                foreach (var it in items)
                {
                    if (it == null) continue;
                    if (userData.GetUserData(user, it)?.Played ?? false) continue;
                    int season = it.ParentIndexNumber ?? int.MaxValue;
                    int number = it.IndexNumber ?? int.MaxValue;
                    if (best == null || season < bestSeason
                        || (season == bestSeason && number < bestNumber))
                    {
                        best = it; bestSeason = season; bestNumber = number;
                    }
                }
                if (best != null)
                    logger?.Info("[LLM_AI] {0}repli next up — premier épisode non vu « {1} » de « {2} ».",
                        logPrefix, best.Name, seriesItem.Name);
                return best;
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] {0}repli next up échoué pour « {1} » : {2}",
                    logPrefix, seriesItem.Name, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// « Épisode suivant » explicite (tool <c>client_command play_next</c>) :
        /// premier épisode NON VU de la série STRICTEMENT APRÈS
        /// <paramref name="currentEpisode"/>, en ordre saison/épisode.
        /// Contrairement au next up d'Emby — qui, pour un épisode en cours non
        /// terminé, retourne CET ÉPISODE (c'est le « prochain à regarder ») —
        /// ce choix traduit l'intention réelle de l'usager qui demande
        /// « passe au suivant » en pleine lecture.
        /// <para><b>Doublons de bibliothèque</b> : la même série peut exister
        /// en deux dossiers (deux <see cref="Series"/>, deux InternalIds par
        /// (saison, épisode) — vécu 2026-09-16). La comparaison est donc par
        /// (saison, épisode), PAS par InternalId, et toute copie de l'épisode
        /// courant est sautée : sans cette garde, le « suivant » résolu était
        /// la copie du courant, et le client rejouait le même épisode.</para>
        /// <para>La position du courant est prise par
        /// <see cref="BaseItem.InternalId"/> dans la liste ordonnée
        /// (résiste aux numéros manquants/spéciaux) ; courant absent de la
        /// liste (série de l'AUTRE dossier) = recherche depuis le début,
        /// dédupliquée par (saison, épisode) pour sauter le courant. Tout vu
        /// après le courant = null. Ne MARQUE PAS l'épisode courant comme vu
        /// (aucune écriture surprise — l'usager décide). Best-effort, ne
        /// lève jamais.</para>
        /// </summary>
        internal static BaseItem FirstUnwatchedAfter(
            ILibraryManager library, IUserDataManager userData,
            User user, BaseItem seriesItem, BaseItem currentEpisode,
            ILogger logger, string logPrefix)
        {
            if (userData == null || user == null || seriesItem == null) return null;
            try
            {
                var items = (seriesItem as Folder)?.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = new[] { "Episode" },
                    EnableTotalRecordCount = false
                });
                if (items == null) return null;
                var ordered = items
                    .Where(i => i != null)
                    .OrderBy(i => i.ParentIndexNumber ?? int.MaxValue)
                    .ThenBy(i => i.IndexNumber ?? int.MaxValue)
                    .ToList();
                int idx = currentEpisode == null ? -1
                    : ordered.FindIndex(i => i.InternalId == currentEpisode.InternalId);
                // Doublons : identité d'épisode = (saison, épisode), pas
                // l'InternalId (deux dossiers = deux ids pour le même S/E).
                int curSeason = currentEpisode?.ParentIndexNumber ?? int.MinValue;
                int curNumber = currentEpisode?.IndexNumber ?? int.MinValue;
                for (int i = idx + 1; i < ordered.Count; i++)
                {
                    var it = ordered[i];
                    int season = it.ParentIndexNumber ?? int.MinValue;
                    int number = it.IndexNumber ?? int.MinValue;
                    // Copie de l'épisode courant (autre dossier) ≠ suivant.
                    if (currentEpisode != null && season == curSeason && number == curNumber)
                        continue;
                    if (IsPlayedForUser(userData, user, it)) continue;
                    logger?.Info("[LLM_AI] {0}épisode suivant — « {1} » (après « {2} »).",
                        logPrefix, it.Name,
                        idx >= 0 ? currentEpisode.Name : "le début de la série");
                    return it;
                }
                return null;
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] {0}épisode suivant échoué pour « {1} » : {2}",
                    logPrefix, seriesItem.Name, ex.Message);
                return null;
            }
        }
    }
}