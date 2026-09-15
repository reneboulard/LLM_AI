using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Globalization;

namespace LLM_AI
{
    /// <summary>
    /// Store du <b>bucket d'enregistrements à confirmer</b> du chat externe
    /// (v1.13.23, human-in-the-loop à PIN) : le tool <c>record_program</c>
    /// y dépose une RÉSERVATION (aucun effet serveur) avec un code à
    /// 4 chiffres généré ICI ; seul le code — fourni par l'usager dans son
    /// message, relayé par l'app compagnon — déclenche la création du timer.
    /// <list type="bullet">
    /// <item><b>Code hors bande</b> : le PIN n'est JAMAIS remis au LLM —
    ///     il est retiré du store par l'endpoint et ajouté au DTO de
    ///     réponse (<c>ConfirmCode</c>), affiché par l'app à l'écran.
    ///     L'humain est le seul canal.</item>
    /// <item><b>Verrou « log-in »</b> : 3 codes erronés pour un usager →
    ///     fiche détruite + verrou 15 min sur <c>record</c> ET
    ///     <c>confirm</c> (impossible d'obtenir un nouveau code pendant le
    ///     verrou). Un essai raté ne compte JAMAIS le quota.</item>
    /// <item><b>Quota dur</b> : compteur glissant 24 h par usager, compté
    ///     UNIQUEMENT à la création réelle du timer (confirm réussi) — les
    ///     réservations (expirées, refusées) ne paient rien.</item>
    /// <item><b>Un pending par usager</b> : une nouvelle réservation
    ///     remplace la précédente (le code d'un vieux pending confirmé trop
    ///     tard ne crée rien).</item>
    /// <item><b>En mémoire</b> (reset au restart Emby — voulu, même
    ///     sémantique que <see cref="ChatRateLimiter"/> : un pending ou un
    ///     verrou perdu n'a jamais d'effet serveur). Accès sous un lock
    ///     global, pattern test-puis-écrit d'<see cref="ActivateFeedback"/>
    ///     (simple et race-free).</item>
    /// </list>
    /// Le code circule en clair dans la réponse API (HTTPS/loopback de
    /// l'app compagnon) — c'est un secret d'ergonomie, pas un secret de
    /// sécurité : il empêche un « oui » ambigü et l'auto-confirmation du
    /// LLM, pas une attaque réseau (les autres couches s'en chargent).
    /// </summary>
    public static class RecordingPendingStore
    {
        private const int PinTtlMinutes = 5;
        private const int LockoutMinutes = 15;
        /// <summary>Essais de code erronés tolérés avant destruction de la
        /// fiche et verrouillage — rendu public pour l'affichage de l'action
        /// <c>status</c> du tool.</summary>
        public const int MaxFailedAttempts = 3;
        private static readonly long DayTicks = TimeSpan.FromHours(24).Ticks;

        /// <summary>Une réservation en attente de son code.</summary>
        public sealed class Pending
        {
            public string User;
            public string ProgramId;
            public string Title;
            public string Kind;           // movie | series
            public int PostPaddingMin;    // plafonné côté tool (≤ 30)
            public string Pin;            // code à 4 chiffres — hors bande
            public long CreatedUtc;       // Ticks
            public long ExpiresUtc;       // Ticks
            public int FailedAttempts;
        }

        private static readonly ConcurrentDictionary<string, Pending> s_pendings
            = new ConcurrentDictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);

