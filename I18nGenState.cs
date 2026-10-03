using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>Le dernier rapport de génération de langue persisté (un seul
    /// enregistrement — miroir <see cref="LastAuditReport"/>).</summary>
    internal sealed class LastI18nGenReport
    {
        /// <summary>Date/heure (UTC) de production du rapport.</summary>
        public DateTimeOffset GeneratedAt;
        /// <summary>Code de langue généré (es, de…).</summary>
        public string Lang;
        /// <summary>Mode : full | missing | skipped.</summary>
        public string Mode;
        /// <summary>Rapport Markdown brut (rendu côté config.js).</summary>
        public string Report;
    }

    // ---------------------------------------------------------------------
    //  I18nGenReportStore : persistance du DERNIER rapport de génération de
    //  langue (miroir exact d'AuditReportStore). Un fichier unique
    //  i18n_gen_report.json à côté de LLM_AI.xml — un rapport d'erreur n'y
    //  écrase jamais le dernier vrai rapport ; fail-open IO total.
    // ---------------------------------------------------------------------
    internal static class I18nGenReportStore
    {
        private static string PathOf()
        {
            try
            {
                var dir = Plugin.Paths?.PluginConfigurationsPath;
                if (string.IsNullOrEmpty(dir)) return null;
                return Path.Combine(dir, "i18n_gen_report.json");
            }
            catch { return null; }
        }

        /// <summary>Charge le dernier rapport, ou null (absent/corrompu).</summary>
        public static LastI18nGenReport Load()
        {
            var path = PathOf();
            if (path == null || !File.Exists(path)) return null;
            string json;
            try { json = File.ReadAllText(path); }
            catch { return null; }
            try
            {
                if (!(JsonNode.Parse(json) is JsonObject root)) return null;
                var r = new LastI18nGenReport
                {
                    Lang = OStr(root, "lang"),
                    Mode = OStr(root, "mode"),
                    Report = OStr(root, "report")
                };
                if (DateTimeOffset.TryParse(OStr(root, "generated_at") ?? "", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var d))
                    r.GeneratedAt = d.ToUniversalTime();
                if (string.IsNullOrWhiteSpace(r.Report)) return null;
                return r;
            }
            catch { return null; }
        }

        /// <summary>Écrit le dernier rapport (best-effort : n'élève jamais —
        /// un échec disque ne doit pas faire échouer un run réussi).</summary>
        public static void Save(LastI18nGenReport rep, ILogger logger)
        {
            if (rep == null) return;
            var path = PathOf();
            if (path == null) return;
            try
            {
                var obj = new JsonObject
                {
                    ["generated_at"] = rep.GeneratedAt.ToString("o", CultureInfo.InvariantCulture),
                    ["lang"] = rep.Lang,
                    ["mode"] = rep.Mode,
                    ["report"] = rep.Report
                };
                File.WriteAllText(path, obj.ToJsonString());
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] I18n génération : persistance du rapport impossible : {0}", ex.Message);
            }
        }

        private static string OStr(JsonObject o, string prop)
            => o[prop] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    // ---------------------------------------------------------------------
    //  I18nGenState : état partagé du run de génération DÉTACHÉ — miroir
    //  exact d'AuditRunState (les mêmes terrains ont motivé la même forme :
    //  single-flight contre les clics en rafale, jalons survivant au
    //  rechargement de page, run détaché de la requête HTTP). Statique
    //  volontairement : l'état vit et meurt avec le process Emby.
    // ---------------------------------------------------------------------
    internal static class I18nGenState
    {
        private static readonly object _lock = new object();
        private static bool _running;
        private static string _progress;          // jalon courant (LOCALISÉ par l'appelant)
        private static DateTimeOffset _startedAt;
        private static DateTimeOffset? _finishedAt;
        private static string _outcome;           // "ok" | "error" du DERNIER run détaché
        private static string _lastError;

        /// <summary>Tente de démarrer un run (single-flight). True = l'appelant
        /// détient le run et DOIT appeler FinishOk/FinishError ; false = un run
        /// est déjà en cours (renvoyer l'état au client).
        /// <paramref name="initialProgress"/> = premier jalon, LOCALISÉ PAR
        /// L'APPELANT (bucket interface).</summary>
        internal static bool TryStart(string initialProgress)
        {
            lock (_lock)
            {
                if (_running) return false;
                _running = true;
                _progress = initialProgress ?? string.Empty;
                _startedAt = DateTimeOffset.UtcNow;
                _finishedAt = null;
                _outcome = null;
                _lastError = null;
                return true;
            }
        }

        /// <summary>Publie un jalon de progression (no-op hors run actif).</summary>
        internal static void SetProgress(string progress)
        {
            if (string.IsNullOrEmpty(progress)) return;
            lock (_lock)
            {
                if (_running) _progress = progress;
            }
        }

        /// <summary>Fin normale : un rapport a été produit/persisté.</summary>
        internal static void FinishOk()
        {
            lock (_lock)
            {
                if (!_running) return;
                _running = false;
                _progress = null;
                _finishedAt = DateTimeOffset.UtcNow;
                _outcome = "ok";
                _lastError = null;
            }
        }

        /// <summary>Fin en échec (timeout, backend, exception, écriture).</summary>
        internal static void FinishError(string error)
        {
            lock (_lock)
            {
                if (!_running) return;
                _running = false;
                _progress = null;
                _finishedAt = DateTimeOffset.UtcNow;
                _outcome = "error";
                _lastError = string.IsNullOrWhiteSpace(error) ? "échec" : error;
            }
        }

        /// <summary>Photo atomique de l'état (le struct d'audit est réutilisé :
        /// mêmes champs, DTO muet sans identité — un état de run est un état
        /// de run).</summary>
        internal static AuditRunSnapshot Snapshot()
        {
            lock (_lock)
            {
                return new AuditRunSnapshot(
                    _running,
                    _progress,
                    _running ? _startedAt : (DateTimeOffset?)null,
                    _finishedAt,
                    _outcome,
                    _lastError);
            }
        }
    }
}