using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Api;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace LLM_AI
{
    /// <summary>
    /// Endpoint « i18n » (v1.16.0, plan P2) : sert les traductions
    /// communautaires aux pages web du plugin, et la base EN native aux
    /// traducteurs. Auth Emby standard (aucun gate admin : la page recos est
    /// en menu usager, et le dictionnaire ne contient que des libellés
    /// d'interface déjà rendus aux usagers — pas de config, pas de prompt :
    /// le contenu des prompts vit dans <c>DefaultPrompts.cs</c>, pas ici).
    /// <list type="bullet">
    /// <item><c>GET /Plugins/LLMAI/I18n</c> — overlay : les slices
    ///   <c>web</c> du fichier <c>LLM_AI_i18n.json</c> par langue
    ///   (<c>{ "es": { "web": {…} }, … }</c>). Force-refresh
    ///   (<see cref="I18nOverlay.TryRefresh"/>) : frais au GET, zéro coût
    ///   pour <c>I18n.S</c>. Overlay inactif → <c>{}</c> (le client merge
    ///   rien → natif, fail-open symétrique du chargeur). La section
    ///   <c>server</c> n'est JAMAIS servie ici.</item>
    /// <item><c>GET /Plugins/LLMAI/I18n?base</c> — base EN native générée à
    ///   chaud (<c>{"en": {"web": {…}, "server": {…}, "ext": {…}}}</c>, P2b :
    ///   une section « ext » en plus) : la moitié serveur vient directement
    ///   de <see cref="I18n"/>, la famille ext de <see cref="I18n.EnExtDict"/> ;
    ///   la moitié web est EXTRAITE du module embarqué <c>i18n.js</c> (scan
    ///   borné de <c>STRINGS.en</c> — ancre, accolades équilibrées, paires
    ///   "clé" : "valeur" — approche de <c>extract_i18n.js</c>, sans eval).
    ///   C'est le pivot de traduction : le traducteur remplit les sections
    ///   pour sa langue.</item>
    /// <item><c>GET /Plugins/LLMAI/I18n?missing&lang=es</c> — diff usager vs
    ///   EN natif : ne sert que les clés ABSENTES du fichier usager pour la
    ///   langue (même forme que <c>?base</c>, sous la langue demandée —
    ///   collable direct dans son fichier). Diffe contre le RAW du fichier :
    ///   une clé présente mais fautive (invalidée au chargement) n'est PAS
    ///   « manquante » — elle est déjà signalée par le log du chargeur.
    ///   Fichier absent/illisible → tout est manquant (template complet).</item>
    /// </list>
    /// <para>Le <c>lang</c> accepte « es », « es-ES », « Español »… (même
    /// normalisation que le chargeur). Drapeaux par présence du paramètre
    /// (<c>?base</c>, <c>?missing</c> — ServiceStack lie l'absence de valeur
    /// à string vide).</para>
    /// </summary>
    public class I18nApiService : BaseApiService
    {
        // ------------------------------------------------------------------
        //  DTO requête / réponse
        // ------------------------------------------------------------------

        /// <summary>Requête GET <c>/Plugins/LLMAI/I18n</c> : sans paramètre =
        /// overlay ; <c>?base=1</c> = base EN native ; <c>?missing=1&amp;lang=…</c> =
        /// diff. Forme canonique AVEC valeur (constat déploiement 2026-10-03 :
        /// le binder Emby/ServiceStack ne lie pas un paramètre nu —
        /// <c>?base</c> sans <c>=</c> retombe sur le payload par défaut ;
        /// vérifié live). La valeur du drapeau est ignorée.</summary>
        [Route("/Plugins/LLMAI/I18n", "GET")]
        public class I18nRequest : IReturn<object>
        {
            /// <summary>Présence = servir la base EN native (drapeau).</summary>
            public string Base { get; set; }
            /// <summary>Clé de langue pour <c>?missing</c> (es, es-ES, Español…).</summary>
            public string Lang { get; set; }
            /// <summary>Présence = ne servir que les clés absentes du fichier usager.</summary>
            public string Missing { get; set; }
        }

        // ------------------------------------------------------------------
        //  Handlers
        // ------------------------------------------------------------------

        public object Get(I18nRequest request)
        {
            bool wantBase = request.Base != null;
            bool wantMissing = request.Missing != null;
            if (!wantBase && !wantMissing)
                return GetOverlayPayload();
            return GetBase(request, wantMissing);
        }

        /// <summary>Overlay : slices web par langue (force-refresh au GET ;
        /// drapeaux par présence — cf. résumé). Overlay inactif → <c>{}</c>.</summary>
        private object GetOverlayPayload()
        {
            var snap = I18nOverlay.TryRefresh();
            var resp = new Dictionary<string, object>(StringComparer.Ordinal);
            if (snap?.Web != null)
                foreach (var kv in snap.Web)
                    resp[kv.Key] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["web"] = Materialize(kv.Value)
                    };
            return resp;
        }

        /// <summary>Base EN native (+ éventuellement la diff « manquantes »).
        /// La forme de réponse est celle du fichier usager :
        /// <c>{ "&lt;lang&gt;": { "web": {…}, "server": {…}, "ext": {…} } }</c>
        /// — collable direct (la section ext, v1.16.0 P2b, couvre la famille
        /// du chat externe : chrome ext.* + messages Python srv.*).</summary>
        private object GetBase(I18nRequest request, bool missingOnly)
        {
            var enWeb = WebEnExtracted(Logger);
            var enServer = Materialize(I18n.EnServerDict);
            var enExt = Materialize(I18n.EnExtDict);

            string langKey = null;
            if (missingOnly)
            {
                var langParam = request?.Lang ?? string.Empty;
                langKey = I18nOverlay.NormalizeLang(langParam, out _);
                if (langKey == null)
                {
                    return new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["Error"] = string.Format(CultureInfo.InvariantCulture,
                            I18n.S("err.i18n.badlang", I18n.ResolveDisplayLangKey(ApplicationHost)),
                            langParam)
                    };
                }
                var userKeys = ReadUserOverlayKeys(langKey);
                int webTotal = enWeb.Count, serverTotal = enServer.Count, extTotal = enExt.Count;
                enWeb = enWeb.Where(kv => !userKeys.Web.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                enServer = enServer.Where(kv => !userKeys.Server.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                enExt = enExt.Where(kv => !userKeys.Ext.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                Logger.Info("[LLM_AI] GET /I18n ?missing « {0} » — manquantes web {1}/{2}, server {3}/{4}, ext {5}/{6}.",
                    langKey, enWeb.Count, webTotal, enServer.Count, serverTotal, enExt.Count, extTotal);
            }
            else
            {
                Logger.Info("[LLM_AI] GET /I18n ?base — web {0} clés, server {1} clés, ext {2} clés.",
                    enWeb.Count, enServer.Count, enExt.Count);
            }

            var section = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["web"] = enWeb,
                ["server"] = enServer,
                ["ext"] = enExt
            };
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [langKey ?? "en"] = section
            };
        }

        // ------------------------------------------------------------------
        //  Lecture des clés usager (diff ?missing — RAW, tolérant)
        // ------------------------------------------------------------------

        /// <summary>Clés PRÉSENTES dans le fichier usager (RAW, parsage
        /// tolérant — indépendant de la validation du chargeur : une clé
        /// présente mais fautive n'est pas « manquante », elle est déjà
        /// signalée par le log du chargeur). Fichier absent/illisible/JSON
        /// cassé → sets vides (tout manquant = template complet). Les trois
        /// sections du fichier sont lues (web/server/ext — P2b).</summary>
        private static (HashSet<string> Web, HashSet<string> Server, HashSet<string> Ext)
            ReadUserOverlayKeys(string langKey)
        {
            var web = new HashSet<string>(StringComparer.Ordinal);
            var server = new HashSet<string>(StringComparer.Ordinal);
            var ext = new HashSet<string>(StringComparer.Ordinal);
            string raw = null;
            try
            {
                var path = I18nOverlay.OverlayPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    raw = File.ReadAllText(path);
            }
            catch { raw = null; }
            if (raw == null) return (web, server, ext);

            try
            {
                using (var doc = JsonDocument.Parse(raw))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        return (web, server, ext);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (!string.Equals(I18nOverlay.NormalizeLang(prop.Name, out _), langKey,
                                StringComparison.Ordinal)) continue;
                        if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                        if (prop.Value.TryGetProperty("web", out var w)
                            && w.ValueKind == JsonValueKind.Object)
                            foreach (var e in w.EnumerateObject()) web.Add(e.Name);
                        if (prop.Value.TryGetProperty("server", out var sv)
                            && sv.ValueKind == JsonValueKind.Object)
                            foreach (var e in sv.EnumerateObject()) server.Add(e.Name);
                        if (prop.Value.TryGetProperty("ext", out var ev3)
                            && ev3.ValueKind == JsonValueKind.Object)
                            foreach (var e in ev3.EnumerateObject()) ext.Add(e.Name);
                    }
                }
            }
            catch { /* JSON cassé → sets vides (tout manquant) */ }
            return (web, server, ext);
        }

        // ------------------------------------------------------------------
        //  Extraction web EN du i18n.js embarqué (base « ?base »)
        // ------------------------------------------------------------------

        /// <summary>Nom de ressource embarquée du module i18n js.</summary>
        private const string I18nJsResource = "LLM_AI.i18n.js";

        /// <summary>Compte attendu des clés web EN (kit 2026-10-02 : 393 ;
        /// T1a 2026-10-03 : +20 clés du panneau « Langues » = 413) —
        /// la ligne de log d'extraction le rapporte pour l'œil du
        /// mainteneur ; un écart signalé = i18n.js a dérivé du kit.</summary>
        private const int ExpectedWebKeys = 413;

        private static readonly object s_webEnLock = new object();
        private static Dictionary<string, string> s_webEn;       //null = jamais extrait
        private static bool s_webEnFailed;                        // log d'échec déjà émis

        /// <summary>STRINGS.en extrait de l'embarqué — cache statique (la
        /// ressource est immuable) ; échec d'extraction → dictionnaire vide +
        /// un seul log Warn (fail-open : ?base reste servie sans la moitié
        /// web plutôt que 500).</summary>
        private static Dictionary<string, string> WebEnExtracted(ILogger logger)
        {
            if (s_webEn != null) return s_webEn;
            lock (s_webEnLock)
            {
                if (s_webEn != null) return s_webEn;
                Dictionary<string, string> extracted = null;
                try
                {
                    var asm = typeof(I18nApiService).Assembly;
                    using (var stream = asm.GetManifestResourceStream(I18nJsResource))
                    {
                        if (stream != null)
                        {
                            using (var reader = new StreamReader(stream))
                                extracted = ExtractWebStringsEn(reader.ReadToEnd());
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (logger != null && !s_webEnFailed)
                        logger.Warn("[LLM_AI] I18n ?base : extraction web EN impossible ({0}) — base servie SANS la moitié web.",
                            ex.Message);
                }
                if (extracted == null)
                {
                    if (logger != null && !s_webEnFailed)
                        logger.Warn("[LLM_AI] I18n ?base : ancre « var STRINGS = { » / sous-objet « en » introuvable dans {0} — base servie SANS la moitié web.",
                            I18nJsResource);
                    s_webEnFailed = true;
                    s_webEn = new Dictionary<string, string>(StringComparer.Ordinal);
                    return s_webEn;
                }
                if (logger != null)
                    logger.Info("[LLM_AI] I18n ?base : STRINGS.en extrait du {0} embarqué — {1} clés (attendu ~{2}{3}).",
                        I18nJsResource, extracted.Count, ExpectedWebKeys,
                        extracted.Count == ExpectedWebKeys ? "" : " — ÉCART : i18n.js a dérivé du kit");
                s_webEn = extracted;
                return s_webEn;
            }
        }

        // FR web : cache miroir (même ressource immuable, sous-objet « fr »).

        private static readonly object s_webFrLock = new object();
        private static Dictionary<string, string> s_webFr;       //null = jamais extrait
        private static bool s_webFrFailed;                        // log d'échec déjà émis

        /// <summary>STRINGS.fr extrait de l'embarqué — cache statique miroir
        /// de WebEnExtracted (v1.17.0 T1b : la moitié FR des paires de contexte
        /// des doses). Pas de compte attendu (le FR suit le EN par convention
        /// de maintenance ; une dérive se verrait dans les comptes du ?base).</summary>
        private static Dictionary<string, string> WebFrExtracted(ILogger logger)
        {
            if (s_webFr != null) return s_webFr;
            lock (s_webFrLock)
            {
                if (s_webFr != null) return s_webFr;
                Dictionary<string, string> extracted = null;
                try
                {
                    var asm = typeof(I18nApiService).Assembly;
                    using (var stream = asm.GetManifestResourceStream(I18nJsResource))
                    {
                        if (stream != null)
                        {
                            using (var reader = new StreamReader(stream))
                                extracted = ExtractWebStringsLang(reader.ReadToEnd(), "fr");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (logger != null && !s_webFrFailed)
                        logger.Warn("[LLM_AI] I18n : extraction web FR impossible ({0}) — doses servies sans paires FR pour la partie web.", ex.Message);
                }
                if (extracted == null)
                {
                    if (logger != null && !s_webFrFailed)
                        logger.Warn("[LLM_AI] I18n : ancre « var STRINGS = { » / sous-objet « fr » introuvable dans {0} — doses sans paires FR web.", I18nJsResource);
                    s_webFrFailed = true;
                    s_webFr = new Dictionary<string, string>(StringComparer.Ordinal);
                    return s_webFr;
                }
                if (logger != null)
                    logger.Info("[LLM_AI] I18n : STRINGS.fr extrait du {0} embarqué — {1} clés.", I18nJsResource, extracted.Count);
                s_webFr = extracted;
                return s_webFr;
            }
        }

        /// <summary>Table web native pour le moteur de génération (T1b) : « en »
        /// = base formelle, « fr » = contexte auteur. Toute autre langue → EN
        /// (pas de natif web au-delà des deux, par convention de maintenance).</summary>
        internal static IReadOnlyDictionary<string, string> WebNativesLang(string lang, ILogger logger)
            => lang == "fr" ? WebFrExtracted(logger) : WebEnExtracted(logger);

        /// <summary>Extraction bornée de <c>STRINGS.en</c> depuis le source du
        /// module AMD i18n.js (spécialisation EN de
        /// <see cref="ExtractWebStringsLang"/> — garde son nom pour la symétrie
        /// du harnais réflexion avec le kit).</summary>
        internal static Dictionary<string, string> ExtractWebStringsEn(string js)
            => ExtractWebStringsLang(js, "en");

        /// <summary>Extraction bornée de <c>STRINGS.<paramref name="langHead"/></c>
        /// depuis le source du module AMD i18n.js — SANS eval : ancre
        /// <c>var STRINGS = {</c>, accolades équilibrées (chaînes + commentaires
        /// // et /* */ gérés), sous-objet <c>lang:</c>, paires "clé" : "valeur".
        /// Null si l'ancre ou le sous-objet ne se trouvent pas (fail-open côté
        /// appelant). v1.17.0 T1b : généralisée EN+FR — le FR web vit dans
        /// i18n.js exactement comme l'EN et sert la moitié FR des paires de
        /// contexte des doses. Pur et statique : rejouable au harnais réflexion
        /// (symétrie des comptes avec le kit).</summary>
        internal static Dictionary<string, string> ExtractWebStringsLang(string js, string langHead)
        {
            if (string.IsNullOrEmpty(js)) return null;
            int anchor = js.IndexOf("var STRINGS = {", StringComparison.Ordinal);
            if (anchor < 0) return null;
            int objStart = js.IndexOf('{', anchor);
            if (objStart < 0) return null;
            int objEnd = ScanJsObjectEnd(js, objStart);
            if (objEnd < 0) return null;

            // Le regex ne matche QUE la tête demandée (l'ancre « en » n'attrape
            // pas « fr » et réciproquement — i18n.js a les deux sous-objets).
            foreach (Match m in SubobjectRx(langHead).Matches(js.Substring(objStart, objEnd - objStart)))
            {
                int subStart = objStart + m.Index + m.Length - 1; // le '{' du match (index relatif → absolu)
                int subEnd = ScanJsObjectEnd(js, subStart);
                if (subEnd < 0) continue;
                var span = js.Substring(subStart + 1, subEnd - subStart - 2);
                var result = ParseJsStringPairs(span);
                if (result.Count > 0) return result;
            }
            return null;
        }

        /// <summary>Ancre « <c>lang:</c> <c>{</c> » au niveau zéro d'un objet JS
        /// (head = en OU fr). Ancré en début de ligne : une valeur de chaîne
        /// contenant « en: { » ne matche pas (i18n.js n'a pas de saut de
        /// ligne réel dans ses littéraux).</summary>
        private static readonly Dictionary<string, Regex> s_subobjectRx =
            new Dictionary<string, Regex>(StringComparer.Ordinal);

        private static Regex SubobjectRx(string langHead)
        {
            lock (s_subobjectRx)
            {
                if (!s_subobjectRx.TryGetValue(langHead, out var rx))
                {
                    rx = new Regex("^[\t ]*" + Regex.Escape(langHead) + @"\s*:\s*\{",
                        RegexOptions.Compiled | RegexOptions.Multiline);
                    s_subobjectRx[langHead] = rx;
                }
                return rx;
            }
        }

        /// <summary>Paire "clé" : "valeur" JS (échappements supportés).</summary>
        private static readonly Regex JsPairRx =
            new Regex("\"((?:[^\"\\\\]|\\\\.)+)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
                RegexOptions.Compiled);

        /// <summary>Parse les paires "clé" : "valeur" d'un span d'objet JS.
        /// Clé dupliquée → la dernière gagne (le kit throw, ici log-agnostiques :
        /// i18n.js est sous notre contrôle source; le compte de la ligne de log
        /// révèle toute dérive).</summary>
        private static Dictionary<string, string> ParseJsStringPairs(string span)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in JsPairRx.Matches(span))
                result[UnescapeJsLiteral(m.Groups[1].Value)] = UnescapeJsLiteral(m.Groups[2].Value);
            return result;
        }

        /// <summary>Index APRÈS l'accolade fermante qui ferme l'objet ouvert à
        /// <paramref name="openBraceIndex"/> — chaînes " ' ` (avec \), commentaires
        /// // et /* */ gérés. -1 si déséquilibre (fail-open).</summary>
        private static int ScanJsObjectEnd(string js, int openBraceIndex)
        {
            int depth = 0;
            char inString = '\0';
            bool escaped = false, lineComment = false, blockComment = false;
            for (int i = openBraceIndex; i < js.Length; i++)
            {
                char c = js[i];
                if (lineComment) { if (c == '\n') lineComment = false; continue; }
                if (blockComment)
                {
                    if (c == '*' && i + 1 < js.Length && js[i + 1] == '/') { blockComment = false; i++; }
                    continue;
                }
                if (inString != '\0')
                {
                    if (escaped) { escaped = false; continue; }
                    if (c == '\\') { escaped = true; continue; }
                    if (c == inString) inString = '\0';
                    continue;
                }
                if (c == '/' && i + 1 < js.Length)
                {
                    if (js[i + 1] == '/') { lineComment = true; i++; continue; }
                    if (js[i + 1] == '*') { blockComment = true; i++; continue; }
                }
                if (c == '"' || c == '\'' || c == '`') { inString = c; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i + 1;
                }
            }
            return -1;
        }

        private static readonly Regex JsUnicodeEscapeRx =
            new Regex(@"\\u([0-9a-fA-F]{4})", RegexOptions.Compiled);

        /// <summary>Déséchapement d'un littéral JS (miroir de l'unesc du kit +
        /// \uXXXX). En l'état du source : uniquement \" et \n réellement
        /// utilisés — le reste est une robustesse gratuite.</summary>
        internal static string UnescapeJsLiteral(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case '"': case '\\': case '\'': case '`': case '/':
                        sb.Append(n); break;
                    case 'u':
                        var u = JsUnicodeEscapeRx.Match(s.Substring(i - 1));
                        if (u != null && u.Length > 0)
                        {
                            sb.Append((char)Convert.ToInt32(u.Groups[1].Value, 16));
                            i += 4;
                        }
                        else sb.Append('u');
                        break;
                    default: sb.Append(n); break;
                }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  Helpers
        // ------------------------------------------------------------------

        /// <summary>Copie matérielle ServiceStack-safe (les snapshots sont
        /// IReadOnlyDictionary — le sérialiseur de l'hôte préfère les dictionnaires
        /// banaux) ; null → vide.</summary>
        private static Dictionary<string, string> Materialize(
            IReadOnlyDictionary<string, string> src)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (src != null)
                foreach (var kv in src) d[kv.Key] = kv.Value;
            return d;
        }
    }
}