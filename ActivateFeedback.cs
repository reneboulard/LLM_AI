using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;

namespace LLM_AI
{
    /// <summary>
    /// Retour visuel de l'activation d'une carte <c>.strm</c> (endpoint
    /// <c>/Plugins/LLMAI/Activate</c>, v1.12) : <b>toast Emby</b> au client qui
    /// lit la carte + <b>suppression de la carte par Emby</b> en cas de succès.
    /// </summary>
    /// <remarks>
    /// <para><b>Toast</b> : <c>DisplayMessage</c> via
    /// <see cref="ISessionManager.SendMessageCommand"/> (même mécanique que
    /// <c>TonightLoginService</c> — Header NON rendu par les clients web/Android,
    /// tout le texte vit dans <see cref="MessageCommand.Text"/>, préfixe 🤖).
    /// La session cible est retrouvée en interrogeant
    /// <see cref="ISessionManager.Sessions"/> : celle dont l'item en cours
    /// (<c>FullNowPlayingItem.Path</c>, repli <c>NowPlayingItem.Path</c>) est le
    /// fichier <c>.strm</c> de la carte. Aucune cloche/notification : l'usager a
    /// cliqué la carte, le toast suffit (choix design v1.12). Session
    /// introuvable (client qui n'expose pas NowPlayingItem, lecture côté
    /// transcodeur seul…) → on logue et on abandonne.</para>
    /// <para><b>Suppression</b> : JAMAIS de suppression directe de fichier par
    /// le plugin — on demande à Emby, par item :
    /// <c>ILibraryManager.FindByPath(strm)</c> puis
    /// <c>DeleteItem(item, DeleteOptions { DeleteFileLocation = true })</c>,
    /// ce qui retire l'item de la bibliothèque ET le fichier <c>.strm</c> par le
    /// pipeline d'Emby (diffusion LibraryUpdate). Constaté sur ce serveur :
    /// Emby retire le <b>dossier de carte entier</b> (<c>.strm</c>, <c>.nfo</c>,
    /// marker, poster). La suppression est
    /// DIFFÉRÉE (~60 s) pour ne pas couper la lecture du clip en cours.</para>
    /// <para><b>Anti-doublon</b> : UNE lecture de carte génère PLUSIEURS appels
    /// GET à Activate (sonde ffprobe, requêtes Range du lecteur…) — le cache
    /// statique <see cref="s_recent"/> (TTL 5 min) fait que seul le premier
    /// appel déclenche toast + suppression ; les suivants sont silencieux (la
    /// création du timer reste elle-même idempotente via le dedup de
    /// <c>AutoProgrammer.ProgramOneAsync</c>). Passé le TTL, une nouvelle
    /// lecture redevient éligible (retry après échec).</para>
    /// <para>Toutes les méthodes sont best-effort, fire-and-forget (appelées via
    /// <c>Task.Run</c> avec <c>CancellationToken.None</c>) et ne lèvent jamais
    /// vers l'appelant — la latence de la requête Activate reste inchangée.</para>
    /// </remarks>
    internal static class ActivateFeedback
    {
        /// <summary>Fenêtre de déduplication d'une activation : les appels
        /// répétés pour la même carte dans cette fenêtre (sonde ffmpeg, Range,
        /// re-fetch lecteur) ne re-déclenchent ni toast ni suppression.</summary>
        private static readonly TimeSpan SuppressionTtl = TimeSpan.FromMinutes(5);

        /// <summary>Délai avant suppression de l'item carte par Emby : laisse
        /// finir la lecture du clip de confirmation (~8 s) avec une marge large
        /// (les clients peuvent bufferiser/rejouer en Range).</summary>
        private static readonly TimeSpan DeleteDelay = TimeSpan.FromSeconds(60);

        /// <summary>Attente max de détection de la session lecteur après
        /// l'activation (le reporting de lecture peut suivre de peu la requête
        /// GET émise par la chaîne de lecture elle-même).</summary>
        private static readonly TimeSpan SessionWait = TimeSpan.FromSeconds(15);

        // clé = chemin .strm (repli : programId|kind) → dernier moment d'activation.
        private static readonly ConcurrentDictionary<string, DateTimeOffset> s_recent
            = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        // Sérialise l'accès test-puis-écrit de TryMarkFresh (deux GET d'une même
        // lecture peuvent se chevaucher : sonde ffmpeg + lecteur).
        private static readonly object s_gate = new object();

