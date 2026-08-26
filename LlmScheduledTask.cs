using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace LLM_AI
{
    /// <summary>
    /// Tâche planifiée qui appelle le LLM (Ollama) avec les directives RAG
    /// et le prompt de tâche configurés, puis logue la réponse dans le
    /// journal Emby (embyserver.txt).
    /// </summary>
    public class LlmScheduledTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly ILogger _logger;
        private readonly IJsonSerializer _json;

        public LlmScheduledTask(ILogger logger, IJsonSerializer jsonSerializer)
        {
            _logger = logger;
            _json = jsonSerializer;
        }

        public string Name => "LLM AI Task";

        /// <summary>Identifiant stable de la tâche (GUID du plugin).</summary>
        public string Key => "e7d3dee6-ef19-46a9-985f-06318b682e60";

        public string Description => "Interroge le LLM configuré (Ollama) avec les directives RAG et le prompt de tâche, puis logue la réponse.";

        public string Category => "LLM AI";

        public bool IsHidden => false;

        public bool IsEnabled => true;

        public bool IsLogged => true;

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
            {
                _logger.Warn("[LLM_AI] Tâche exécutée mais aucune configuration disponible (Plugin.Instance null).");
                return;
            }

            progress?.Report(10);

            // Sépare planification (ignorée à l'exécution) et prompt utilisateur.
            string userPrompt = cfg.ScheduleTask ?? string.Empty;
            int sep = userPrompt.IndexOf('|');
            if (sep >= 0)
                userPrompt = userPrompt.Substring(sep + 1).Trim();
            else
                userPrompt = userPrompt.Trim();

            if (string.IsNullOrWhiteSpace(userPrompt))
            {
                _logger.Warn("[LLM_AI] Aucun prompt de tâche configuré (champ « Schedule Task » vide ou sans texte après '|'). Tâche ignorée.");
                return;
            }

            progress?.Report(30);

            try
            {
                _logger.Info("[LLM_AI] Exécution de la tâche — prompt: {0}", Truncate(userPrompt, 200));

                var reply = await LlmClient.ChatAsync(
                    cfg.LlmUrl,
                    cfg.ModelName,
                    cfg.RagDirectives,
                    userPrompt,
                    _json,
                    _logger,
                    cancellationToken).ConfigureAwait(false);

                progress?.Report(90);

                // Logue la réponse (apparaît dans /var/lib/emby/logs/embyserver.txt).
                _logger.Info("[LLM_AI] Réponse du LLM :\n{0}", reply);
            }
            catch (OperationCanceledException)
            {
                _logger.Info("[LLM_AI] Tâche annulée.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[LLM_AI] Échec de l'appel au LLM : {0}", ex, ex.Message);
                throw;
            }
            finally
            {
                progress?.Report(100);
            }
        }

        /// <summary>
        /// Triggers par défaut déduits de la partie gauche du champ ScheduleTask
        /// (ex. « Daily 03:00 », « Hourly », « Weekly Monday 03:00 »).
        /// L'utilisateur peut toujours les ajuster dans le planificateur Emby.
        /// </summary>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var cfg = Plugin.Instance?.Configuration;
            var spec = cfg?.ScheduleTask ?? string.Empty;

            int sep = spec.IndexOf('|');
            string schedule = (sep >= 0 ? spec.Substring(0, sep) : spec).Trim();

            foreach (var t in ParseTriggers(schedule))
                yield return t;
        }

        private static IEnumerable<TaskTriggerInfo> ParseTriggers(string schedule)
        {
            if (string.IsNullOrWhiteSpace(schedule))
            {
                yield return DailyAt(0, 0);
                yield break;
            }

            var parts = schedule.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var keyword = parts[0].ToUpperInvariant();

            switch (keyword)
            {
                case "HOURLY":
                    yield return new TaskTriggerInfo
                    {
                        Type = "IntervalTrigger",
                        IntervalTicks = TimeSpan.FromHours(1).Ticks
                    };
                    yield break;

                case "INTERVAL":
                    // « INTERVAL <heures> »
                    if (parts.Length >= 2 && int.TryParse(parts[1], out var hours) && hours > 0)
                        yield return new TaskTriggerInfo
                        {
                            Type = "IntervalTrigger",
                            IntervalTicks = TimeSpan.FromHours(hours).Ticks
                        };
                    else
                        yield return new TaskTriggerInfo
                        {
                            Type = "IntervalTrigger",
                            IntervalTicks = TimeSpan.FromHours(1).Ticks
                        };
                    yield break;

                case "WEEKLY":
                    {
                        var (h, m) = ParseTime(parts, 2, 1); // Weekly [Day] HH:MM
                        yield return new TaskTriggerInfo
                        {
                            Type = "WeeklyTrigger",
                            DayOfWeek = ParseDayOfWeek(parts, 1),
                            TimeOfDayTicks = new TimeSpan(h, m, 0).Ticks
                        };
                        yield break;
                    }

                case "DAILY":
                    {
                        var (h, m) = ParseTime(parts, 1, 0);
                        yield return DailyAt(h, m);
                        yield break;
                    }

                default:
                    // Peut-être juste une heure « 03:00 » → quotidien à cette heure.
                    if (TryParseTime(schedule, out var hh, out var mm))
                        yield return DailyAt(hh, mm);
                    else
                        yield return DailyAt(0, 0);
                    yield break;
            }
        }

        private static TaskTriggerInfo DailyAt(int h, int m) => new TaskTriggerInfo
        {
            Type = "DailyTrigger",
            TimeOfDayTicks = new TimeSpan(h, m, 0).Ticks
        };

        private static (int h, int m) ParseTime(string[] parts, int index, int defaultH)
        {
            if (index < parts.Length && TryParseTime(parts[index], out var h, out var m))
                return (h, m);
            return (defaultH, 0);
        }

        private static bool TryParseTime(string s, out int h, out int m)
        {
            h = 0; m = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            int colon = s.IndexOf(':');
            if (colon <= 0 || colon >= s.Length - 1) return false;
            if (!int.TryParse(s.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out h)) return false;
            if (!int.TryParse(s.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out m)) return false;
            return h >= 0 && h < 24 && m >= 0 && m < 60;
        }

        private static DayOfWeek? ParseDayOfWeek(string[] parts, int index)
        {
            if (index >= parts.Length) return null;
            return parts[index].ToUpperInvariant() switch
            {
                "MONDAY" => DayOfWeek.Monday,
                "TUESDAY" => DayOfWeek.Tuesday,
                "WEDNESDAY" => DayOfWeek.Wednesday,
                "THURSDAY" => DayOfWeek.Thursday,
                "FRIDAY" => DayOfWeek.Friday,
                "SATURDAY" => DayOfWeek.Saturday,
                "SUNDAY" => DayOfWeek.Sunday,
                _ => (DayOfWeek?)null
            };
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");
    }
}