        // clé = nom d'usager → Ticks (UtcNow) de fin de verrou.
        private static readonly ConcurrentDictionary<string, long> s_locks
            = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // clé = nom d'usager → horodatages (Ticks) des créations réelles ;
        // trié ascendant (append-only, prune en tête) — même pattern que
        // ChatRateLimiter s_turns.
        private static readonly ConcurrentDictionary<string, List<long>> s_created
            = new ConcurrentDictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);

        // clé = nom d'usager → notice du dernier tour (canal déterministe :
        // refus de confirm = message VÉRIDIQUE joint au DTO, affiché par
        // l'app même si le modèle embellit sa réponse). Consommée une fois.
        private sealed class TurnNotice
        {
            public long CreatedUtc;   // Ticks
            public string Text;
        }
        private static readonly ConcurrentDictionary<string, TurnNotice> s_notices
            = new ConcurrentDictionary<string, TurnNotice>(StringComparer.OrdinalIgnoreCase);

        private static readonly object s_gate = new object();

        // ------------------------------------------------------------------
        //  Réservation (action record)
        // ------------------------------------------------------------------

        /// <summary>
        /// Dépose une réservation pour l'usager (remplace toute fiche
        /// précédente) et génère le code. Refus si l'usager est verrouillé.
        /// <paramref name="pin"/> est remis à l'APPELANT INTERNE (endpoint)
        /// seul — il ne doit jamais rejoindre le texte du LLM.
        /// <paramref name="langKey"/> : langue de l'usager pour les libellés.
        /// </summary>
        public static bool TryReserve(string user, string programId, string title,
            string kind, int postPaddingMin, out string pin, out string error,
            string langKey = null)
        {
            pin = null;
            lock (s_gate)
            {
                PruneLocked(DateTime.UtcNow.Ticks);
                long until;
                if (s_locks.TryGetValue(user, out until) && until > DateTime.UtcNow.Ticks)
                {
                    error = LockedMessage(user, until, langKey);
                    return false;
                }
                pin = GeneratePin();
                s_pendings[user] = new Pending
                {
                    User = user,
                    ProgramId = programId,
                    Title = title,
                    Kind = kind,
                    PostPaddingMin = postPaddingMin,
                    Pin = pin,
                    CreatedUtc = DateTime.UtcNow.Ticks,
                    ExpiresUtc = DateTime.UtcNow.Ticks + TimeSpan.FromMinutes(PinTtlMinutes).Ticks
                };
                error = null;
                return true;
            }
        }

        /// <summary>Une réservation non expirée existe pour l'usager (et pas
        /// de verrou) — la base de l'interception endpoint
        /// (<see cref="RecordingChatTool.TryConfirmFromMessageAsync"/>).
        /// Purge les fiches et verrous périmés au passage.</summary>
        public static bool HasPending(string user)
        {
            lock (s_gate)
            {
                long now = DateTime.UtcNow.Ticks;
                PruneLocked(now);
                long until;
                if (s_locks.TryGetValue(user, out until) && until > now) return false;
                Pending p;
                return s_pendings.TryGetValue(user, out p) && p.ExpiresUtc > now;
            }
        }

        /// <summary>Vue de lecture de la réservation — SANS le code (il ne
        /// quitte JAMAIS le store). Base de l'action <c>status</c> du tool.</summary>
        public sealed class PendingView
        {
            public string Title;
            public string Kind;             // movie | series
            public int PostPaddingMin;
            public long ExpiresUtc;         // Ticks
            public int FailedAttempts;
        }

        /// <summary>La fiche non expirée de l'usager, vue SANS code —
        /// null si rien en attente ou si l'usager est verrouillé.</summary>
        public static PendingView DescribePending(string user)
        {
            lock (s_gate)
            {
                long now = DateTime.UtcNow.Ticks;
                PruneLocked(now);
                long until;
                if (s_locks.TryGetValue(user, out until) && until > now) return null;
                Pending p;
                if (!s_pendings.TryGetValue(user, out p) || p.ExpiresUtc <= now) return null;
                return new PendingView
                {
                    Title = p.Title,
                    Kind = p.Kind,
                    PostPaddingMin = p.PostPaddingMin,
                    ExpiresUtc = p.ExpiresUtc,
                    FailedAttempts = p.FailedAttempts
                };
            }
        }

        /// <summary>Minutes restantes de verrouillage de l'usager (0 si pas
        /// de verrou) — l'état « outil verrouillé » de l'action status.</summary>
        public static int LockMinutesLeft(string user)
        {
            lock (s_gate)
            {
                long now = DateTime.UtcNow.Ticks;
                PruneLocked(now);
                long until;
                return s_locks.TryGetValue(user, out until) && until > now
                    ? Math.Max(1, (int)Math.Ceiling((until - now) / 600_000_000.0))
                    : 0;
            }
        }

        // ------------------------------------------------------------------
        //  Confirmation (action confirm)
        // ------------------------------------------------------------------

        /// <summary>
        /// Vérifie le code de l'usager : verrou → fiche présente et non
        /// expirée → code (temps constant). Code erroné → compteur
        /// d'essais ; à 3, la fiche est détruite et le verrou posé.
        /// Code correct → fiche RETIRÉE (consommée une fois) et remise.
        /// <paramref name="langKey"/> : langue de l'usager pour les libellés
        /// (erreurs ET notice anti-menteur — la notice est affichée par
        /// l'app compagnon, elle doit parler la langue de l'usager).
        /// </summary>
        public static bool TryConfirm(string user, string pin, out Pending pending,
            out string error, string langKey = null)
        {
            pending = null;
            lock (s_gate)
            {
                long now = DateTime.UtcNow.Ticks;
                long until;
                if (s_locks.TryGetValue(user, out until) && until > now)
                {
                    error = LockedMessage(user, until, langKey);
                    SetNoticeLocked(user, error);
                    return false;
                }

                Pending p;
                if (!s_pendings.TryGetValue(user, out p) || p.ExpiresUtc <= now)
                {
                    if (p != null) s_pendings.TryRemove(user, out p); // expiré : mort
                    error = I18n.S("rec.pending.none", langKey);
                    SetNoticeLocked(user, I18n.S("rec.notice.none", langKey));
                    return false;
                }

                if (!ConstantTimeEquals(p.Pin, pin ?? string.Empty))
                {
                    p.FailedAttempts++;
                    if (p.FailedAttempts >= MaxFailedAttempts)
                    {
                        s_pendings.TryRemove(user, out p);   // fiche détruite
                        long lockUntil = now + TimeSpan.FromMinutes(LockoutMinutes).Ticks;
                        s_locks[user] = lockUntil;
                        error = LockedMessage(user, lockUntil, langKey);
                        SetNoticeLocked(user, I18n.S("rec.notice.locked", langKey));
                        return false;
                    }
                    error = string.Format(CultureInfo.CurrentUICulture,
                        I18n.S("rec.notice.wrong", langKey), p.FailedAttempts, MaxFailedAttempts)
                        + " " + (langKey == I18n.Fr
                            ? "Demandez-le à l'usager ; ne devinez, n'inventez et ne réessayez pas d'autres codes."
                            : "Ask the user for it; never guess, invent or try other codes.");
                    SetNoticeLocked(user, string.Format(CultureInfo.CurrentUICulture,
                        I18n.S("rec.notice.wrong", langKey), p.FailedAttempts, MaxFailedAttempts));
                    return false;
                }

                // Code correct : consommation unique.
                s_pendings.TryRemove(user, out p);
                pending = p;
                error = null;
                return true;
            }
        }

        // ------------------------------------------------------------------
        //  Notice du tour (canal déterministe, anti-menteur)
        // ------------------------------------------------------------------

        /// <summary>
        /// Pose la notice VÉRIDIQUE du tour (refus de confirm : code erroné,
        /// verrou, quota, création ratée). Le texte est joint au DTO par
        /// l'endpoint — il ne passe JAMAIS par le modèle.
        /// </summary>
        public static void SetTurnNotice(string user, string text)
        {
            lock (s_gate) SetNoticeLocked(user, text);
        }

        /// <summary>
        /// La notice posée pendant CE tour (créée après
        /// <paramref name="sinceUtcTicks"/>) — consommée une fois, null sinon.
        /// </summary>
        public static string GetFreshNotice(string user, long sinceUtcTicks)
        {
            lock (s_gate)
            {
                TurnNotice n;
                if (!s_notices.TryGetValue(user, out n)) return null;
                s_notices.TryRemove(user, out _);
                if (n.CreatedUtc < sinceUtcTicks) return null;
                return n.Text;
            }
        }

        private static void SetNoticeLocked(string user, string text)
        {
            s_notices[user] = new TurnNotice
            {
                CreatedUtc = DateTime.UtcNow.Ticks,
                Text = text
            };
        }

        // ------------------------------------------------------------------
        //  Quota dur (créations réelles seulement)
        // ------------------------------------------------------------------

        /// <summary>Créations de timers dans la fenêtre glissante 24 h de
        /// l'usager. <paramref name="maxPerDay"/> &lt;= 0 = illimité.</summary>
        public static bool TryConsumeCreation(string user, int maxPerDay, out string error,
            string langKey = null)
        {
            lock (s_gate)
            {
                long now = DateTime.UtcNow.Ticks;
                PruneCreated(user, now);
                if (maxPerDay <= 0)
                {
                    RegisterCreation(user, now);
                    error = null;
                    return true;
                }
                List<long> list;
                if (s_created.TryGetValue(user, out list) && list.Count >= maxPerDay)
                {
                    error = string.Format(CultureInfo.CurrentUICulture,
                        I18n.S("rec.quota", langKey), maxPerDay);
                    return false;
                }
                RegisterCreation(user, now);
                error = null;
                return true;
            }
        }

        /// <summary>Retire la DERNIÈRE création de l'usager (remboursement :
        /// le quota a été consommé mais le timer n'a pas été créé — la
        /// réservation payée ne correspond à aucun effet).</summary>
        public static void RefundCreation(string user)
        {
            lock (s_gate)
            {
                List<long> list;
                if (!s_created.TryGetValue(user, out list)) return;
                if (list.Count > 0) list.RemoveAt(list.Count - 1);
                if (list.Count == 0) s_created.TryRemove(user, out _);
            }
        }

        // ------------------------------------------------------------------
        //  Lecture (endpoint — canal hors bande du code)
        // ------------------------------------------------------------------

        /// <summary>
        /// Le code d'une fiche non expirée créée APRÈS <paramref name="sinceUtcTicks"/>
        /// (le tour qui vient de la générer) — null sinon. N'expose jamais
        /// un vieux pending : le code n'est joint au DTO que par le tour
        /// qui l'a produit.
        /// </summary>
        public static string GetFreshCode(string user, long sinceUtcTicks)
        {
            lock (s_gate)
            {
                long now = DateTime.UtcNow.Ticks;
                Pending p;
                if (!s_pendings.TryGetValue(user, out p)) return null;
                if (p.CreatedUtc < sinceUtcTicks || p.ExpiresUtc <= now) return null;
                return p.Pin;
            }
        }

        // ------------------------------------------------------------------
        //  Internes
        // ------------------------------------------------------------------

        /// <summary>Code à 4 chiffres aléatoire, non trivial (rejette les
        /// codes répétitifs/consécutifs simples et régénère).</summary>
        private static string GeneratePin()
        {
            for (int attempt = 0; ; attempt++)
            {
                int code = RandomNumberGenerator.GetInt32(0, 10000);
                string pin = code.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
                if (IsNonTrivial(pin)) return pin;
                // Boucle réelle impossible à boucler (10 000 - ~200 codes
                // triviaux) — la borne est du filet anti-pathologie.
                if (attempt >= 20) return pin;
            }
        }

        private static bool IsNonTrivial(string pin)
        {
            if (pin[0] == pin[1] && pin[1] == pin[2] && pin[2] == pin[3]) return false; // 1111
            if (pin[1] == pin[2] && pin[2] == pin[3]
                && (pin[0] == pin[1] - 1 || pin[0] == pin[1] + 1)) return false;         // 1123
            bool asc = pin[1] == pin[0] + 1 && pin[2] == pin[1] + 1 && pin[3] == pin[2] + 1;
            bool desc = pin[1] == pin[0] - 1 && pin[2] == pin[1] - 1 && pin[3] == pin[2] - 1;
            return !asc && !desc;                                                        // 1234 / 9876
        }

        private static void PruneLocked(long now)
        {
            foreach (var kv in s_locks)
                if (kv.Value <= now)
                    s_locks.TryRemove(kv.Key, out _);
            foreach (var kv in s_pendings)
                if (kv.Value.ExpiresUtc <= now)
                    s_pendings.TryRemove(kv.Key, out _);
        }

        private static void PruneCreated(string user, long now)
        {
            List<long> list;
            if (!s_created.TryGetValue(user, out list)) return;
            list.RemoveAll(t => now - t >= DayTicks);
            if (list.Count == 0) s_created.TryRemove(user, out _);
        }

        private static void RegisterCreation(string user, long now)
        {
            List<long> list = s_created.GetOrAdd(user, _ => new List<long>());
            list.Add(now);
        }

        private static string LockedMessage(string user, long lockUntilTicks, string langKey)
        {
            int minutes = Math.Max(1,
                (int)Math.Ceiling((lockUntilTicks - DateTime.UtcNow.Ticks) / 600_000_000.0));
            return string.Format(CultureInfo.CurrentUICulture,
                I18n.S("rec.locked", langKey), user, minutes);
        }

        /// <summary>Comparaison à temps constant (aucune fuite de timing) —
        /// même discipline que le secret du chat externe.</summary>
        private static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}