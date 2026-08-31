using System;
using System.Collections.Generic;
using System.Linq;

namespace LLM_AI
{
    /// <summary>
    /// Cache en mémoire partagé (24h) pour les outils web
    /// (<see cref="WebSearchTool"/>, <see cref="WebFetchTool"/>). Économise le
    /// quota de l'API cloud Ollama (web_search/web_fetch sont comptés) en
    /// servant les requêtes identiques depuis le cache pendant 24h — même
    /// fenêtre de fraîcheur que l'outil new_releases. Cohérent avec l'usage de
    /// l'agent (tâche de recommandation quotidienne : l'info d'une série ne
    /// bouge pas en 24h).
    /// <para>Ne cache QUE les résultats réussis (l'appelant décide de n'appeler
    /// <see cref="Set"/> qu'en cas de succès) ; les erreurs ne sont pas
    /// mémorisées. Thread-safe (lock). Croissance bornée : au-delà de 200
    /// entrées on purge les plus anciennes / expirées.</para>
    /// </summary>
    internal static class WebResultCache
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
        private const int MaxEntries = 200;
        private const int KeepEntries = 150;

        private static readonly Dictionary<string, Entry> _store =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly object _lock = new object();

        private struct Entry
        {
            public string Result;
            public DateTime At;
        }

        /// <summary>
        /// Tente de récupérer un résultat cached pour <paramref name="key"/>.
        /// Retourne false si absent ou expiré (&gt; 24h).
        /// </summary>
        public static bool TryGet(string key, out string result)
        {
            result = null;
            if (string.IsNullOrEmpty(key)) return false;
            lock (_lock)
            {
                if (_store.TryGetValue(key, out var e) &&
                    DateTime.UtcNow - e.At < Ttl)
                {
                    result = e.Result;
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Mémorise un résultat réussi pour <paramref name="key"/> (24h).
        /// N'opère pas si la clé ou le résultat est vide.
        /// </summary>
        public static void Set(string key, string result)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(result)) return;
            lock (_lock)
            {
                _store[key] = new Entry { Result = result, At = DateTime.UtcNow };
                if (_store.Count > MaxEntries) Trim();
            }
        }

        /// <summary>
        /// Normalise une clé de cache : trim + collapse des espaces + minuscules.
        /// Utilisé pour les requêtes web_search (la recherche est insensible à
        /// la casse/espacement).
        /// </summary>
        public static string NormalizeQuery(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return string.Empty;
            var parts = q.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts).ToLowerInvariant();
        }

        /// <summary>
        /// Normalise une URL pour la clé de cache : trim + minuscules du schéma/
        /// hôte (la casse du chemin/query est conservée pour éviter les faux
        /// positifs sur les URLs sensibles à la casse).
        /// </summary>
        public static string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
            url = url.Trim();
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            {
                var host = (u.Host ?? "").ToLowerInvariant();
                var scheme = (u.Scheme ?? "").ToLowerInvariant();
                return scheme + "://" + host + u.PathAndQuery + u.Fragment;
            }
            return url.ToLowerInvariant();
        }

        private static void Trim()
        {
            var now = DateTime.UtcNow;
            // 1) Purge les entrées expirées.
            foreach (var kv in _store.Where(x => now - x.Value.At >= Ttl).ToList())
                _store.Remove(kv.Key);
            // 2) Si encore trop plein, garde les plus récentes.
            if (_store.Count > KeepEntries)
            {
                foreach (var kv in _store.OrderBy(x => x.Value.At)
                              .Take(_store.Count - KeepEntries).ToList())
                    _store.Remove(kv.Key);
            }
        }
    }
}