        // ------------------------------------------------------------------
        //  Gate anti-doublon
        // ------------------------------------------------------------------

        /// <summary>
        /// Détermine si cette activation est « fraîche » (premier appel pour
        /// cette carte depuis <see cref="SuppressionTtl"/>) et la marque. Les
        /// appels suivants (même lecture) retournent faux → toast et suppression
        /// sautés (l'horodatage est prolongé : une lecture qui s'étire reste
        /// couverte). Clé : chemin .strm si connu, sinon programId|kind.
        /// </summary>
        public static bool TryMarkFresh(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            var now = DateTimeOffset.UtcNow;
            lock (s_gate)
            {
                Prune(now);
                if (s_recent.TryGetValue(key, out var prev) && now - prev < SuppressionTtl)
                {
                    s_recent[key] = now;   // prolonge : même lecture en cours
                    return false;
                }
                s_recent[key] = now;
                return true;
            }
        }

        /// <summary>Retire les entrées expirées (best-effort, opportuniste).</summary>
        private static void Prune(DateTimeOffset now)
        {
            foreach (var kv in s_recent)
                if (now - kv.Value >= SuppressionTtl)
                    s_recent.TryRemove(kv.Key, out _);
        }

        // ------------------------------------------------------------------
        //  Toast
        // ------------------------------------------------------------------

        /// <summary>
        /// Cherche la session lisant <paramref name="strmPath"/> (poll 2 s,
        /// jusqu'à <see cref="SessionWait"/>), puis envoie le toast
        /// <paramref name="text"/> (déjà localisé et formaté par l'appelant ;
        /// le préfixe 🤖 est ajouté ici) à cette session si elle supporte
        /// <c>DisplayMessage</c>. Ne lève jamais.
        /// </summary>
        public static async Task SendToastAsync(
            ISessionManager sessions, string strmPath, string text, ILogger logger)
        {
            try
            {
                if (sessions == null || string.IsNullOrWhiteSpace(text))
                {
                    logger?.Info("[LLM_AI] Activate toast : session manager ou texte absent — toast ignoré.");
                    return;
                }

                string full = "🤖 " + text;

                var session = await FindPlayingSessionAsync(sessions, strmPath, logger).ConfigureAwait(false);
                if (session == null)
                {
                    logger?.Info("[LLM_AI] Activate toast : session lisant « {0} » introuvable en {1}s — toast ignoré (pas de cloche, choix design).",
                        strmPath, (int)SessionWait.TotalSeconds);
                    return;
                }
                if (!SessionSupportsDisplay(session))
                {
                    logger?.Info("[LLM_AI] Activate toast : session « {0} » sans DisplayMessage — toast ignoré.", session.Id);
                    return;
                }

                var msg = new MessageCommand
                {
                    Header = string.Empty,   // non rendu par web/Android — tout dans Text
                    Text = full,
                    TimeoutMs = 8000,
                };
                await sessions.SendMessageCommand(session.Id, session.Id, msg, CancellationToken.None).ConfigureAwait(false);
                logger?.Info("[LLM_AI] Activate toast envoyé à la session « {0} » : {1}", session.Id, full);
            }
            catch (Exception ex)
            {
                logger?.Info("[LLM_AI] Activate toast échoué (best-effort) : {0}", ex.Message);
            }
        }

        /// <summary>
        /// Poll <see cref="ISessionManager.Sessions"/> pour trouver la session
        /// dont l'item en cours est <paramref name="strmPath"/> (comparaison
        /// OrdinalIgnoreCase, chemin trimé, sur <c>FullNowPlayingItem.Path</c>
        /// puis repli <c>NowPlayingItem.Path</c>). Null si rien en <see cref="SessionWait"/>.
        /// </summary>
        private static async Task<SessionInfo> FindPlayingSessionAsync(
            ISessionManager sessions, string strmPath, ILogger logger)
        {
            var deadline = DateTimeOffset.UtcNow + SessionWait;
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    var hit = (sessions.Sessions ?? Enumerable.Empty<SessionInfo>())
                        .FirstOrDefault(s => SessionPaths(s).Any(p =>
                            string.Equals(p, strmPath, StringComparison.OrdinalIgnoreCase)));
                    if (hit != null) return hit;
                }
                catch (Exception ex)
                {
                    logger?.Info("[LLM_AI] Activate toast : lecture des sessions échouée : {0}", ex.Message);
                    return null;
                }
                try { await Task.Delay(2000, CancellationToken.None).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
            }
            return null;
        }

