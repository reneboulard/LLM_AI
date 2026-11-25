using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Porte de concurrence des tâches planifiées LLM (agent EPG « Tâche
    /// LLM AI », analyse hebdo de rétroaction, mémoire réflexive) : au
    /// plus <see cref="PluginConfiguration.LlmMaxConcurrentTasks"/> runs
    /// LLM du plugin en même temps (défaut 1 — un Ollama local traite une
    /// conversation à la fois ; des runs empilés s'étouffent sur le timeout
    /// par appel de 2 min et la cascade de replis s'épuise).
    ///
    /// Sémantique (règle usager 2026-10-08) : une tâche qui arrive sur une
    /// porte pleine <b>attend</b> — réveil immédiat à la libération, pas de
    /// sondage — puis passe ; aucun passage nocturne n'est perdu. Les
    /// surfaces interactives (chat, Tonight, audit) ne passent PAS par la
    /// porte : elles doivent rester réactives et s'appuient sur la cascade
    /// timeout → serveur suivant.
    ///
    /// Capacité relue à chaque acquisition/libération → un changement de
    /// config s'applique sans redémarrage. Limite connue : une
    /// AUGMENTATION de capacité ne réveille pas les attendants déjà en
    /// file (aucun hook de changement de config) — la prochaine
    /// libération les desservira avec la nouvelle capacité.
    /// </summary>
    public static class LlmTaskGate
    {
        // File FIFO des attendants + compteur d'actifs. Le verrou protège
        // tout l'état ; les réveils se font hors verrou via les
        // TaskCompletionSource (RunContinuationsAsynchronously : le
        // réveillé ne s'exécute pas dans le thread qui libère).
        private static readonly object _sync = new object();
        private static readonly LinkedList<(TaskCompletionSource<bool> Done, string Label)> _waiters =
            new LinkedList<(TaskCompletionSource<bool>, string)>();
        private static readonly List<string> _holders = new List<string>();
        private static int _active;

        /// <summary>
        /// Acquiert un passage. Retourne immédiatement si la porte a de la
        /// place ; sinon attend (et le logue) jusqu'à libération. Une
        /// annulation (arrêt serveur / fin de tâche Emby) pendant l'attente
        /// lève <see cref="OperationCanceledException"/> — l'appelant qui
        /// place l'acquisition AVANT son try n'a rien à libérer.
        /// </summary>
        /// <param name="label">Étiquette de la tâche pour le journal (« Tâche LLM AI (EPG) », …).</param>
        /// <param name="logger">Logger de la tâche (peut être nul).</param>
        /// <param name="ct">Token d'annulation de la tâche Emby.</param>
        public static async Task AcquireAsync(string label, ILogger logger, CancellationToken ct)
        {
            Task waitTask;
            int cap;
            string holders;
            CancellationTokenRegistration reg;

            lock (_sync)
            {
                cap = CapFromConfig();
                if (_active < cap)
                {
                    // Cas commun : passage immédiat, silencieux.
                    _active++;
                    _holders.Add(label);
                    return;
                }

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.AddLast((tcs, label));
                waitTask = tcs.Task;
                holders = string.Join(", ", _holders);

                logger?.Info(
                    "[LLM_AI] Porte LLM occupée (capacité {0}) — « {1} » attend la fin de : {2}.",
                    cap, label, string.IsNullOrEmpty(holders) ? "un autre run" : holders);

                // Annulation paresseuse : l'attendant quitte la file en
                // douceur (Release saute les attendants annulés — le slot
                // n'est jamais consommé par un parti).
                reg = ct.Register(() => tcs.TrySetCanceled());
            }

            var started = DateTime.UtcNow;

            // await hors verrou : le réveil vient de Release (ou de
            // l'annulation → TaskCanceledException : OperationCanceledException).
            try
            {
                await waitTask.ConfigureAwait(false);
            }
            finally
            {
                reg.Dispose(); // ne garde pas le CTS vivant après le réveil
            }

            logger?.Info("[LLM_AI] Porte LLM : « {0} » passe après {1:N1} min d'attente.",
                label, (DateTime.UtcNow - started).TotalMinutes);
        }

        /// <summary>
        /// Libère le passage tenu par <paramref name="label"/> et réveille
        /// les attendants que la capacité permet (FIFO, les annulés sont
        /// sautés sans consommer de slot).
        /// </summary>
        /// <param name="label">Même étiquette que celle passée à <see cref="AcquireAsync"/>.</param>
        public static void Release(string label)
        {
            lock (_sync)
            {
                _active--;
                // Retire la première occurrence du libéreur (capacité > 1 :
                // deux passages peuvent porter la même étiquette).
                for (int i = 0; i < _holders.Count; i++)
                {
                    if (string.Equals(_holders[i], label, StringComparison.Ordinal))
                    {
                        _holders.RemoveAt(i);
                        break;
                    }
                }

                int cap = CapFromConfig();
                while (_active < cap && _waiters.Count > 0)
                {
                    var (tcs, wl) = _waiters.First.Value;
                    _waiters.RemoveFirst();
                    // TrySetResult est l'arbitre atomique face au callback
                    // d'annulation (hors verrou) : si l'annulation a gagné la
                    // course, l'attendant est parti — slot NON consommé. On ne
                    // compte l'actif QU'APRÈS avoir remporté le résultat, sinon
                    // le slot fuit (personne n'appelle Release pour un
                    // attendant qui a levé OperationCanceledException).
                    if (!tcs.TrySetResult(true)) continue;
                    _active++;
                    _holders.Add(wl);
                }
            }
        }

        /// <summary>Capacité courante : config clampée à ≥ 1 (défaut 1).</summary>
        private static int CapFromConfig()
        {
            int cap = Plugin.Instance?.Configuration?.LlmMaxConcurrentTasks ?? 1;
            return cap < 1 ? 1 : cap;
        }
    }
}