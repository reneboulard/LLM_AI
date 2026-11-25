using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Pont « titre francisé → titre original (v.o.) » par le Répertoire des
    /// films classés du Québec (MCC) — repertoire.cinema.mcc.gouv.qc.ca.
    /// <para>Certains titres de distribution québécois ne sont pas reconnus
    /// par TMDB. Ce "pont" utilise le répertoire gouvernemental pour trouver
    /// le titre original d'une œuvre à partir de son titre québécois,
    /// permettant à l'appelant de relancer une recherche TMDB avec ce
    /// titre original.</para>
    /// <para>Pour gérer les homonymes, le pont utilise l'année attendue
    /// (fournie par l'appelant) pour désambiguïser les résultats. En cas
    /// d'ambiguïté persistante, il retourne `null` plutôt que de deviner.</para>
    /// <para>L'utilisation de ce service est légère : il n'est appelé qu'en
    /// cas d'échec de la recherche TMDB, intègre un cache de 24h, et est
    /// conçu pour échouer silencieusement sans impacter la performance.</para>
    /// <para>Ce client sert deux fonctionnalités : (1) le pont silencieux
    /// de résolution de titre et (2) la consultation explicite via l'outil
    /// LLM <see cref="MccLookupTool"/>. Les deux partagent la même logique
    /// et la même condition d'activation.</para>
    /// <para>Ce service est activé (opt-in sémantique) uniquement si la
    /// configuration <see cref="PluginConfiguration.TmdbLangPriority"/>
    /// contient "fr-CA", signalant une installation qui gère du contenu
    /// canadien-français.</para>
    /// <para>Le titre original trouvé est un indice de recherche, pas une
    /// preuve. La fiche TMDB finalement obtenue est soumise aux mêmes
    /// validations que n'importe quelle autre.</para>
    /// </summary>
    internal static class MccClient
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        /// <summary>Vérifie si les fonctionnalités liées au MCC sont activées.
        /// L'activation est sémantique : elle dépend de la présence de "fr-CA"
        /// dans la configuration <see cref="PluginConfiguration.TmdbLangPriority"/>,
        /// indiquant que l'installation gère du contenu canadien-français.</summary>
        internal static bool IsEnabledForInstall =>
            I18n.ParseTmdbLangPriority(Plugin.Instance?.Configuration?.TmdbLangPriority ?? "", out _)
                .Any(l => string.Equals(l, "fr-CA", StringComparison.OrdinalIgnoreCase));

        // Cartes de résultat de la page /films/ : h2.entry-title > a[href=
        // https://repertoire.cinema.mcc.gouv.qc.ca/film/<slug>-<num>/]
        // (href absolu en page avancée, parfois relatif + query _rt ailleurs).
        private static readonly Regex _cardRegex = new Regex(
            "entry-title[^>]*>\\s*<a href=\"(?<url>[^\"]*?/film/[^\"]*?[-/](?<num>\\d+)/)\"[^>]*>(?<title>[^<]{2,200})",
            RegexOptions.Compiled);

        // Sur une fiche : « v.o. : The Love Punch » ; sur une fiche ET parfois
        // une carte de recherche : « Autre titre : … ». Valeur jusqu'à la fin
        // de la ligne détagée.
        private static readonly Regex _voRegex = new Regex(
            "(?:v\\.o\\.|Autres? titres?)\\s*:\\s*(?<vo>[^\\r\\n]{2,150})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Cartes de la recherche GLOBALE du site (moteur indexé,
        // /?s=…&paged=N) : ancre de titre rel="bookmark", href suivi d'un
        // suffixe de suivi ?_rt=…&#038;_rt_nonce=… que la regex SAUTE — l'URL
        // capturée s'arrête au numéro de fiche (propre au dédoublonnage).
        // NB : la carte porte AUSSI une ancre « entry-media » (image, sans
        // rel) que ce filtre ignore d'office.
        private static readonly Regex _cardRegexGlobal = new Regex(
            "<a href=\"(?<url>[^\"]*?/film/[^\"]*?[-/](?<num>\\d+)/)[^\"]*\"[^>]*rel=\"bookmark\"[^>]*>(?<title>[^<]{2,200})</a>",
            RegexOptions.Compiled);

        /// <summary>Nombre de pages à parcourir pour la recherche globale.
        /// Une borne haute est nécessaire car la pertinence des résultats
        /// diminue rapidement.</summary>
        private const int GlobalSearchPages = 3;

        /// <summary>Carte de résultat parsée d'une page de recherche : titre
        /// QC, v.o. inline (« Autre titre : … ») le cas échéant, URL de
        /// fiche. Type privé — aucune sérialisation.</summary>
        private sealed class Card
        {
            public string Qc;
            public string InlineVo;
            public string Url;
        }

        /// <summary>
        /// Interroge les deux surfaces de recherche du répertoire MCC pour
        /// trouver des correspondances de titre exact, en dédoublonnant les
        /// résultats par URL. Deux surfaces sont nécessaires car certaines
        /// fiches n'apparaissent que dans l'une ou l'autre.
        /// </summary>
        private static async Task<List<Card>> SearchCardsAsync(string title, string norm, int cap, ILogger logger, CancellationToken windowToken)
        {
            var cards = new List<Card>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await AddSurfaceCardsAsync(cards, seen,
                "https://repertoire.cinema.mcc.gouv.qc.ca/films/?s=" + Uri.EscapeDataString(title),
                _cardRegex, norm, cap, logger, windowToken).ConfigureAwait(false);

            for (int p = 1; p <= GlobalSearchPages; p++)
            {
                if (cap > 0 && cards.Count >= cap) break;
                await AddSurfaceCardsAsync(cards, seen,
                    "https://repertoire.cinema.mcc.gouv.qc.ca/?s=" + Uri.EscapeDataString(title) + "&paged=" + p,
                    _cardRegexGlobal, norm, cap, logger, windowToken).ConfigureAwait(false);
            }

            return cards;
        }

        private static async Task AddSurfaceCardsAsync(List<Card> cards, HashSet<string> seen,
            string url, Regex rx, string norm, int cap, ILogger logger, CancellationToken windowToken)
        {
            string html;
            try
            {
                html = await _http.GetStringAsync(new Uri(url), windowToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!windowToken.IsCancellationRequested)
            {
                // Le timeout de HttpClient lève une TaskCanceledException
                // même si le CancellationToken de la fenêtre n'est pas annulé.
                // On intercepte ce cas pour passer à la surface suivante
                // sans annuler toute l'opération.
                logger?.Info("[LLM_AI] MCC : surface de recherche expirée ({0}) — surface suivante", url);
                return;
            }
            catch (OperationCanceledException)
            {
                throw; // la fenêtre/l'hôte annule — l'appelant décide (fail-open)
            }
            catch (Exception ex)
            {
                logger?.Info("[LLM_AI] MCC : surface de recherche indisponible ({0}) : {1} — surface suivante", url, ex.Message);
                return;
            }

            foreach (Match m in rx.Matches(html))
            {
                string cardTitle = WebUtility.HtmlDecode(m.Groups["title"].Value).Trim();
                int sep = cardTitle.IndexOf("Autre titre :", StringComparison.OrdinalIgnoreCase);
                string qc = sep > 0 ? cardTitle.Substring(0, sep).Trim() : cardTitle;
                string inlineVo = sep > 0 ? cardTitle.Substring(sep + "Autre titre :".Length).Trim() : null;

                if (!string.Equals(OrphanResolver.SameTitleKey(qc), norm, StringComparison.Ordinal))
                    continue;
                string cardUrl = Absolute(m.Groups["url"].Value);
                if (string.IsNullOrEmpty(cardUrl) || !seen.Add(cardUrl)) continue;
                cards.Add(new Card { Qc = qc, InlineVo = inlineVo, Url = cardUrl });
                if (cap > 0 && cards.Count >= cap) return;
            }
        }

        /// <summary>
        /// Titre original (« v.o. ») d'un titre de distribution québécois, ou
        /// null. Ne rend qu'une v.o. UNAMBIGUE : les fiches MCC à titre exact
        /// portant des v.o. distinctes sont d'abord filtrées par
        /// <paramref name="expectedYear"/> (±1, année inconnue = pas de garde
        /// — et une v.o. à année illisible reste candidate) ; ce qui reste
        /// d'équivoque conduit à null avec journalisation Warn. Ne lève pas
        /// pour un problème réseau MCC (fail-open, retour null) — seule
        /// l'annulation hôte se propage.
        /// </summary>
        internal static async Task<string> TryGetOriginalTitleAsync(string title, int? expectedYear, ILogger logger, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            string norm = OrphanResolver.SameTitleKey(title);
            if (norm.Length < 3) return null; // « a », « le »… équivoque garantie

            string cacheKey = "mcc:vo:" + norm + ":" + (expectedYear?.ToString() ?? "");
            if (WebResultCache.TryGet(cacheKey, out var cached) && !string.IsNullOrEmpty(cached))
                return cached;

            logger?.Info("[LLM_AI] MCC pont : « {0} » sans fiche TMDB → recherche du titre original (Répertoire des films classés)", title);
            try
            {
                // Une fenêtre de temps est définie pour l'ensemble de
                // l'opération (recherche + lecture des fiches) pour garantir
                // que ce mécanisme de repli ne ralentit pas excessivement
                // le processus principal.
                using (var window = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    window.CancelAfter(TimeSpan.FromSeconds(15));

                    // Les deux surfaces de recherche sont interrogées pour
                    // maximiser les chances de trouver une correspondance, car
                    // certaines fiches ne sont présentes que sur l'une d'elles.
                    var cards = await SearchCardsAsync(title, norm, 6, logger, window.Token).ConfigureAwait(false);

                    var fiches = new List<string>();   // URLs de fiches à lire
                    var voSet = new Dictionary<string, (string Vo, int? Year)>(StringComparer.Ordinal);
                    foreach (Card c in cards)
                    {
                        if (!string.IsNullOrWhiteSpace(c.InlineVo))
                            voSet[OrphanResolver.SameTitleKey(c.InlineVo)] = (c.InlineVo, null);
                        else if (fiches.Count < 2)
                            fiches.Add(c.Url); // ≤ 2 lectures de fiches
                    }

                    if (cards.Count == 0)
                    {
                        logger?.Info("[LLM_AI] MCC pont : aucun titre exact « {0} » au répertoire — fail-open", title);
                        return null;
                    }

                    foreach (string url in fiches)
                    {
                        string html;
                        try
                        {
                            html = await _http.GetStringAsync(new Uri(url), window.Token).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            if (ct.IsCancellationRequested) throw;
                            continue; // fiche illisible / délai de fenêtre : candidat suivant
                        }
                        string text = DeTag(html);
                        Match vo = _voRegex.Match(text);
                        if (!vo.Success) continue;
                        string v = vo.Groups["vo"].Value.Trim();
                        string vKey = OrphanResolver.SameTitleKey(v);
                        if (v.Length > 0 && vKey.Length > 0)
                            voSet[vKey] = (v, FicheYear(text));
                    }

                    // Le filtre par année est utilisé pour désambiguïser les
                    // résultats, mais si ce filtre élimine tous les candidats
                    // (par exemple, à cause d'une année de surface incorrecte),
                    // la recherche est retentée sans le filtre pour ne pas
                    // écarter à tort une correspondance valide. L'ambiguïté
                    // restante est gérée par un abandon.
                    List<string> distinct = VoDistinct(voSet, expectedYear);
                    if (distinct.Count == 0 && expectedYear.HasValue)
                    {
                        distinct = VoDistinct(voSet, null);
                        if (distinct.Count > 0)
                            logger?.Info("[LLM_AI] MCC pont : année {0} élimine toutes les v.o. → cascade sans filtre ({1} résultat(s))", expectedYear, distinct.Count);
                    }

                    if (distinct.Count == 1)
                    {
                        string vo = distinct[0];
                        logger?.Info("[LLM_AI] MCC pont : « {0} » → titre original « {1} » (v.o. corroborée, Répertoire des films classés)", title, vo);
                        WebResultCache.Set(cacheKey, vo);
                        return vo;
                    }
                    if (distinct.Count == 0)
                    {
                        logger?.Info("[LLM_AI] MCC pont : « {0} » — aucune v.o. exploitable au répertoire (œuvre francophone, ou année {1} éliminatrice) — fail-open", title, expectedYear);
                        return null;
                    }
                    logger?.Warn("[LLM_AI] MCC pont : « {0} » — v.o. équivoques ({1}) — abandon honnête", title, string.Join(" | ", distinct));
                    return null;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // l'hôte annule — propager, le pont n'est prioritaire sur rien
            }
            catch (OperationCanceledException)
            {
                logger?.Info("[LLM_AI] MCC pont : délai de fenêtre dépassé sur « {0} » — fail-open", title); // piège HttpClient timeout : ct hôte non annulé
                return null;
            }
            catch (Exception ex)
            {
                logger?.Info("[LLM_AI] MCC pont indisponible : {0} — fail-open", ex.Message);
                return null;
            }
        }

        /// <summary>v.o. distinctes du pool, éventuellement filtrées par
        /// année attendue (±1 — <see cref="OrphanResolver.YearCompatible"/>,
        /// année illisible d'une fiche jamais éliminante). Clé = titre
        /// normalisé — deux orthographes de la même v.o. comptent une fois.</summary>
        private static List<string> VoDistinct(Dictionary<string, (string Vo, int? Year)> voSet, int? expectedYear)
        {
            IEnumerable<(string Vo, int? Year)> pool = voSet.Values;
            if (expectedYear.HasValue)
                pool = pool.Where(e => !e.Year.HasValue || OrphanResolver.YearCompatible(expectedYear, e.Year));
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in pool)
                if (!string.IsNullOrWhiteSpace(e.Vo) && seen.Add(OrphanResolver.SameTitleKey(e.Vo)))
                    list.Add(e.Vo);
            return list;
        }

        /// <summary>
        /// Recherche explicite dans le répertoire MCC, utilisée par l'outil
        /// LLM <see cref="MccLookupTool"/> et pour la concordance des cartes
        /// "identify". Contrairement au pont de résolution, cette méthode
        /// retourne toutes les correspondances exactes, même ambiguës,
        /// laissant à l'utilisateur ou au LLM le soin de les départager.
        /// </summary>
        internal static async Task<List<MccFiche>> LookupFichesAsync(string title, int max, ILogger logger, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(title) || max <= 0) return null;
            string norm = OrphanResolver.SameTitleKey(title);
            if (norm.Length < 3) return null;

            string cacheKey = "mcc:fiches:" + norm;
            if (WebResultCache.TryGet(cacheKey, out var cachedJson) && !string.IsNullOrEmpty(cachedJson))
            {
                try { return JsonSerializer.Deserialize<List<MccFiche>>(cachedJson); }
                catch { /* cache corrompu : relancé en direct */ }
            }

            logger?.Info("[LLM_AI] MCC consultation : « {0} » (Répertoire des films classés)", title);
            try
            {
                using (var window = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    window.CancelAfter(TimeSpan.FromSeconds(20));

                    var cards = await SearchCardsAsync(title, norm, max, logger, window.Token).ConfigureAwait(false);

                    var list = new List<MccFiche>();
                    foreach (Card c in cards)
                    {
                        list.Add(new MccFiche { Titre = c.Qc, Vo = c.InlineVo, Url = c.Url });
                        if (list.Count >= max) break;
                    }

                    if (list.Count == 0)
                    {
                        logger?.Info("[LLM_AI] MCC consultation : aucun titre exact « {0} » au répertoire", title);
                        return new List<MccFiche>(); // vide = fait valide, non caché (doctrine : les zéros sont relancés)
                    }

                    foreach (MccFiche f in list)
                    {
                        string html;
                        try
                        {
                            html = await _http.GetStringAsync(new Uri(f.Url), window.Token).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            if (ct.IsCancellationRequested) throw;
                            continue; // fiche illisible : la carte garde titre/v.o. inline, fail-soft
                        }
                        string text = DeTag(html);
                        Match vo = _voRegex.Match(text);
                        if (vo.Success && !string.IsNullOrWhiteSpace(vo.Groups["vo"].Value))
                            f.Vo = vo.Groups["vo"].Value.Trim(); // la page prime sur la carte (autorité)
                        f.Year = FicheYear(text);
                        ParseFicheBody(text, out string genre, out string synopsis);
                        f.Genre = genre;
                        f.Synopsis = synopsis;
                    }

                    logger?.Info("[LLM_AI] MCC consultation : « {0} » → {1} fiche(s) à titre exact", title, list.Count);
                    WebResultCache.Set(cacheKey, JsonSerializer.Serialize(list));
                    return list;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                logger?.Info("[LLM_AI] MCC consultation : délai de fenêtre dépassé sur « {0} » — fail-open", title);
                return null;
            }
            catch (Exception ex)
            {
                logger?.Info("[LLM_AI] MCC consultation indisponible : {0} — fail-open", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Vérifie si une fiche MCC corrobore une fiche TMDB proposée.
        /// La correspondance se base sur le titre original (v.o.) et une
        /// année de production compatible (±1 an). Le titre québécois n'est
        /// pas utilisé pour éviter les faux positifs sur les homonymes.
        /// </summary>
        internal static bool Corroborates(MccFiche fiche, TmdbFicheCheck proposed)
        {
            if (fiche == null || proposed == null || string.IsNullOrWhiteSpace(fiche.Vo)) return false;
            string voKey = OrphanResolver.SameTitleKey(fiche.Vo);
            if (voKey.Length == 0) return false;
            bool title = KeyEquals(proposed.OriginalTitle, voKey) || KeyEquals(proposed.Title, voKey);
            if (!title) return false;
            return OrphanResolver.YearCompatible(fiche.Year, proposed.Year);
        }

        private static bool KeyEquals(string raw, string key)
            => !string.IsNullOrEmpty(raw) && string.Equals(OrphanResolver.SameTitleKey(raw), key, StringComparison.Ordinal);

        // Marqueurs de sections qui suivent le synopsis sur une fiche MCC —
        // la collecte s'arrête au premier rencontré (terrain 368406).
        private static readonly string[] _sectionMarkers =
        {
            "Classement du film", "Fiche technique", "Distributeurs",
            "Versions du film", "Bandes-annonces", "Motifs de classement"
        };

        /// <summary>Extrait le genre et le synopsis du corps d'une fiche MCC.
        /// Utilise une heuristique basée sur la position relative de l'année,
        /// du mot-clé "Synopsis" et des marqueurs de section pour extraire
        /// les informations pertinentes.</summary>
        private static void ParseFicheBody(string text, out string genre, out string synopsis)
        {
            genre = null;
            synopsis = null;
            if (string.IsNullOrEmpty(text)) return;
            var lines = text.Split('\n');
            int yearIdx = -1, synIdx = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i].Trim();
                if (l.Length == 0) continue;
                if (yearIdx < 0 && l.Length == 4 && char.IsDigit(l[0]) && int.TryParse(l, out int y) && y >= 1900 && y <= 2099)
                    yearIdx = i;
                else if (synIdx < 0 && l.Equals("Synopsis", StringComparison.OrdinalIgnoreCase))
                    synIdx = i;
            }
            if (yearIdx >= 0)
            {
                for (int i = yearIdx + 1; i < lines.Length; i++)
                {
                    string l = lines[i].Trim();
                    if (l.Length == 0) continue;
                    if (!l.Equals("Synopsis", StringComparison.OrdinalIgnoreCase)
                        && !_sectionMarkers.Any(m => l.Equals(m, StringComparison.OrdinalIgnoreCase)))
                        genre = l;
                    break;
                }
            }
            if (synIdx >= 0)
            {
                var sb = new StringBuilder();
                for (int i = synIdx + 1; i < lines.Length && sb.Length < 600; i++)
                {
                    string l = lines[i].Trim();
                    if (l.Length == 0) continue;
                    if (_sectionMarkers.Any(m => l.Equals(m, StringComparison.OrdinalIgnoreCase))) break;
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(l);
                }
                if (sb.Length > 0)
                    synopsis = sb.Length > 500 ? sb.ToString(0, 500) + "…" : sb.ToString();
            }
        }

        /// <summary>href de carte → URL absolue (les pages avancées servent
        /// des hrefs absolus ; d'autres pages servent du relatif + query).</summary>
        private static string Absolute(string href)
        {
            if (string.IsNullOrEmpty(href)) return null;
            if (href.StartsWith("/", StringComparison.Ordinal))
                href = "https://repertoire.cinema.mcc.gouv.qc.ca" + href;
            return href;
        }

        private static readonly Regex _yearRegex = new Regex("\\b(19\\d{2}|20\\d{2})\\b", RegexOptions.Compiled);

        /// <summary>Extrait la première année plausible (1900-2099) de l'en-tête
        /// d'une fiche. Retourne null si aucune année n'est trouvée, ce qui
        /// permet à une fiche sans année d'être quand même considérée lors
        /// du filtrage.</summary>
        private static int? FicheYear(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            Match m = _yearRegex.Match(text.Length > 4000 ? text.Substring(0, 4000) : text);
            return m.Success && int.TryParse(m.Value, out int y) ? y : (int?)null;
        }

        /// <summary>HTML → texte : scripts/styles décapités, balises → saut
        /// de ligne, entités décodées (WebUtility). Assez bon pour les 2 regex.</summary>
        private static string DeTag(string html)
        {
            if (string.IsNullOrEmpty(html)) return string.Empty;
            string s = Regex.Replace(html, "<script[^>]*>[\\s\\S]*?</script>|<style[^>]*>[\\s\\S]*?</style>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "<[^>]*>", "\n");
            return WebUtility.HtmlDecode(s);
        }
    }

    /// <summary>Une fiche du Répertoire des films classés (MCC), telle que
    /// servie au LLM (<see cref="MccLookupTool"/>) et à la concordance des
    /// cartes identify. Propriétés (pas de champs publics) : sérialisation
    /// System.Text.Json directe pour le cache 24 h.</summary>
    internal sealed class MccFiche
    {
        /// <summary>Titre de distribution québécois (celui de la carte de recherche).</summary>
        public string Titre { get; set; }
        /// <summary>Titre original (« v.o. : ») — null si œuvre francophone ou illisible.</summary>
        public string Vo { get; set; }
        /// <summary>Année de la fiche (production/distribution MCC) — null si illisible.</summary>
        public int? Year { get; set; }
        /// <summary>Genre MCC (« Chronique », « Comédie »…) — null si illisible.</summary>
        public string Genre { get; set; }
        /// <summary>Synopsis FR de la fiche (tronqué ~500 car.) — null si absent.</summary>
        public string Synopsis { get; set; }
        /// <summary>URL de la fiche MCC.</summary>
        public string Url { get; set; }
    }
}
