using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Génère une bibliothèque Emby dédiée de cartes <c>.strm</c>+<c>.nfo</c>
    /// pour les recommandations du <b>record bucket</b> (programmes EPG à venir,
    /// non possédés). Appelé par la tâche planifiée
    /// <see cref="LlmScheduledTask"/> après chaque run, si
    /// <see cref="PluginConfiguration.StrmLibraryEnabled"/> est coché.
    /// </summary>
    /// <remarks>
    /// <para>Chaque reco du record bucket devient un sous-dossier
    /// <c>&lt;root&gt;/&lt;Title&gt;/</c> contenant :</para>
    /// <list type="bullet">
    /// <item><c>&lt;Title&gt;.strm</c> : URL vers l'endpoint
    /// <c>/Plugins/LLMAI/Activate</c> — lire la carte crée le timer puis renvoie
    /// un clip de confirmation.</item>
    /// <item><c>&lt;Title&gt;.nfo</c> : métadonnées <c>&lt;movie&gt;</c>
    /// (titre, plot = raison LLM, studio = chaine, genre « AI Suggestion »,
    /// priority, date) — fonctionne dans une bibliothèque de type « Films » ou
    /// « Contenu mixte ».</item>
    /// <item><c>poster.jpg</c> : poster TMDB (si clé configurée + match).</item>
    /// <item><c>.llmai_reco</c> : marker identifiant le dossier comme généré
    /// par le plugin (pour le nettoyage de la passe précédente).</item>
    /// </list>
    /// <para>Indépendant de <see cref="PluginConfiguration.AutoProgram"/> : la
    /// bibliothèque .strm est la surface <b>manuelle</b> (l'usager déclenche
    /// l'enregistrement en lisant la carte), AutoProgram la surface
    /// <b>automatique</b> (tous les timers d'un coup).</para>
    /// </remarks>
    internal class StrmLibraryGenerator
    {
        private readonly ILibraryManager _library;
        private readonly ILiveTvManager _liveTv;
        private readonly ILogger _logger;

        // HttpClient partagé (poster TMDB). Pas de credentials, timeouts courts.
        private static readonly HttpClient s_http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        private const string MarkerFile = ".llmai_reco";

        public StrmLibraryGenerator(ILibraryManager library, ILiveTvManager liveTv, ILogger logger)
        {
            _library = library;
            _liveTv = liveTv;
            _logger = logger;
        }

        /// <summary>
        /// Génère la bibliothèque .strm pour le payload JSON de recommandations.
        /// <paramref name="embyBaseUrl"/> = URL de base d'Emby (résolue par
        /// l'appelant via <c>IServerApplicationHost.GetLocalHostApiUrl</c> ou
        /// <see cref="PluginConfiguration.EmbyPublicUrl"/>) — doit être
        /// joignable depuis les clients qui liront les cartes.
        /// </summary>
        public async Task GenerateAsync(string payload, PluginConfiguration cfg, string embyBaseUrl, CancellationToken ct)
        {
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.StrmLibraryName))
            {
                _logger?.Info("[LLM_AI] Strm library : aucun nom de bibliothèque configuré → inactif.");
                return;
            }

            // 1) Résoudre le dossier cible depuis la bibliothèque Emby nommée.
            string root = ResolveLibraryRoot(cfg.StrmLibraryName);
            if (string.IsNullOrWhiteSpace(root))
            {
                _logger?.Warn("[LLM_AI] Strm library : bibliothèque « {0} » introuvable (GetVirtualFolders) → inactif. Créez-la dans Emby (type Films ou Contenu mixte).", cfg.StrmLibraryName);
                return;
            }
            Directory.CreateDirectory(root);

            // 2) Auto-générer le secret de capacité si vide (une seule fois).
            EnsureSecret(cfg);

            // 3) Nettoyer la passe précédente (marker sweep).
            CleanPrevious(root);

            // 4) Filtrer le record bucket (programmes à venir, non possédés).
            var recos = AutoProgrammer.ParseRecommendations(payload);
            var dropped = GetEmbyInfoTool.DroppedTitlesSet();
            var bucket = new List<AutoProgrammer.Reco>();
            foreach (var r in recos)
            {
                if (AutoProgrammer.IsWatchBucket(r)) continue;          // déjà dispo
                if (!string.IsNullOrEmpty(r.LibraryId)) continue;       // possédé
                if (string.IsNullOrEmpty(r.Id)) continue;              // pas d'id EPG
                string norm = GetEmbyInfoTool.Norm(r.Title ?? string.Empty);
                if (!string.IsNullOrEmpty(norm) && dropped.Contains(norm)) continue; // drop list
                bucket.Add(r);
            }

            _logger?.Info("[LLM_AI] Strm library : {0} reco(s) record bucket → écriture dans « {1} ».", bucket.Count, root);

            // 5) Une carte par reco.
            string baseApi = (embyBaseUrl ?? string.Empty).TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseApi))
            {
                _logger?.Warn("[LLM_AI] Strm library : URL de base Emby indéfinie (EmbyPublicUrl vide et GetLocalHostApiUrl indisponible) → cartes .strm ignorées (URL relative injouable). Renseignez « URL publique Emby » dans la config.");
                return;
            }
            int written = 0;
            foreach (var r in bucket)
            {
                try
                {
                    WriteCard(root, r, cfg, baseApi);
                    written++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] Strm library : échec écriture carte « {0} » : {1}", r.Title, ex.Message);
                }
            }

            // 6) Poster TMDB (best-effort, séquentiel, court) — après les cartes
            // pour que la bibliothèque soit fonctionnelle même si TMDB échoue.
            foreach (var r in bucket)
            {
                ct.ThrowIfCancellationRequested();
                try { await DownloadPosterAsync(root, r, cfg, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _logger?.Info("[LLM_AI] Strm library : pas de poster pour « {0} » ({1}).", r.Title, ex.Message); }
            }

            // 7) Déclencher un scan pour faire apparaître les nouvelles cartes.
            TriggerScan(ct);

            _logger?.Info("[LLM_AI] Strm library : {0} carte(s) écrites, scan déclenché.", written);
        }

        // ------------------------------------------------------------------
        //  Résolution de la bibliothèque cible
        // ------------------------------------------------------------------

        /// <summary>
        /// Résout le chemin disque de la bibliothèque Emby nommée
        /// <paramref name="name"/> via <see cref="ILibraryManager.GetVirtualFolders"/>
        /// (premier <c>Location</c> non vide). Null si introuvable.
        /// </summary>
        private string ResolveLibraryRoot(string name)
        {
            try
            {
                var folders = _library.GetVirtualFolders();
                if (folders == null) return null;
                foreach (var f in folders)
                {
                    if (f == null) continue;
                    if (!string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    var locs = f.Locations;
                    if (locs == null) continue;
                    foreach (var l in locs)
                        if (!string.IsNullOrWhiteSpace(l)) return l;
                }
            }
            catch (Exception ex) { _logger?.Warn("[LLM_AI] Strm library : GetVirtualFolders a échoué : {0}", ex.Message); }
            return null;
        }

        // ------------------------------------------------------------------
        //  Secret de capacité
        // ------------------------------------------------------------------

        /// <summary>
        /// Si <see cref="PluginConfiguration.StrmSecret"/> est vide, génère un
        /// jeton aléatoire (32 octets → hex) et persiste la config. Appelé une
        /// seule fois à la première génération.
        /// </summary>
        private void EnsureSecret(PluginConfiguration cfg)
        {
            if (!string.IsNullOrWhiteSpace(cfg.StrmSecret)) return;
            try
            {
                var bytes = new byte[32];
                using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                    rng.GetBytes(bytes);
                cfg.StrmSecret = BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
                Plugin.Instance?.SaveConfiguration();
                _logger?.Info("[LLM_AI] Strm library : jeton StrmSecret auto-généré et persisté.");
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Strm library : impossible de générer StrmSecret : {0}", ex.Message);
            }
        }

        // ------------------------------------------------------------------
        //  Nettoyage de la passe précédente
        // ------------------------------------------------------------------

        /// <summary>
        /// Supprime tout sous-dossier de <paramref name="root"/> contenant le
        /// marker <see cref="MarkerFile"/> (= généré par le plugin). Ne touche
        /// pas au contenu utilisateur sans marker.
        /// </summary>
        private void CleanPrevious(string root)
        {
            try
            {
                // ToArray : on matérialise avant de supprimer (modification du
                // répertoire pendant l'énumération → exception sinon).
                foreach (var dir in Directory.EnumerateDirectories(root).ToArray())
                {
                    try
                    {
                        if (File.Exists(Path.Combine(dir, MarkerFile)))
                            Directory.Delete(dir, recursive: true);
                    }
                    catch (Exception ex) { _logger?.Warn("[LLM_AI] Strm library : nettoyage « {0} » échoué : {1}", dir, ex.Message); }
                }
            }
            catch (DirectoryNotFoundException) { /* root n'existe pas encore : rien à nettoyer */ }
            catch (Exception ex) { _logger?.Warn("[LLM_AI] Strm library : énumération du root pour nettoyage échouée : {0}", ex.Message); }
        }

        // ------------------------------------------------------------------
        //  Écriture d'une carte
        // ------------------------------------------------------------------

        private void WriteCard(string root, AutoProgrammer.Reco r, PluginConfiguration cfg, string baseApi)
        {
            string safe = SanitizeName(r.Title);
            if (string.IsNullOrWhiteSpace(safe)) safe = "reco_" + SanitizeName(r.Id);
            string folder = Path.Combine(root, safe);
            Directory.CreateDirectory(folder);

            // .strm : URL vers l'endpoint Activate.
            string url = string.Format(CultureInfo.InvariantCulture,
                "{0}/Plugins/LLMAI/Activate?programId={1}&kind={2}&t={3}",
                baseApi,
                Uri.EscapeDataString(r.Id ?? string.Empty),
                Uri.EscapeDataString(string.IsNullOrEmpty(r.Kind) ? "movie" : r.Kind),
                Uri.EscapeDataString(cfg.StrmSecret ?? string.Empty));
            File.WriteAllText(Path.Combine(folder, safe + ".strm"), url + Environment.NewLine, new UTF8Encoding(false));

            // .nfo : <movie> (valide en bibliothèque Films ou Contenu mixte).
            File.WriteAllText(Path.Combine(folder, safe + ".nfo"), BuildNfo(r), new UTF8Encoding(false));

            // Marker (identifie le dossier comme généré par le plugin).
            File.WriteAllText(Path.Combine(folder, MarkerFile), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), new UTF8Encoding(false));
        }

        /// <summary>
        /// Construit le NFO <c>&lt;movie&gt;</c> : titre (suffixe « (série) »
        /// pour kind=series), plot = raison LLM + note d'usage, studio = chaine,
        /// genres « AI Suggestion » + priority, premiered depuis start.
        /// </summary>
        private string BuildNfo(AutoProgrammer.Reco r)
        {
            bool isSeries = AutoProgrammer.IsSeries(r.Kind);
            string title = (r.Title ?? string.Empty).Trim();
            if (isSeries && !title.EndsWith(" (série)", StringComparison.OrdinalIgnoreCase))
                title += " (série)";

            string plot = (r.Reason ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(plot)) plot += " ";
            plot += "Diffusion à venir — lire cette carte programme l'enregistrement.";

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n");
            sb.Append("<movie>\n");
            sb.Append("  <title>").Append(XmlEsc(title)).Append("</title>\n");
            sb.Append("  <plot>").Append(XmlEsc(plot)).Append("</plot>\n");
            sb.Append("  <outline>").Append(XmlEsc((r.Reason ?? string.Empty).Trim())).Append("</outline>\n");
            if (!string.IsNullOrWhiteSpace(r.Channel))
                sb.Append("  <studio>").Append(XmlEsc(r.Channel.Trim())).Append("</studio>\n");
            sb.Append("  <genre>AI Suggestion</genre>\n");
            if (!string.IsNullOrWhiteSpace(r.Priority))
                sb.Append("  <genre>priority:").Append(XmlEsc(r.Priority.Trim())).Append("</genre>\n");
            if (!string.IsNullOrWhiteSpace(r.Kind))
                sb.Append("  <genre>kind:").Append(XmlEsc(r.Kind.Trim())).Append("</genre>\n");
            string premiered = ParsePremiered(r.Start);
            if (!string.IsNullOrEmpty(premiered))
            {
                sb.Append("  <premiered>").Append(premiered).Append("</premiered>\n");
                sb.Append("  <year>").Append(premiered.Length >= 4 ? premiered.Substring(0, 4) : "").Append("</year>\n");
            }
            sb.Append("  <tag>LLM_AI</tag>\n");
            sb.Append("</movie>\n");
            return sb.ToString();
        }

        /// <summary>Extrait une date ISO yyyy-MM-dd depuis le champ start (ISO 8601).</summary>
        private static string ParsePremiered(string start)
        {
            if (string.IsNullOrWhiteSpace(start)) return null;
            if (DateTime.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
                return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return null;
        }

        // ------------------------------------------------------------------
        //  Poster TMDB
        // ------------------------------------------------------------------

        /// <summary>
        /// Télécharge le poster TMDB de la reco dans <c>poster.jpg</c>
        /// (best-effort). Reprend le même endpoint search/tv | search/movie que
        /// <see cref="TmdbLookupTool"/> : premier résultat, poster_path →
        /// image.tmdb.org/t/p/w500. Sans clé ou sans match : no-op.
        /// </summary>
        private async Task DownloadPosterAsync(string root, AutoProgrammer.Reco r, PluginConfiguration cfg, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(cfg.TmdbApiKey) || string.IsNullOrWhiteSpace(r.Title)) return;

            string safe = SanitizeName(r.Title);
            if (string.IsNullOrWhiteSpace(safe)) safe = "reco_" + SanitizeName(r.Id);
            string folder = Path.Combine(root, safe);
            if (!Directory.Exists(folder)) return;

            bool isSeries = AutoProgrammer.IsSeries(r.Kind);
            string query = Uri.EscapeDataString(r.Title);
            string lang = Uri.EscapeDataString(string.IsNullOrWhiteSpace(cfg.TmdbLanguage) ? "en-US" : cfg.TmdbLanguage);
            string searchUrl = string.Format(CultureInfo.InvariantCulture,
                "https://api.themoviedb.org/3/search/{0}?api_key={1}&language={2}&query={3}",
                isSeries ? "tv" : "movie", Uri.EscapeDataString(cfg.TmdbApiKey), lang, query);

            string posterPath;
            try
            {
                using (var resp = await s_http.GetAsync(searchUrl, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    using (var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false))
                    {
                        if (!doc.RootElement.TryGetProperty("results", out var results) ||
                            results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
                            return;
                        var first = results.EnumerateArray().First();
                        if (!first.TryGetProperty("poster_path", out var pp) || pp.ValueKind != JsonValueKind.String)
                            return;
                        posterPath = pp.GetString();
                    }
                }
            }
            catch { return; }

            if (string.IsNullOrWhiteSpace(posterPath)) return;

            string imgUrl = "https://image.tmdb.org/t/p/w500" + posterPath;
            try
            {
                using (var resp = await s_http.GetAsync(imgUrl, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    using (var fs = File.Create(Path.Combine(folder, "poster.jpg")))
                        await (await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false)).CopyToAsync(fs, 81920, ct).ConfigureAwait(false);
                }
            }
            catch { /* poster optionnel */ }
        }

        // ------------------------------------------------------------------
        //  Scan de la bibliothèque
        // ------------------------------------------------------------------

        /// <summary>
        /// Déclenche un scan Emby pour faire apparaître les nouvelles cartes.
        /// <see cref="ILibraryManager.ValidateMediaLibrary"/> scanne toutes les
        /// bibliothèques (le scan ciblé d'une seule bibliothèque n'est pas
        /// exposé sûrement par l'API hôte). Sur la plupart des installations un
        /// filesystem watcher aurait déjà détecté les fichiers ; cet appel est
        /// la safety net.
        /// </summary>
        private void TriggerScan(CancellationToken ct)
        {
            try
            {
                _library.ValidateMediaLibrary(new Progress<double>(), ct);
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Strm library : ValidateMediaLibrary a échoué ({0}) — les cartes apparaîtront au prochain scan.", ex.Message);
            }
        }

        // ------------------------------------------------------------------
        //  Utilitaires
        // ------------------------------------------------------------------

        /// <summary>
        /// Nettoie un titre pour un nom de dossier/fichier : retire les
        /// caractères interdits (< > : " / \ | ? *) et les séparateurs de
        /// chemin, trim, fall-back géré par l'appelant.
        /// </summary>
        private static string SanitizeName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var sb = new StringBuilder(s.Trim());
            foreach (char c in Path.GetInvalidFileNameChars())
                sb.Replace(c, ' ');
            // Retire aussi les points finaux (Windows n'aime pas les noms
            // terminés par un point) et collapse les espaces.
            string v = sb.ToString().Trim().TrimEnd('.');
            while (v.Contains("  ")) v = v.Replace("  ", " ");
            return v;
        }

        /// <summary>Échappe les caractères XML pour l'insertion en texte.</summary>
        private static string XmlEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&apos;");
        }
    }
}