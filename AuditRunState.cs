using System;
using System.Threading;

namespace LLM_AI
{
    /// <summary>
    /// État partagé du run d'audit <b>détaché</b> (P1+P2, terrain 2026-10-01 :
    /// six clics en rafale avaient lancé six audits concurrents sur un seul
    /// gemma4:latest, et la fermeture de l'onglet annulait tout par
    /// déconnexion HTTP — « Audit annulé » ×9, aucun rapport persisté).
    /// Deux rôles :
    /// <list type="bullet">
    /// <item><b>Single-flight (P1)</b> : <see cref="TryStart"/> ne laisse
    /// passer qu'UN run à la fois — un clic pendant qu'un audit tourne
    /// renvoie l'état « en cours » au lieu d'en relancer un neuf.</item>
    /// <item><b>Progression (P2)</b> : le run s'exécute en tâche de fond
    /// (indépendante de la requête HTTP) et publie ici ses jalons
    /// (« Dose 3/7 — … ») que la page de config interroge via
    /// <c>GET /Plugins/LLMAI/Audit?Status=true</c>.</item>
    /// </list>
    /// Statique volontairement : le plugin est chargé une fois par process
    /// Emby, l'état vit et meurt avec lui (un redémarrage en cours de run
    /// réinitialise proprement — aucun rapport ne traîne en « running »).
    /// </summary>
    /// <remarks>
    /// Le rapport terminé n'est PAS gardé ici : à la fin du run il est
    /// persisté via <see cref="AuditReportStore"/> (branche Status de
    /// <c>AuditApiService</c>) et le client le reçoit dans la même réponse.
    /// <see cref="Outcome"/> distingue « ok » (un vrai rapport a été persisté)
    /// de « error » (échec/annulation — l'éventuel rapport précédent reste
    /// le dernier valide). Ces jetons sont des valeurs de protocole (le
    /// client les compare), pas du texte affiché.
    /// </remarks>
    internal static class AuditRunState
    {
        private static readonly object _lock = new object();
        private static bool _running;
        private static string _progress;         // jalon courant (donnée FR, affichée telle quelle)
        private static DateTimeOffset _startedAt;
        private static DateTimeOffset? _finishedAt;
        private static string _outcome;         // "ok" | "error" du DERNIER run détaché
        private static string _lastError;       // message si outcome = "error"

        /// <summary>
        /// Tente de démarrer un run. True = l'appelant détient le run (il
        /// DOIT appeler <see cref="FinishOk"/> ou <see cref="FinishError"/>
        /// dans un finally équivalent). False = un run est déjà en cours
        /// (single-flight) — l'appelant renvoie l'état au client.
        /// </summary>
        internal static bool TryStart()
        {
            lock (_lock)
            {
                if (_running) return false;
                _running = true;
                _progress = "Collecte des données du serveur…";
                _startedAt = DateTimeOffset.UtcNow;
                _finishedAt = null;
                _outcome = null;
                _lastError = null;
                return true;
            }
        }

        /// <summary>
        /// Publie un jalon de progression. No-op si aucun run n'est actif
        /// (hooks de progression appelés aussi sur les paths non détachés :
        /// tâche planifiée, tests).
        /// </summary>
        internal static void SetProgress(string progress)
        {
            if (string.IsNullOrEmpty(progress)) return;
            lock (_lock)
            {
                if (_running) _progress = progress;
            }
        }

        /// <summary>Fin normale : un rapport a été produit/persisté.</summary>
        internal static void FinishOk()
        {
            lock (_lock)
            {
                if (!_running) return;
                _running = false;
                _progress = null;
                _finishedAt = DateTimeOffset.UtcNow;
                _outcome = "ok";
                _lastError = null;
            }
        }

        /// <summary>
        /// Fin en échec/annulation (timeout, backend, exception). Le rapport
        /// précédemment persisté reste le dernier valide — le client affiche
        /// l'erreur et continue d'afficher l'ancien rapport.
        /// </summary>
        internal static void FinishError(string error)
        {
            lock (_lock)
            {
                if (!_running) return;
                _running = false;
                _progress = null;
                _finishedAt = DateTimeOffset.UtcNow;
                _outcome = "error";
                _lastError = string.IsNullOrWhiteSpace(error) ? "échec" : error;
            }
        }

        /// <summary>Photo atomique de l'état (sérialisable vers le client).</summary>
        internal static AuditRunSnapshot Snapshot()
        {
            lock (_lock)
            {
                return new AuditRunSnapshot(
                    _running,
                    _progress,
                    _running ? _startedAt : (DateTimeOffset?)null,
                    _finishedAt,
                    _outcome,
                    _lastError);
            }
        }
    }

    /// <summary>Photo de l'état du run détaché (voir <see cref="AuditRunState.Snapshot"/>).</summary>
    internal readonly struct AuditRunSnapshot
    {
        public readonly bool Running;
        public readonly string Progress;
        public readonly DateTimeOffset? StartedAt;    // seulement si Running
        public readonly DateTimeOffset? FinishedAt;   // du dernier run détaché terminé
        public readonly string Outcome;                // "ok" | "error" | null (jamais terminé)
        public readonly string Error;                  // message si Outcome = "error"

        internal AuditRunSnapshot(bool running, string progress,
            DateTimeOffset? startedAt, DateTimeOffset? finishedAt,
            string outcome, string error)
        {
            Running = running;
            Progress = progress;
            StartedAt = startedAt;
            FinishedAt = finishedAt;
            Outcome = outcome;
            Error = error;
        }
    }
}