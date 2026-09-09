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
    /// Action de chat « en attente d'approbation » (two-phase) : le tool
    /// <c>plugin_prompts</c> n'écrit JAMAIS la config lui-même — il sérialise
    /// la modification complète (champ, ancien texte, nouveau texte) ici, et
    /// seul un clic « Approuver » de l'admin (endpoint
    /// <c>POST /Plugins/LLMAI/ChatPrompt/Approve</c>) exécute l'écriture en
    /// C# déterministe. Le navigateur n'envoie QUE l'identifiant d'action :
    /// les paramètres viennent de ce store, jamais du client
    /// (anti-détournement).
    /// </summary>
    /// <remarks>
    /// Persistance : <c>chat_pending.json</c> dans le répertoire de
    /// configuration du plugin (même maison que <c>chat_memory.json</c>) —
    /// l'approbation survit à un redémarrage du serveur tant qu'elle n'a pas
    /// expiré. Une seule action en attente par session de chat (une nouvelle
    /// proposition remplace l'ancienne) ; expiration 10 minutes.
    /// </remarks>
    internal sealed class ChatPendingAction
    {
        public string ActionId { get; set; }
        public string Session { get; set; }
        public string User { get; set; }
        /// <summary>Id du champ de prompt visé (liste blanche
        /// <see cref="ChatPromptsTool.FieldIds"/>).</summary>
        public string Field { get; set; }
        public string Label { get; set; }
        public string OldText { get; set; }
        public string NewText { get; set; }
        /// <summary>Avertissement de divergence (null si none) : le
        /// nouveau texte recouvre peu le texte courant — affiché sur la
        /// carte de diff (v1.13.9).</summary>
        public string Warn { get; set; }
        public DateTimeOffset Created { get; set; }
    }

    internal static class ChatPromptStore
    {
        /// <summary>Durée de vie d'une action en attente (au-delà : refusée,
        /// l'usager redemande la sauvegarde dans la conversation).</summary>
        internal static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

        // Canal en mémoire vers la page : le tool pose ici le descriptif du
        // pending créé pendant le tour ; l'endpoint de chat le relève après
        // le run (ChatResponse.Pending) — la page ne parse jamais le texte
        // du LLM pour découvrir une action.
        private static readonly ConcurrentDictionary<string, ChatPendingAction> _pageNotified =
            new ConcurrentDictionary<string, ChatPendingAction>(StringComparer.OrdinalIgnoreCase);

        private static readonly object _fileLock = new object();

        /// <summary>Enregistre une action en attente (une par session : la
        /// nouvelle remplace l'ancienne), la persiste et la publie au canal
        /// page. Retourne l'action créée (null si paramètres invalides).</summary>
        internal static ChatPendingAction Create(PluginConfiguration cfg, string sessionId,
            string userId, string fieldId, string newText, string label, string warn, ILogger logger)
        {
            if (cfg == null || string.IsNullOrWhiteSpace(fieldId) || newText == null) return null;
            var action = new ChatPendingAction
            {
                ActionId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture).Substring(0, 16),
                Session = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId.Trim(),
                User = userId ?? "",
                Field = fieldId.Trim(),
                Label = label ?? "",
                OldText = ChatPromptsTool.GetPrompt(cfg, fieldId) ?? "",
                NewText = newText,
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
        internal static ChatPendingAction TakePagePending(string sessionId)
        {
            var key = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId.Trim();
            return _pageNotified.TryRemove(key, out var a) ? a : null;
        }

        /// <summary>Consulte SANS consommer le descriptif destiné à la page
        /// pour ce tour (v1.13.9.11 : le filet anti-différence du handler
        /// Chat doit savoir si une proposition est déjà en attente avant de
        /// rejouer un nudge — sans la consommer, la carte de diff finale
        /// doit toujours la recevoir).</summary>
        internal static ChatPendingAction PeekPagePending(string sessionId)
        {
            var key = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId.Trim();
            return _pageNotified.TryGetValue(key, out var a) ? a : null;
        }

        /// <summary>Consomme une action en attente : la retire du store et
        /// retourne-la si elle existe, n'a pas expiré, appartient à cet
        /// usager (obligatoire) ET à la session indiquée (vérifiée seulement
        /// si <paramref name="sessionId"/> est non vide — l'endpoint passe
        /// l'id rejoué par la page ; absent, la liaison usager reste).
        /// Null sinon — rien n'est exécutable hors de ces conditions.</summary>
        internal static ChatPendingAction Consume(string actionId, string sessionId, string userId, ILogger logger)
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

        /// <summary>Supprime une action en attente (bouton Refuser) — le
        /// refus n'a pas besoin de la session : la demande de retrait est
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

        private static bool IsExpired(ChatPendingAction a) =>
            a == null || DateTimeOffset.UtcNow - a.Created > Ttl;

        // -----------------------------------------------------------------
        //  IO (chat_pending.json)
        // -----------------------------------------------------------------

        private static string PathOf()
        {
            try
            {
                var dir = Plugin.Paths?.PluginConfigurationsPath;
                if (string.IsNullOrEmpty(dir)) return null;
                return Path.Combine(dir, "chat_pending.json");
            }
            catch { return null; }
        }

        private static System.Collections.Generic.List<ChatPendingAction> LoadAll()
        {
            var list = new System.Collections.Generic.List<ChatPendingAction>();
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
                        var a = new ChatPendingAction
                        {
                            ActionId = OStr(o, "id") ?? "",
                            Session = OStr(o, "s") ?? "",
                            User = OStr(o, "u") ?? "",
                            Field = OStr(o, "f") ?? "",
                            Label = OStr(o, "l") ?? "",
                            OldText = OStr(o, "o") ?? "",
                            NewText = OStr(o, "n") ?? "",
                            Warn = OStr(o, "w")
                        };
                        if (DateTimeOffset.TryParse(OStr(o, "c") ?? "", CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal, out var c))
                            a.Created = c.ToUniversalTime();
                        if (a.ActionId.Length > 0 && a.Field.Length > 0) list.Add(a);
                    }
                }
            }
            catch { /* corrompu : vide (fail-open) */ }
            return list;
        }

        private static void SaveAll(System.Collections.Generic.List<ChatPendingAction> actions, ILogger logger)
        {
            var path = PathOf();
            if (path == null) return;
            try
            {
                var arr = new JsonArray();
                foreach (var a in actions.Where(a => a != null && !IsExpired(a)))
                {
                    arr.Add(new JsonObject
                    {
                        ["id"] = a.ActionId,
                        ["s"] = a.Session,
                        ["u"] = a.User,
                        ["f"] = a.Field,
                        ["l"] = a.Label,
                        ["o"] = a.OldText,
                        ["n"] = a.NewText,
                        ["w"] = a.Warn,
                        ["c"] = a.Created.ToString("o", CultureInfo.InvariantCulture)
                    });
                }
                var root = new JsonObject { ["pending"] = arr };
                File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] chat_pending.json : écriture impossible ({0}).", ex.Message);
            }
        }

        private static string OStr(JsonObject o, string key) =>
            o.TryGetPropertyValue(key, out var v) && v != null ? v.ToString() : null;
    }
}