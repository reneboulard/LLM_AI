using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LLM_AI
{
    /// <summary>
    /// Découpe + validation des « doses » de traduction (v1.17.0, T1b) —
    /// port C# des outils du kit local (<c>i18n-kit/split_i18n.js</c> +
    /// règles 4-5 de <c>validate_i18n.js</c>). Pur et statique :
    /// rejouable au harnais réflexion (même convention que
    /// <see cref="I18nOverlay.LoadFrom"/>).
    /// <para><b>Dose</b> = familles atomiques du MÊME domaine (web + server
    /// confondus — la cohérence terminologique d'un écran se joue sur ses
    /// clés ensemble) ; les familles <c>srv.*</c> rejoignent le domaine
    /// « ext » (chrome + serveur du chat externe = une seule page). Une
    /// famille insécable ne traverse jamais un bin ; une famille plus large
    /// que le cap forme sa propre dose (la cohérence passe avant le cap —
    /// règle du kit). Cap 50 clés : la plus grosse dose ≈ 10-12 Ko de
    /// prompt — tient dans le <c>num_ctx</c> 8192 d'un petit modèle local
    /// (terrain kit : la passe unique 529 clés échoue, attention étirée,
    /// 514/529 mesurés).</para>
    /// </summary>
    internal static class I18nDoses
    {
        /// <summary>Clés max par dose (kit <c>split_i18n.js</c> : CAP = 50).</summary>
        internal const int Cap = 50;

        /// <summary>Seuil de fusion des doses trop petites (kit : &lt; 12).</summary>
        internal const int MinMergeSize = 12;

        /// <summary>Cap des doses « tags » (valeurs dont la native EN porte
        /// des balises HTML) : plus bas que <see cref="Cap"/> — la classe
        /// existe pour l'attention ET la représentation (bloc de règles
        /// balises dans DoseUser), pas pour la compacité. Terrain
        /// 2026-10-04 : la valeur la plus balisée (cfg.extchat.desc, 10
        /// balises) a été refusée 2× par gemma4:latest en dose mélangée PUIS
        /// en réparation avec la raison pourtant affichée — la cause était
        /// la représentation des balises, pas l'attention ; population
        /// mesurée 2026-10-04 : 40 clés / 626, toutes web, moy. 416 car.</summary>
        internal const int TagCap = 15;

        /// <summary>Une dose : un domaine sémantique (+ part « _pN »), familles
        /// atomiques, ordre stable (web d'abord, puis server, puis ext).</summary>
        internal sealed class Dose
        {
            /// <summary>Nom d'affichage : « chat », « audit_p2 », « ext »…</summary>
            internal string Name;

            /// <summary>(section, clé) dans l'ordre de collecte.</summary>
            internal readonly List<(string Sec, string Key)> Entries = new();

            /// <summary>Dose « tags » : réservée aux valeurs balisées HTML —
            /// DoseUser y branche le bloc de règles balises + l'inventaire
            /// exact par clé (le rappel final y ajoute la contrainte
            /// multiset).</summary>
            internal bool Tagged;

            internal int Size => Entries.Count;
        }

        /// <summary>Famille d'une clé : 2 premiers segments pour <c>cfg.X</c> /
        /// <c>act.X</c> (famille = section de panneau / type de carte), 1er
        /// segment sinon (port exact de <c>famOf</c> du kit).</summary>
        internal static string FamilyOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return key ?? string.Empty;
            var parts = key.Split('.');
            return parts[0] == "cfg" || parts[0] == "act"
                ? string.Join(".", parts, 0, Math.Min(2, parts.Length))
                : parts[0];
        }

        /// <summary>Domaine d'une famille (port de <c>domOf</c> du kit, +
        /// regroupement <c>srv</c> → <c>ext</c> : chrome ext.* et messages
        /// srv.* du chat externe partagent leur dose — même page, même voix).
        /// Le rec/reco fusionne comme au kit.</summary>
        internal static string DomainOf(string fam)
        {
            string d = fam.StartsWith("cfg.", StringComparison.Ordinal)
                ? fam.Substring(4)
                : fam.StartsWith("act.", StringComparison.Ordinal) ? "act" : fam;
            if (d == "reco" || d == "srv") d = d == "reco" ? "rec" : "ext";
            return d;
        }

        /// <summary>
        /// Découpe la liste (section, clé) en doses : 0) classe optionnelle
        /// « tags » — les entrées balisées (décidées par l'APPELANT, via
        /// <paramref name="tagged"/> : Split n'a pas les natives) partent dans
        /// des doses dédiées, au niveau ENTRÉE (pas à la famille : une
        /// famille cfg.X mêle naturellement des clés balisées — les desc
        /// longues — et nues — les libellés), cap <see cref="TagCap"/>, sans
        /// fusion MinMergeSize, émises EN TÊTE (les plus fragiles d'abord,
        /// modèle frais) ; 1) familles ; 2) domaines ; 3) empaquetage
        /// premier-satisfait par domaine (familles triées par taille
        /// décroissante, bins ≤ cap) ; 4) fusion des doses trop petites ;
        /// 5) nommage (dom / dom_pN ; doses triées par taille décroissante
        /// comme au kit). Déterministe : mêmes entrées → mêmes doses (les
        /// dictionnaires sont parcourus dans l'ordre de déposition).
        /// </summary>
        internal static List<Dose> Split(List<(string Sec, string Key)> all,
            Func<(string, string), bool> tagged = null)
        {
            var doses = new List<Dose>();
            if (all == null || all.Count == 0) return doses;

            // 0) extraction de la classe « tags » (niveau ENTRÉE, ordre de
            //    collecte conservé) + empaquetage greed ≤ TagCap + nommage
            //    tags / tags_pN (convention du 5 : pas de _p1 quand une seule).
            List<(string Sec, string Key)> rest = all;
            if (tagged != null)
            {
                var tagEntries = new List<(string, string)>();
                rest = new List<(string, string)>();
                foreach (var e in all)
                    (tagged(e) ? tagEntries : rest).Add(e);
                if (tagEntries.Count > 0)
                {
                    var tagBins = new List<Dose>();
                    Dose cur = null;
                    int taken = TagCap; // force un bin neuf au premier élément
                    foreach (var e in tagEntries)
                    {
                        if (taken >= TagCap)
                        {
                            tagBins.Add(cur = new Dose { Tagged = true });
                            taken = 0;
                        }
                        cur.Entries.Add(e);
                        taken++;
                    }
                    for (int i = 0; i < tagBins.Count; i++)
                        tagBins[i].Name = tagBins.Count > 1 ? "tags_p" + (i + 1) : "tags";
                    doses.AddRange(tagBins);
                }
            }

            // 1) familles → entrées (ordre de collecte conservé).
            var fams = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
            foreach (var e in rest)
            {
                var f = FamilyOf(e.Item2);
                if (!fams.TryGetValue(f, out var list))
                    fams[f] = list = new List<(string, string)>();
                list.Add(e);
            }

            // 2) regroupement par domaine (ordre d'apparition des familles).
            var doms = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var f in fams.Keys)
            {
                var d = DomainOf(f);
                if (!doms.TryGetValue(d, out var fl)) doms[d] = fl = new List<string>();
                fl.Add(f);
            }

            // 3) empaquetage : domaines par taille décroissante, familles
            //    atomiques, premier-satisfait ≤ cap.
            var bins = new List<(List<string> Fams, int Size)>();
            foreach (var dom in doms.Keys
                         .OrderByDescending(d => doms[d].Sum(f => fams[f].Count))
                         .ThenBy(d => d, StringComparer.Ordinal))
            {
                var domBins = new List<(List<string> Fams, int Size)>();
                foreach (var f in doms[dom].OrderByDescending(f => fams[f].Count)
                             .ThenBy(f => f, StringComparer.Ordinal))
                {
                    int n = fams[f].Count;
                    bool placed = false;
                    for (int i = 0; i < domBins.Count; i++)
                        if (domBins[i].Size + n <= Cap)
                        {
                            domBins[i].Fams.Add(f);
                            domBins[i] = (domBins[i].Fams, domBins[i].Size + n);
                            placed = true;
                            break;
                        }
                    if (!placed) domBins.Add((new List<string> { f }, n));
                }
                bins.AddRange(domBins);
            }

            // 4) fusion des doses trop petites (règle kit : les deux < 12 et
            //    la somme ≤ cap — une dose fusionnée peut mélanger deux
            //    domaines minuscules).
            var merged = new List<(List<string> Fams, int Size)>();
            foreach (var d in bins)
            {
                var hit = merged.FindIndex(m =>
                    m.Size + d.Size <= Cap && m.Size < MinMergeSize && d.Size < MinMergeSize);
                if (hit >= 0)
                {
                    merged[hit].Fams.AddRange(d.Fams);
                    merged[hit] = (merged[hit].Fams, merged[hit].Size + d.Size);
                }
                else merged.Add(d);
            }

            // 5) nommage : tri par taille décroissante (kit) ; un domaine qui
            //    s'étale sur plusieurs doses porte _pN.
            var ordered = merged.OrderByDescending(m => m.Size).ThenBy(m => m.Fams[0], StringComparer.Ordinal);
            var domCount = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var m in ordered)
                foreach (var f in m.Fams)
                {
                    var d = DomainOf(f);
                    domCount[d] = (domCount.TryGetValue(d, out var c) ? c : 0) + 1;
                }
            var domSeen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var m in ordered)
            {
                // Un bin appartient à un seul domaine ; une dose FUSIONNÉE peut
                // en porter deux → tous listés (les fusionnées sont rares).
                var domSet = m.Fams.Select(f => DomainOf(f)).Distinct().ToList();
                string name = string.Join("_", domSet);
                bool multi = domSet.Any(d => domCount[d] > 1);
                if (multi)
                {
                    var dom = domSet[0];
                    domSeen[dom] = (domSeen.TryGetValue(dom, out var c) ? c : 0) + 1;
                    name = dom + "_p" + domSeen[dom];
                }
                var dose = new Dose { Name = name };
                foreach (var f in m.Fams) dose.Entries.AddRange(fams[f]);
                doses.Add(dose);
            }
            return doses;
        }

        // ------------------------------------------------------------------
        //  Validation par clé (miroir exact du chargeur I18nOverlay)
        // ------------------------------------------------------------------

        /// <summary>Verdict de validation d'une valeur traduite contre sa
        /// native EN. La règle est commune aux 3 familles : placeholders {n}
        /// (multiset) ET balises HTML (multiset) identiques — pour la famille
        /// ext (native sans balise) le même test interdit littéralement le
        /// HTML dans la réponse (famille texte brut : le rendu textContent /
        /// str.format afficherait la balise littéralement).</summary>
        internal enum Verdict
        {
            Ok,
            Unknown,                // pas une clé native EN (hallucinée / glissement)
            Empty,                  // blanc
            PlaceholderMismatch,    // multiset {n} divergent
            TagMismatch             // multiset balises divergent (incl. HTML dans ext)
        }

        /// <summary>Valide <paramref name="value"/> contre la native EN
        /// <paramref name="enNative"/> (null = clé inconnue →
        /// <see cref="Verdict.Unknown"/>).</summary>
        internal static Verdict Validate(string enNative, string value)
        {
            if (enNative == null) return Verdict.Unknown;
            if (string.IsNullOrWhiteSpace(value)) return Verdict.Empty;
            if (I18nOverlay.PlaceholderSig(enNative) != I18nOverlay.PlaceholderSig(value))
                return Verdict.PlaceholderMismatch;
            if (I18nOverlay.HtmlTagSig(enNative) != I18nOverlay.HtmlTagSig(value))
                return Verdict.TagMismatch;
            return Verdict.Ok;
        }

        // ------------------------------------------------------------------
        //  Garde « identique à l'anglais » (leçon du kit : le faux conforme)
        // ------------------------------------------------------------------

        /// <summary>Mots non traduits (marques, tokens techniques) — la
        /// comparaison est insensible à la casse.</summary>
        private static readonly HashSet<string> NoTranslate =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "plugin", "llm", "llm_ai", "emby", "embyserver", "ai", "tonight",
                "suggestions", "nfo", "strm", "api", "url", "tmdb", "tvdb",
                "ollama", "gemini", "searxng", "json", "html", "xml", "css",
                "http", "https", "token", "key", "id", "ids", "uuid", "etag",
                "cron", "gpu", "ram", "ssd", "linux", "windows", "docker",
                "youtube", "trakt", "imdb", "fanart",
                // trouvées au harnais T1b (chaînes identiques légitimes)
                "server", "servers", "tv", "live", "dvr", "epg", "app"
            };

        /// <summary>
        /// La valeur est TRIVIALEMENT identique à l'EN : purement
        /// non-alphabétique (icônes, emoji, chiffres, ponctuation), très
        /// courte (≤ 3 caractères), ou composée uniquement de mots
        /// non-traduits (marques). Ex. « 🔊 Auto » contient « Auto » — mot
        /// alphabétique hors marques → PAS trivial (suspect à relire même si
        /// légitime dans la langue). Le vérdict final (garder + flag ⚠️) vit
        /// dans le moteur — ici seule la classification.
        /// </summary>
        internal static bool TriviallyIdenticalEn(string enNative, string value)
        {
            if (enNative == null || value == null ||
                !string.Equals(enNative, value, StringComparison.Ordinal)) return false;
            if (!HasAlphabetic(value)) return true;                 // icônes/purement symbolique
            if (value.Length <= 3) return true;                     // « × », « OK »…
            if (OnlyNoTranslateWords(value)) return true;           // marques/tokens
            return false;
        }

        private static bool HasAlphabetic(string s)
            => !string.IsNullOrEmpty(s) && s.Any(char.IsLetter);

        /// <summary>Tous les mots alphabétique de la valeur sont dans la liste
        /// non-traduite (le split isole les runs alphabétiques — les {n},
        /// chiffres et séparateurs tombent).</summary>
        private static bool OnlyNoTranslateWords(string value)
        {
            var words = Regex.Matches(value, "[A-Za-zÀ-ÿ]{2,}")
                .Select(m => m.Value);
            return words.All(w => NoTranslate.Contains(w));
        }
    }
}