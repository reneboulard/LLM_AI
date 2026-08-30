using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;

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
        private readonly IServerApplicationHost _host;
        // Runner LLM pour le tier-3 de la cascade TMDB (traduction synopsis en-US
        // -> langue usager en dernier recours). Null = pas de LLM (on garde le
        // synopsis en-US tel quel). Ne casse jamais la génération de cartes.
        private readonly LlmRunner _runner;

        // HttpClient partagé (poster TMDB). Pas de credentials, timeouts courts.
        private static readonly HttpClient s_http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        private const string MarkerFile = ".llmai_reco";

        public StrmLibraryGenerator(ILibraryManager library, ILiveTvManager liveTv,
            IServerApplicationHost host, ILogger logger, LlmRunner runner = null)
        {
            _library = library;
            _liveTv = liveTv;
            _host = host;
            _logger = logger;
            _runner = runner;
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

            // 1b) Image par défaut standardisée sur la bibliothèque .strm (idempotent).
            //     Posée tôt, avant l'écriture des cartes, pour qu'elle s'applique même si
            //     la génération échoue plus loin. Best-effort.
            try
            {
                var libItem = _library.FindByPath(root, true);
                if (libItem != null)
                    await DefaultImageApplier.ApplyPrimaryIfMissingAsync(libItem, _host, _library, _logger, ct)
                        .ConfigureAwait(false);
            }
            catch (Exception ex) { _logger?.Warn("[LLM_AI] Strm library : image par défaut échouée : {0}", ex.Message); }

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

            // Identifiant du serveur Emby pour le lien profond web vers le
            // programme EPG (/{baseApi}/web/index.html#!/item?id=<pgm>&serverId=<srv>).
            // GetPublicSystemInfo est le chemin fiable (GetSystemInfo lève une
            // NRE sur certains hôtes Windows). Fail-open : si l'id manque, on
            // omet simplement le lien <website> de la carte.
            string serverId = null;
            try
            {
                if (_host != null)
                {
                    var pub = await _host.GetPublicSystemInfo(ct).ConfigureAwait(false);
                    serverId = pub?.Id;
                }
            }
            catch (Exception ex) { _logger?.Warn("[LLM_AI] Strm library : serverId indispo ({0}) — lien EPG omis sur les cartes.", ex.Message); }

            // 5+6) Une carte par reco : lookup TMDB (cache 24h partagé avec l'agent
            //      LLM) -> écriture de la carte (.strm + .nfo enrichi + marker) ->
            //      poster (URL TMDB du même lookup, sinon poster EPG en repli).
            //      Un seul appel TMDB par reco donne à la fois les métadonnées du
            //      .nfo et le poster.
            // Langue des métadonnées (scaffolding NFO + synopsis TMDB) : suit
            // ResponseLanguage, sinon la langue d'affichage Emby (Auto), sinon
            // legacy TmdbLanguage, sinon anglais. Calculé une fois avant la
            // boucle — la même langue sert pour toutes les cartes de cette passe.
            string langKey = I18n.ResolveMetaLangKey(cfg, _host);
            string userTmdb = I18n.ToTmdbLang(langKey);

            int written = 0;
            var tmdb = new TmdbLookupTool(_logger);
            foreach (var r in bucket)
            {
                ct.ThrowIfCancellationRequested();

                // Lookup TMDB : synopsis/note/genres/année pour le .nfo + URL poster.
                string kind = AutoProgrammer.IsSeries(r.Kind) ? "series" : "movie";

                // Programme EPG (une seule requête in-process par reco, via r.Id =
                // programId) : fournit l'overview natif du diffuseur — dans la
                // langue de la chaîne, indépendante de ResponseLanguage — que l'on
                // place en tête du <plot>. Sert aussi de source au poster de repli.
                BaseItem epgProgram = TryGetEpgProgram(r);
                string epgOverview = (epgProgram?.Overview ?? string.Empty).Trim();

                TmdbMeta meta = null;
                string queryTitle = r.Title;
                try { meta = await tmdb.LookupMetaAsync(queryTitle, kind, null, userTmdb, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _logger?.Info("[LLM_AI] Strm library : lookup TMDB échoué pour « {0} » ({1}).", r.Title, ex.Message); }

                // Suffixe « on <chaîne> » : les titres Gracenote du type
                // « Moonflower Murders on Masterpiece » n'ont PAS de match TMDB
                // sous leur forme complète (l'entrée TMDB s'appelle « Moonflower
                // Murders »). Si le lookup complet échoue, on retente UNE fois
                // sans le suffixe — inoffensif : la forme complète est toujours
                // essayée d'abord, donc un titre légitime contenant « on »
                // (p.ex. « Attack on Titan ») n'est tronqué que si son lookup
                // complet a déjà raté. Le repli poster EPG couvre le reste.
                if (meta == null)
                {
                    string stripped = StripChannelSuffix(queryTitle);
                    if (!string.IsNullOrEmpty(stripped))
                    {
                        try { meta = await tmdb.LookupMetaAsync(stripped, kind, null, userTmdb, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { _logger?.Info("[LLM_AI] Strm library : lookup TMDB (titre sans suffixe) échoué pour « {0} » ({1}).", stripped, ex.Message); }

                        if (meta != null)
                        {
                            _logger?.Info("[LLM_AI] Strm library : match TMDB pour « {0} » trouvé via « {1} » (suffixe « on … » retiré).", r.Title, stripped);
                            queryTitle = stripped;   // le tier 2 (en-US) re-cherche sur ce titre.
                        }
                    }
                }

                // Cascade TMDB (tiers 2 + 3) :
                //  Tier 1 : langue de l'usager (userTmdb) — déjà fait ci-dessus.
                //  Tier 2 : si pas de synopsis (ou pas de match) et userTmdb != en-US,
                //           repli en-US et on fusionne poster/genres/année.
                //  Tier 3 : dernier recours, on traduit le synopsis en-US vers la
                //           langue de l'usager via le LLM. Sauté si userTmdb == en-US
                //           (le tier 1 est déjà en anglais -> zéro appel LLM).
                if (!string.Equals(userTmdb, "en-US", StringComparison.OrdinalIgnoreCase)
                    && (meta == null || string.IsNullOrWhiteSpace(meta.Overview)))
                {
                    TmdbMeta metaEn = null;
                    try { metaEn = await tmdb.LookupMetaAsync(queryTitle, kind, null, "en-US", ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { _logger?.Info("[LLM_AI] Strm library : lookup TMDB en-US échoué pour « {0} » ({1}).", r.Title, ex.Message); }

                    if (metaEn != null)
                    {
                        if (meta == null)
                        {
                            // Aucun match dans la langue usager : on part du match en-US.
                            meta = metaEn;
                        }
                        else
                        {
                            // Match partiel en langue usager (souvent sans synopsis) :
                            // on comble poster/genres/année depuis le match en-US.
                            if (string.IsNullOrWhiteSpace(meta.PosterUrl)) meta.PosterUrl = metaEn.PosterUrl;
                            if (meta.Genres == null || meta.Genres.Length == 0) meta.Genres = metaEn.Genres;
                            if (!meta.Year.HasValue) meta.Year = metaEn.Year;
                            if (!meta.Rating.HasValue) meta.Rating = metaEn.Rating;
                        }

                        // Tier 3 : traduction LLM du synopsis en-US -> langue usager.
                        if (!string.IsNullOrWhiteSpace(metaEn.Overview))
                        {
                            if (_runner != null)
                            {
                                try
                                {
                                    string translated = await _runner.TranslateTextAsync(
                                        cfg, metaEn.Overview, I18n.ToLangName(langKey), ct).ConfigureAwait(false);
                                    if (!string.IsNullOrWhiteSpace(translated))
                                        meta.Overview = translated;
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex)
                                {
                                    _logger?.Warn("[LLM_AI] Strm library : traduction synopsis « {0} » échouée ({1}) — synopsis en-US conservé.",
                                        r.Title, ex.Message);
                                    meta.Overview = metaEn.Overview;
                                }
                            }
                            else
                            {
                                // Pas de runner (LLM indispo) : on garde le synopsis en-US.
                                meta.Overview = metaEn.Overview;
                            }
                        }
                    }
                }

                // 5) Écriture de la carte (.strm + .nfo enrichi + marker).
                try
                {
                    WriteCard(root, r, cfg, baseApi, serverId, langKey, meta, epgOverview);
                    written++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] Strm library : échec écriture carte « {0} » : {1}", r.Title, ex.Message);
                }

                // 6) Poster : URL TMDB (depuis le lookup) sinon poster EPG en repli.
                bool posterWritten = false;
                if (!string.IsNullOrWhiteSpace(meta?.PosterUrl))
                {
                    try { posterWritten = await DownloadPosterAsync(root, r, meta.PosterUrl, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { _logger?.Info("[LLM_AI] Strm library : pas de poster TMDB pour « {0} » ({1}).", r.Title, ex.Message); }
                }

                // Repli : récupère l'affiche Primary du programme EPG pointé par
                // r.Id (téléfilm/titre régional absent de TMDB mais dont la
                // chaîne fournit déjà un poster, souvent un vrai portrait 2:3).
                // L'affiche peut être un fichier local OU une URL distante
                // (Gracenote/TMS référence presque toujours une URL http).
                if (!posterWritten)
                {
                    try { posterWritten = await TryCopyProgramPosterAsync(root, r, epgProgram, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { _logger?.Info("[LLM_AI] Strm library : pas de poster EPG pour « {0} » ({1}).", r.Title, ex.Message); }
                }
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
        private string ResolveLibraryRoot(string name) => ResolveLibraryRoot(_library, name, _logger);

        /// <summary>
        /// Résout le chemin disque d'une bibliothèque Emby nommée via
        /// <see cref="ILibraryManager.GetVirtualFolders"/> (premier
        /// <c>Location</c> non vide). La comparaison de nom est
        /// <b>normalisée</b> (<c>-</c>/<c>_</c>/espaces équivalents) :
        /// <c>GetVirtualFolders</c> renvoie le nom du UserView slugifié (traits
        /// d'union) qui peut différer du nom configuré (underscores). Null si
        /// introuvable. Partagé entre la génération .strm et l'exclusion
        /// circulaire de <see cref="TonightService"/>.
        /// </summary>
        internal static string ResolveLibraryRoot(ILibraryManager library, string name, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                var folders = library.GetVirtualFolders();
                if (folders == null) return null;
                var key = NormLibName(name);
                foreach (var f in folders)
                {
                    if (f == null) continue;
                    if (!string.Equals(NormLibName(f.Name), key, StringComparison.Ordinal)) continue;
                    var locs = f.Locations;
                    if (locs == null) continue;
                    foreach (var l in locs)
                        if (!string.IsNullOrWhiteSpace(l)) return l;
                }
            }
            catch (Exception ex) { logger?.Warn("[LLM_AI] ResolveLibraryRoot : GetVirtualFolders a échoué : {0}", ex.Message); }
            return null;
        }

        /// <summary>
        /// Normalise un nom de bibliothèque pour la comparaison : minuscules,
        /// et toute séquence de <c>-</c>/<c>_</c>/espaces → un seul
        /// <c>_</c>. Évite le décalage « ai-suggestions » (UserView) vs
        /// « ai_suggestions » (config) observé sur Emby.
        /// </summary>
        internal static string NormLibName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var sb = new StringBuilder();
            bool sep = false;
            foreach (var c in s.Trim().ToLowerInvariant())
            {
                if (c == '-' || c == '_' || char.IsWhiteSpace(c))
                {
                    if (!sep) { sb.Append('_'); sep = true; }
                }
                else { sb.Append(c); sep = false; }
            }
            return sb.ToString();
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

        private void WriteCard(string root, AutoProgrammer.Reco r, PluginConfiguration cfg, string baseApi, string serverId, string langKey, TmdbMeta meta, string epgOverview)
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

            // .nfo : <movie> (valide en bibliothèque Films ou Contenu mixte),
            // enrichi des métadonnées TMDB quand disponibles (synopsis, note,
            // genres, année). Scaffolding localisé via I18n (langKey). L'overview
            // EPG natif (epgOverview) ouvre le <plot>, avant l'enrichissement
            // dans la langue de l'usager.
            File.WriteAllText(Path.Combine(folder, safe + ".nfo"), BuildNfo(r, baseApi, serverId, langKey, meta, epgOverview), new UTF8Encoding(false));

            // Marker (identifie le dossier comme généré par le plugin).
            File.WriteAllText(Path.Combine(folder, MarkerFile), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), new UTF8Encoding(false));
        }

        /// <summary>
        /// Construit le NFO <c>&lt;movie&gt;</c> : titre (suffixe « (série)/(series) »
        /// pour kind=series), <c>&lt;plot&gt;</c> enrichi du synopsis TMDB +
        /// ligne méta (note/genres/année) + raison LLM + info de diffusion +
        /// lien interne vers la fiche EPG (en clair, visible dans l'Overview —
        /// Emby n'affichant pas <c>&lt;website&gt;</c>), studio = chaine, genres
        /// « AI Suggestion » + genres réels TMDB + priority, premiered depuis
        /// start, et le même lien EPG dans <c>&lt;website&gt;</c>
        /// (<c>asSeries=true</c> pour une série seulement).
        /// <para>Structure du <c>&lt;plot&gt;</c> : (0) synopsis EPG natif
        /// (<paramref name="epgOverview"/>) — description du diffuseur dans la
        /// langue de la chaîne, indépendante de <c>ResponseLanguage</c> — placé en
        /// tête ; puis (1) enrichissement dans la langue de l'usager
        /// (<paramref name="langKey"/>) : synopsis TMDB + ligne méta
        /// (note/genres/année), (2) raison LLM (« Pourquoi ce soir »), (3) info de
        /// diffusion, (4) lien fiche EPG. Le scaffolding est localisé via
        /// <see cref="I18n.S"/>. La raison LLM et le synopsis TMDB sont déjà dans
        /// la langue de l'usager (directive LLM + cascade TMDB) ; on n'y touche
        /// pas. Best-effort : sans overview EPG ni TMDB, on retombe sur raison +
        /// diffusion (comportement précédent).</para>
        /// </summary>
        private string BuildNfo(AutoProgrammer.Reco r, string baseApi, string serverId, string langKey, TmdbMeta meta, string epgOverview)
        {
            bool isSeries = AutoProgrammer.IsSeries(r.Kind);
            string seriesSuffix = I18n.S("nfo.seriesSuffix", langKey);
            string title = (r.Title ?? string.Empty).Trim();
            if (isSeries && !title.EndsWith(seriesSuffix, StringComparison.OrdinalIgnoreCase))
                title += seriesSuffix;

            // Lien profond vers la fiche EPG interne (asSeries=true pour une série,
            // omis pour un film). Construit une fois, utilisé à la fois dans le
            // <plot> (visible dans l'Overview Emby) et dans <website>.
            string epgLink = EpgLink(r.Id, serverId, baseApi, isSeries);

            // Plot enrichi :
            //  0) synopsis EPG natif (langue de la chaîne, du diffuseur) — en tête ;
            //  1) synopsis TMDB (si disponible) + ligne méta (note/genres/année) ;
            //  2) raison LLM (« Pourquoi ce soir ») ;
            //  3) info de diffusion (chaîne + date/heure dans le fuseau EPG) +
            //     rappel d'usage. Sans overview EPG ni TMDB, on retombe sur raison +
            //     diffusion (comportement précédent).
            var plot = new StringBuilder();

            // 0) Synopsis EPG natif en tête : description authentique du programme
            //    dans la langue du diffuseur, quelle que soit ResponseLanguage.
            //    Best-effort : absent si l'EPG ne fournit pas d'overview.
            if (!string.IsNullOrEmpty(epgOverview))
            {
                plot.Append(epgOverview);
            }

            // 1) Enrichissement dans la langue de l'usager (ResponseLanguage) :
            //    synopsis TMDB + ligne méta (note/genres/année).
            string overview = (meta?.Overview ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(overview))
            {
                if (plot.Length > 0) plot.Append("\n\n");
                plot.Append(overview);
                var metaParts = new List<string>();
                if (meta.Rating.HasValue && meta.Rating.Value > 0)
                    metaParts.Add(string.Format(CultureInfo.InvariantCulture,
                        I18n.S("nfo.rating", langKey), meta.Rating.Value.ToString("0.0", CultureInfo.InvariantCulture)));
                if (meta.Genres != null && meta.Genres.Length > 0)
                    metaParts.Add(string.Format(CultureInfo.InvariantCulture,
                        I18n.S("nfo.genres", langKey), string.Join(", ", meta.Genres)));
                if (meta.Year.HasValue)
                    metaParts.Add(meta.Year.Value.ToString(CultureInfo.InvariantCulture));
                if (metaParts.Count > 0)
                {
                    plot.Append("\n\n").Append(string.Join(" · ", metaParts));
                }
            }

            string reason = (r.Reason ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(reason))
            {
                if (plot.Length > 0) plot.Append("\n\n");
                plot.Append(I18n.S("nfo.why", langKey)).Append(reason);
            }

            {
                if (plot.Length > 0) plot.Append("\n\n");
                plot.Append(I18n.S("nfo.airs.prefix", langKey));
                string chan = (r.Channel ?? string.Empty).Trim();
                string air = FormatAirTime(r.Start);
                if (!string.IsNullOrEmpty(chan))
                    plot.Append(string.Format(CultureInfo.InvariantCulture, I18n.S("nfo.airs.chan", langKey), chan));
                if (!string.IsNullOrEmpty(air))
                    plot.Append(string.Format(CultureInfo.InvariantCulture, I18n.S("nfo.airs.date", langKey), air));
                plot.Append(I18n.S("nfo.airs.suffix", langKey));
            }

            // Lien interne vers la fiche EPG, en clair dans l'Overview : Emby
            // n'affiche pas <website> dans son UI, on l'inscrit donc aussi dans le
            // <plot> pour qu'il soit visible (et copiable) sur la fiche de la carte.
            if (!string.IsNullOrEmpty(epgLink))
            {
                if (plot.Length > 0) plot.Append("\n\n");
                plot.Append(I18n.S("nfo.epglink", langKey)).Append(epgLink);
            }

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n");
            sb.Append("<movie>\n");
            sb.Append("  <title>").Append(XmlEsc(title)).Append("</title>\n");
            sb.Append("  <plot>").Append(XmlEsc(plot.ToString())).Append("</plot>\n");
            sb.Append("  <outline>").Append(XmlEsc(reason)).Append("</outline>\n");
            if (!string.IsNullOrWhiteSpace(r.Channel))
                sb.Append("  <studio>").Append(XmlEsc(r.Channel.Trim())).Append("</studio>\n");
            sb.Append("  <genre>AI Suggestion</genre>\n");
            // Genres réels TMDB (en plus du marqueur « AI Suggestion ») : la carte
            // devient filtrable par genre dans Emby. Best-effort (meta peut être null).
            if (meta?.Genres != null)
            {
                foreach (var g in meta.Genres)
                {
                    if (string.IsNullOrWhiteSpace(g)) continue;
                    sb.Append("  <genre>").Append(XmlEsc(g.Trim())).Append("</genre>\n");
                }
            }
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
            // External IDs (provider ids) : Emby lit ces éléments NFO et peuple
            // BaseItem.ProviderIds, ce qui génère les liens profonds vers les bases
            // externes (TMDB/IMDb/TVDB) dans la section « External IDs » de la fiche.
            // Uniquement quand on a un match TMDB (ids portés par le lookup). Les ids
            // étant indépendants de la langue, ils sont identiques quel que soit le
            // tier de la cascade. Best-effort (meta peut être null ou sans id).
            if (meta != null)
            {
                if (meta.TmdbId > 0)
                    sb.Append("  <tmdbid>").Append(meta.TmdbId.ToString(CultureInfo.InvariantCulture)).Append("</tmdbid>\n");
                if (!string.IsNullOrWhiteSpace(meta.ImdbId))
                    sb.Append("  <imdbid>").Append(XmlEsc(meta.ImdbId.Trim())).Append("</imdbid>\n");
                if (!string.IsNullOrWhiteSpace(meta.TvdbId))
                    sb.Append("  <tvdbid>").Append(XmlEsc(meta.TvdbId.Trim())).Append("</tvdbid>\n");
            }
            // Lien profond vers la fiche EPG interne Emby. Pour une SÉRIE,
            // asSeries=true : Emby ouvre le programme groupé par série (tous les
            // passages/épisodes de ce titre), comme le fait la fiche EPG native.
            // Pour un film/one-off, on omet asSeries (le programme n'appartient à
            // aucune série ; une vue groupée serait vide). Conservé dans <website>
            // en plus du <plot> (certains clients Emby exposent le champ lien).
            if (!string.IsNullOrEmpty(epgLink))
                sb.Append("  <website>").Append(XmlEsc(epgLink)).Append("</website>\n");
            sb.Append("  <tag>LLM_AI</tag>\n");
            sb.Append("</movie>\n");
            return sb.ToString();
        }

        /// <summary>
        /// Formate la date/heure de diffusion depuis le champ start (ISO 8601)
        /// en « yyyy-MM-dd HH:mm », en préservant le fuseau de l'EPG (celui du
        /// DateTimeOffset tel que reçu, sans conversion vers le fuseau serveur).
        /// Retourne null si start est absent ou non analysable.
        /// </summary>
        private static string FormatAirTime(string start)
        {
            if (string.IsNullOrWhiteSpace(start)) return null;
            if (DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                return dto.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return null;
        }

        /// <summary>Extrait une date ISO yyyy-MM-dd depuis le champ start (ISO 8601).</summary>
        private static string ParsePremiered(string start)
        {
            if (string.IsNullOrWhiteSpace(start)) return null;
            if (DateTime.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
                return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return null;
        }

        /// <summary>
        /// Construit le lien web profond vers la fiche EPG interne Emby du
        /// programme <paramref name="programId"/>. Pour une série
        /// (<paramref name="isSeries"/>=true) on ajoute <c>&amp;asSeries=true</c>
        /// (vue groupée par série — tous les passages du titre) ; pour un film on
        /// l'omet (fiche directe du programme). Retourne <c>null</c> si un des
        /// trois paramètres requis (id / serverId / baseApi) est vide.
        /// </summary>
        private static string EpgLink(string programId, string serverId, string baseApi, bool isSeries)
        {
            if (string.IsNullOrWhiteSpace(programId) || string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(baseApi))
                return null;
            return string.Format(CultureInfo.InvariantCulture,
                isSeries ? "{0}/web/index.html#!/item?id={1}&serverId={2}&asSeries=true"
                         : "{0}/web/index.html#!/item?id={1}&serverId={2}",
                baseApi.TrimEnd('/'),
                Uri.EscapeDataString(programId),
                Uri.EscapeDataString(serverId));
        }

        // ------------------------------------------------------------------
        //  Nettoyage du titre pour la requête TMDB
        // ------------------------------------------------------------------

        /// <summary>
        /// Suffixe « on &lt;chaîne/brand&gt; » en fin de titre (1 à 3 mots) :
        /// convention Gracenote/PBS du type « Moonflower Murders on Masterpiece »
        /// — l'entrée TMDB correspondante s'appelle « Moonflower Murders ».
        /// </summary>
        private static readonly Regex s_channelSuffix = new Regex(
            @"\s+on\s+[\p{L}0-9'&]+(?:\s+[\p{L}0-9'&]+){0,2}\s*$",
            RegexOptions.Compiled);

        /// <summary>
        /// Retire le suffixe de chaîne d'un titre EPG pour la requête TMDB :
        /// « Moonflower Murders on Masterpiece » → « Moonflower Murders ».
        /// Retourne <c>null</c> si aucun suffixe détachable (le titre doit rester
        /// tel quel). Conservateur : la forme complète est TOUJOURS essayée
        /// d'abord par l'appelant — ce tronquage n'intervient qu'après un échec
        /// du lookup complet. Le titre de la carte (dossier/.nfo) n'est jamais
        /// modifié, ceci ne sert qu'à la requête.
        /// </summary>
        private static string StripChannelSuffix(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            Match m = s_channelSuffix.Match(title);
            if (!m.Success) return null;
            string stripped = title.Substring(0, m.Index).Trim();
            return stripped.Length >= 3 ? stripped : null;
        }

        // ------------------------------------------------------------------
        //  Poster TMDB
        // ------------------------------------------------------------------

        /// <summary>
        /// Télécharge le poster TMDB de la reco dans <c>poster.jpg</c>
        /// (best-effort) depuis l'URL <paramref name="posterUrl"/> fournie par le
        /// lookup TMDB partagé (<see cref="TmdbLookupTool.LookupMetaAsync"/> —
        /// image.tmdb.org/t/p/w500). Retourne <c>true</c> si <c>poster.jpg</c> a
        /// été écrit, <c>false</c> sinon (URL absente ou échec réseau) —
        /// l'appelant enchaîne alors sur le repli par poster EPG
        /// (<see cref="TryCopyProgramPoster"/>).
        /// </summary>
        private async Task<bool> DownloadPosterAsync(string root, AutoProgrammer.Reco r, string posterUrl, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(posterUrl) || string.IsNullOrWhiteSpace(r.Title)) return false;

            string safe = SanitizeName(r.Title);
            if (string.IsNullOrWhiteSpace(safe)) safe = "reco_" + SanitizeName(r.Id);
            string folder = Path.Combine(root, safe);
            if (!Directory.Exists(folder)) return false;

            try
            {
                using (var resp = await s_http.GetAsync(posterUrl, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    using (var fs = File.Create(Path.Combine(folder, "poster.jpg")))
                        await (await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false)).CopyToAsync(fs, 81920, ct).ConfigureAwait(false);
                }
                return true;
            }
            catch { /* poster optionnel */ return false; }
        }

        /// <summary>
        /// Résout le <see cref="BaseItem"/> du programme EPG pointé par
        /// <c>r.Id</c> (programId). Une seule requête in-process par reco, partagée
        /// entre (a) la lecture de l'<c>Overview</c> natif du diffuseur — placé en
        /// tête du <c>&lt;plot&gt;</c> via <see cref="BuildNfo"/> — et (b) le repli
        /// poster (<see cref="TryCopyProgramPoster"/>). Best-effort : retourne
        /// <c>null</c> si l'id est absent/invalide ou si le lookup échoue.
        /// </summary>
        /// <remarks>
        /// <c>r.Id</c> est l'<c>InternalId</c> (long) du programme EPG exposé en
        /// chaîne (cf. <c>GetEmbyInfoTool</c> : <c>EnableInternalIdsExternally</c>
        /// — le <c>DTO.Id</c> de <c>GetPrograms</c> est l'<c>InternalId</c> Int64,
        /// pas le <c>Guid</c> <c>BaseItem.Id</c>). On résout donc le
        /// <see cref="BaseItem"/> via <see cref="InternalItemsQuery.ItemIds"/>
        /// (long[]).
        /// </remarks>
        private BaseItem TryGetEpgProgram(AutoProgrammer.Reco r)
        {
            if (string.IsNullOrWhiteSpace(r.Id)) return null;
            if (!long.TryParse(r.Id, out long programId) || programId <= 0) return null;
            try
            {
                var q = new InternalItemsQuery
                {
                    ItemIds = new[] { programId },
                    Limit = 1
                };
                return (_library.GetItemList(q) ?? Array.Empty<BaseItem>()).FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger?.Info("[LLM_AI] Strm library : lookup programme EPG « {0} » échoué ({1}).", r.Title, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Repli de poster : récupère l'image <see cref="ImageType.Primary"/> du
        /// programme EPG (<paramref name="program"/>, déjà résolu par
        /// <see cref="TryGetEpgProgram"/>) vers <c>poster.jpg</c> dans le dossier
        /// de la carte. Comble le cas fréquent où <see cref="DownloadPosterAsync"/>
        /// ne trouve pas de match TMDB (téléfilm/titre régional absent du
        /// catalogue) alors que le programme EPG possède déjà une affiche — souvent
        /// un vrai poster portrait fourni par la chaîne, donc de meilleure facture
        /// que le poster par défaut générique.
        /// <para>L'affiche EPG peut être un <b>fichier local</b> (image en cache
        /// sur disque) OU une <b>URL distante</b> : les programmes Gracenote/TMS
        /// référencent presque toujours une URL <c>ebyl.tmsimg.com</c> dans le
        /// champ Path de leur image Primary — un <c>File.Exists</c> seul échoue
        /// donc systématiquement sur ce type de source (bug des cartes sans
        /// poster alors que l'EPG en affiche une). Le chemin http est alors
        /// téléchargé avec le <see cref="HttpClient"/> partagé du générateur.</para>
        /// Best-effort, ne lève jamais. Chaque garde logue sa raison (Info) pour
        /// rendre les « carte sans poster » diagnosticables. <c>true</c> si
        /// poster écrit.
        /// </summary>
        private async Task<bool> TryCopyProgramPosterAsync(string root, AutoProgrammer.Reco r, BaseItem program, CancellationToken ct)
        {
            // Logue la raison du renoncement puis retourne false — remplace les
            // return false silencieux d'origine (cartes sans poster indiagnosticables).
            bool Skip(string why)
            {
                _logger?.Info("[LLM_AI] Strm library : pas de poster EPG pour « {0} » ({1}).", r.Title, why);
                return false;
            }

            if (program == null) return Skip("programme EPG introuvable");
            if (string.IsNullOrWhiteSpace(r.Title)) return Skip("titre absent de la reco");

            try
            {
                if (!program.HasImage(ImageType.Primary, 0)) return Skip("programme sans image Primary");

                string src = program.GetImageInfo(ImageType.Primary, 0)?.Path;
                if (string.IsNullOrWhiteSpace(src)) return Skip("chemin d'image indisponible");

                string safe = SanitizeName(r.Title);
                if (string.IsNullOrWhiteSpace(safe)) safe = "reco_" + SanitizeName(r.Id);
                string folder = Path.Combine(root, safe);
                if (!Directory.Exists(folder)) return Skip("dossier de carte absent : " + folder);

                string dst = Path.Combine(folder, "poster.jpg");
                bool isLocal = File.Exists(src);
                if (isLocal)
                {
                    // Image en cache sur disque : simple copie.
                    File.Copy(src, dst, overwrite: true);
                }
                else if (Uri.TryCreate(src, UriKind.Absolute, out Uri uri)
                    && (uri.Scheme == "http" || uri.Scheme == "https"))
                {
                    // Affiche distante (Gracenote/TMS) : téléchargement via le
                    // HttpClient partagé, comme un poster TMDB.
                    using (var resp = await s_http.GetAsync(src, ct).ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();
                        using (var fs = File.Create(dst))
                            await (await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false)).CopyToAsync(fs, 81920, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    return Skip("source ni fichier local ni URL : " + src);
                }

                _logger?.Info("[LLM_AI] Strm library : poster EPG {0} pour « {1} » (depuis programme {2}).",
                    isLocal ? "copié" : "téléchargé", r.Title, r.Id);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Info("[LLM_AI] Strm library : poster EPG « {0} » : récupération échouée ({1}).", r.Title, ex.Message);
                return false;
            }
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