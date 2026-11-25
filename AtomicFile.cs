using System;
using System.IO;
using System.Text;

namespace LLM_AI
{
    /// <summary>
    /// Écriture atomique des fichiers d'état JSON : contenu écrit dans un
    /// fichier temporaire voisin puis renommé par-dessus la cible. Un crash
    /// ou un arrêt serveur en plein write laisse l'ANCIEN fichier intact au
    /// lieu d'un JSON tronqué (qui, relu comme « vide », serait ensuite
    /// réécrit par-dessus et perdrait tout l'état — contrat « fichiers
    /// d'état = contrats de nettoyage »).
    /// Les exceptions remontent comme avec <see cref="File.WriteAllText(string,string)"/> :
    /// l'appelant garde sa politique (log Warn, fail-open…).
    /// </summary>
    internal static class AtomicFile
    {
        internal static void WriteAllText(string path, string contents)
        {
            var tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllText(tmp, contents, new UTF8Encoding(false));
                File.Move(tmp, path, true);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
                throw;
            }
        }

        /// <summary>
        /// Met un fichier illisible de côté (<c>.corrupt-AAAAMMJJhhmmss</c>)
        /// pour qu'une réécriture ne l'écrase pas. Retourne true si déplacé.
        /// </summary>
        internal static bool Quarantine(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var dest = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                File.Move(path, dest, true);
                return true;
            }
            catch { return false; }
        }
    }
}
