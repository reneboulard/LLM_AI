using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Favoris <b>éphémères</b> pour « À regarder ce soir » : à chaque run
    /// frais, les recos du <b>watch bucket</b> sont mises en favori
    /// (<see cref="IUserDataManager.SaveUserData"/>) pour l'usager « Tonight »
    /// — l'usager les retrouve dans « Ma liste » de son client Emby. Au
    /// nettoyage nocturne (<c>AiTonightCleanupTask</c>, 3 h), on retire
    /// <b>uniquement les favoris que nous avons posés</b> — jamais les favoris
    /// préexistants de l'usager.
    /// </summary>
    /// <remarks>
    /// <para><b>Propriété des favoris</b> : le set exact des items mis en
    /// favori par le plugin est tracé dans un fichier d'état
    /// (<see cref="StateFileName"/>, dossier de configuration du plugin) —
    /// <b>fusionné</b> à chaque run (les entrées des runs précédents pas
    /// encore nettoyées survivent, dédupliquées), lu puis supprimé par le
    /// nettoyage. Règles :
    /// <list type="bullet">
    /// <item>un item DÉJÀ favori avant le run est <b>ignoré</b> (pas dans le
    /// set → jamais retiré par le nettoyage, qu'il vienne de l'usager ou
    /// d'un run antérieur) ;</item>
    /// <item>le nettoyage ne retire que les entrées du fichier d'état, et
    /// seulement si l'item est encore favori ; le fichier n'est supprimé
    /// qu'en cas de nettoyage complet (échecs de persistance → retry au
    /// prochain passage) ;</item>
    /// <item>la fusion rend le cycle robuste aux nettoyages manqués
    /// (serveur arrêté à 3 h) et aux runs multiples d'une même journée —
    /// le prochain nettoyage réussi remet tout à zéro ;</item>
    /// <item>limite : si l'usager re-favorise manuellement un item que nous
    /// avions mis en favori, le nettoyage le retire quand même —
    /// indistinguable d'un nôtre ; documenté.</item>
    /// </list></para>
    /// <para><b>API Emby utilisées</b> (signatures vérifiées par réflexion sur
    /// <c>MediaBrowser.Controller.dll</c> de cet hôte, Emby 4.9.5.0) :
    /// <see cref="IUserDataManager.GetUserData"/> (User, BaseItem) →
    /// <c>UserItemData</c> (muté en place : <c>IsFavorite</c>, rating
    /// préservés) puis <see cref="IUserDataManager.SaveUserData"/> (User,
    /// BaseItem, UserItemData, <c>UserDataSaveReason.UpdateUserRating</c>,
    /// CancellationToken).</para>
    /// <para>Best-effort partout : un item non résolvable ou un échec de
    /// persistance est logué sans interrompre le reste.</para>
    /// </remarks>
    internal static class AiTonightFavoritesManager
    {
        /// <summary>
        /// Fichier d'état (dossier de configuration du plugin —
        /// <c>Plugin.Paths.PluginConfigurationsPath</c>, pattern
        /// <see cref="GenreCleanerMap"/>) : set exact des favoris posés par
        /// le dernier run, repris par le nettoyage.
        /// </summary>
        private const string StateFileName = "tonight_favorites_state.json";

        // ------------------------------------------------------------------
        //  Pose des favoris (run Tonight)
        // ------------------------------------------------------------------

        /// <summary>
        /// Met en favori <paramref name="user"/> chaque item Emby dont l'id
        /// (chaîne, cf. <see cref="ItemIdResolver"/>) figure dans
        /// <paramref name="itemGuidIds"/> — SAUF les items déjà favoris
        /// (préexistants : jamais touchés). <b>Fusionne</b> le set de ce run
        /// dans le fichier d'état (dédup par item+usager) : les entrées des
        /// runs précédents NON ENCORE nettoyées sont conservées — un
        /// nettoyage manqué (serveur arrêté à 3 h) ou plusieurs runs le
        /// même jour ne fuient plus : la prochaine exécution du nettoyage
        /// reverte tout le cumul, puis supprime le fichier (retour à zéro
        /// après chaque nettoyage réussi).
        /// </summary>
        internal static void Apply(
            IUserDataManager userData, ILibraryManager library, ILogger logger,
            IEnumerable<string> itemGuidIds, User user, CancellationToken ct)
        {
            if (userData == null || library == null || itemGuidIds == null || user == null)
                return;

            var added = new List<StateEntry>();
            int skippedFavorite = 0, skippedResolve = 0;

            foreach (var raw in itemGuidIds)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                BaseItem item;
                try { item = ItemIdResolver.Resolve(library, raw); }
                catch (Exception ex) { logger?.Warn("[LLM_AI] Favoris : résolution id {0} échouée : {1}", raw, ex.Message); continue; }
                if (item == null) { skippedResolve++; continue; }

                try
                {
                    var data = userData.GetUserData(user, item);
                    if (data != null && data.IsFavorite)
                    {
                        // Favori préexistant : on ne le touche pas, on ne le
                        // met PAS dans le set (le nettoyage ne le reverra jamais).
                        skippedFavorite++;
                        continue;
                    }

                    data ??= new UserItemData();
                    data.IsFavorite = true;
                    userData.SaveUserData(user, item, data,
                        MediaBrowser.Model.Entities.UserDataSaveReason.UpdateUserRating, ct);
                    added.Add(new StateEntry { ItemId = raw.Trim(), UserId = user.Id.ToString("N") });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] Favoris : échec SaveUserData pour « {0} » : {1}", item.Name, ex.Message);
                }
            }

            WriteState(logger, MergeState(added));
            logger?.Info("[LLM_AI] Favoris : {0} mis en favori pour « {1} » ({2} déjà favori(s) ignoré(s), {3} non résolu(s)).",
                added.Count, user.Name, skippedFavorite, skippedResolve);
        }

        // ------------------------------------------------------------------
        //  Reprise des favoris (nettoyage 3 h)
        // ------------------------------------------------------------------

        /// <summary>
        /// Retire les favoris <b>que nous avons posés</b> (fichier d'état —
        /// cumul des runs depuis le dernier nettoyage réussi) — et seulement
        /// eux : un item est re-favorisé (retour à non-favori) SI il est
        /// encore en favori. L'usager de chaque entrée est re-résolu par l'id
        /// stocké dans le fichier d'état (<paramref name="users"/>) ; une
        /// entrée sans usager résolvable est ignorée. Le fichier d'état est
        /// supprimé ensuite SEULEMENT si aucun échec n'a eu lieu — des
        /// échecs de persistance gardent leurs entrées pour retry au
        /// prochain nettoyage (sinon elles fuieraient en favoris
        /// permanents) ; absent/corrompu → no-op (ne lève jamais).
        /// </summary>
        internal static void Revert(
            IUserDataManager userData, IUserManager users, ILibraryManager library,
            ILogger logger, CancellationToken ct)
        {
            if (userData == null || library == null)
                return;

            List<StateEntry> state;
            try
            {
                string json = File.Exists(StatePath) ? File.ReadAllText(StatePath) : null;
                state = ParseState(json);
            }
            catch (Exception)
            {
                state = null;
            }
            if (state == null || state.Count == 0)
                return;

            int reverted = 0, alreadyGone = 0, failed = 0;
            foreach (var entry in state)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(entry.ItemId)) continue;

                BaseItem item;
                try { item = ItemIdResolver.Resolve(library, entry.ItemId); }
                catch (Exception) { item = null; }
                if (item == null)
                {
                    // Item supprimé depuis le run : rien à revert (son UserData
                    // disparaît avec lui).
                    alreadyGone++;
                    continue;
                }

                var user = FindUserById(users, entry.UserId);
                if (user == null)
                {
                    alreadyGone++;
                    continue;
                }

                try
                {
                    var data = userData.GetUserData(user, item);
                    if (data == null || !data.IsFavorite)
                    {
                        alreadyGone++;
                        continue;
                    }
                    data.IsFavorite = false;
                    userData.SaveUserData(user, item, data,
                        MediaBrowser.Model.Entities.UserDataSaveReason.UpdateUserRating, ct);
                    reverted++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    logger?.Warn("[LLM_AI] Favoris : échec revert pour « {0} » : {1}", item.Name, ex.Message);
                }
            }

            // Suppression du fichier d'état SEULEMENT en cas de succès complet :
            // un échec de persistance (failed > 0) garde les entrées pour
            // retry au prochain nettoyage — supprimer les entrées non
            // rétablies les transformerait en favoris permanents.
            if (failed == 0)
            {
                try { File.Delete(StatePath); }
                catch (Exception ex) { logger?.Warn("[LLM_AI] Favoris : suppression fichier d'état échouée : {0}", ex.Message); }
            }
            else
            {
                logger?.Warn("[LLM_AI] Favoris : {0} échec(s) de revert — fichier d'état conservé pour retry au prochain nettoyage.", failed);
            }
            logger?.Info("[LLM_AI] Favoris : {0} rétabli(s), {1} déjà absent(s)/item supprimé(s), {2} échec(s).",
                reverted, alreadyGone, failed);
        }

        // ------------------------------------------------------------------
        //  Fichier d'état
        // ------------------------------------------------------------------

        private sealed class StateEntry
        {
            // Contrat du fichier d'état : camelCase (« itemId »/« userId »).
            // HISTORIQUE : la sérialisation par défaut de System.Text.Json
            // (PascalCase « ItemId »/« UserId ») ne correspondait PAS au parse
            // camelCase — le revert ne trouvait donc JAMAIS les entrées et
            // les favoris s'accumulaient indéfiniment (constaté 2026-10-07 :
            // 63 favoris orphelins sur le serveur de test). Noms épinglés
            // explicitement + parse tolérant aux deux casses (fichiers
            // écrits par l'ancienne version).
            [JsonPropertyName("itemId")]
            public string ItemId { get; set; }

            [JsonPropertyName("userId")]
            public string UserId { get; set; }
        }

        private static string StatePath
        {
            get
            {
                try
                {
                    var dir = Plugin.Paths?.PluginConfigurationsPath;
                    if (string.IsNullOrEmpty(dir)) return null;
                    return Path.Combine(dir, StateFileName);
                }
                catch { return null; }
            }
        }

        /// <summary>
        /// Fusionne les entrées fraîches du run avec le fichier d'état
        /// existant (dédup par ItemId+UserId, insensible à la casse) : les
        /// entrées des runs précédents pas encore nettoyées survivent —
        /// c'est ce qui empêche une fuite quand le nettoyage nocturne est
        /// manqué ou que plusieurs runs tombent le même jour.
        /// </summary>
        private static List<StateEntry> MergeState(List<StateEntry> fresh)
        {
            var merged = new List<StateEntry>(fresh);
            try
            {
                string json = File.Exists(StatePath) ? File.ReadAllText(StatePath) : null;
                var existing = ParseState(json) ?? new List<StateEntry>();
                var seen = new HashSet<string>(
                    merged.ConvertAll(e => (e.ItemId ?? "") + "|" + (e.UserId ?? "")),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var e in existing)
                {
                    var key = (e.ItemId ?? "") + "|" + (e.UserId ?? "");
                    if (string.IsNullOrWhiteSpace(e.ItemId) || seen.Contains(key)) continue;
                    seen.Add(key);
                    merged.Add(e);
                }
            }
            catch { /* best-effort : en cas d'échec de lecture, run frais seul */ }
            return merged;
        }

        /// <summary>Écrit le fichier d'état fusionné (best-effort — mais un
        /// échec est LOGUÉ : ce fichier est la source de vérité du
        /// nettoyage, sans lui les favoris posés fuient en permanence).</summary>
        private static void WriteState(ILogger logger, List<StateEntry> entries)
        {
            try
            {
                var path = StatePath;
                if (path == null) return;
                var payload = new Dictionary<string, object>
                {
                    { "run", DateTimeOffset.UtcNow.ToString("o") },
                    { "items", entries }
                };
                File.WriteAllText(path, JsonSerializer.Serialize(payload));
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Favoris : écriture du fichier d'état échouée — les favoris de ce run ne pourront PAS être retirés par le nettoyage : {0}", ex.Message);
            }
        }

        /// <summary>Parse le fichier d'état ; null/JSON invalide → set vide.
        /// Tolérant aux deux casses de propriétés (« itemId » du contrat
        /// courant, « ItemId » des fichiers écrits avant le correctif
        /// 2026-10-07 — leur contenu redevient ainsi nettoyable).</summary>
        private static List<StateEntry> ParseState(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<StateEntry>();
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var result = new List<StateEntry>();
                    if (doc.RootElement.TryGetProperty("items", out var items)
                        && items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in items.EnumerateArray())
                        {
                            if (el.ValueKind != JsonValueKind.Object) continue;
                            var entry = new StateEntry();
                            entry.ItemId = GetStringAnyCase(el, "itemId");
                            entry.UserId = GetStringAnyCase(el, "userId");
                            result.Add(entry);
                        }
                    }
                    return result;
                }
            }
            catch { return new List<StateEntry>(); }
        }

        /// <summary>Propriété de nom <paramref name="name"/> en casse
        /// quelconque (camelCase ou PascalCase), null si absente/non-chaîne.</summary>
        private static string GetStringAnyCase(JsonElement obj, string name)
        {
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var prop))
            {
                // TryGetProperty est sensible à la casse — retour sur la
                // casse inverse du contrat (anciens fichiers PascalCase).
                var alt = char.IsLower(name[0])
                    ? char.ToUpperInvariant(name[0]) + name.Substring(1)
                    : char.ToLowerInvariant(name[0]) + name.Substring(1);
                if (!obj.TryGetProperty(alt, out prop)) return null;
            }
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
        }

        /// <summary>
        /// Résout l'usager du fichier d'état par son id (Guid « N ») via
        /// <paramref name="users"/> — best-effort : null si introuvable
        /// (l'entrée sera alors ignorée au revert).
        /// </summary>
        private static User FindUserById(IUserManager users, string userIdRaw)
        {
            if (users == null || string.IsNullOrWhiteSpace(userIdRaw)) return null;
            try
            {
                // IUserManager.GetUserById(String) sur cette build (vérifié par
                // réflexion) — le format stocké est Guid « N » ; en cas d'échec
                // (usager supprimé) → null via le catch.
                return users.GetUserById(userIdRaw);
            }
            catch { return null; }
        }
    }
}