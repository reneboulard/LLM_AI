using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Registre en mémoire des programmes EPG porteurs du badge « AI »
    /// (clé = <see cref="MediaBrowser.Controller.Entities.BaseItem.InternalId"/>,
    /// long). Consulté par <see cref="AiBadgeEnhancer.Supports"/> à chaque
    /// demande d'image ; alimenté par <c>LlmScheduledTask</c> après chaque run
    /// (suggestions d'enregistrement du record bucket).
    /// </summary>
    /// <remarks>
    /// <para><b>Sémantique « tout remplacer »</b> : chaque run de la tâche
    /// planifiée remplace le contenu entier — les suggestions de la veille ne
    /// badgent plus. Pas de tâche de nettoyage dédiée : le remplacement à
    /// chaque run + la garde <c>EndDate &gt; now</c> dans
    /// <see cref="AiBadgeEnhancer.Supports"/> suffisent (une entrée périmée
    /// n'est jamais servie badgée, même si elle traîne dans le registre).</para>
    /// <para><b>Persistance</b> : le contenu est persisté dans
    /// <see cref="PluginConfiguration.AiBadgeProgramIds"/> (config XML du
    /// plugin) pour survivre à un redémarrage du serveur — rechargé au
    /// démarrage (<c>Plugin</c> ctor → <see cref="LoadFrom"/>) ; écrit via
    /// <see cref="ReplaceAndPersist"/> après chaque run. Lecture sans verrou
    /// (<see cref="ConcurrentDictionary{TKey,TValue}"/>).</para>
    /// </remarks>
    internal static class AiBadgeRegistry
    {
        /// <summary>Clé = InternalId du programme ; valeur = sentinelle.</summary>
        private static readonly ConcurrentDictionary<long, byte> s_programs =
            new ConcurrentDictionary<long, byte>();

        /// <summary>Chargement paresseux effectué (voir <see cref="EnsureLoaded"/>).</summary>
        private static bool s_loaded;

        /// <summary>
        /// Le programme (InternalId) porte-t-il le badge « AI » ?
        /// Appelé par <see cref="AiBadgeEnhancer.Supports"/> à chaque demande
        /// d'image — doit rester O(1) et sans allocation. Le premier appel
        /// déclenche le chargement initial (lazy) — jamais depuis le ctor du
        /// plugin, où l'accès à la config lève (AssemblyFilePath n'est posé
        /// par l'hôte qu'après la construction).
        /// </summary>
        internal static bool IsRegistered(long internalId)
        {
            EnsureLoaded();
            return s_programs.ContainsKey(internalId);
        }

        /// <summary>
        /// Chargement initial one-shot depuis la config (restaure les badges du
        /// dernier run après un redémarrage du serveur). Les appels suivants
        /// sont un simple test de booléen. Un échec de lecture logue en
        /// avertissement et marque le chargement fait (registre vide : aucun
        /// badge servi — jamais d'exception dans le pipeline d'image).
        /// </summary>
        private static void EnsureLoaded()
        {
            if (s_loaded) return;
            lock (s_loadLock)
            {
                if (s_loaded) return;
                try
                {
                    LoadFrom(Plugin.Instance?.Configuration);
                }
                catch (Exception)
                {
                    // Pas de logger ici (contexte statique sans DI) : le
                    // registre vide = aucun badge, comportement dégradé
                    // silencieux mais sûr.
                }
                s_loaded = true;
            }
        }

        private static readonly object s_loadLock = new object();

        /// <summary>
        /// Nombre courant d'entrées (log de diagnostic uniquement).
        /// </summary>
        internal static int Count => s_programs.Count;

        /// <summary>
        /// Remplace le contenu du registre par les ids donnés (dédupliqués,
        /// ordre préservé pour le log) et persiste dans la config du plugin.
        /// Appelé après chaque run de la tâche planifiée.
        /// </summary>
        internal static void ReplaceAndPersist(IEnumerable<long> ids, ILogger logger)
        {
            var fresh = (ids ?? Array.Empty<long>()).Distinct().ToList();

            s_programs.Clear();
            foreach (var id in fresh)
                s_programs.TryAdd(id, 0);

            try
            {
                var cfg = Plugin.Instance?.Configuration;
                if (cfg != null)
                {
                    cfg.AiBadgeProgramIds = fresh;
                    Plugin.Instance.SaveConfiguration();
                }
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Badge AI : persistance du registre échouée : {0}", ex.Message);
            }

            logger?.Info("[LLM_AI] Badge AI : registre remplacé — {0} programme(s) porteur(s) du badge.", fresh.Count);
        }

        /// <summary>
        /// Recharge le registre depuis <see cref="PluginConfiguration.AiBadgeProgramIds"/>.
        /// Appelé au démarrage du plugin (restaure les badges du dernier run
        /// après un redémarrage du serveur, sans attendre le prochain run).
        /// </summary>
        internal static void LoadFrom(PluginConfiguration cfg)
        {
            var ids = cfg?.AiBadgeProgramIds;
            if (ids == null) return;

            s_programs.Clear();
            foreach (var id in ids)
                if (id > 0)
                    s_programs.TryAdd(id, 0);
        }

        /// <summary>
        /// Alimente le registre depuis le payload JSON des recommandations
        /// d'une run fraîche : conserve le <b>record bucket</b> — recos à
        /// enregistrer uniquement, même filtre que
        /// <c>StrmLibraryGenerator</c>/<c>AutoProgrammer</c> : hors watch
        /// bucket (déjà dispo), non possédées (<c>library_id</c>), id EPG
        /// présent et parsable (long), hors drop list.
        /// </summary>
        /// <remarks>Best-effort : un payload non parsable logue et laisse le
        /// registre inchangé.</remarks>
        internal static void ApplyRecos(string payload, ILogger logger)
        {
            List<long> fresh;
            try
            {
                var recos = AutoProgrammer.ParseRecommendations(payload);
                var dropped = GetEmbyInfoTool.DroppedTitlesSet();

                fresh = new List<long>();
                foreach (var r in recos)
                {
                    if (AutoProgrammer.IsWatchBucket(r)) continue;     // déjà dispo
                    if (!string.IsNullOrEmpty(r.LibraryId)) continue;  // possédé
                    if (string.IsNullOrEmpty(r.Id)) continue;          // pas d'id EPG
                    string norm = GetEmbyInfoTool.Norm(r.Title ?? string.Empty);
                    if (!string.IsNullOrEmpty(norm) && dropped.Contains(norm)) continue; // drop list
                    if (!long.TryParse(r.Id, out long programId) || programId <= 0) continue;
                    fresh.Add(programId);
                }
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Badge AI : parsing du payload recos échoué : {0}", ex.Message);
                return;
            }

            ReplaceAndPersist(fresh, logger);
        }
    }
}