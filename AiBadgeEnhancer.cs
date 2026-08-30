using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using SkiaSharp;

namespace LLM_AI
{
    /// <summary>
    /// Badges « AI » sur l'image <see cref="ImageType.Primary"/> des programmes
    /// EPG — overlay au moment du service (pipeline
    /// <see cref="IImageEnhancer"/>), sans jamais muter l'artwork stocké.
    /// </summary>
    /// <remarks>
    /// <para>Deux badges, visuellement distincts :</para>
    /// <list type="bullet">
    /// <item><b>AI</b> (vert <c>#21963F</c> + étincelle blanche) : programme
    /// suggéré à enregistrer par la tâche planifiée (appartenance au
    /// <see cref="AiBadgeRegistry"/>). « Ce programme a été recommandé par
    /// l'IA. »</item>
    /// <item><b>Déjà possédé</b> (jaune <c>#FBC02D</c>, sans icône) : l'émission
    /// (série via <c>SeriesName</c>, film via <c>Name</c>) figure déjà dans la
    /// bibliothèque — même rapprochement par nom normalisé
    /// (<see cref="GetEmbyInfoTool.Norm"/>) que les outils
    /// <c>epg_series</c>/<c>epg_movies</c> utilisent pour exclure le possédé
    /// des suggestions. « Ne l'enregistre pas, tu l'as déjà. »</item>
    /// </list>
    /// <para>Les deux étant <b>exclusifs par construction</b> (le record bucket
    /// exclut le possédé), l'ordre de priorité AI &gt; possédé ne sert qu'aux
    /// transitions : chaque badge a sa <b>propre clé de cache</b>
    /// (<see cref="CacheKeyAi"/> / <see cref="CacheKeyOwned"/>), donc un
    /// changement d'état (ex. la série est importée en bibliothèque) fait
    /// régénérer l'image au lieu de resservir le badge périmé.</para>
    /// <para>Conséquences voulues de l'overlay au service : l'enregistrement
    /// importé reçoit l'image <b>d'origine</b> (le badge « disparaît » une fois
    /// l'émission enregistrée, sans rien nettoyer) ; un refresh EPG est sans
    /// effet (réapplication à chaque demande).</para>
    /// <para><b>Coût</b> : <see cref="Supports"/> est consulté à chaque demande
    /// d'image — les gardes bon marché (type, flag, dates) passent avant le
    /// rapprochement bibliothèque, et l'ensemble des noms possédés est
    /// reconstruit au plus toutes les <see cref="OwnedCacheTtl"/> (2 requêtes
    /// library), jamais par demande.</para>
    /// </remarks>
    public class AiBadgeEnhancer : IImageEnhancer
    {
        /// <summary>Clé de cache des images badgées « AI » (suggestion).</summary>
        public const string CacheKeyAi = "aibadge-v1";

        /// <summary>Clé de cache des images badgées « déjà possédé ».</summary>
        public const string CacheKeyOwned = "ownedbadge-v1";

        /// <summary>Diamètre du badge = 14 % de la largeur (borné 24–72 px).</summary>
        private const float BadgeSizeRatio = 0.14f;
        private const float BadgeMinPx = 24f;
        private const float BadgeMaxPx = 72f;

        /// <summary>Marge entre le badge et le coin = 4 % de la largeur (min 3 px).</summary>
        private const float BadgePaddingRatio = 0.04f;

        /// <summary>TTL du cache des noms possédés (biblio Series + Movies).</summary>
        private static readonly TimeSpan OwnedCacheTtl = TimeSpan.FromMinutes(10);

        private readonly ILogger _logger;
        private readonly ILibraryManager _library;

        /// <summary>Un seul log du premier appel (évitons le spam par image).</summary>
        private static bool _supportsLogged;

        /// <summary>Noms possédés normalisés (Norm) — cache partagé TTL.</summary>
        private static HashSet<string> s_ownedNames;
        private static DateTime s_ownedNamesAtUtc;
        private static readonly object s_ownedLock = new object();

