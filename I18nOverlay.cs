using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Chargeur du fichier optionnel de <b>traductions communautaires</b>
    /// <c>LLM_AI_i18n.json</c> (v1.16.0, plan P1 — spécifié 2026-10-02/03,
    /// voir TODO.md). Ajouter une langue d'interface = déposer ce fichier à
    /// côté de <c>LLM_AI.xml</c> (PluginConfigurationsPath) — zéro code, zéro
    /// recompile, pas de fork pour le traducteur. Format :
    /// <c>{ "es": { "web": {…}, "server": {…}, "ext": {…} }, "fr": {…} }</c> —
    /// une clé de langue par section de tête ; les sections <c>server</c>
    /// (fusionnée au lookup par <see cref="I18n"/>) et <c>web</c> (servie aux
    /// pages web par l'endpoint /I18n, v1.16.0) sont attendues par convention
    /// (les namespaces se chevauchent : « chat.* » existe des deux côtés)
    /// mais chacune est consommée indépendamment — une section manquante
    /// signifie « pas d'overlay pour ce consommateur », pas une erreur ; la
    /// section <c>ext</c> (v1.16.0 P2b — chat externe, chrome ext.* +
    /// messages Python srv.*) est OPTIONNELLE, servie par /I18nExt à la
    /// langue résolue.
    /// <list type="bullet">
    /// <item><b>Fail-open total</b> : sans fichier, ou fichier illisible /
    /// JSON cassé → dictionnaires natifs FR+EN seuls (comportement
    /// inchangé) ; un JSON cassé conserve le snapshot précédent (retour à
    /// la validité au prochain changement de mtime).</item>
    /// <item><b>Re-scan piggyback throttlé</b> : le stat (taille + mtime)
    /// vit dans <see cref="EnsureFresh"/>, appelé au moment des lookups
    /// via <see cref="I18n.S"/> — aucun thread, aucun FileSystemWatcher ;
    /// au plus UN stat par 30 s sur tout le process (serveur idle = zéro
    /// accès disque). Édition live du fichier → effectif au prochain
    /// lookup (≤ 30 s), sans restart Emby.</item>
    /// <item><b>Snapshot immuable</b> : construit entier puis échangé par
    /// référence (volatile) — un lookup concurrent ne voit jamais un état
    /// à moitié rempli.</item>
    /// <item><b>Validation par clé</b> (port C# des règles 4-5 du kit
    /// <c>validate_i18n.js</c>, regex en miroir exact) : multiset des
    /// placeholders <c>{n}</c> et multiset des balises HTML comparés à la
    /// base EN native (s_res["en"]). Clé invalide → sautée + ligne de log —
    /// l'UI reste dans la langue pour le reste (repli EN par clé, état de
    /// release légitime : Emby livre pire). Clé inconnue du dictionnaire
    /// serveur (souvent un glissement web→server) → sautée + log. La
    /// section <c>web</c> n'est PAS validée ici : elle est validée côté
    /// client (JS, au merge — drop + console.warn) car le dictionnaire web
    /// EN natif vit dans i18n.js, pas dans la DLL. La section <c>ext</c>
    /// (P2b) est validée ici, règle {n}-SEULE contre s_ext["en"] — famille
    /// texte brut (textContent / str.format), pas de règle balises.</item>
    /// <item><b>Patch fr/en supporté</b> (feature documentée) : l'overlay
    /// est autoritaire par langue — une section "fr" ou "en" remplace la
    /// valeur native pour les clés qu'elle porte, et le repli EN par défaut
    /// consulte l'overlay "en" AVANT le natif (voir <c>I18n.S</c>). Sans le
    /// fichier, tout est identique (invariant).</item>
    /// </list>
    /// <para><b>Logging</b> : le loader vit sous les appels <c>I18n.S</c>
    /// (100+ call-sites — signature inchangée, aucun logger à disposition
    /// à l'appel) ; le logger est capturé paresseusement via
    /// <see cref="SetLogger"/> (appelé au constructeur de LlmRunner, même
    /// pattern que SecurityMonitor — idempotent, premier non null gagne).
    /// Les lignes émises avant l'enregistrement sont mises en tampon borné
    /// et vidées à l'enregistrement ; la ligne résumé du dernier chargement
    /// reste disponible (<see cref="LastReport"/>) pour l'endpoint de
    /// diagnostic v1.16.0.</para>
    /// </summary>
    /// <remarks>
    /// Fichier hors repo (gitignored, contenu usager) ; la DLL reste
    /// auto-suffisante : sans le fichier, aucun chemin de ce code ne
    /// s'active. Modèle de confiance : qui écrit dans le dossier de
    /// données Emby a déjà le niveau admin — les chaînes du fichier portent
    /// du HTML comme les natives (inhérent au design, pas une surface
    /// nouvelle).
    /// </remarks>
    internal static class I18nOverlay
    {
        /// <summary>Nom du fichier overlay, à côté de LLM_AI.xml.</summary>
        internal const string FileName = "LLM_AI_i18n.json";

        /// <summary>Intervalle min entre deux stats du fichier (piggyback au
        /// premier lookup qui le déclenche). Throttle GLOBAL au process.</summary>
        private const int RescanIntervalMs = 30_000;

        /// <summary>Borne du tampon de logs émis avant l'enregistrement d'un
        /// logger (les plus anciens perdus ; le résumé du dernier chargement
        /// reste dans <see cref="LastReport"/>).</summary>
        private const int MaxPendingLogs = 40;

        /// <summary>Sentinel de stamp : garantit que le premier probe
        /// procède, même si le fichier est absent depuis le démarrage.</summary>
        private const string NeverProbed = "\x2never-probed";

        private static readonly object s_loadLock = new object();
        private static readonly object s_logLock = new object();
        private static readonly List<KeyValuePair<string, bool>> s_pendingLogs
            = new List<KeyValuePair<string, bool>>();   // ligne + niveau (true = warn)

        // Snapshots échangés par référence — jamais mutés après la pose.
        private static volatile Snapshot s_snapshot;
        private static string s_stamp = NeverProbed;    // forme "len|mtime" ; null = fichier absent
        private static int s_lastProbeTick;             // throttle (Environment.TickCount, wrap-safe)
        private static string s_lastReport;             // résumé du dernier chargement (ou dernière erreur)
        private static ILogger s_logger;                // paresseux (SetLogger)

        /// <summary>Hook de harnais/tests : chemin imposé (jamais posé en
        /// prod — la résolution passe par Plugin.Paths).</summary>
        internal static string TestPathOverride { get; set; }

        /// <summary>Regex des placeholders {n} — MIROIR EXACT du kit
        /// (validate_i18n.js), triés puis joints pour former un multiset
        /// comparable.</summary>
        private static readonly Regex PlaceholderRx =
            new Regex(@"\{\d+\}", RegexOptions.Compiled);

        /// <summary>Regex des balises HTML — miroir exact du kit
        /// (/&lt;/?[a-zA-Z][^&gt;]*&gt;/g).</summary>
        private static readonly Regex HtmlTagRx =
            new Regex(@"</?[a-zA-Z][^>]*>", RegexOptions.Compiled);

        // ------------------------------------------------------------------
        //  Snapshot
        // ------------------------------------------------------------------

        /// <summary>Photo immuable de l'overlay parsé + validé. Les deux
        /// sections sont indexées langue → clé → valeur ; les dictionnaires
        /// ne sont jamais mutés après la pose (échange par référence).</summary>
        internal sealed class Snapshot
        {
            /// <summary>Section "server" : consultée par I18n.S au rendu
            /// (étage 1, devant le natif).</summary>
            internal readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Server;

            /// <summary>Section "web" : servie aux pages par l'endpoint
            /// /I18n (v1.16.0) — PAS validée ici (règle client au merge).</summary>
            internal readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Web;

            /// <summary>Section "ext" (v1.16.0 P2b) : chaînes du chat externe
            /// (chrome ext.* + messages Python srv.*) — servies par l'endpoint
            /// /I18nExt à la langue résolue (cascade UICulture). Validée ICI,
            /// règle {n}-seule (famille texte brut : pas de règle balises —
            /// rendu textContent / str.format).</summary>
            internal readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Ext;

            /// <summary>Instant du chargement (diagnostic).</summary>
            internal readonly DateTimeOffset LoadedAtUtc;

            internal Snapshot(
                IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> server,
                IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> web,
                IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ext,
                DateTimeOffset loadedAtUtc, string report)
            {
                Server = server;
                Web = web;
                Ext = ext;
                LoadedAtUtc = loadedAtUtc;
                Report = report;
            }

            /// <summary>Résumé du chargement (ligne [LLM_AI]).</summary>
            internal readonly string Report;

            /// <summary>Nombre de clés "server" actives pour une langue (0 si absente).</summary>
            internal int ServerKeys(string langKey)
                => Server != null && Server.TryGetValue(langKey ?? "", out var d) ? d.Count : 0;
        }

        /// <summary>Snapshot courant (null = pas d'overlay actif).</summary>
        internal static Snapshot Current => s_snapshot;

        /// <summary>Résumé du dernier chargement — inclut les erreurs
        /// structurelles (null si jamais tenté). Pour l'endpoint de
        /// diagnostic v1.16.0.</summary>
        internal static string LastReport => s_lastReport;

        // ------------------------------------------------------------------
        //  Logger paresseux (pattern SecurityMonitor — idempotent + tampon)
        // ------------------------------------------------------------------

        /// <summary>
        /// Installe le logger des lignes <c>[LLM_AI] I18n overlay</c>.
        /// Appelé au constructeur de <see cref="LlmRunner"/> (idempotent :
        /// premier non null gagne). Les lignes émises avant l'installation
        /// sont mises en tampon et vidées ici.
        /// </summary>
        internal static void SetLogger(ILogger logger)
        {
            if (logger == null) return;
            List<KeyValuePair<string, bool>> flush;
            lock (s_logLock)
            {
                if (s_logger != null) return;
                s_logger = logger;
                flush = s_pendingLogs.ToList();
                s_pendingLogs.Clear();
            }
            foreach (var kv in flush) Emit(logger, kv.Key, kv.Value);
        }

        private static void Emit(ILogger logger, string line, bool warn)
        {
            if (logger == null) return;
            try
            {
                if (warn) logger.Warn("[LLM_AI] {0}", line);
                else logger.Info("[LLM_AI] {0}", line);
            }
            catch { /* le loader ne lève jamais — le log est best-effort */ }
        }

        /// <summary>Log avec tampon si le logger n'est pas encore connu.</summary>
        private static void QueueLog(string line, bool warn)
        {
            ILogger logger;
            lock (s_logLock)
            {
                logger = s_logger;
                if (logger == null)
                {
                    s_pendingLogs.Add(new KeyValuePair<string, bool>(line, warn));
                    if (s_pendingLogs.Count > MaxPendingLogs)
                        s_pendingLogs.RemoveAt(0);
                    return;
                }
            }
            Emit(logger, line, warn);
        }

        // ------------------------------------------------------------------
        //  Chemin
        // ------------------------------------------------------------------

        /// <summary>Chemin de l'overlay : PluginConfigurationsPath (à côté
        /// de LLM_AI.xml), ou le hook de harnais. Null si l'hôte n'est pas
        /// prêt (fail-open : l'overlay est simplement inactif).</summary>
        /// <remarks>Le hook harnais est testé AVANT tout accès à
        /// <see cref="Plugin"/> — l'accès hôte vit dans <see cref="HostPath"/>
        /// (méthode séparée : elle n'est JITée qu'on l'appelle — le harnais
        /// réflexion, sans hôte Emby, ne la touchera jamais).</remarks>
        internal static string OverlayPath
        {
            get
            {
                if (TestPathOverride != null) return TestPathOverride;
                return HostPath();
            }
        }

        /// <summary>Chemin hôte : Plugin.Paths (IApplicationPaths résolu au
        /// constructeur du plugin) — jamais touché quand le hook harnais est
        /// posé (cf. remarque <see cref="OverlayPath"/>).</summary>
        private static string HostPath()
        {
            try
            {
                var dir = Plugin.Paths?.PluginConfigurationsPath;
                if (string.IsNullOrEmpty(dir)) return null;
                return Path.Combine(dir, FileName);
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------
        //  Lookup (étage 1 de I18n.S) + re-scan throttlé
        // ------------------------------------------------------------------

        /// <summary>
        /// Cherche <paramref name="key"/> dans la section "server" de
        /// <paramref name="langKey"/>, overlay actif. Déclenche le stat
        /// throttlé (30 s) — ne lève jamais (false = repli natif ininterrompu).
        /// </summary>
        internal static bool TryLookup(string langKey, string key, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(langKey)) return false;
            try { EnsureFresh(); } catch { return false; }
            var snap = s_snapshot;
            if (snap == null) return false;
            return snap.Server.TryGetValue(langKey, out var dict)
                && dict.TryGetValue(key, out value);
        }

        /// <summary>
        /// Re-scan piggyback : au plus un stat de fichier par
        /// <see cref="RescanIntervalMs"/> sur tout le process ; chargement
        /// seulement si le stamp (taille + mtime UTC) a changé. Aucun
        /// thread — le stat vit dans le lookup appelant (serveur idle =
        /// zéro accès disque). Ne lève jamais.
        /// </summary>
        private static void EnsureFresh()
        {
            // Wrap-safe : après le wrap de TickCount (~24,9 j), now-last
            // devient négatif → un probe de trop, bénin.
            int now = Environment.TickCount;
            int last = Volatile.Read(ref s_lastProbeTick);
            int delta = unchecked(now - last);
            if (delta >= 0 && delta < RescanIntervalMs) return;
            // CAS : un seul probe gagne — les autres retournent au lookup
            // direct (le gagnant échange le snapshot le cas échéant).
            if (Interlocked.CompareExchange(ref s_lastProbeTick, now, last) != last) return;

            string path = OverlayPath;
            string stamp;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) stamp = null;
            else
            {
                var fi = new FileInfo(path);
                stamp = fi.Length.ToString(CultureInfo.InvariantCulture)
                        + "|" + fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            }

            string cur;
            lock (s_loadLock) { cur = s_stamp; }
            if (stamp == cur) return;                          // inchangé (ou déjà traité)

            lock (s_loadLock)
            {
                if (s_stamp == stamp) return;                  // double-check (concurrent gagnant)
                Install(path, stamp);
            }
        }

        /// <summary>Force le re-scan sans attendre le throttle (les
        /// endpoints qui SERVENT l'overlay l'appellent au GET — frais au
        /// GET, zéro coût pour S()). Retourne le snapshot résultant.</summary>
        internal static Snapshot TryRefresh()
        {
            Interlocked.Exchange(ref s_lastProbeTick, 0);
            lock (s_loadLock)
            {
                EnsureFresh();
                return s_snapshot;
            }
        }

        /// <summary>Pose le snapshot pour le stamp courant de
        /// <paramref name="path"/>. Sémantiques d'échec : JSON cassé /
        /// structure incorrecte → snapshot précédent CONSERVÉ + stamp posé
        /// (pas de re-parse à chaque 30 s — le prochain changement de fichier
        /// re-tente) ; erreur IO transitoire (écriture concurrente) → stamp
        /// ANTÉRIEUR conservé (re-lecté au prochain probe). Fichier absent →
        /// overlay désactivé proprement (sans fichier = comportement inchangé).</summary>
        private static void Install(string path, string stamp)
        {
            Snapshot prior = s_snapshot;
            bool wasPresent = prior != null;
            if (stamp == null)
            {
                s_snapshot = null;
                s_stamp = null;
                if (wasPresent)
                    QueueLog("I18n overlay : fichier retiré — overlay désactivé, "
                        + "dictionnaires natifs seuls.", false);
                return;
            }

            try
            {
                var res = LoadFrom(path);
                foreach (var l in res.LogLines) QueueLog(l, false);
                foreach (var l in res.FailedKeys) QueueLog(l, true);
                if (res.ParseError != null)
                {
                    // JSON cassé / structure : snapshot précédent conservé +
                    // stamp POSÉ — l'échec est déterministe, pas de re-parse
                    // toutes les 30 s ; le prochain changement de fichier
                    // (mtime) re-tente naturellement.
                    s_stamp = stamp;
                    s_lastReport = res.ParseError;
                    QueueLog("I18n overlay : " + res.ParseError
                        + " — snapshot précédent conservé ("
                        + (wasPresent ? "actif" : "aucun")
                        + "), re-tenté au prochain changement de fichier.", true);
                    return;
                }
                s_snapshot = res.Snap;
                s_stamp = stamp;
                s_lastReport = res.Snap.Report;
                QueueLog("I18n overlay : " + res.Snap.Report, false);
            }
            catch (Exception ex)
            {
                // IO transitoire (écriture concurrente, permissions…) : stamp
                // ANTÉRIEUR conservé → re-lecture au prochain probe (30 s).
                // Catch-all : ce loader vit sous I18n.S — rien ne doit sortir
                // (TryLookup re-capture de toute façon, fail-open final).
                QueueLog("I18n overlay : lecture impossible (" + ex.GetType().Name + ": "
                    + ex.Message + ") — re-tenté au prochain probe.", true);
            }
        }

        // ------------------------------------------------------------------
        //  Parsage + validation (pur — rejouable au harnais)
        // ------------------------------------------------------------------

        /// <summary>Résultat unitaire de <see cref="LoadFrom"/> (tout dans
        /// une struct légère : le purisme out-x4 de la version harnais a été
        /// raboté — un objet nommé, rejouable tel quel).</summary>
        internal sealed class LoadResult
        {
            internal Snapshot Snap;               // null si ParseError
            internal List<string> LogLines;       // événements Info
            internal List<string> FailedKeys;     // clés sautées (Warn)
            internal string ParseError;           // structure/JSON (snapshot précédent conservé)
        }

        /// <summary>Charge le fichier et construit le snapshot : section
        /// "server" validée par clé contre la base EN native (règles 4-5 du
        /// kit : multiset placeholders + multiset balises HTML), section
        /// "web" acceptée sans validation (règle client au merge), section
        /// "ext" validée {n}-seule contre s_ext["en"] (famille texte brut :
        /// pas de règle balises, v1.16.0 P2b). Les clés fautives sont
        /// sautées (jamais d'échec global pour une clé) ; un fichier
        /// structurellement cassé (JSON, racine non-objet, aucune langue
        /// exploitable) → ParseError posé.</summary>
        internal static LoadResult LoadFrom(string path)
        {
            var res = new LoadResult
            {
                LogLines = new List<string>(),
                FailedKeys = new List<string>()
            };

            string raw = File.ReadAllText(path);           // IO remonté à l'appelant (transitoire)

            JsonDocument doc;
            try { doc = JsonDocument.Parse(raw); }
            catch (JsonException ex)
            {
                res.ParseError = "JSON cassé : " + ex.Message;
                return res;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    res.ParseError = "racine non-objet (attendu { \"<lang>\": { \"web\": {…}, \"server\": {…}, \"ext\": {…}? } })";
                    return res;
                }

                int expectedServer = I18n.EnServerKeyCount;
                int expectedExt = I18n.EnExtKeyCount;

                var server = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                var web = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                var ext = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                var summary = new List<string>();

                foreach (var langProp in doc.RootElement.EnumerateObject())
                {
                    string langKey = NormalizeLang(langProp.Name, out string normNote);
                    if (langKey == null)
                    {
                        res.LogLines.Add("clé de tête « " + langProp.Name
                            + " » sautée (" + (normNote ?? "langue non reconnue") + ").");
                        continue;
                    }
                    if (langProp.Value.ValueKind != JsonValueKind.Object)
                    {
                        res.FailedKeys.Add("section de « " + langProp.Name
                            + " » non-objet — clé de langue sautée.");
                        continue;
                    }

                    // Sections web/server : chacune optionnelle, consommée
                    // indépendamment (convention : le fichier complet porte
                    // les deux ; les namespaces se chevauchent).
                    int sOk, sBad;
                    if (langProp.Value.TryGetProperty("server", out var sv)
                        && sv.ValueKind == JsonValueKind.Object)
                    {
                        sOk = 0; sBad = 0;
                        var dict = ServerGetOrAdd(server, langKey);
                        foreach (var entry in sv.EnumerateObject())
                        {
                            if (entry.Value.ValueKind != JsonValueKind.String)
                            {
                                sBad++;
                                res.FailedKeys.Add("[server." + langKey + "] " + entry.Name
                                    + " — valeur non-chaîne, clé sautée.");
                                continue;
                            }
                            var val = entry.Value.GetString();
                            if (string.IsNullOrWhiteSpace(val))
                            {
                                sBad++;
                                res.FailedKeys.Add("[server." + langKey + "] " + entry.Name
                                    + " — valeur vide, clé sautée.");
                                continue;
                            }
                            if (!I18n.TryEnServerString(entry.Name, out string enVal))
                            {
                                sBad++;
                                res.FailedKeys.Add("[server." + langKey + "] " + entry.Name
                                    + " — clé inconnue du dictionnaire serveur (glissement web→server ?), clé sautée.");
                                continue;
                            }
                            string phEn = PlaceholderSig(enVal), phV = PlaceholderSig(val);
                            if (phEn != phV)
                            {
                                sBad++;
                                res.FailedKeys.Add("[server." + langKey + "] " + entry.Name
                                    + " — placeholders divergents (EN " + SigOrNone(phEn)
                                    + " vs soumis " + SigOrNone(phV) + "), clé sautée.");
                                continue;
                            }
                            string tgEn = HtmlTagSig(enVal), tgV = HtmlTagSig(val);
                            if (tgEn != tgV)
                            {
                                sBad++;
                                res.FailedKeys.Add("[server." + langKey + "] " + entry.Name
                                    + " — balises HTML divergentes (EN " + SigOrNone(tgEn)
                                    + " vs soumis " + SigOrNone(tgV) + "), clé sautée.");
                                continue;
                            }
                            dict[entry.Name] = val;
                            sOk++;
                        }
                        if (sOk == 0) server.Remove(langKey);
                    }
                    else sOk = sBad = -1;                  // section absente

                    int wOk, wBad;
                    if (langProp.Value.TryGetProperty("web", out var wv)
                        && wv.ValueKind == JsonValueKind.Object)
                    {
                        wOk = 0; wBad = 0;
                        var dict = ServerGetOrAdd(web, langKey);
                        foreach (var entry in wv.EnumerateObject())
                        {
                            // PAS de validation ici (référence EN web = i18n.js,
                            // règle client au merge) — uniquement la forme.
                            if (entry.Value.ValueKind != JsonValueKind.String)
                            {
                                wBad++;
                                res.FailedKeys.Add("[web." + langKey + "] " + entry.Name
                                    + " — valeur non-chaîne, clé sautée.");
                                continue;
                            }
                            var val = entry.Value.GetString();
                            if (string.IsNullOrWhiteSpace(val))
                            {
                                wBad++;
                                res.FailedKeys.Add("[web." + langKey + "] " + entry.Name
                                    + " — valeur vide, clé sautée.");
                                continue;
                            }
                            dict[entry.Name] = val;
                            wOk++;
                        }
                        if (wOk == 0) web.Remove(langKey);
                    }
                    else wOk = wBad = -1;                  // section absente

                    // Section "ext" (v1.16.0 P2b) : chat externe — validation
                    // {n}-SEULE contre s_ext["en"] (famille texte brut : le
                    // rendu est textContent côté page et str.format côté
                    // Python — pas de balises, donc pas de règle balises).
                    // Section optionnelle et additive (web/server obligatoires
                    // par convention, ext nouvelle).
                    int eOk, eBad;
                    if (langProp.Value.TryGetProperty("ext", out var ev2)
                        && ev2.ValueKind == JsonValueKind.Object)
                    {
                        eOk = 0; eBad = 0;
                        var dict = ServerGetOrAdd(ext, langKey);
                        foreach (var entry in ev2.EnumerateObject())
                        {
                            if (entry.Value.ValueKind != JsonValueKind.String)
                            {
                                eBad++;
                                res.FailedKeys.Add("[ext." + langKey + "] " + entry.Name
                                    + " — valeur non-chaîne, clé sautée.");
                                continue;
                            }
                            var val = entry.Value.GetString();
                            if (string.IsNullOrWhiteSpace(val))
                            {
                                eBad++;
                                res.FailedKeys.Add("[ext." + langKey + "] " + entry.Name
                                    + " — valeur vide, clé sautée.");
                                continue;
                            }
                            if (!I18n.TryEnExtString(entry.Name, out string enVal))
                            {
                                eBad++;
                                res.FailedKeys.Add("[ext." + langKey + "] " + entry.Name
                                    + " — clé inconnue du dictionnaire ext (glissement web/server→ext ?), clé sautée.");
                                continue;
                            }
                            string phEn = PlaceholderSig(enVal), phV = PlaceholderSig(val);
                            if (phEn != phV)
                            {
                                eBad++;
                                res.FailedKeys.Add("[ext." + langKey + "] " + entry.Name
                                    + " — placeholders divergents (EN " + SigOrNone(phEn)
                                    + " vs soumis " + SigOrNone(phV) + "), clé sautée.");
                                continue;
                            }
                            dict[entry.Name] = val;
                            eOk++;
                        }
                        if (eOk == 0) ext.Remove(langKey);
                    }
                    else eOk = eBad = -1;                  // section absente

                    if (sOk == -1 && wOk == -1 && eOk == -1)
                    {
                        res.LogLines.Add("langue « " + langKey + " » sans aucune section — ignorée.");
                        continue;
                    }
                    if (sOk == 0 && wOk == 0 && eOk == 0)
                    {
                        res.LogLines.Add("langue « " + langKey + " » sans clés exploitables — ignorée.");
                        continue;
                    }
                    summary.Add(langKey
                        + ": server " + SectionCount(sOk) + "/" + expectedServer
                        + " (sautées " + SectionCount(sBad) + ") ; web "
                        + SectionCount(wOk) + " (sautées " + SectionCount(wBad) + ")"
                        + (eOk == -1 ? "" : " ; ext " + eOk + "/" + expectedExt
                            + " (sautées " + SectionCount(eBad) + ")"));

                    // Signal si la même langue réapparaît (ex. "es-ES" puis
                    // "es" normalisés ensemble) : fusion en cours, dernier
                    // gagne par clé — bénin, mais lisible dans le log.
                    if ((sOk > 0 && server.ContainsKey(langKey) && server[langKey].Count > sOk)
                        || (wOk > 0 && web.ContainsKey(langKey) && web[langKey].Count > wOk)
                        || (eOk > 0 && ext.ContainsKey(langKey) && ext[langKey].Count > eOk))
                        res.LogLines.Add("langue « " + langKey + " » présente plusieurs fois — valeurs fusionnées, dernier gagne.");
                }

                if (summary.Count == 0)
                {
                    res.ParseError = "aucune langue exploitable dans le fichier";
                    return res;
                }
                res.Snap = new Snapshot(
                    FreezeNonEmpty(server), FreezeNonEmpty(web), FreezeNonEmpty(ext),
                    DateTimeOffset.UtcNow,
                    "chargé (" + Path.GetFileName(path) + ") — "
                        + string.Join(" ; ", summary));
                return res;
            }
        }

        private static Dictionary<string, string> ServerGetOrAdd(
            Dictionary<string, Dictionary<string, string>> map, string langKey)
        {
            if (!map.TryGetValue(langKey, out var d))
            {
                d = new Dictionary<string, string>(StringComparer.Ordinal);
                map[langKey] = d;
            }
            return d;
        }

        /// <summary>Section absente → « — » (le zéro n'y est pas un état).</summary>
        private static string SectionCount(int n) => n < 0 ? "—" : n.ToString(CultureInfo.InvariantCulture);

        /// <summary>Gele les cartes : les entrées vides sont écartées
        /// (langue sans section utile = langue absente du snapshot).</summary>
        private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> FreezeNonEmpty(
            Dictionary<string, Dictionary<string, string>> src)
        {
            var ro = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
            foreach (var kv in src)
                if (kv.Value.Count > 0)
                    ro[kv.Key] = kv.Value;
            return ro;
        }

        /// <summary>Regex du code de langue LIBRE — décision v1.17.0 (atelier
        /// de langues) : au-delà de la table fermée du plugin, tout code de
        /// 2-3 lettres a-z est accepté (le glossaire Emby sera peut-être vide,
        /// l'atelier l'annonce au rapport). Sans cela, une langue générée mais
        /// hors table ne résoudrait JAMAIS (fichier écrit, jamais servi).</summary>
        private static readonly Regex FreeCodeRx =
            new Regex("^[a-z]{2,3}$", RegexOptions.Compiled);

        /// <summary>Normalise une clé de langue d'overlay : « es », « es-ES »,
        /// « ESPANOL », « Español », « pt-br » → clé 2 lettres du plugin
        /// (même table que <see cref="I18n"/>). Hors table → repli code libre
        /// (v1.17.0) : 2-3 lettres a-z après repli des diacritiques (« Hâwai »
        /// → haw). Retourne null si non reconnaissable.</summary>
        internal static string NormalizeLang(string name, out string note)
        {
            note = null;
            if (string.IsNullOrWhiteSpace(name)) return null;
            var key = I18n.ParseLangName(name);
            if (key == null)
            {
                string freeNote;
                key = FreeLangCode(name, out freeNote);
                if (key == null) return null;
                if (!string.Equals(name.Trim(), key, StringComparison.OrdinalIgnoreCase))
                    note = "normalisée en « " + key + " »"
                        + (freeNote != null ? " (" + freeNote + ")" : "");
                return key;
            }
            if (!string.Equals(name.Trim(), key, StringComparison.OrdinalIgnoreCase))
                note = "normalisée en « " + key + " »";
            return key;
        }

        /// <summary>Repli code libre : minuscule + diacritiques repliés (FormD
        /// — l'accent combiné tombe, la lettre de base reste), filtrage a-z →
        /// accepté si le résultat est un code de 2-3 lettres; sinon null.
        /// « PL » → pl, « Hâwai » → haw, « ΕΛ » (grec) → null. Jamais d'erreur :
        /// le pire cas est un code libre qui ne résoudra pas côté affichage.</summary>
        private static string FreeLangCode(string name, out string note)
        {
            note = null;
            try
            {
                var folded = name.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
                var sb = new System.Text.StringBuilder(folded.Length);
                foreach (var c in folded)
                    // a-z uniquement : les accents combinés (NonSpacingMark),
                    // chiffres, séparateurs et autres écritures tombent.
                    if (c >= 'a' && c <= 'z') sb.Append(c);
                var code = sb.ToString();
                if (FreeCodeRx.IsMatch(code))
                {
                    if (!string.Equals(name.Trim(), code, StringComparison.Ordinal))
                        note = "code libre hors table du plugin";
                    return code;
                }
            }
            catch { /* repli jamais fatal */ }
            return null;
        }

        /// <summary>Multiset des placeholders {n} d'une chaîne — miroir
        /// exact de la règle 4 du kit (tri ordinal + jointure ',').</summary>
        internal static string PlaceholderSig(string s)
            => string.Join(",", PlaceholderRx.Matches(s ?? "")
                .Cast<Match>().Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal));

        /// <summary>Multiset des balises HTML d'une chaîne — miroir exact
        /// de la règle 5 du kit (tri ordinal + jointure '|').</summary>
        internal static string HtmlTagSig(string s)
            => string.Join("|", HtmlTagRx.Matches(s ?? "")
                .Cast<Match>().Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal));

        /// <summary>Format de signature pour les logs (multiset vide → « aucune »).</summary>
        private static string SigOrNone(string sig)
            => sig.Length == 0 ? "aucun" : sig;
    }
}