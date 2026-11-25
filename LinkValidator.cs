using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    // ---------------------------------------------------------------------
    //  Validation des liens de fiches (v2.22.0, « validate_links ») :
    //  LECTURE SEULE. Pour chaque film/série de la bibliothèque portant au
    //  moins un id public (TMDB/IMDb/TVDB), on relit la fiche TMDB visée
    //  et on croise les axes indépendants d'identité — imdb, tvdb, clé de
    //  titre exacte (SameTitleKey), année — pour rendre un VERDICT côté
    //  serveur, déterministe et indépendant du modèle (le LLM ne fait que
    //  rapporter ; le banc 2026-11-19 a montré gemma4/ministral incapables
    //  de tenir seuls la règle « l'id qui a servi à trouver la fiche
    //  n'est jamais une preuve »).
    //
    //  Doctrine de terrain (leçons déjà payées, encodées dans le
    //  classifieur) :
    //  - un id ABSENT d'un côté ne prouve RIEN ; seule une VALEUR PRÉSENTE
    //    contradictoire prouve une divergence ;
    //  - l'axe TMDB n'est une preuve que si la fiche a été trouvée par un
    //    chemin INDÉPENDANT de l'id TMDB de l'item (/find imdb/tvdb) —
    //    une fiche trouvée via son propre id « concorde » par construction ;
    //  - le titre de l'item est la forme EPG canonique : on ne le compare
    //    JAMAIS pour le corriger, on le compare pour CORROBORER ou réfuter ;
    //  - la contenance de clé (« de souche » ⊂ « immigrants de souche »,
    //    faux positif historique du 2026-11-16) sans AUCUN id
    //    indépendant = à réviser (bruit fort), jamais une variante ;
    //  - les numéros de suite divergents (« Tuer Bill Vol. 2 » fiché
    //    « Vol. 1 », ids copiés ensemble) = à réviser, jamais une variante ;
    //  - fr-CA ≠ fr-FR : la fiche est lue UNE fois avec
    //    append_to_response=external_ids,translations,alternative_titles
    //    et la clé de titre candidate inclut la langue du fetch,
    //    l'original, TOUTES les variantes fr-* ET les titres alternatifs
    //    du Canada (TMDB ne fait AUCUN repli croisé) — plus des
    //    variantes locales de clé (« & » ≡ « et », numéraux romains
    //    multi-lettres ≡ chiffres) et une similitude de tokens du titre
    //    brut (Jaccard ≥ 0,6, ≥ 2 communs) pour les titres quasi
    //    identiques sans contenance de clé (« Plus étrange que la
    //    fiction » vs fr-CA « …que fiction ») ;
    //  - TVDB n'est JAMAIS appelé directement : l'axe tvdb se valide par
    //    le croisé external_ids.tvdb_id (zéro clé TVDB requise).
    //
    //  Périmètre : films/séries (épisodes et dossiers exclus — l'épisode
    //  n'est pas un champ d'arbitrage d'ids). Zéro
    //  écriture sur les items : aucun tag, aucun verrou, jamais de
    //  suppression/renommage d'id.
    // ---------------------------------------------------------------------

    /// <summary>Verdict du croisement des liens d'une fiche.</summary>
    internal enum LinkVerdict
    {
        /// <summary>Titre (clé exacte) + année + ids d'accord.</summary>
        Corroborated,
        /// <summary>Ids corroborés mais titre différent, avec relation
        /// plausible (traduction/régionale — « Les Pyjamasques » vs
        /// « Pyjamasques »).</summary>
        Variant,
        /// <summary>Item avec ids internes EPG uniquement (zap2it/
        /// gracenote/trakt) — compté, jamais validé (liens cosmétiques).</summary>
        InternalEpg,
        /// <summary>Fiche introuvable/muette : rien à croiser, JAMAIS
        /// « validé » par défaut (discipline fail-closed).</summary>
        NonVerifiable,
        /// <summary>Signaux contradictoires ou faibles : lisible et
        /// listé, à trancher à la main — jamais actionné par le plugin.</summary>
        Review,
        /// <summary>Preuve qu'une valeur présente contredit l'identité :
        /// le seul verdict actionnable (retrait manuel de l'id fautif).</summary>
        Divergent
    }

    /// <summary>Entrées pures du classifieur — les DEUX côtés de la
    /// comparaison, sans aucune dépendance HTTP (testable unitairement).</summary>
    internal sealed class LinkFacts
    {
        /// <summary>Titre de l'item Emby (forme EPG canonique).</summary>
        internal string ItemTitle;
        /// <summary>ProductionYear de l'item.</summary>
        internal int? ItemYear;
        /// <summary>Item série (garde d'année saison-vs-série).</summary>
        internal bool IsSeries;
        /// <summary>Ids présents sur l'ITEM (null/vide = absent).</summary>
        internal string ItemTmdb, ItemImdb, ItemTvdb;
        /// <summary>Fiche TMDB résolue (faux = introuvable).</summary>
        internal bool FicheFound;
        /// <summary>Fiche résolue sur le type OPPOSÉ (film↔série).</summary>
        internal bool CrossKind;
        /// <summary>Axe TMDB indépendant : la fiche a été trouvée via
        /// /find (imdb/tvdb), PAS via l'id tmdb de l'item — comparer
        /// item.tmdb vs fiche.tmdb est alors une PREUVE (et non une
        /// tautologie).</summary>
        internal bool TmdbAxisIndependent;
        /// <summary>Champs de la fiche (langue du fetch).</summary>
        internal string FicheTitle, FicheOriginalTitle, FicheImdb, FicheTvdb, FicheTmdb;
        /// <summary>Variantes fr-* de la table translations (titres).</summary>
        internal string[] FicheFrTitles;
        /// <summary>Année de sortie / première diffusion de la fiche.</summary>
        internal int? FicheYear;
    }

    /// <summary>Sortie du classifieur pur.</summary>
    internal sealed class LinkVerdictInfo
    {
        internal LinkVerdict Verdict;
        /// <summary>Clé i18n de la justification (rendue au rapport).</summary>
        internal string ReasonKey;
        /// <summary>Substituants de la clé (InvariantCulture).</summary>
        internal object[] ReasonArgs;
        /// <summary>Nb d'axes indépendants corroborants (imdb/tvdb/tmdb-indépendant).</summary>
        internal int IdsCorroborated;
        /// <summary>Détail des axes en conflit (déjà assemblé, pour le
        /// rapport et le journal).</summary>
        internal string ConflictDetail;
    }

    /// <summary>Résultat d'un item validé (champs du rapport).</summary>
    internal sealed class LinkCheckResult
    {
        internal long ItemId;
        internal string ItemName;
        internal int? ItemYear;
        internal bool IsSeries;
        internal LinkVerdict Verdict;
        internal string ReasonKey;
        internal object[] ReasonArgs;
        internal string ConflictDetail;
        internal int IdsCorroborated;
        internal int? FicheTmdbId;
        internal string FicheTitle;
        internal int? FicheYear;
        internal bool CrossKind;
        /// <summary>Lien profond Emby (forme relative, construit côté serveur).</summary>
        internal string DeepLink;
        /// <summary>DateCreated.Ticks de l'item (avance du curseur d'audit).</summary>
        internal long DateCreatedTicks;
    }

    /// <summary>Bilan d'un lot validé.</summary>
    internal sealed class LinkBatchResult
    {
        internal int Audited, Corroborated, Variant, InternalEpg, NonVerifiable, Review, Divergent;
        /// <summary>Tout sauf corroboré — ordre du rapport :
        /// divergent → à réviser → variante → non vérifiable.</summary>
        internal readonly List<LinkCheckResult> Notable = new List<LinkCheckResult>();
        internal int TmdbCalls;
        internal double ElapsedSec;
        /// <summary>Curseur de fin (dernier item TRAITÉ du lot) —
        /// 0/0 si lot vide.</summary>
        internal long LastTicks, LastItemId;
    }

    /// <summary>
    /// Classifieur PUR (aucune I/O) : applique les règles de croisement
    /// aux <see cref="LinkFacts"/>. Premier match gagne. Indépendant du
    /// modèle et du langage — les justifications sont des clés i18n.
    /// </summary>
    internal static class LinkRules
    {
        /// <summary>
        /// Rend le verdict. Ordre : fiche introuvable → ids en conflit
        /// (divergent, la preuve la plus forte) → cross-kind (jamais
        /// validé automatiquement — doctrine du plugin) → fiche muette →
        /// numéro de suite divergent → titre exact corroboré → homonyme
        /// (titre exact, année incompatible, aucune preuve) → variante
        /// (ids corroborés + relation de titre) → ids corroborés sans
        /// rapport de titre → double réfutation titre+année (divergent)
        /// → signaux faibles (à réviser).
        /// </summary>
        internal static LinkVerdictInfo Classify(LinkFacts f)
        {
            var v = new LinkVerdictInfo();
            if (f == null)
            {
                v.Verdict = LinkVerdict.NonVerifiable;
                v.ReasonKey = "links.reason.notfound";
                return v;
            }
            if (!f.FicheFound)
            {
                v.Verdict = LinkVerdict.NonVerifiable;
                v.ReasonKey = "links.reason.notfound";
                return v;
            }

            // --- Axes indépendants : une valeur PRÉSENTE contradictoire
            // prouve ; une valeur absente d'un côté ne prouve rien.
            int corroborations = 0, conflicts = 0;
            var conflictSb = new StringBuilder();
            CompareAxis("imdb", f.ItemImdb, f.FicheImdb, ref corroborations, ref conflicts, conflictSb);
            CompareAxis("tvdb", f.ItemTvdb, f.FicheTvdb, ref corroborations, ref conflicts, conflictSb);
            if (f.TmdbAxisIndependent)
                CompareAxis("tmdb", f.ItemTmdb, f.FicheTmdb, ref corroborations, ref conflicts, conflictSb);
            v.IdsCorroborated = corroborations;
            v.ConflictDetail = conflictSb.Length > 0 ? conflictSb.ToString() : null;

            if (conflicts > 0)
            {
                v.Verdict = LinkVerdict.Divergent;
                v.ReasonKey = "links.reason.conflict";
                v.ReasonArgs = new object[] { v.ConflictDetail };
                return v;
            }
            if (f.CrossKind)
            {
                v.Verdict = LinkVerdict.Review;
                v.ReasonKey = "links.reason.crosskind";
                return v;
            }

            bool mute = string.IsNullOrWhiteSpace(f.FicheTitle) && string.IsNullOrWhiteSpace(f.FicheOriginalTitle);
            if (mute)
            {
                // Fiche muette : les seuls axes parlants sont les ids.
                if (corroborations > 0)
                {
                    v.Verdict = LinkVerdict.Corroborated;
                    v.ReasonKey = "links.reason.ids";
                }
                else
                {
                    v.Verdict = LinkVerdict.NonVerifiable;
                    v.ReasonKey = "links.reason.mute";
                }
                return v;
            }

            // v2.22.0 retouches (terrain du 2026-11-19) : le comparer
            // titre tourne en VARIANTES de clé (« & » ≡ « et », numéraux
            // romains ≡ chiffres) et la relation tolère une similitude de
            // tokens du TITRE BRUT — toujours APRÈS les garde-fous
            // (conflit, cross-kind, suite) et JAMAIS synonyme de preuve
            // sans ids corroborés.
            var itemKeys = new List<string>();
            AddKeyVariants(itemKeys, f.ItemTitle);
            var ficheTitles = new List<string>();
            AddTitle(ficheTitles, f.FicheTitle);
            AddTitle(ficheTitles, f.FicheOriginalTitle);
            foreach (var t in f.FicheFrTitles ?? Array.Empty<string>()) AddTitle(ficheTitles, t);
            var ficheKeys = new List<string>();
            foreach (var t in ficheTitles) AddKeyVariants(ficheKeys, t);

            bool titleExact = itemKeys.Count > 0 && ficheKeys.Count > 0
                && itemKeys.Any(i => ficheKeys.Contains(i));
            bool titleRelated = titleExact;
            if (!titleRelated)
            {
                // Contenance de clé (inchangée) sur le produit des variantes.
                foreach (var i in itemKeys)
                {
                    if (i.Length < 4) continue;
                    if (ficheKeys.Any(k => k.Length >= 4 && (k.Contains(i) || i.Contains(k))))
                    {
                        titleRelated = true;
                        break;
                    }
                }
            }
            if (!titleRelated)
            {
                var itemTokens = TitleTokens(f.ItemTitle);
                if (itemTokens.Count > 0 && itemKeys.Any(k => k.Length >= 4))
                    titleRelated = ficheTitles.Any(t => TokensSimilar(itemTokens, TitleTokens(t)));
            }
            bool sequelMismatch = SequelMismatch(f.ItemTitle, f.FicheTitle) || SequelMismatch(f.ItemTitle, f.FicheOriginalTitle);
            bool yearOk = YearOk(f.ItemYear, f.FicheYear, f.IsSeries);

            if (sequelMismatch)
            {
                // « Tuer Bill Vol. 2 » fiché « Vol. 1 » : les ids copiés
                // ensemble « concordent » — c'est le numéro qui réfute.
                v.Verdict = LinkVerdict.Review;
                v.ReasonKey = "links.reason.sequel";
                v.ReasonArgs = new object[] { SequelText(f.ItemTitle), SequelText(f.FicheTitle) };
                return v;
            }
            if (titleExact && (yearOk || corroborations > 0))
            {
                v.Verdict = LinkVerdict.Corroborated;
                v.ReasonKey = "links.reason.corroborated";
                return v;
            }
            if (titleExact)
            {
                // Même titre, autre époque, aucune preuve d'id : la classe
                // homonyme (« 1945 : l'année qui a changé l'histoire »).
                v.Verdict = LinkVerdict.Review;
                v.ReasonKey = "links.reason.homonym";
                return v;
            }
            if (corroborations > 0 && titleRelated)
            {
                v.Verdict = LinkVerdict.Variant;
                v.ReasonKey = "links.reason.variant";
                return v;
            }
            if (corroborations > 0)
            {
                // « Le Lorax » fiché « Serenade » : ids corroborés mais le
                // titre réfute — l'id a été copié d'une autre œuvre.
                v.Verdict = LinkVerdict.Review;
                v.ReasonKey = "links.reason.unrelatedids";
                return v;
            }
            if (!titleRelated && !yearOk)
            {
                // Double réfutation sans aucune preuve (« STRENGTH-1-
                // handbrake.nfo » fiché « Columbo », Δ=30 ans).
                v.Verdict = LinkVerdict.Divergent;
                v.ReasonKey = "links.reason.mismatch";
                return v;
            }
            if (titleRelated)
            {
                // Le faux positif historique : contenance sans AUCUN id
                // indépendant (« de souche » ⊂ « immigrants de souche »).
                // Bruit fort et à réviser — jamais une « variante ».
                v.Verdict = LinkVerdict.Review;
                v.ReasonKey = "links.reason.containment";
                return v;
            }
            v.Verdict = LinkVerdict.Review;
            v.ReasonKey = "links.reason.unrelatedyear";
            return v;
        }

        /// <summary>Garde d'année : |Δ| ≤ 1 toléré ; pour une SÉRIE,
        /// item ≥ fiche accepté (l'année d'un item peut être l'année de
        /// SAISON — doctrine AnchorSeriesYearCompatible : « Les enquêtes
        /// de Vera (2020) » = la série fichée 2011). Inconnue/&lt;1900 :
        /// pas de garde (doctrine commune).</summary>
        internal static bool YearOk(int? itemYear, int? ficheYear, bool isSeries)
        {
            if (!itemYear.HasValue || itemYear.Value < 1900) return true;
            if (!ficheYear.HasValue || ficheYear.Value < 1900) return true;
            if (Math.Abs(itemYear.Value - ficheYear.Value) <= 1) return true;
            return isSeries && itemYear.Value >= ficheYear.Value;
        }

        /// <summary>Compare un axe d'id : présent des deux côtés →
        /// corroboration (égalité, insensible à la casse) ou conflit
        /// (différence). Un seul côté → rien (l'absence ne
        /// prouve jamais rien).</summary>
        private static void CompareAxis(string axis, string itemVal, string ficheVal,
            ref int corroborations, ref int conflicts, StringBuilder conflictSb)
        {
            string a = Norm(itemVal), b = Norm(ficheVal);
            if (a == null || b == null) return;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                corroborations++;
                return;
            }
            conflicts++;
            if (conflictSb.Length > 0) conflictSb.Append(" ; ");
            conflictSb.Append(axis).Append(" : item=").Append(a).Append(" fiche=").Append(b);
        }

        private static string Norm(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static void AddKey(List<string> keys, string title)
        {
            string k = OrphanResolver.SameTitleKey(title);
            if (k.Length > 0 && !keys.Contains(k)) keys.Add(k);
        }

        // --- Variantes locales de clé (terrain du 2026-11-19, lots 1-2) —
        // CORROBORATION des titres uniquement : « Astérix & Obélix » (fiche)
        // ≡ « Astérix et Obélix » (item) et numéraux romains MULTI-LETTRES
        // (« Les visiteurs II » ≡ « Les Visiteurs 2 »). Variantes symétriques
        // sur les DEUX côtés ; les singletons i/v/x/l (articles,
        // « Malcolm X », « L » de la nomenclature EPG) ne sont PAS mappés.

        private static readonly Regex s_romanTokens = new Regex(
            @"\b(?:xviii|viii|xvii|xiii|xiv|xvi|xix|iii|vii|xi|ii|iv|vi|ix|xv|xx)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static string Romanize(string raw) =>
            string.IsNullOrWhiteSpace(raw) ? raw : s_romanTokens.Replace(raw, m => RomanValue(m.Value));

        private static string RomanValue(string token)
        {
            switch (token?.ToLowerInvariant())
            {
                case "ii": return "2";
                case "iii": return "3";
                case "iv": return "4";
                case "vi": return "6";
                case "vii": return "7";
                case "viii": return "8";
                case "ix": return "9";
                case "xi": return "11";
                case "xii": return "12";
                case "xiii": return "13";
                case "xiv": return "14";
                case "xv": return "15";
                case "xvi": return "16";
                case "xvii": return "17";
                case "xviii": return "18";
                case "xix": return "19";
                case "xx": return "20";
                default: return null;
            }
        }

        /// <summary>Clé de titre + variantes locales (esperluette
        /// « & »→« et », chiffres romains, combinées) — dédupliquées.
        /// Corroboration uniquement : une « exacte » obtenue par variante
        /// reste subordonnée aux ids corroborés (branches amont).</summary>
        private static void AddKeyVariants(List<string> keys, string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return;
            AddKey(keys, title);
            AddKey(keys, title.Replace("&", " et "));
            string roman = Romanize(title);
            AddKey(keys, roman);
            AddKey(keys, roman.Replace("&", " et "));
        }

        // --- Similitude de tokens sur le TITRE BRUT (la clé SameTitleKey
        // est une concaténation sans séparateurs, inutilisable pour ça) :
        // « Plus étrange que la fiction » ≡ fr-CA « Plus étrange que
        // fiction », « chapitre 2 » inséré par l'item… Seuil Jaccard ≥ 0,6
        // ET ≥ 2 tokens communs — « Damnée United » vs « The Damned
        // United » (1 commun), « Le Lorax » vs « Sérénade », « La vie en
        // rose » vs « La Môme » restent hors similitude.

        private static readonly Regex s_wordTokens = new Regex(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);

        private static HashSet<string> TitleTokens(string title)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(title)) return set;
            foreach (var t in s_wordTokens.Split(title))
            {
                if (t.Length == 0) continue;
                string n = OrphanResolver.NormalizeTitle(t);
                if (n.Length > 0) set.Add(n);
            }
            return set;
        }

        private static bool TokensSimilar(HashSet<string> a, HashSet<string> b)
        {
            if (a == null || b == null || a.Count == 0 || b.Count == 0) return false;
            int common = a.Count(t => b.Contains(t));
            if (common < 2) return false;
            int union = a.Count + b.Count - common;
            return (double)common / union >= 0.6;
        }

        private static void AddTitle(List<string> titles, string t)
        {
            string n = t?.Trim();
            if (!string.IsNullOrEmpty(n) && !titles.Contains(n, StringComparer.Ordinal)) titles.Add(n);
        }

        internal static string[] MergeTitles(string[] a, string[] b)
        {
            if (a == null || a.Length == 0) return b;
            if (b == null || b.Length == 0) return a;
            var all = new List<string>(a);
            foreach (var t in b)
                if (!all.Contains(t, StringComparer.Ordinal)) all.Add(t);
            return all.ToArray();
        }

        // --- Numéros de suite (terrain 2026-11-19, « Tuer Bill Vol. 2 »
        // fiché Vol. 1) : on extrait les nombres du titre de CHAQUE côté ;
        // des ensembles NON VIDES et DISJOINTS = numéros de suite
        // divergents (un seul côté numéroté, ou aucun → pas de garde —
        // « Océans 8 » vs « Ocean's Eight » reste comparable par la clé
        // de titre). Les 4 chiffres d'une année dans le titre des DEUX
        // côtés s'intersectent naturellement (« 1917 » ↔ « 1917 »).

        private static readonly Regex s_seqTokens = new Regex(@"\p{Nd}+|[\p{L}]+", RegexOptions.Compiled);

        private static bool SequelMismatch(string itemTitle, string ficheTitle)
        {
            var a = SequelSet(itemTitle);
            var b = SequelSet(ficheTitle);
            if (a.Count == 0 || b.Count == 0) return false;
            return !a.Overlaps(b);
        }

        // Numéros de suite : chiffres + romains MULTI-LETTRES (« partie II »
        // ≡ {2} — « Les visiteurs II » vs « Visiteurs 2 » intersecte, « Vol. 2 »
        // vs « Vol. 1 » reste disjoint, « Visiteurs II » vs « 3 » reste
        // disjoint → à réviser). Les singletons i/v/x/l ne sont pas mappés
        // (articles, « Malcolm X »). Un seul côté numéroté, ou aucun → pas
        // de garde (comportement inchangé).
        private static HashSet<string> SequelSet(string title)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(title)) return set;
            foreach (Match m in s_seqTokens.Matches(title))
            {
                if (char.IsDigit(m.Value[0])) { set.Add(m.Value); continue; }
                string rom = RomanValue(m.Value);
                if (rom != null) set.Add(rom);
            }
            return set;
        }

        private static string SequelText(string title)
        {
            var set = SequelSet(title);
            return set.Count == 0 ? "—" : string.Join(",", set.OrderBy(x => x, StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// État-curseur de l'audit incrémental « links_check » :
    /// <c>validate_links_state.json</c> dans le répertoire de
    /// configuration du plugin. Le curseur = (DateCreated.Ticks,
    /// InternalId) du DERNIER item traité — chaque passage prend les
    /// items suivants dans l'ordre (DateCreated, Id) : « les X après la
    /// dernière date auditée ». Convention des fichiers d'état (v2.1.0) :
    /// noms figés explicitement, échec d'écriture LOUD — ici il a une
    /// conséquence fonctionnelle documentée : le lot non persisté est
    /// REJOUÉ au passage suivant (idempotent par construction, la
    /// validation est lecture seule).
    /// </summary>
    internal sealed class LinksAuditState
    {
        /// <summary>DateCreated.Ticks du dernier item traité (0 = jamais
        /// audité → premier passage = baseline complète depuis le plus ancien).</summary>
        public long CursorTicks;
        /// <summary>InternalId de départage des égalités de date (import
        /// massif à la même seconde).</summary>
        public long CursorItemId;
        /// <summary>Date/heure UTC ISO du dernier passage.</summary>
        public string LastRunUtc;
        /// <summary>Compteurs du dernier passage.</summary>
        public int LastAudited, LastDivergent;
    }

    /// <summary>IO de l'état-curseur (fail-open en lecture, LOUD en
    /// écriture : un échec retourne false et l'appelant signale le
    /// rejeu).</summary>
    internal static class LinksAuditStateStore
    {
        private const string FileName = "validate_links_state.json";

        private static string PathOf()
        {
            try
            {
                var dir = Plugin.Paths?.PluginConfigurationsPath;
                if (string.IsNullOrEmpty(dir)) return null;
                return Path.Combine(dir, FileName);
            }
            catch { return null; }
        }

        /// <summary>Charge l'état (null si absent/corrompu — tolérant,
        /// jamais d'exception ; le premier passage part alors du plus
        /// ancien).</summary>
        internal static LinksAuditState Load(ILogger logger)
        {
            var path = PathOf();
            if (path == null || !File.Exists(path)) return null;
            string json;
            try { json = File.ReadAllText(path); }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Liens : lecture de l'état d'audit échouée ({0}) — repart du plus ancien.", ex.Message);
                return null;
            }
            try
            {
                if (!(JsonNode.Parse(json) is JsonObject root)) return null;
                var s = new LinksAuditState
                {
                    CursorTicks = ONum(root, "cursor_ticks"),
                    CursorItemId = ONum(root, "cursor_item_id"),
                    LastRunUtc = OStr(root, "last_run_utc"),
                    LastAudited = (int)ONum(root, "last_audited"),
                    LastDivergent = (int)ONum(root, "last_divergent")
                };
                return s;
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Liens : état d'audit corrompu ({0}) — repart du plus ancien.", ex.Message);
                return null;
            }
        }

        /// <summary>Écrit l'état. Retourne false (et loggue en Error —
        /// convention LOUD) si l'écriture échoue : le lot courant n'est
        /// PAS persisté, il sera revalidé au prochain passage.</summary>
        internal static bool Save(LinksAuditState state, ILogger logger)
        {
            if (state == null) return false;
            var path = PathOf();
            if (path == null)
            {
                logger?.Error("[LLM_AI] Liens : répertoire de configuration indisponible — état d'audit NON persisté, le lot sera rejoué.");
                return false;
            }
            try
            {
                var root = new JsonObject
                {
                    ["cursor_ticks"] = state.CursorTicks,
                    ["cursor_item_id"] = state.CursorItemId,
                    ["last_run_utc"] = state.LastRunUtc ?? "",
                    ["last_audited"] = state.LastAudited,
                    ["last_divergent"] = state.LastDivergent
                };
                AtomicFile.WriteAllText(path, root.ToJsonString());
                return true;
            }
            catch (Exception ex)
            {
                logger?.ErrorException("[LLM_AI] Liens : écriture de l'état d'audit ({0}) ÉCHOUÉE : {1} — le lot sera rejoué au prochain passage.",
                    ex, FileName, ex.Message);
                return false;
            }
        }

        private static string OStr(JsonObject o, string key) =>
            o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;

        private static long ONum(JsonObject o, string key) =>
            o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<long>(out var n) ? n : 0L;
    }

    /// <summary>
    /// Orchestrateur de validation d'un lot d'items : fetch TMDB (via
    /// <see cref="TmdbLookupTool"/>) avec THROTTLE fixe de 300 ms entre
    /// appels (~33 req/10 s — sous le plafond de débit TMDB en
    /// permanence ; une clé v3 ne s'épuise pas, c'est la rafale qui est
    /// dangereuse), construction des <see cref="LinkFacts"/>, verdict
    /// <see cref="LinkRules.Classify"/>, journal greppable par item
    /// notable, et rapport prêt à relayer.
    /// </summary>
    internal sealed class LinkValidator
    {
        /// <summary>Throttle TMDB (ms) — fixe, non configurable : le
        /// plafond de débit TMDB (~40-50 req/10 s) est une propriété du
        /// service, pas une préférence locale.</summary>
        private const int ThrottleMs = 300;

        private readonly TmdbLookupTool _tmdb;
        private readonly ILogger _logger;
        private long _lastCallUtcTicks;

        public LinkValidator(TmdbLookupTool tmdb, ILogger logger)
        {
            _tmdb = tmdb ?? new TmdbLookupTool(logger);
            _logger = logger;
        }

        /// <summary>L'id TMDB présent sur l'item (int, 0 si absent/invalide).</summary>
        internal static int ItemTmdbId(BaseItem item)
        {
            var raw = SafeProvider(item, "tmdb");
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
        }

        internal static string SafeProvider(BaseItem item, string key)
        {
            if (item == null) return null;
            try
            {
                var v = item.GetProviderId(key);
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            }
            catch { return null; }
        }

        /// <summary>L'item porte-t-il au moins un id public (tmdb/imdb/tvdb) ?</summary>
        internal static bool HasPublicId(BaseItem item) =>
            SafeProvider(item, "tmdb") != null || SafeProvider(item, "imdb") != null || SafeProvider(item, "tvdb") != null;

        /// <summary>L'item porte-t-il au moins un id QUELCONQUE (public ou
        /// interne EPG — zap2it/gracenote/trakt) ? Ceux sans AUCUN id
        /// sont les orphelins du pipeline d'identification — hors lot.</summary>
        internal static bool HasAnyProviderId(BaseItem item)
        {
            try { var ids = item?.ProviderIds; return ids != null && ids.Count > 0; }
            catch { return false; }
        }

        /// <summary>Éligibilité au lot : film/série (jamais épisode —
        /// conventions de numération, pas un champ d'arbitrage d'ids),
        /// pas une carte .strm, au moins un id.</summary>
        internal static bool IsEligible(BaseItem item) =>
            item != null
            && !OrphanResolver.IsStrmCard(item)
            && (item is Movie || item is Series)
            && HasAnyProviderId(item);

        /// <summary>serverId du routeur web (GetPublicSystemInfo — le
        /// chemin fiable, GetSystemInfo lève une NRE sur certains hôtes).
        /// Null toléré : le rapport omet alors les liens.</summary>
        internal static async Task<string> ResolveServerIdAsync(IServerApplicationHost host)
        {
            if (host == null) return null;
            try { return (await host.GetPublicSystemInfo(CancellationToken.None).ConfigureAwait(false))?.Id; }
            catch { return null; }
        }

        /// <summary>Valide un lot complet. Le cap est appliqué par
        /// l'APPELANT (le lot transmis est déjà borné). Ne lève pas
        /// (l'annulation réelle se propage — discipline commune).</summary>
        internal async Task<LinkBatchResult> CheckItemsAsync(IReadOnlyList<BaseItem> lot, string lang, string serverId, CancellationToken ct)
        {
            var b = new LinkBatchResult();
            TmdbCallCount = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < lot.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = lot[i];
                if (item == null) continue;
                var r = await CheckItemAsync(item, lang, ct).ConfigureAwait(false);
                b.Audited++;
                switch (r.Verdict)
                {
                    case LinkVerdict.Corroborated: b.Corroborated++; break;
                    case LinkVerdict.Variant: b.Variant++; break;
                    case LinkVerdict.InternalEpg: b.InternalEpg++; break;
                    case LinkVerdict.NonVerifiable: b.NonVerifiable++; break;
                    case LinkVerdict.Review: b.Review++; break;
                    case LinkVerdict.Divergent: b.Divergent++; break;
                }
                if (r.Verdict != LinkVerdict.Corroborated && r.Verdict != LinkVerdict.InternalEpg)
                {
                    r.DeepLink = BuildDeepLink(item, serverId);
                    b.Notable.Add(r);
                    // Journal greppable : chaque non-corroboré laisse une
                    // ligne (classe + item + justification) — l'audit de
                    // liens se lit au log comme le reste du plugin.
                    _logger?.Info("[LLM_AI] Liens : [{0}] item={1} « {2} » — {3}",
                        ClassLabel(r.Verdict, I18n.Fr), r.ItemId, r.ItemName, ReasonText(r));
                }
                b.LastTicks = item.DateCreated.Ticks;
                b.LastItemId = item.InternalId;
            }
            sw.Stop();
            b.ElapsedSec = sw.Elapsed.TotalSeconds;
            b.TmdbCalls = TmdbCallCount;
            SortNotable(b.Notable);
            _logger?.Info("[LLM_AI] Liens : lot de {0} item(s) — corroboré={1} variante={2} interne-EPG={3} non vérifiable={4} à réviser={5} divergent={6} ({7} appel(s) TMDB, {8:0.#} s).",
                b.Audited, b.Corroborated, b.Variant, b.InternalEpg, b.NonVerifiable, b.Review, b.Divergent,
                b.TmdbCalls, b.ElapsedSec);
            return b;
        }

        /// <summary>Valide UN item : fetch de la fiche (avec throttle), croisement,
        /// verdict. Ne lève jamais (annulation réelle exceptée).</summary>
        internal async Task<LinkCheckResult> CheckItemAsync(BaseItem item, string lang, CancellationToken ct)
        {
            bool isSeries = item is Series;
            string kind = isSeries ? "tv" : "movie";
            string otherKind = isSeries ? "movie" : "tv";
            var r = NewResult(item, isSeries);

            string tmdbRaw = SafeProvider(item, "tmdb");
            string imdb = SafeProvider(item, "imdb");
            string tvdb = SafeProvider(item, "tvdb");
            if (!HasAnyProviderId(item))
            {
                // Sélection explicite d'un item sans AUCUN id : rien à
                // valider (le pipeline orphelins en est propriétaire).
                r.Verdict = LinkVerdict.NonVerifiable;
                r.ReasonKey = "links.reason.noid";
                return r;
            }
            if (tmdbRaw == null && imdb == null && tvdb == null)
            {
                // Ids internes EPG seulement (zap2it/gracenote/trakt) :
                // comptés, jamais validés — liens cosmétiques du gabarit.
                r.Verdict = LinkVerdict.InternalEpg;
                r.ReasonKey = "links.reason.internalepg";
                return r;
            }

            // null = pas encore résolue : le cas « id tmdb non numérique,
            // aucun autre id public » (rare mais vivant) doit tomber dans
            // le NON-VÉRIFIABLE « introuvable », pas dans une fiche vide.
            TmdbFicheCheck fiche = null;
            bool viaTmdb = false, crossKind = false;
            int tmdbId = ItemTmdbId(item);
            if (tmdbId > 0)
            {
                fiche = await FetchFicheAsync(tmdbId, kind, lang, ct).ConfigureAwait(false);
                if (fiche != null) viaTmdb = true;
                else
                {
                    // Id mort sur SON type → l'autre type ? (fiche croisée
                    // film↔série — doctrine cross-kind : jamais validé
                    // automatiquement.)
                    fiche = await FetchFicheAsync(tmdbId, otherKind, lang, ct).ConfigureAwait(false);
                    crossKind = fiche != null;
                }
            }
            if (fiche == null && (imdb != null || tvdb != null))
            {
                // Sans id TMDB (ou id TMDB mort) : résolution indépendante
                // par /find — l'axe tmdb devient alors une PREUVE (fiche
                // trouvée par imdb/tvdb, pas par son propre id).
                fiche = await FindFicheAsync(imdb, tvdb, kind, lang, ct).ConfigureAwait(false);
                if (fiche == null)
                {
                    fiche = await FindFicheAsync(imdb, tvdb, otherKind, lang, ct).ConfigureAwait(false);
                    crossKind = fiche != null;
                }
            }
            if (fiche == null)
            {
                r.Verdict = LinkVerdict.NonVerifiable;
                r.ReasonKey = "links.reason.notfound";
                return r;
            }

            r.FicheTmdbId = fiche.TmdbId;
            r.FicheTitle = fiche.OriginalTitle ?? fiche.Title;   // pour la ligne de rapport : original prioritaire
            r.FicheYear = fiche.Year;
            r.CrossKind = crossKind;

            var facts = new LinkFacts
            {
                ItemTitle = item.Name,
                ItemYear = item.ProductionYear,
                IsSeries = isSeries,
                ItemTmdb = tmdbRaw,
                ItemImdb = imdb,
                ItemTvdb = tvdb,
                FicheFound = true,
                CrossKind = crossKind,
                // Tautologie : une fiche trouvée via l'id tmdb de
                // l'item « concorde » par construction sur cet axe — il ne
                // devient une preuve QUE via /find (imdb/tvdb).
                TmdbAxisIndependent = !viaTmdb,
                FicheTitle = fiche.Title,
                FicheOriginalTitle = fiche.OriginalTitle,
                FicheImdb = fiche.ImdbId,
                FicheTvdb = fiche.TvdbId,
                FicheTmdb = fiche.TmdbId > 0 ? fiche.TmdbId.ToString(CultureInfo.InvariantCulture) : null,
                // Titres alternatifs du Canada ajoutés aux variantes fr-*
                // (terrain 2026-11-19 : fiches sans TOUTE traduction fr côté
                // TMDB — « Damnée United », « Rome mon amour » — les alts CA
                // sont le dernier axe « titre fr »).
                FicheFrTitles = LinkRules.MergeTitles(fiche.FrTitles, fiche.AltCaTitles),
                FicheYear = fiche.Year
            };
            var verdict = LinkRules.Classify(facts);
            r.Verdict = verdict.Verdict;
            r.ReasonKey = verdict.ReasonKey;
            r.ReasonArgs = verdict.ReasonArgs;
            r.ConflictDetail = verdict.ConflictDetail;
            r.IdsCorroborated = verdict.IdsCorroborated;
            return r;
        }

        // ------------------------------------------------------------------
        //  Fetch (throttle + résolution /find)
        // ------------------------------------------------------------------

        private async Task<TmdbFicheCheck> FetchFicheAsync(int tmdbId, string kind, string lang, CancellationToken ct)
        {
            await ThrottleAsync(ct).ConfigureAwait(false);
            TmdbCallCount++;
            try
            {
                return await _tmdb.FetchValidationFicheAsync(tmdbId, kind, lang, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Info("[LLM_AI] Liens : fiche tmdb={0} ({1}) illisible ({2}).", tmdbId, kind, ex.Message);
                return null;
            }
        }

        private async Task<TmdbFicheCheck> FindFicheAsync(string imdb, string tvdb, string kind, string lang, CancellationToken ct)
        {
            // IMDb prioritaire (mieux mappé côté TMDB), TVDB ensuite. La
            // fiche trouvée est RELUE par FetchValidationFicheAsync pour
            // le keyset complet (external_ids + translations fr-*) : le
            // /find seul ne porte pas les ids externes.
            if (imdb != null)
            {
                await ThrottleAsync(ct).ConfigureAwait(false);
                TmdbCallCount++;
                var id = await TryFindAsync(imdb, "imdb_id", kind, ct).ConfigureAwait(false);
                if (id.HasValue) return await FetchFicheAsync(id.Value, kind, lang, ct).ConfigureAwait(false);
            }
            if (tvdb != null)
            {
                await ThrottleAsync(ct).ConfigureAwait(false);
                TmdbCallCount++;
                var id = await TryFindAsync(tvdb, "tvdb_id", kind, ct).ConfigureAwait(false);
                if (id.HasValue) return await FetchFicheAsync(id.Value, kind, lang, ct).ConfigureAwait(false);
            }
            return null;
        }

        private async Task<int?> TryFindAsync(string externalId, string source, string kind, CancellationToken ct)
        {
            try { return await _tmdb.FindTmdbIdByExternalIdAsync(externalId, source, kind, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Info("[LLM_AI] Liens : tmdb /find {0}={1} échoué ({2}).", source, externalId, ex.Message);
                return null;
            }
        }

        /// <summary>Compteur d'appels TMDB du lot courant (reset par
        /// CheckItemsAsync — l'instance vit le temps d'un lot).</summary>
        internal int TmdbCallCount { get; private set; }

        private async Task ThrottleAsync(CancellationToken ct)
        {
            if (_lastCallUtcTicks > 0)
            {
                var since = DateTime.UtcNow.Ticks - _lastCallUtcTicks;
                var wait = TimeSpan.FromMilliseconds(ThrottleMs).Ticks - since;
                if (wait > 0) await Task.Delay(TimeSpan.FromTicks(wait), ct).ConfigureAwait(false);
            }
            _lastCallUtcTicks = DateTime.UtcNow.Ticks;
        }

        // ------------------------------------------------------------------
        //  Rapport (texte prêt à relayer — verdict indépendant du modèle)
        // ------------------------------------------------------------------

        internal static string ClassLabel(LinkVerdict v, string langKey = null)
        {
            string key;
            switch (v)
            {
                case LinkVerdict.Corroborated: key = "links.class.corroborated"; break;
                case LinkVerdict.Variant: key = "links.class.variant"; break;
                case LinkVerdict.InternalEpg: key = "links.class.internalepg"; break;
                case LinkVerdict.NonVerifiable: key = "links.class.nonverifiable"; break;
                case LinkVerdict.Review: key = "links.class.review"; break;
                default: key = "links.class.divergent"; break;
            }
            return string.IsNullOrEmpty(langKey) ? key : I18n.S(key, langKey);
        }

        /// <summary>Justification localisée d'un résultat ({0} substitués
        /// en InvariantCulture — les ids ne se localisent pas).</summary>
        internal static string ReasonText(LinkCheckResult r, string langKey = null) =>
            r == null || string.IsNullOrEmpty(r.ReasonKey) ? string.Empty
                : string.Format(CultureInfo.InvariantCulture,
                    I18n.S(r.ReasonKey, string.IsNullOrEmpty(langKey) ? I18n.Fr : langKey),
                    r.ReasonArgs ?? Array.Empty<object>());

        /// <summary>Bilan textuel multi-lignes : entête + notables (tous
        /// divergents/à réviser, variante/non-vérifiable plafonnés) —
        /// construit côté serveur pour rester indépendant du modèle (les liens
        /// profonds sont déjà portés par les résultats).</summary>
        internal static string BuildReportText(LinkBatchResult b, string langKey, int maxLines = 20)
        {
            if (b == null) return string.Empty;
            var sb = new StringBuilder();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                I18n.S("links.report.header", langKey),
                b.Audited, b.Corroborated, b.Variant, b.InternalEpg, b.NonVerifiable, b.Review, b.Divergent));

            int listed = 0;
            Action<LinkCheckResult> line = r =>
            {
                sb.Append("[").Append(ClassLabel(r.Verdict, langKey)).Append("] ")
                  .Append(string.Format(CultureInfo.InvariantCulture, I18n.S("links.report.line", langKey),
                      r.ItemName ?? "?",
                      r.ItemYear?.ToString(CultureInfo.InvariantCulture) ?? "?",
                      I18n.S(r.IsSeries ? "links.kind.series" : "links.kind.movie", langKey),
                      r.ItemId.ToString(CultureInfo.InvariantCulture),
                      ReasonText(r, langKey)));
                if (r.FicheTmdbId.HasValue)
                    sb.Append(string.Format(CultureInfo.InvariantCulture, I18n.S("links.report.fiche", langKey),
                        r.FicheTmdbId.Value.ToString(CultureInfo.InvariantCulture),
                        r.FicheTitle ?? "?",
                        r.FicheYear?.ToString(CultureInfo.InvariantCulture) ?? "?"));
                sb.AppendLine();
                if (!string.IsNullOrEmpty(r.DeepLink))
                    sb.AppendLine("  " + string.Format(CultureInfo.InvariantCulture,
                        I18n.S("links.report.link", langKey), r.DeepLink));
                listed++;
            };

            var order = new[] { LinkVerdict.Divergent, LinkVerdict.Review, LinkVerdict.Variant, LinkVerdict.NonVerifiable };
            foreach (var cls in order)
            {
                foreach (var r in b.Notable.Where(n => n.Verdict == cls))
                {
                    if (listed >= maxLines) goto done;
                    line(r);
                }
            }
        done:
            if (listed < b.Notable.Count)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    I18n.S("links.report.more", langKey), b.Notable.Count - listed));
            return sb.ToString().TrimEnd();
        }

        /// <summary>Projection JSON compacte des notables (pour que le LLM
        /// puisse répondre aux relances SANS re-valider).</summary>
        internal static List<object> BuildNotableProjection(LinkBatchResult b, string langKey, int max = 25)
        {
            var list = new List<object>();
            if (b == null) return list;
            foreach (var r in b.Notable)
            {
                if (list.Count >= max) break;
                list.Add(new
                {
                    @class = ClassLabel(r.Verdict, langKey),
                    id = r.ItemId.ToString(CultureInfo.InvariantCulture),
                    name = r.ItemName,
                    year = r.ItemYear,
                    kind = r.IsSeries ? "series" : "movie",
                    fiche_tmdb = r.FicheTmdbId,
                    fiche_title = r.FicheTitle,
                    fiche_year = r.FicheYear,
                    reason = ReasonText(r, langKey),
                    link = r.DeepLink
                });
            }
            return list;
        }

        private static LinkCheckResult NewResult(BaseItem item, bool isSeries)
        {
            return new LinkCheckResult
            {
                ItemId = item.InternalId,
                ItemName = item.Name,
                ItemYear = item.ProductionYear,
                IsSeries = isSeries,
                DateCreatedTicks = item.DateCreated.Ticks
            };
        }

        private static void SortNotable(List<LinkCheckResult> notable) =>
            notable.Sort((x, y) => Rank(x.Verdict).CompareTo(Rank(y.Verdict)));

        private static int Rank(LinkVerdict v)
        {
            switch (v)
            {
                case LinkVerdict.Divergent: return 0;
                case LinkVerdict.Review: return 1;
                case LinkVerdict.Variant: return 2;
                default: return 3;
            }
        }

        /// <summary>Lien profond RELATIF (reconnu par la page chat ET
        /// l'app compagnon — le serveur fait respecter la forme, jamais le
        /// LLM, leçon v2.6.9) ; serverId résolu une fois par lot.</summary>
        private static string BuildDeepLink(BaseItem item, string serverId)
        {
            if (item == null || string.IsNullOrWhiteSpace(serverId)) return null;
            try
            {
                return "/web/index.html#!/item?id=" + item.InternalId.ToString(CultureInfo.InvariantCulture)
                    + "&serverId=" + serverId
                    + (item is Series ? "&asSeries=true" : string.Empty);
            }
            catch { return null; }
        }
    }
}