        /// <summary>
        /// Emby découvre et instancie l'export <see cref="IImageEnhancer"/> du
        /// plugin au démarrage (même mécanisme de scan d'assembly que
        /// <c>IScheduledTask</c>/<c>IServerEntryPoint</c>) — vérifié
        /// empiriquement sur Emby 4.9.5. Le plugin ne doit PAS toucher sa
        /// propre config dans son ctor, mais l'enrichisseur peut recevoir des
        /// services Emby par DI (ici <see cref="ILibraryManager"/>).
        /// </summary>
        public AiBadgeEnhancer(ILibraryManager library, ILogger logger)
        {
            _library = library;
            _logger = logger;
        }

        /// <summary>Kind de badge à dessiner pour un programme EPG.</summary>
        private enum BadgeKind
        {
            None,
            /// <summary>Suggestion d'enregistrement de la tâche planifiée.</summary>
            Ai,
            /// <summary>Émission/série déjà dans la bibliothèque.</summary>
            Owned
        }

        /// <inheritdoc />
        /// <remarks>Consulté à chaque demande d'image — garder les gardes
        /// bon marché en tête. Le premier appel est logué une fois
        /// (diagnostic : confirme que le pipeline consulte l'enrichisseur).
        /// Ne JAMAIS lever : Emby loguerait une erreur par demande.</remarks>
        public bool Supports(BaseItem item, ImageType imageType)
        {
            if (!_supportsLogged)
            {
                _supportsLogged = true;
                _logger?.Info("[LLM_AI] AiBadgeEnhancer.Supports consulté (item « {0} », image {1}) — pipeline actif.",
                    item?.Name, imageType);
            }

            if (imageType != ImageType.Primary) return false;
            if (!(item is LiveTvProgram program)) return false;

            var (ai, owned) = GetFlags();
            if (!ai && !owned) return false;

            // Auto-expiration : une diffusion passée ne sort jamais badgée.
            if (!program.EndDate.HasValue || program.EndDate.Value <= DateTimeOffset.UtcNow)
                return false;

            return ComputeBadgeKind(program) != BadgeKind.None;
        }

        /// <inheritdoc />
        public MetadataProviderPriority Priority => MetadataProviderPriority.Last;

        /// <inheritdoc />
        /// <remarks><b>Par état</b> : la clé fait partie du chemin du fichier
        /// caché, donc basculer AI ↔ possédé (ex. la série vient d'être
        /// importée en bibliothèque) régénère l'image au lieu de resservir
        /// l'ancien badge.</remarks>
        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType)
            => (item is LiveTvProgram p && ComputeBadgeKind(p) == BadgeKind.Owned)
                ? CacheKeyOwned
                : CacheKeyAi;

