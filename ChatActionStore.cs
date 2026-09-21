using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LLM_AI
{
    /// <summary>
    /// Action Emby déposée par un outil du chat en attente d'approbation
    /// (v1.13.29 — deux phases mécaniques) : l'outil du LLM ne s'exécute
    /// JAMAIS directement — il dépose ici une proposition (args JSON figés)
    /// que la page rend sous forme de carte « Approuver / Refuser » ;
    /// l'endpoint <c>/Plugins/LLMAI/ChatAction/Approve</c> consomme la
    /// proposition et exécute en code serveur déterministe — le LLM n'a
    /// AUCUN rôle dans l'exécution (pattern <see cref="ChatPromptStore"/>,
    /// parent du System Guard 2FA de l'app compagnon).
    /// </summary>
    /// <remarks>
    /// <para><b>Liaison de l'action</b> : le pending fige l'outil ET ses
    /// arguments complets (chaîne JSON) — l'approbation porte sur CE qui a
    /// été déposé, rien d'autre. Un code/approbation ne peut pas être
    /// dévié vers d'autres paramètres (anti-TOCTOU).</para>
    /// <para><b>Mémoire uniquement</b> : pas de persistance disque — le
    /// redémarrage vide les pendings (aucun effet destructeur : rien n'a
    /// été exécuté ; l'admin redemande l'action dans la conversation). TTL
    /// 10 min comme <see cref="ChatPromptStore"/> : pas d'approbation
    /// périmée.</para>
    /// <para><b>Un seul consommateur</b> : <see cref="Consume"/> retire le
    /// pending (single-use) après vérification usager + session — un clic
    /// double ne peut pas exécuter deux fois.</para>
    /// </remarks>
    internal class ChatPendingEmbyAction
    {
        public string ActionId { get; set; }
        public string Session { get; set; }
        /// <summary>Usager admin émetteur (liaison obligatoire à
        /// l'approbation — un autre compte ne peut pas approuver).</summary>
        public string User { get; set; }
        /// <summary>Clé d'usine de l'outil (nom exposé au LLM).</summary>
        public string Tool { get; set; }
        /// <summary>Libellé affiché sur la carte (français, même ton que
        /// les toasts de traçabilité).</summary>
        public string Label { get; set; }
        /// <summary>Arguments figés de l'appel (JSON brut).</summary>
        public string ArgsJson { get; set; }
        public DateTimeOffset Created { get; set; }
    }

    internal static class ChatActionStore
    {
        /// <summary>Durée de vie d'une action en attente (au-delà :
        /// introuvable — l'admin redemande l'action dans la conversation).</summary>
        internal static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

        /// <summary>Plafond de propositions en attente par session : au-delà,
        /// <see cref="Create"/> refuse (un LLM ou une injection ne peut pas
        /// noyer l'admin sous les cartes — le dépôt refuse proprement).</summary>
        internal const int MaxPerSession = 10;

        // Store autoritatif (clé = session) : une proposition y VIT jusqu'à
        // consommation, refus ou expiration — le relevé page ne l'enlève pas.
        private static readonly ConcurrentDictionary<string, List<ChatPendingEmbyAction>> _store =
            new ConcurrentDictionary<string, List<ChatPendingEmbyAction>>(StringComparer.OrdinalIgnoreCase);

        // Canal en mémoire vers la page (indépendant du store, pattern
        // ChatPromptStore) : le tool pose ici le descriptif du pending créé
        // pendant le tour ; l'endpoint de chat relève et VIDE ce canal à la
        // fin du tour (ChatResponse.PendingActions) — la page ne parse jamais
        // le texte du LLM pour découvrir une action.
        private static readonly ConcurrentDictionary<string, List<ChatPendingEmbyAction>> _pageNotified =
            new ConcurrentDictionary<string, List<ChatPendingEmbyAction>>(StringComparer.OrdinalIgnoreCase);

        private static readonly object _lock = new object();

        /// <summary>Clé de session normalisée (même règle que
        /// <c>ChatActions.For</c>).</summary>
        private static string Key(string sessionId) =>
            string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId.Trim();

        /// <summary>Dépose une action en attente pour la session (store
        /// autoritatif + canal page). Retourne l'action créée, ou null si le
        /// plafond de la session est atteint (aucune proposition n'est
        /// alors créée).</summary>
        internal static ChatPendingEmbyAction Create(string sessionId, string userId,
            string tool, string argsJson, string label)
        {
            if (string.IsNullOrWhiteSpace(tool) || argsJson == null) return null;
            var action = new ChatPendingEmbyAction
            {
                ActionId = Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture).Substring(0, 16),
                Session = Key(sessionId),
                User = userId ?? "",
                Tool = tool.Trim(),
                Label = label ?? "",
                ArgsJson = argsJson,
                Created = DateTimeOffset.UtcNow
            };
            lock (_lock)
            {
                var list = _store.GetOrAdd(action.Session, _ => new List<ChatPendingEmbyAction>());
                Prune(list);
                if (list.Count >= MaxPerSession) return null;
                list.Add(action);
                var page = _pageNotified.GetOrAdd(action.Session, _ => new List<ChatPendingEmbyAction>());
                Prune(page);
                page.Add(action);
            }
            return action;
        }

        /// <summary>Relève (et vide) les descriptifs destinés à la page pour
        /// ce tour de chat — le store autoritatif n'est PAS touché (les
        /// propositions restent consommables jusqu'à TTL/approbation) ; la
        /// page ne parse jamais le texte du LLM pour les découvrir (même
        /// règle que les prompts).</summary>
        internal static List<ChatPendingEmbyAction> TakePagePending(string sessionId)
        {
            var key = Key(sessionId);
            lock (_lock)
            {
                if (!_pageNotified.TryGetValue(key, out var page)) return new List<ChatPendingEmbyAction>();
                var outList = page.Where(a => a != null && !IsExpired(a)).ToList();
                page.Clear();
                return outList;
            }
        }

        /// <summary>Consomme une action en attente : la retire du store et
        /// la retourne si elle existe, n'a pas expiré, appartient à cet
        /// usager (obligatoire) ET à la session indiquée (vérifiée seulement
        /// si <paramref name="sessionId"/> est non vide). Null sinon — rien
        /// n'est exécutable hors de ces conditions.</summary>
        internal static ChatPendingEmbyAction Consume(string actionId, string sessionId, string userId)
        {
            if (string.IsNullOrWhiteSpace(actionId)) return null;
            lock (_lock)
            {
                foreach (var kv in _store)
                {
                    var list = kv.Value;
                    var action = list.FirstOrDefault(a => a != null &&
                        string.Equals(a.ActionId, actionId.Trim(), StringComparison.Ordinal));
                    if (action == null) continue;
                    if (IsExpired(action))
                    {
                        list.Remove(action);
                        return null;
                    }
                    if (!string.IsNullOrWhiteSpace(sessionId) &&
                        !string.Equals(action.Session, sessionId.Trim(), StringComparison.OrdinalIgnoreCase))
                        return null; // pas la session émettrice : rien à exécuter
                    if (!string.Equals(action.User, userId ?? "", StringComparison.Ordinal))
                        return null; // pas le propriétaire : rien à exécuter
                    list.Remove(action);
                    return action;
                }
                return null;
            }
        }

        /// <summary>Supprime une action en attente (bouton Refuser) — le
        /// refus n'exécute rien et ne requiert pas la session (nettoyage
        /// légitime par n'importe quel admin, comme les prompts).</summary>
        internal static void Discard(string actionId)
        {
            if (string.IsNullOrWhiteSpace(actionId)) return;
            lock (_lock)
            {
                foreach (var kv in _store)
                    kv.Value.RemoveAll(a => a == null || IsExpired(a) ||
                        string.Equals(a.ActionId, actionId.Trim(), StringComparison.Ordinal));
                foreach (var kv in _pageNotified)
                    kv.Value.RemoveAll(a => a == null || IsExpired(a) ||
                        string.Equals(a.ActionId, actionId.Trim(), StringComparison.Ordinal));
            }
        }

        private static void Prune(List<ChatPendingEmbyAction> list)
        {
            list.RemoveAll(a => a == null || IsExpired(a));
        }

        private static bool IsExpired(ChatPendingEmbyAction a) =>
            a == null || DateTimeOffset.UtcNow - a.Created > Ttl;
    }
}