        /// <summary>Chemins candidats de l'item en cours d'une session
        /// (<c>FullNowPlayingItem.Path</c> d'abord — BaseItem serveur, puis
        /// <c>NowPlayingItem.Path</c> — DTO client). Tolérant aux nulls.</summary>
        private static IEnumerable<string> SessionPaths(SessionInfo s)
        {
            if (s == null) yield break;
            string p1 = null;
            try { p1 = s.FullNowPlayingItem?.Path; } catch { }
            if (!string.IsNullOrWhiteSpace(p1)) yield return p1.Trim();
            string p2 = null;
            try { p2 = s.NowPlayingItem?.Path; } catch { }
            if (!string.IsNullOrWhiteSpace(p2)) yield return p2.Trim();
        }

        /// <summary>
        /// Capacité <c>DisplayMessage</c> : champ
        /// <see cref="SessionInfo.Capabilities"/>.SupportedCommands (canonique),
        /// repli champ legacy <see cref="SessionInfo.SupportedCommands"/> —
        /// même logique que TonightLoginService.SessionSupportsDisplay.
        /// </summary>
        private static bool SessionSupportsDisplay(SessionInfo s)
        {
            try
            {
                var cmds = s.Capabilities?.SupportedCommands;
                if (cmds != null && cmds.Contains("DisplayMessage", StringComparer.OrdinalIgnoreCase)) return true;
                var legacy = s.SupportedCommands;
                return legacy != null && legacy.Contains("DisplayMessage", StringComparer.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        //  Suppression de la carte (par Emby, différée)
        // ------------------------------------------------------------------

        /// <summary>
        /// Après <see cref="DeleteDelay"/> : résout l'item bibliothèque de la
        /// carte par son chemin (<c>FindByPath</c>) et demande à Emby de le
        /// supprimer (<c>DeleteItem</c>, <c>DeleteFileLocation = true</c> —
        /// retire l'item ET le fichier .strm par le pipeline d'Emby). Best-effort :
        /// item introuvable (pas encore importé / déjà disparu) → logué et
        /// abandonné (CleanPrevious fait le ménage au prochain run).
        /// </summary>
        public static async Task DeleteCardItemLaterAsync(
            ILibraryManager library, string strmPath, ILogger logger)
        {
            try
            {
                if (library == null || string.IsNullOrWhiteSpace(strmPath))
                {
                    logger?.Info("[LLM_AI] Activate : suppression carte — bibliothèque ou chemin absent, ignorée.");
                    return;
                }

                await Task.Delay(DeleteDelay, CancellationToken.None).ConfigureAwait(false);

                var item = library.FindByPath(strmPath, false);
                if (item == null || item.Id == Guid.Empty)
                {
                    logger?.Info("[LLM_AI] Activate : suppression carte — item introuvable pour « {0} » (non importé ou déjà supprimé), abandon.", strmPath);
                    return;
                }

                library.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true });
                logger?.Info("[LLM_AI] Activate : carte « {0} » supprimée via Emby (item {1}, fichier .strm inclus).", strmPath, item.Id);
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Activate : suppression carte « {0} » échouée (best-effort, ignorée) : {1}", strmPath, ex.Message);
            }
        }

        // ------------------------------------------------------------------
        //  Titre du programme (texte du toast)
        // ------------------------------------------------------------------

        /// <summary>
        /// Résout le titre du programme EPG pointé par <paramref name="programId"/>
        /// (une requête in-process par activation — même mécanique que
        /// StrmLibraryGenerator.TryGetEpgProgram). Repli : programId brut.
        /// </summary>
        public static string ResolveProgramTitle(ILibraryManager library, string programId)
        {
            if (string.IsNullOrWhiteSpace(programId)) return programId ?? string.Empty;
            if (!long.TryParse(programId, out long id) || id <= 0) return programId;
            try
            {
                var q = new InternalItemsQuery { ItemIds = new[] { id }, Limit = 1 };
                var item = (library?.GetItemList(q) ?? Array.Empty<BaseItem>()).FirstOrDefault();
                return string.IsNullOrWhiteSpace(item?.Name) ? programId : item.Name;
            }
            catch { return programId; }
        }
    }
}