using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    /// réécrit intégralement à chaque run (set = recos courantes), lu par le
    /// nettoyage. Règles :
    /// <list type="bullet">
    /// <item>un item DÉJÀ favori avant le run est <b>ignoré</b> (pas dans le
    /// set → jamais retiré par le nettoyage, qu'il vienne de l'usager ou
    /// d'un run antérieur) ;</item>
    /// <item>le nettoyage ne retire que les entrées du fichier d'état, et
    /// seulement si l'item est encore favori ;</item>
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
        /// (préexistants : jamais touchés). Réécrit le fichier d'état avec le
        /// set exact de ce run (favoris du run précédent remplacés : le
        /// nettoyage nocturne ne revert que ce set frais).
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

            WriteState(added);
            logger?.Info("[LLM_AI] Favoris : {0} mis en favori pour « {1} » ({2} déjà favori(s) ignoré(s), {3} non résolu(s)).",
                added.Count, user.Name, skippedFavorite, skippedResolve);
        }

        // ------------------------------------------------------------------
        //  Reprise des favoris (nettoyage 3 h)
        // ------------------------------------------------------------------

        /// <summary>
        /// Retire les favoris <b>que nous avons posés</b> au dernier run
        /// (fichier d'état) — et seulement eux : un item est re-favorisé
        /// (retour à non-favori) SI il est encore en favori. L'usager de
        /// chaque entrée est re-résolu par l'id stocké dans le fichier d'état
        /// (<paramref name="users"/>) ; une entrée sans usager résolvable
        /// est ignorée. Le fichier d'état est supprimé ensuite ;
        /// absent/corrompu → no-op (ne lève jamais).
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

            try { File.Delete(StatePath); } catch (Exception ex) { logger?.Warn("[LLM_AI] Favoris : suppression fichier d'état échouée : {0}", ex.Message); }
            logger?.Info("[LLM_AI] Favoris : {0} rétabli(s), {1} déjà absent(s)/item supprimé(s), {2} échec(s).",
                reverted, alreadyGone, failed);
        }

        // ------------------------------------------------------------------
        //  Fichier d'état
        // ------------------------------------------------------------------

        private sealed class StateEntry
        {
            public string ItemId { get; set; }
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

        /// <summary>Réécrit intégralement le fichier d'état (best-effort : un échec est logué, non bloquant).</summary>
        private static void WriteState(List<StateEntry> entries)
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
            catch (Exception) { /* l'état est un confort : ne jamais lever */ }
        }

        /// <summary>Parse le fichier d'état ; null/JSON invalide → set vide.</summary>
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
                            if (el.TryGetProperty("itemId", out var iid) && iid.ValueKind == JsonValueKind.String)
                                entry.ItemId = iid.GetString();
                            if (el.TryGetProperty("userId", out var uid) && uid.ValueKind == JsonValueKind.String)
                                entry.UserId = uid.GetString();
                            result.Add(entry);
                        }
                    }
                    return result;
                }
            }
            catch { return new List<StateEntry>(); }
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