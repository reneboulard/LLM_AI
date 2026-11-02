using System;
using System.Threading;

namespace LLM_AI
{
    /// <summary>
    /// État partagé du run « Gestion bibliothèque » <b>détaché</b> (v2.10.0) —
    /// même contrat que <see cref="AuditRunState"/> : single-flight (un seul
    /// run à la fois, les clics en rafale renvoient l'état en cours) + run en
    /// tâche de fond dont les jalons sont publiés ici pour le polling
    /// <c>GET /Plugins/LLMAI/LibReview?Status=true</c>. Quitter la page de
    /// config coupe le polling, PAS le run. Statique volontairement : l'état
    /// vit et meurt avec le process Emby.
    /// </summary>
    internal static class LibReviewRunState
    {
        private static readonly object _lock = new object();
        private static bool _running;
        private static string _progress;         // jalon courant (localisé par l'appelant)
        private static DateTimeOffset _startedAt;
        private static DateTimeOffset? _finishedAt;
        private static string _outcome;         // "ok" | "error" du DERNIER run détaché
        private static string _lastError;       // message si outcome = "error"

        /// <summary>
        /// Tente de démarrer un run. True = l'appelant détient le run (il
        /// DOIT appeler <see cref="FinishOk"/> ou <see cref="FinishError"/>
        /// dans un finally équivalent). False = un run est déjà en cours.
        /// </summary>
        internal static bool TryStart(string initialProgress)
        {
            lock (_lock)
            {
                if (_running) return false;
                _running = true;
                _progress = initialProgress ?? string.Empty;
                _startedAt = DateTimeOffset.UtcNow;
                _finishedAt = null;
                _outcome = null;
                _lastError = null;
                return true;
            }
        }

        /// <summary>Publie un jalon de progression. No-op si aucun run actif.</summary>
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

        /// <summary>Fin en échec/annulation. Le rapport précédemment persisté reste le dernier valide.</summary>
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
        internal static LibReviewRunSnapshot Snapshot()
        {
            lock (_lock)
            {
                return new LibReviewRunSnapshot(
                    _running,
                    _progress,
                    _running ? _startedAt : (DateTimeOffset?)null,
                    _finishedAt,
                    _outcome,
                    _lastError);
            }
        }
    }

    /// <summary>Photo de l'état du run détaché (voir <see cref="LibReviewRunState.Snapshot"/>).</summary>
    internal readonly struct LibReviewRunSnapshot
    {
        public readonly bool Running;
        public readonly string Progress;
        public readonly DateTimeOffset? StartedAt;
        public readonly DateTimeOffset? FinishedAt;
        public readonly string Outcome;   // "ok" | "error" | null (jamais terminé)
        public readonly string Error;

        internal LibReviewRunSnapshot(bool running, string progress,
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
