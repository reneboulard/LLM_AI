using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace LLM_AI
{
    /// <summary>
    /// Endpoint HTTP plugin « Relevé de sécurité » : expose
    /// <c>GET /Plugins/LLMAI/SecurityMetrics</c> (admin uniquement) — compteurs
    /// d'activité + fenêtre d'événements de sécurité du plugin
    /// (<see cref="SecurityMonitor"/>) : appels web_fetch/web_search, SSRF
    /// bloqués, appels d'outils malformés/inconnus, échecs backend, tours de
    /// chat refusés, actions déposées/approuvées/refusées. Lecture seule,
    /// zéro LLM : c'est l'instrument de DÉTECTION à côté des sondes
    /// système de <c>system_audit</c>. Volatile (mémoire) — la trace durable
    /// est le journal Emby (lignes <c>[LLM_AI][SEC]</c>).
    /// </summary>
    /// <remarks>Service ServiceStack découvert par scanning d'assembly :
    /// hérite <see cref="BaseApiService"/> (calqué sur
    /// <see cref="AuditApiService"/> — résolution de l'usager par le token,
    /// admin uniquement : le relevé expose le comportement du plugin et des
    /// usagers).</remarks>
    public class SecurityMetricsApiService : BaseApiService
    {
        // ------------------------------------------------------------------
        //  DTO requête / réponse
        // ------------------------------------------------------------------

        /// <summary>Requête GET <c>/Plugins/LLMAI/SecurityMetrics</c>.</summary>
        [Route("/Plugins/LLMAI/SecurityMetrics", "GET")]
        public class SecurityMetricsRequest : IReturn<object>
        {
        }

        /// <summary>Réponse du relevé. Volatile — remis à zéro au
        /// redémarrage d'Emby ; l'historique durable est dans le journal Emby
        /// (lignes <c>[LLM_AI][SEC]</c>).</summary>
        public class SecurityMetricsResponse
        {
            public string GeneratedAt { get; set; }
            public string StartedAt { get; set; }
            public string Error { get; set; }
            public Dictionary<string, long> Counters { get; set; }
            public List<SecurityEventDto> Events { get; set; }
        }

        public class SecurityEventDto
        {
            public string Time { get; set; }
            public string Kind { get; set; }
            public string Detail { get; set; }
        }

        // ------------------------------------------------------------------
        //  Handler GET
        // ------------------------------------------------------------------

        public Task<object> Get(SecurityMetricsRequest req)
        {
            // Réservé aux administrateurs : le relevé expose l'activité et
            // les refus usager-par-usager (chat, actions) — pas pour tous.
            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
            {
                return Task.FromResult<object>(new SecurityMetricsResponse
                {
                    GeneratedAt = DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                    Error = "Réservé aux administrateurs."
                });
            }

            // Le relevé JSON du moniteur est déjà la source : on le re-parse
            // en DTO typé pour la sérialisation hôte (même forme, PascalCase).
            var dto = new SecurityMetricsResponse
            {
                GeneratedAt = DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                Counters = new Dictionary<string, long>(StringComparer.Ordinal),
                Events = new List<SecurityEventDto>()
            };
            try
            {
                using (var doc = JsonDocument.Parse(SecurityMonitor.SnapshotJson()))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("started_at", out var started))
                        dto.StartedAt = started.GetString();
                    if (root.TryGetProperty("counters", out var counters) &&
                        counters.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var c in counters.EnumerateObject())
                            dto.Counters[c.Name] = c.Value.ValueKind == JsonValueKind.Number
                                ? c.Value.GetInt64() : 0;
                    }
                    if (root.TryGetProperty("recent_events", out var events) &&
                        events.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var e in events.EnumerateArray())
                        {
                            dto.Events.Add(new SecurityEventDto
                            {
                                Time = e.TryGetProperty("time", out var t) ? t.GetString() : null,
                                Kind = e.TryGetProperty("kind", out var k) ? k.GetString() : null,
                                Detail = e.TryGetProperty("detail", out var d) ? d.GetString() : null
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                dto.Error = "relevé indisponible : " + ex.Message;
            }
            return Task.FromResult<object>(dto);
        }

        // ------------------------------------------------------------------
        //  Auth : résolution de l'administrateur appelant
        // ------------------------------------------------------------------

        /// <summary>Calqué sur <c>AuditApiService.ResolveAdmin</c> : priorité
        /// au User du token, puis au UserId du token.</summary>
        private User ResolveAdmin()
        {
            try
            {
                var auth = AuthorizationContext?.GetAuthorizationInfo(Request);
                var user = auth?.User;
                if (user == null && auth != null && auth.UserId != 0)
                    user = UserManager.GetUserById(auth.UserId);
                return user;
            }
            catch { return null; }
        }
    }
}