        /// <inheritdoc />
        /// <remarks>Le badge ne change pas la taille de l'image.</remarks>
        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalImageSize)
            => originalImageSize;

        /// <inheritdoc />
        /// <remarks><see cref="EnhancedImageInfo.RequiresTransparency"/> reste
        /// false : le badge est composé sur l'artwork opaque (JPEG).</remarks>
        public EnhancedImageInfo GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex)
            => new EnhancedImageInfo();

        /// <inheritdoc />
        /// <remarks>Lit <paramref name="inputFile"/>, dessine le badge (AI ou
        /// possédé selon l'état courant), écrit <paramref name="outputFile"/>
        /// (format déduit de son extension — Emby nomme son fichier de cache
        /// d'après le format source). Tout échec (décodage, disque) replie sur
        /// une copie identique : l'image ressort non badgée, jamais cassée.</remarks>
        public Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex)
        {
            try
            {
                var kind = item is LiveTvProgram p ? ComputeBadgeKind(p) : BadgeKind.None;
                DrawBadge(inputFile, outputFile, kind);
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Badge EPG : dessin échoué pour « {0} » ({1}) — image servie sans badge.",
                    item?.Name, ex.Message);
                try { File.Copy(inputFile, outputFile, true); }
                catch (Exception ex2) { _logger?.Warn("[LLM_AI] Badge EPG : copie de repli échouée : {0}", ex2.Message); }
            }
            return Task.CompletedTask;
        }

        // ------------------------------------------------------------------
        //  Décision du badge
        // ------------------------------------------------------------------

        /// <summary>
        /// État des deux flags de config — accès défensif : une demande d'image
        /// peut arriver avant que le plugin soit pleinement prêt (ou si sa
        /// création a échoué) ; dans ce cas aucun badge.
        /// </summary>
        private static (bool ai, bool owned) GetFlags()
        {
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                return (cfg != null && cfg.AiBadgeEnabled,
                        cfg != null && cfg.AiOwnedBadgeEnabled);
            }
            catch (Exception)
            {
                return (false, false);
            }
        }

        /// <summary>
        /// Kind de badge pour un programme : <b>AI prioritaire</b> (le record
        /// bucket exclut déjà le possédé, ce priority ne couvre que la
        /// transition), sinon « déjà possédé » si l'émission figure dans la
        /// bibliothèque (série par <c>SeriesName</c>, film par <c>Name</c> —
        /// même rapprochement <see cref="GetEmbyInfoTool.Norm"/> que
        /// l'exclusion biblio des outils <c>epg_series</c>/<c>epg_movies</c>).
        /// </summary>
        private BadgeKind ComputeBadgeKind(LiveTvProgram program)
        {
            var (ai, owned) = GetFlags();

            if (ai && AiBadgeRegistry.IsRegistered(program.InternalId))
                return BadgeKind.Ai;

            if (!owned || _library == null)
                return BadgeKind.None;

            var title = !string.IsNullOrEmpty(program.SeriesName) ? program.SeriesName : program.Name;
            var key = GetEmbyInfoTool.Norm(title);
            if (string.IsNullOrEmpty(key)) return BadgeKind.None;

            return GetOwnedNames().Contains(key) ? BadgeKind.Owned : BadgeKind.None;
        }

        /// <summary>
        /// Noms possédés normalisés (biblio <c>Series</c> + <c>Movie</c>),
        /// reconstruits au plus toutes les <see cref="OwnedCacheTtl"/> — jamais
        /// par demande d'image. Un échec de requête library rend un ensemble
        /// vide (aucun badge possédé servi, jamais d'exception dans le
        /// pipeline d'image) ; il sera retenté au TTL suivant.
        /// </summary>
        private HashSet<string> GetOwnedNames()
        {
            var now = DateTime.UtcNow;
            if (s_ownedNames != null && now - s_ownedNamesAtUtc < OwnedCacheTtl)
                return s_ownedNames;

            lock (s_ownedLock)
            {
                if (s_ownedNames != null && now - s_ownedNamesAtUtc < OwnedCacheTtl)
                    return s_ownedNames;

                var set = new HashSet<string>();
                foreach (var itemType in new[] { "Series", "Movie" })
                {
                    try
                    {
                        var q = new InternalItemsQuery
                        {
                            IncludeItemTypes = new[] { itemType },
                            Recursive = true,
                            EnableTotalRecordCount = false
                        };
                        foreach (var name in (_library.GetItemList(q) ?? Array.Empty<BaseItem>())
                                     .Select(i => i.Name))
                        {
                            var k = GetEmbyInfoTool.Norm(name);
                            if (!string.IsNullOrEmpty(k)) set.Add(k);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Info("[LLM_AI] Badge « déjà possédé » : requête library {0} échouée ({1}).",
                            itemType, ex.Message);
                    }
                }

                s_ownedNames = set;
                s_ownedNamesAtUtc = now;
                _logger?.Info("[LLM_AI] Badge « déjà possédé » : {0} nom(s) biblio (Series+Movie) mis en cache (TTL {1} min).",
                    set.Count, (int)OwnedCacheTtl.TotalMinutes);
                return set;
            }
        }

        // ------------------------------------------------------------------
        //  Dessin des badges (SkiaSharp, pur vectoriel)
        // ------------------------------------------------------------------

        /// <summary>
        /// Dessine la pastille puis le badge selon <paramref name="kind"/> :
        /// chip vert + étincelle (AI), ou chip jaune seul (déjà possédé).
        /// Encode vers <paramref name="outputFile"/> au format déduit de son
        /// extension.
        /// </summary>
        private void DrawBadge(string inputFile, string outputFile, BadgeKind kind)
        {
            if (kind == BadgeKind.None)
                throw new InvalidOperationException("aucun badge à dessiner (état attendu AI ou Owned)");

            using (var bitmap = SKBitmap.Decode(inputFile))
            {
                if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
                    throw new InvalidOperationException("image indécodable ou vide");

                int w = bitmap.Width;

                // Taille proportionnelle (visible en vignette guide, discret
                // en page détail) ; marge qui suit la même échelle.
                float d = w * BadgeSizeRatio;
                if (d < BadgeMinPx) d = BadgeMinPx;
                if (d > BadgeMaxPx) d = BadgeMaxPx;
                float pad = Math.Max(3f, w * BadgePaddingRatio);
                float cx = w - pad - d / 2f;
                float cy = pad + d / 2f;

                bool isAi = kind == BadgeKind.Ai;
                var chipColor = isAi
                    ? new SKColor(0x21, 0x96, 0x3F, 0xFF)   // vert « AI »
                    : new SKColor(0xFB, 0xC0, 0x2D, 0xFF);   // jaune « déjà possédé »
                var rimColor = isAi
                    ? new SKColor(20, 70, 30, 150)            // liseré vert sombre
                    : new SKColor(110, 80, 0, 150);           // liseré ambre sombre

                using (var canvas = new SKCanvas(bitmap))
                {
                    // Liseré sombre translucide (halo) : garantit le contraste
                    // du badge sur une pochette claire.
                    using (var rim = new SKPaint { Color = rimColor, IsAntialias = true })
                    {
                        canvas.DrawCircle(cx, cy, d / 2f + MathF.Max(2f, d * 0.05f), rim);
                    }

                    // Pastille pleine.
                    using (var chip = new SKPaint { Color = chipColor, IsAntialias = true })
                    {
                        canvas.DrawCircle(cx, cy, d / 2f, chip);
                    }

                    // Étincelle blanche — uniquement pour le badge « AI ».
                    if (isAi)
                    {
                        using (var white = new SKPaint { Color = SKColors.White, IsAntialias = true })
                        {
                            canvas.DrawPath(BuildSparklePath(cx, cy, d * 0.54f), white);
                        }
                    }

                    canvas.Flush();
                }

                // Format de sortie déduit de l'extension du fichier de cache
                // qu'Emby a nommé d'après l'image source.
                var format = SKEncodedImageFormat.Jpeg;
                int quality = 90;
                string ext = (Path.GetExtension(outputFile) ?? string.Empty)
                    .TrimStart('.').ToLowerInvariant();
                if (ext == "png") { format = SKEncodedImageFormat.Png; quality = 100; }
                else if (ext == "webp") { format = SKEncodedImageFormat.Webp; quality = 90; }

                using (var fs = File.Open(outputFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    if (!bitmap.Encode(fs, format, quality))
                        throw new InvalidOperationException("encodage SkiaSharp échoué");
                }
            }
        }

        /// <summary>
        /// Chemin vectoriel de l'étincelle à quatre branches (glyphe IA
        /// universel, sans texte → multilingue). Pointes en N/E/S/W ; côtés
        /// concaves : chaque segment est une quadratique dont le point de
        /// contrôle est tiré vers le centre le long de la diagonale
        /// (décalage 0,15 × <paramref name="radius"/>), ce qui donne une
        /// « taille » de côté ≈ 0,33 × <paramref name="radius"/> — l'astuce
        /// qui distingue l'étincelle d'une simple étoile à branches droites.
        /// </summary>
        private static SKPath BuildSparklePath(float cx, float cy, float radius)
        {
            float m = radius * 0.15f; // tirage du contrôle vers le centre

            var path = new SKPath();
            path.MoveTo(cx, cy - radius);            // pointe N
            path.QuadTo(cx + m, cy - m, cx + radius, cy);   // N → E (concave)
            path.QuadTo(cx + m, cy + m, cx, cy + radius);   // E → S
            path.QuadTo(cx - m, cy + m, cx - radius, cy);   // S → W
            path.QuadTo(cx - m, cy - m, cx, cy - radius);   // W → N
            path.Close();
            return path;
        }
    }
}