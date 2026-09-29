using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Moniteur de sécurité in-process du plugin (détection — complément de la
    /// prévention SSRF/deux-phases) : compteurs d'activité (appels des outils
    /// web, erreurs, tours de chat refusés, actions déposées/approuvées,
    /// échecs backend) + journal borné des événements de sécurité (SSRF
    /// bloqué, neutralisation de balise spoofée dans un payload web, outil
    /// inconnu, appel d'outil malformé, consommation refusée…).
    /// <para>Volontairement SANS configuration et purement en mémoire
    /// (pattern <see cref="ChatRateLimiter"/>) : zéro coût, remise à zéro au
    /// redémarrage d'Emby. La trace DURABLE est le journal Emby : chaque
    /// événement est loggé <c>[LLM_AI][SEC]</c> — un grep externe, une rotation
    /// de logs ou un watchdog peut donc conserver l'historique complet.</para>
    /// Lecture seule et sans effet sur le flux : <see cref="Record"/> et
    /// <see cref="Count"/> ne lèvent jamais (le monitoring ne doit jamais
    /// casser le chemin appelant). Exposé par l'endpoint admin
    /// <c>GET /Plugins/LLMAI/SecurityMetrics</c> et par la sonde
    /// <c>system_audit action="security_metrics"</c> du rapport d'audit.
    /// </summary>
    internal static class SecurityMonitor
    {
        private static readonly object _lock = new object();

        // Compteurs cumulés (clé = nom du compteur, souvent = kind de
        // l'événement) ; incrément atomique sous lock (débit faible, un lock
        // par événement est sans conséquence).
        private static readonly Dictionary<string, long> _counters =
            new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Capacité du journal borné : au-delà, on évince le plus
        /// ancien (l'historique complet vit dans le journal Emby).</summary>
        private const int MaxEvents = 200;

        /// <summary>Fenêtre renvoyée par le relevé : les N derniers
        /// événements (chronologiques) — un relevé lisible par le LLM.</summary>
        private const int SnapshotEvents = 100;

        private static readonly List<Event> _events = new List<Event>(MaxEvents);

        private static readonly DateTimeOffset _started = DateTimeOffset.UtcNow;

        // Logger partagé : posé une fois par le premier chemin qui a un logger
        // (LlmRunner, WebFetchTool). Nullable — les événements restent
        // comptés/journalisés en mémoire même sans logger (sondes).
        private static ILogger _logger;

        private sealed class Event
        {
            public DateTimeOffset At { get; set; }
            public string Kind { get; set; }
            public string Detail { get; set; }
        }

        /// <summary>Installe le logger de la ligne <c>[LLM_AI][SEC]</c>.
        /// Appelé par LlmRunner/WebFetchTool à la construction (idempotent :
        /// premier logger non null gagne).</summary>
        internal static void SetLogger(ILogger logger)
        {
            if (logger == null) return;
            if (_logger == null) _logger = logger;
        }

        /// <summary>Incrémente un compteur brut, sans événement ni log
        /// (activité nominale — appels web, cache hits).</summary>
        internal static void Count(string counter)
        {
            try
            {
                if (string.IsNullOrEmpty(counter)) return;
                lock (_lock)
                {
                    _counters.TryGetValue(counter, out var v);
                    _counters[counter] = v + 1;
                }
            }
            catch { /* le monitoring ne doit jamais lever */ }
        }

        /// <summary>
        /// Enregistre un événement de sécurité : incrémente le compteur du
        /// kind, appende le journal borné et trace une ligne durable
        /// <c>[LLM_AI][SEC]</c> dans le journal Emby. Ne lève jamais.
        /// </summary>
        internal static void Record(string kind, string detail)
        {
            try
            {
                if (string.IsNullOrEmpty(kind)) return;
                var e = new Event { At = DateTimeOffset.UtcNow, Kind = kind, Detail = detail ?? "" };
                Count(kind);
                lock (_lock)
                {
                    if (_events.Count >= MaxEvents) _events.RemoveAt(0);
                    _events.Add(e);
                }
                _logger?.Info("[LLM_AI][SEC] {0} : {1}", kind, e.Detail);
            }
            catch { /* le monitoring ne doit jamais lever */ }
        }

        /// <summary>
        /// Relevé JSON (endpoint admin + sonde <c>security_metrics</c>) :
        /// fenêtre de collecte, compteurs triés, derniers événements
        /// chronologiques. CamelCase (même conventions que les sondes
        /// system_audit — consommable par le LLM et par l'endpoint).
        /// </summary>
        internal static string SnapshotJson()
        {
            var obj = new JsonObject
            {
                ["generated_at"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["started_at"] = _started.ToString("o", CultureInfo.InvariantCulture),
                ["note"] = "compteurs et événements en mémoire uniquement — remis à zéro au redémarrage d'Emby ; " +
                           "l'historique durable est dans le journal Emby (lignes [LLM_AI][SEC])"
            };

            var counters = new JsonObject();
            lock (_lock)
            {
                foreach (var kv in _counters.OrderBy(k => k.Key, StringComparer.Ordinal))
                    counters[kv.Key] = kv.Value;
            }
            obj["counters"] = counters;

            var evs = new JsonArray();
            lock (_lock)
            {
                int skip = _events.Count > SnapshotEvents ? _events.Count - SnapshotEvents : 0;
                for (int i = skip; i < _events.Count; i++)
                {
                    var e = _events[i];
                    evs.Add(new JsonObject
                    {
                        ["time"] = e.At.ToString("o", CultureInfo.InvariantCulture),
                        ["kind"] = e.Kind,
                        ["detail"] = e.Detail
                    });
                }
            }
            obj["recent_events"] = evs;

            return obj.ToJsonString(s_json);
        }

        private static readonly JsonSerializerOptions s_json = new JsonSerializerOptions
        {
            // Résolveur explicite obligatoire pour sérialiser des JsonNode
            // (même gotcha .NET 8 que WebFetchTool).
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
        };
    }
}