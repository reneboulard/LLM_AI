using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Proposition d'écriture i18n « en attente d'approbation » (two-phase,
    /// v1.17.0.2 — atelier de langues) : le tool <c>i18n_set_key</c>
    /// n'écrit JAMAIS l'overlay — il valide (gates structurelles, clé réelle,
    /// langue normalisée), sérialise la correction complète ici, et seul un
    /// clic « Approuver » de l'admin (endpoint
    /// <c>POST /Plugins/LLMAI/I18nKey/Approve</c>) exécute l'écriture en C#
    /// déterministe. Le navigateur n'envoie QUE l'identifiant d'action : les
    /// paramètres viennent de ce store, jamais du client (anti-détournement).
    /// </summary>
    /// <remarks>
    /// Persistance : <c>chat_pending_i18n.json</c> dans le répertoire de
    /// configuration du plugin (même maison que <c>chat_pending.json</c>) —
    /// l'approbation survit à un redémarrage du serveur tant qu'elle n'a pas
    /// expiré. Une seule proposition en attente par session de chat (une
    /// nouvelle dépôt remplace l'ancienne) ; expiration 10 minutes (miroir de
    /// <c>ChatPromptStore</c> : même TTL, même liaison usager+session à la
    /// consommation).
    /// </remarks>
    internal sealed class ChatI18nPendingAction
    {
        public string ActionId { get; set; }
        public string Session { get; set; }
        public string User { get; set; }
        /// <summary>Famille de la clé (web | server | ext) résolue sur les
        /// natives EN lors du dépôt — re-vérifiée à l'approbation.</summary>
        public string Sec { get; set; }
        /// <summary>Clé exacte (natives).</summary>
        public string Key { get; set; }
        /// <summary>Code langue normalisé (ex. es).</summary>
        public string Lang { get; set; }
        /// <summary>Valeur d'overlay au moment du dépôt (null si la clé
        /// n'avait pas encore de valeur pour cette langue — première écriture
        /// : la carte l'affiche « absente de l'overlay »).</summary>
        public string OldValue { get; set; }
        /// <summary>Nouvelle valeur complète soumise par le LLM.</summary>
        public string NewValue { get; set; }
        /// <summary>Avertissements du dépôt (identique-EN, langue native de
        /// code, normalisation) — null si aucun ; affichés sur la carte.</summary>
        public string Warn { get; set; }
        public DateTimeOffset Created { get; set; }
    }

    internal static class ChatI18nStore
    {
        /// <summary>Durée de vie d'une proposition en attente (miroir du
        /// store de prompts) — au-delà : refusée, l'usager redemande la
        /// correction dans la conversation.</summary>
        internal static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

        // Canal en mémoire vers la page : le tool pose ici le descriptif du
        // pending créé pendant le tour ; l'endpoint de chat le relève après
        // le run (ChatResponse.PendingI18n) — la page ne parse jamais le
        // texte du LLM pour découvrir une proposition.
        private static readonly ConcurrentDictionary<string, ChatI18nPendingAction> _pageNotified =
            new ConcurrentDictionary<string, ChatI18nPendingAction>(StringComparer.OrdinalIgnoreCase);

        private static readonly object _fileLock = new object();

        /// <summary>Enregistre une proposition en attente (une par session :
        /// la nouvelle remplace l'ancienne), la persiste et la publie au canal
        /// page. Retourne l'action créée (null si paramètres invalides).</summary>
        internal static ChatI18nPendingAction Create(string sessionId, string userId,
            string sec, string key, string langKey, string oldValue, string newValue,
            string warn, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(sec) || string.IsNullOrWhiteSpace(key) ||
                string.IsNullOrWhiteSpace(langKey) || newValue == null) return null;
            var action = new ChatI18nPendingAction
            {
                ActionId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture).Substring(0, 16),
                Session = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId.Trim(),
                User = userId ?? "",
                Sec = sec.Trim(),
                Key = key.Trim(),
                Lang = langKey.Trim(),
                OldValue = oldValue,
                NewValue = newValue,
                Warn = string.IsNullOrWhiteSpace(warn) ? null : warn,
                Created = DateTimeOffset.UtcNow
            };
            var all = LoadAll();
            all.RemoveAll(a => a == null || IsExpired(a) ||
                string.Equals(a.Session, action.Session, StringComparison.OrdinalIgnoreCase));
            all.Add(action);
            SaveAll(all, logger);
            _pageNotified[action.Session] = action;
            return action;
        }

        /// <summary>Relève (et vide) le descriptif destiné à la page pour ce
        /// tour de chat.</summary>
        internal static ChatI18nPendingAction TakePagePending(string sessionId)
        {
            var key = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId.Trim();
            return _pageNotified.TryRemove(key, out var a) ? a : null;
        }

        /// <summary>Consomme une proposition en attente : la retire du store
        /// et retourne-la si elle existe, n'a pas expiré, appartient à cet
        /// usager (obligatoire) ET à la session indiquée (vérifiée seulement
        /// si non vide). Null sinon — rien n'est exécutable hors de ces
        /// conditions (single-use : le second clic tombe sur « introuvable »).
        /// </summary>
        internal static ChatI18nPendingAction Consume(string actionId, string sessionId,
            string userId, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(actionId)) return null;
            lock (_fileLock)
            {
                var all = LoadAll();
                var action = all.FirstOrDefault(a => a != null &&
                    string.Equals(a.ActionId, actionId.Trim(), StringComparison.Ordinal));
                if (action == null || IsExpired(action))
                {
                    all.RemoveAll(a => a == null || IsExpired(a) ||
                        (action != null && string.Equals(a.ActionId, action.ActionId, StringComparison.Ordinal)));
                    SaveAll(all, logger);
                    return null;
                }
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    var sessionKey = sessionId.Trim();
                    if (!string.Equals(action.Session, sessionKey, StringComparison.OrdinalIgnoreCase))
                        return null; // pas la session émettrice : rien à exécuter
                }
                if (!string.Equals(action.User, userId ?? "", StringComparison.Ordinal))
                    return null; // pas le propriétaire : rien à exécuter
                all.Remove(action);
                SaveAll(all, logger);
                return action;
            }
        }

        /// <summary>Supprime une proposition en attente (bouton Refuser) —
        /// le refus n'a pas besoin de la session : la demande de retrait est
        /// légitime de n'importe quel admin, et ne fait que nettoyer.</summary>
        internal static void Discard(string actionId, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(actionId)) return;
            lock (_fileLock)
            {
                var all = LoadAll();
                int before = all.Count;
                all.RemoveAll(a => a == null || IsExpired(a) ||
                    string.Equals(a.ActionId, actionId.Trim(), StringComparison.Ordinal));
                if (all.Count != before) SaveAll(all, logger);
            }
        }

        private static bool IsExpired(ChatI18nPendingAction a) =>
            a == null || DateTimeOffset.UtcNow - a.Created > Ttl;

        // -----------------------------------------------------------------
        //  IO (chat_pending_i18n.json)
        // -----------------------------------------------------------------

        private static string PathOf()
        {
            try
            {
                var dir = Plugin.Paths?.PluginConfigurationsPath;
                if (string.IsNullOrEmpty(dir)) return null;
                return Path.Combine(dir, "chat_pending_i18n.json");
            }
            catch { return null; }
        }

        private static System.Collections.Generic.List<ChatI18nPendingAction> LoadAll()
        {
            var list = new System.Collections.Generic.List<ChatI18nPendingAction>();
            var path = PathOf();
            if (path == null || !File.Exists(path)) return list;
            string json;
            try { json = File.ReadAllText(path); }
            catch { return list; }
            try
            {
                if (!(JsonNode.Parse(json) is JsonObject root)) return list;
                if (root.TryGetPropertyValue("pending", out var pv) && pv is JsonArray pa)
                {
                    foreach (var node in pa)
                    {
                        if (!(node is JsonObject o)) continue;
                        var a = new ChatI18nPendingAction
                        {
                            ActionId = OStr(o, "id") ?? "",
                            Session = OStr(o, "s") ?? "",
                            User = OStr(o, "u") ?? "",
                            Sec = OStr(o, "sec") ?? "",
                            Key = OStr(o, "k") ?? "",
                            Lang = OStr(o, "lang") ?? "",
                            OldValue = OStr(o, "o"),
                            NewValue = OStr(o, "n"),
                            Warn = OStr(o, "w")
                        };
                        if (DateTimeOffset.TryParse(OStr(o, "c") ?? "", CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal, out var c))
                            a.Created = c.ToUniversalTime();
                        if (a.ActionId.Length > 0 && a.Key.Length > 0 &&
                            a.Sec.Length > 0 && a.Lang.Length > 0) list.Add(a);
                    }
                }
            }
            catch { /* corrompu : vide (fail-open) */ }
            return list;
        }

        private static void SaveAll(System.Collections.Generic.List<ChatI18nPendingAction> actions, ILogger logger)
        {
            var path = PathOf();
            if (path == null) return;
            try
            {
                var arr = new JsonArray();
                foreach (var a in actions.Where(a => a != null && !IsExpired(a)))
                {
                    var o = new JsonObject
                    {
                        ["id"] = a.ActionId,
                        ["s"] = a.Session,
                        ["u"] = a.User,
                        ["sec"] = a.Sec,
                        ["k"] = a.Key,
                        ["lang"] = a.Lang,
                        ["n"] = a.NewValue,
                        ["c"] = a.Created.ToString("o", CultureInfo.InvariantCulture)
                    };
                    if (a.OldValue != null) o["o"] = a.OldValue;
                    if (a.Warn != null) o["w"] = a.Warn;
                    arr.Add(o);
                }
                var root = new JsonObject { ["pending"] = arr };
                File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] chat_pending_i18n.json : écriture impossible ({0}).", ex.Message);
            }
        }

        private static string OStr(JsonObject o, string key) =>
            o.TryGetPropertyValue(key, out var v) && v != null ? v.ToString() : null;
    }
}