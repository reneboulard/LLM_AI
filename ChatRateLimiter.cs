using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace LLM_AI
{
    /// <summary>
    /// Limiteur de débit <b>chat externe</b> (v1.13.21.2) : le chat est une
    /// entrée usagers dans Emby et chaque tour déclenche un appel LLM
    /// (possiblement cloud payant) — anti-spam, enfant impatient, boucle ou
    /// script déréglé.
    /// <list type="bullet">
    /// <item><see cref="TryConsumeTurn"/> : fenêtres glissantes par usager
    ///   (par minute + par 24 h, plafonds issus de la configuration —
    ///   <c>0 = illimité</c>). Un tour refusé N'EST PAS consommé.</item>
    /// <item><see cref="TryBeginTurnLock"/> : un tour LLM à la fois par
    ///   usager (<c>SemaphoreSlim(1,1)</c>, acquisition non bloquante) — le
    ///   second message pendant une réponse en cours reçoit un refus
    ///   immédiat, pas un tour en plus.</item>
    /// <item><see cref="TryConsumeShow"/> : projection anti-rafale
    ///   (fenêtre fixe généreuse, sans config — la projection ne coûte pas
    ///   de LLM).</item>
    /// </list>
    /// État purement en mémoire (reset au restart Emby — voulu : pas de
    /// persistance d'un compteur de chat), clé = nom d'usager RÉSOLU par la
    /// gate (pas d'IP : tout l'arrivée du loopback de l'app compagnon).
    /// Accès sous un lock global — le débit visé est faible (chats de
    /// foyer), le pattern test-puis-écrit sous lock est celui de
    /// <see cref="ActivateFeedback"/> (simple et race-free).
    /// </summary>
    public static class ChatRateLimiter
    {
        /// <summary>Plafond anti-rafale de la projection Show (par usager,
        /// fenêtre glissante de 60 s). Volontairement SANS config : la
        /// projection ne coûte pas d'appel LLM, on ne vise que l'abus.</summary>
        private const int MaxShowPerMinute = 30;

        private const int MinuteSeconds = 60;
        private static readonly long DayTicks = TimeSpan.FromHours(24).Ticks;

        // clé = nom d'usager → horodatages (DateTimeOffset.UtcNow.Ticks) des
        // tours acceptés ; trié ascendant (append-only, prune en tête).
        // DEUX stores SÉPARÉS : les demandes Show ne comptent pas dans les
        // fenêtres des tours de chat (et réciproquement) — une rafale de
        // projections ne doit pas bloquer le chat.
        private static readonly ConcurrentDictionary<string, List<long>> s_turns
            = new ConcurrentDictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, List<long>> s_shows
            = new ConcurrentDictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);

        // Verrou de concurrence : un tour LLM en vol par usager.
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_inflight
            = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        // Sérialise l'accès test-puis-écrit des fenêtres (deux requêtes
        // simultanées du même usager ne doivent pas toutes deux compter).
        private static readonly object s_gate = new object();

        /// <summary>Libération du verrou de tour (dispose par le caller).</summary>
        private sealed class TurnRelease : IDisposable
        {
            private readonly SemaphoreSlim _sem;
            public TurnRelease(SemaphoreSlim sem) { _sem = sem; }
            public void Dispose() { _sem.Release(); }
        }

        // ------------------------------------------------------------------
        //  Tour de chat — fenêtres glissantes
        // ------------------------------------------------------------------

        /// <summary>
        /// Consomme un tour de chat pour <paramref name="user"/> si les deux
        /// fenêtres glissantes (60 s / 24 h) le permettent. Un tour refusé
        /// n'est pas compté. <paramref name="maxPerMinute"/> et
        /// <paramref name="maxPerDay"/> &lt;= 0 = fenêtre illimitée.
        /// </summary>
        public static bool TryConsumeTurn(string user, int maxPerMinute, int maxPerDay,
            out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(user))
            {
                error = "Usager invalide.";
                return false;
            }
            var now = DateTimeOffset.UtcNow;
            long minuteCutoff = now.AddSeconds(-MinuteSeconds).UtcTicks;
            long dayCutoff = now.UtcTicks - DayTicks;
            bool unlimitedMinute = maxPerMinute <= 0;
            bool unlimitedDay = maxPerDay <= 0;

            lock (s_gate)
            {
                PruneLocked(now);

                var window = s_turns.GetOrAdd(user, _ => new List<long>());
                Count(window, minuteCutoff, dayCutoff,
                      out int lastMinute, out int lastDay);

                if (!unlimitedMinute && lastMinute >= maxPerMinute)
                {
                    // Attente = délai jusqu'à ce que le plus ancien tour de la
                    // minute sorte de la fenêtre (les tours plus récents
                    // sortiront après lui).
                    int wait = SecondsUntilFree(window, minuteCutoff, MinuteSeconds, now);
                    error = "Trop de messages — patientez " + Math.Max(1, wait)
                            + " s (limite : " + maxPerMinute + " par minute).";
                    return false;
                }
                if (!unlimitedDay && lastDay >= maxPerDay)
                {
                    error = "Quota du jour atteint (" + lastDay + " messages) — "
                            + "réessayez plus tard.";
                    return false;
                }

                window.Add(now.UtcTicks);
                return true;
            }
        }

        // ------------------------------------------------------------------
        //  Verrou de concurrence — un tour LLM en vol par usager
        // ------------------------------------------------------------------

        /// <summary>
        /// Acquiert (non bloquant) le verrou « un tour LLM à la fois » de
        /// <paramref name="user"/>. Le caller DOIT disposer le
        /// <paramref name="release"/> dans un <c>finally</c> dès qu'il a
        /// obtenu <c>true</c>.
        /// </summary>
        public static bool TryBeginTurnLock(string user, out IDisposable release)
        {
            release = null;
            if (string.IsNullOrWhiteSpace(user)) return false;
            var sem = s_inflight.GetOrAdd(user, _ => new SemaphoreSlim(1, 1));
            if (!sem.Wait(0))
                return false;
            release = new TurnRelease(sem);
            return true;
        }

        // ------------------------------------------------------------------
        //  Show — anti-rafale
        // ------------------------------------------------------------------

        /// <summary>Fenêtre glissante fixe (30/min/usager) pour la projection.</summary>
        public static bool TryConsumeShow(string user, out string error)
        {
            return TryConsumeWindow(s_shows, user, MaxShowPerMinute,
                "Trop de demandes de projection — patientez {0} s.", out error);
        }

        /// <summary>Fenêtre glissante unique à plafond fixe (pattern commun
        /// de TryConsumeTurn/TryConsumeShow pour l'attente calculée).
        /// <paramref name="store"/> isole la fenêtre (tours ≠ projections).</summary>
        private static bool TryConsumeWindow(ConcurrentDictionary<string, List<long>> store,
            string user, int max, string errorFormat, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(user))
            {
                error = "Usager invalide.";
                return false;
            }
            var now = DateTimeOffset.UtcNow;
            long cutoff = now.AddSeconds(-MinuteSeconds).UtcTicks;

            lock (s_gate)
            {
                PruneLocked(now);

                var window = store.GetOrAdd(user, _ => new List<long>());
                Count(window, cutoff, long.MaxValue, out int cnt, out _);
                if (cnt >= max)
                {
                    int wait = SecondsUntilFree(window, cutoff, MinuteSeconds, now);
                    error = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        errorFormat, Math.Max(1, wait));
                    return false;
                }
                window.Add(now.UtcTicks);
                return true;
            }
        }

        // ------------------------------------------------------------------
        //  Helpers fenêtres
        // ------------------------------------------------------------------

        /// <summary>Compte les tours postérieurs aux bornes (minute, jour).</summary>
        private static void Count(List<long> window, long minuteCutoff, long dayCutoff,
            out int lastMinute, out int lastDay)
        {
            int minute = 0, day = 0;
            // window est trié ascendant : scan linéaire borné.
            foreach (var t in window)
            {
                if (t > minuteCutoff) minute++;
                if (t > dayCutoff) day++;
            }
            lastMinute = minute;
            lastDay = day;
        }

        /// <summary>Secondes d'attente avant que le plus ancien tour bloquant
        /// sorte de la fenêtre (arrondi supérieur, minimum 1).</summary>
        private static int SecondsUntilFree(List<long> window, long cutoff,
            int windowSeconds, DateTimeOffset now)
        {
            long oldestInWindow = 0;
            foreach (var t in window)
            {
                if (t > cutoff) { oldestInWindow = t; break; }
            }
            if (oldestInWindow == 0) return 1;
            var exit = new DateTimeOffset(oldestInWindow, TimeSpan.Zero)
                .AddSeconds(windowSeconds);
            double wait = (exit - now).TotalSeconds;
            if (wait < 1) wait = 1;
            return (int)Math.Ceiling(wait);
        }

        /// <summary>Retire les tours de plus de 24 h (tous usagers) et les
        /// usagers sans activité depuis 24 h. Appelé SOUS <see cref="s_gate"/>.</summary>
        private static void PruneLocked(DateTimeOffset now)
        {
            long dayCutoff = now.UtcTicks - DayTicks;
            PruneStore(s_turns, dayCutoff);
            PruneStore(s_shows, dayCutoff);
            // Les s_inflight restent (les SemaphoreSlim y vivent leur cycle
            // normal) — leur dictionnaire ne grossit qu'avec les noms
            // d'usagers listés, borné par la liste blanche.
        }

        private static void PruneStore(ConcurrentDictionary<string, List<long>> store,
            long dayCutoff)
        {
            foreach (var kv in store)
            {
                var w = kv.Value;
                w.RemoveAll(t => t <= dayCutoff);
                if (w.Count == 0)
                    store.TryRemove(kv.Key, out _);
            }
        }
    }
}