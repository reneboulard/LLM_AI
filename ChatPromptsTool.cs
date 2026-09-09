using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Tool de chat <c>plugin_prompts</c> (v1.13.8) : le LLM du chat peut
    /// LIRE et (sur approbation) ÉCRIRE les cinq prompts/directives
    /// éditables de la configuration — le cas d'usage visé est « ajoute la
    /// règle X à ma directive films » en mode d'édition (contexte déroulant
    /// de la page chat, <see cref="ChatContexts"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>Deux phases obligatoires</b> : <c>set</c> n'écrit jamais la
    /// config — il valide (liste blanche, plafond de longueur, texte
    /// non vide), sérialise la modification complète dans
    /// <see cref="ChatPromptStore"/> et retourne
    /// <c>{"status":"pending_approval","action_id":…}</c>. L'écriture ne se
    /// produit qu'au clic « Approuver » de l'admin (endpoint
    /// <c>POST /Plugins/LLMAI/ChatPrompt/Approve</c>) en C# déterministe —
    /// le LLM n'a AUCUN chemin d'écriture direct, l'approbation n'est pas
    /// contournable par prompt.</para>
    /// <para><b>Gated</b> par <see cref="PluginConfiguration.ChatPromptsEnabled"/>
    /// (défaut false, opt-in explicite — cohérent avec
    /// <c>run_tonight_run</c>) ; le chat lui-même reste réservé aux
    /// administrateurs. Les actions <c>list</c>/<c>get</c> sont de la
    /// lecture (inoffensives) mais ne sont construites qu'avec le même
    /// interrupteur : une seule surface à raisonner.</para>
    /// </remarks>
    internal sealed class ChatPromptsTool : ILlmTool
    {
        /// <summary>Liste blanche des champs de prompt accessibles
        /// (id → libellé FR) — AUCUN autre champ de config n'est atteignable
        /// par ce tool.</summary>
        internal static readonly (string Id, string Label)[] FieldIds =
        {
            ("rag_directives", "Directives RAG"),
            ("schedule_task", "Tâche séries"),
            ("schedule_task_movies", "Tâche films"),
            ("tonight_prompt", "Run « ce soir »"),
            ("audit_prompt", "Prompt d'audit")
        };

        /// <summary>Plafond de longueur d'un texte de prompt soumis au
        /// <c>set</c> (budget tokens raisonnable pour les modèles locaux ;
        /// les prompts d'origine sont tous ≪).</summary>
        internal const int MaxPromptChars = 8000;

        /// <summary>Seuil de divergence (recouvrement lexical mot à mot,
        /// normalisé) sous lequel la carte de diff affiche l'avertissement
        /// « diffère fortement du texte actuel » — vécu 2026-09-09 : le LLM
        /// a soumis le texte de la tâche séries pour le champ RAG (une
        /// révision honnête du read-modify-write recouvre largement son
        /// texte courant ; un texte rédigé d'un autre champ, presque pas).</summary>
        internal const double WarnOverlapThreshold = 0.25;

        private readonly PluginConfiguration _cfg;
        private readonly string _sessionId;
        private readonly string _userId;
        private readonly string _contextId;
        private readonly ILogger _logger;

        public ChatPromptsTool(PluginConfiguration cfg, string sessionId, string userId,
            string contextId, ILogger logger)
        {
            _cfg = cfg; _sessionId = sessionId; _userId = userId;
            _contextId = contextId; _logger = logger;
        }

        public string Name => "plugin_prompts";

        public string Description =>
            "Lit et (sur approbation) modifie les prompts/directives de la configuration du plugin " +
            "(5 champs). actions : \"list\" (champs disponibles), \"get\" (texte courant d'un champ), " +
            "\"set\" (PROPOSE une modification — elle n'est écrite qu'après le clic « Approuver » de " +
            "l'admin sur la carte de diff ; n'annonce jamais une application avant). En mode d'édition, " +
            "le champ du set DOIT être celui du mode actif (un autre champ est refusé) : modifiez le " +
            "texte actuel fourni par le contexte (read-modify-write) — ne régénérez jamais de mémoire. " +
            "Format : le bloc clôturé ```text est le SEUL canal de livraison d'une révision — " +
            "toute proposition se TERMINE par ce bloc (texte complet), jamais de prose ni de " +
            "demande de confirmation avant livraison.";

        public string ArgumentsSchema =>
            "{\"type\":\"object\",\"properties\":{" +
            "\"action\":{\"type\":\"string\",\"enum\":[\"list\",\"get\",\"set\"]}," +
            "\"field\":{\"type\":\"string\",\"enum\":[\"rag_directives\",\"schedule_task\"," +
            "\"schedule_task_movies\",\"tonight_prompt\",\"audit_prompt\"]," +
            "\"description\":\"Champ visé (get/set)\"}," +
            "\"text\":{\"type\":\"string\",\"description\":\"Texte complet final (set, max " +
            MaxPromptChars + " caractères)\"}}," +
            "\"required\":[\"action\"]}";

        public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            string action = ArgString(args, "action");
            try
            {
                if (_cfg == null)
                    return Task.FromResult(Json(new { status = "failed", detail = "config indisponible." }));

                if (string.Equals(action, "list", StringComparison.OrdinalIgnoreCase))
                {
                    var fields = new System.Collections.Generic.List<object>();
                    foreach (var (id, label) in FieldIds)
                    {
                        var text = GetPrompt(_cfg, id) ?? "";
                        fields.Add(new { field = id, label, chars = text.Length });
                    }
                    return Task.FromResult(Json(new { status = "ok", fields }));
                }

                string field = ArgString(args, "field");
                if (string.Equals(action, "get", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsKnownField(field))
                        return Task.FromResult(Json(new { status = "refused",
                            detail = "field requis — valeurs admises : " + FieldList() + "." }));
                    return Task.FromResult(Json(new
                    {
                        status = "ok",
                        field,
                        label = LabelOf(field),
                        text = GetPrompt(_cfg, field) ?? ""
                    }));
                }

                if (string.Equals(action, "set", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsKnownField(field))
                        return Task.FromResult(Json(new { status = "refused",
                            detail = "field requis — valeurs admises : " + FieldList() + "." }));

                    // Validation croisée champ ↔ mode d'édition actif
                    // (v1.13.9) : la sauvegarde ne peut viser que le prompt
                    // du mode sélectionné dans la liste déroulante — la
                    // confusion de champ vécue 2026-09-09 (texte « tâche
                    // séries » soumis pour le champ RAG) devient
                    // impossible côté serveur.
                    var mode = ChatContexts.Find(_contextId);
                    if (mode == null)
                        return Task.FromResult(Json(new { status = "refused",
                            detail = "Aucun mode d'édition actif — sélectionnez un mode « Éditer — … » " +
                                     "dans la liste déroulante de la page chat, puis réessayez." }));
                    if (!string.Equals(mode.Field, field.Trim(), StringComparison.Ordinal))
                        return Task.FromResult(Json(new { status = "refused",
                            detail = "Champ « " + field.Trim() + " » hors du mode actif « " + mode.Label +
                                     " » (il couvre « " + mode.Field + " »). La sauvegarde doit viser le " +
                                     "prompt du mode actif — demandez à l'admin de changer de mode si besoin." }));

                    string text = (ArgString(args, "text") ?? "").Trim();
                    if (text.Length == 0)
                        return Task.FromResult(Json(new { status = "refused",
                            detail = "text requis (texte COMPLET final du prompt — jamais un diff ni un extrait)." }));
                    if (text.Length > MaxPromptChars)
                        return Task.FromResult(Json(new { status = "refused",
                            detail = "text dépasse " + MaxPromptChars + " caractères (" + text.Length +
                                     ") — condensez la proposition." }));

                    // Avertissement de divergence (non bloquant, affiché sur
                    // la carte) : un texte qui ne recouvre presque pas le
                    // texte courant n'est probablement pas une révision du
                    // read-modify-write.
                    string warn = null;
                    var current = GetPrompt(_cfg, field) ?? "";
                    if (current.Trim().Length > 0 && text.Length > 0)
                    {
                        double overlap = WordOverlap(text, current);
                        if (overlap < WarnOverlapThreshold)
                        {
                            warn = "⚠ Ce texte diffère fortement du texte actuel du champ (recouvrement " +
                                Math.Round(overlap * 100) + "%) — vérifiez qu'il s'agit bien d'une " +
                                "révision de « " + LabelOf(field) + " » et non d'un autre prompt.";
                            _logger?.Info("[LLM_AI] Chat prompts : divergence détectée sur {0} " +
                                "(recouvrement {1}%).", field, Math.Round(overlap * 100));
                        }
                    }

                    var pending = ChatPromptStore.Create(_cfg, _sessionId, _userId, field, text,
                        LabelOf(field), warn, _logger);
                    if (pending == null)
                        return Task.FromResult(Json(new { status = "failed", detail = "création de l'attente impossible." }));

                    _logger?.Info("[LLM_AI] Chat prompts : proposition en attente d'approbation " +
                        "(action_id={0}, champ={1}, {2} caractères).", pending.ActionId, field, text.Length);
                    return Task.FromResult(Json(new
                    {
                        status = "pending_approval",
                        action_id = pending.ActionId,
                        field,
                        label = pending.Label,
                        detail = "Proposition sérialisée — l'admin doit cliquer « Approuver » (ou « Refuser ») " +
                                 "sur la carte de diff de la page. Annoncez-lui la proposition et attendez ; " +
                                 "ne la renvoyez PAS en set sans changement."
                    }));
                }

                return Task.FromResult(Json(new { status = "refused",
                    detail = "action admises : list, get, set." }));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(Json(new { status = "failed", detail = "annulé" }));
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Chat action plugin_prompts : {0}", ex.Message);
                return Task.FromResult(Json(new { status = "failed", detail = ex.Message }));
            }
        }

        // ------------------------------------------------------------------
        //  Accès champ (aussi utilisé par l'endpoint d'approbation)
        // ------------------------------------------------------------------

        internal static bool IsKnownField(string field) =>
            !string.IsNullOrWhiteSpace(field) &&
            Array.Exists(FieldIds, f => string.Equals(f.Id, field.Trim(), StringComparison.Ordinal));

        internal static string LabelOf(string field)
        {
            foreach (var (id, label) in FieldIds)
                if (string.Equals(id, field?.Trim(), StringComparison.Ordinal)) return label;
            return field ?? "";
        }

        /// <summary>Lecteur du texte courant (liste blanche stricte —
        /// reflexion interdite, un champ inconnu retourne null).</summary>
        internal static string GetPrompt(PluginConfiguration cfg, string field)
        {
            if (cfg == null || !IsKnownField(field)) return null;
            switch (field.Trim())
            {
                case "rag_directives": return cfg.RagDirectives;
                case "schedule_task": return cfg.ScheduleTask;
                case "schedule_task_movies": return cfg.ScheduleTaskMovies;
                case "tonight_prompt": return cfg.TonightPrompt;
                case "audit_prompt": return cfg.AuditPrompt;
                default: return null;
            }
        }

        /// <summary>
        /// Indication de test par champ (FR, convention du plugin : les
        /// chaînes serveur affichées à l'admin sont FR) — renvoyée dans la
        /// réponse d'approbation et poussée dans le fil pour que le LLM
        /// sache COMMENT tester la nouvelle directive. Réaliste : elle dit
        /// le chemin qui exécute le VRAI code, ou la simulation possible
        /// quand aucun tool ne rejoue la tâche.
        /// </summary>
        internal static string TestHintFor(string field)
        {
            switch ((field ?? "").Trim())
            {
                case "tonight_prompt":
                    return "Test réel : demandez dans cette conversation « lance le run ce soir » " +
                        "(tool run_tonight_run — opt-in « déclenchement par le chat » en config) : il " +
                        "exécute le VRAI code TonightService avec la nouvelle directive ; le résultat " +
                        "apparaît sur la page Recommandations (badge « générée via chat »).";
                case "schedule_task":
                    return "Test : (a) dry-run conversationnel — demandez « applique la directive aux " +
                        "données epg_series et montre le tableau JSON » (vérifie le format et les champs) ; " +
                        "(b) exécution réelle — déclenchez la tâche « Enregistrements séries » (tableau de " +
                        "bord Emby, ou system_audit action=trigger_task si la remédiation est activée) et " +
                        "vérifiez les recommandations produites.";
                case "schedule_task_movies":
                    return "Test : (a) dry-run conversationnel — demandez « applique la directive aux " +
                        "données epg_movies et montre le tableau JSON » ; (b) exécution réelle — " +
                        "déclenchez la tâche « Enregistrements films » et vérifiez les recommandations.";
                case "audit_prompt":
                    return "Test réel : le bouton « Lancer l'audit santé » de la page de configuration " +
                        "(le chat, lui, exécute system_audit avec son workflow interne, pas ce prompt).";
                case "rag_directives":
                    return "Test : ces directives s'appliquent à TOUS les runs — le meilleur indicateur " +
                        "est un run « ce soir » (ou le dry-run conversationnel d'une directive de tâche) : " +
                        "les garde-fous (jamais un titre possédé, jamais une donnée devinée) doivent y " +
                        "être visibles.";
                default:
                    return "";
            }
        }

        /// <summary>Recouvrement lexical entre deux textes : mots
        /// (≥ 2 caractères, minuscules) communs rapportés au plus petit des
        /// deux ensembles. Approximation volontairement simple et
        /// déterministe — elle ne sert qu'à l'avertissement de la carte,
        /// jamais à refuser.</summary>
        private static double WordOverlap(string a, string b)
        {
            var sa = Tokens(a);
            var sb = Tokens(b);
            int min = Math.Min(sa.Count, sb.Count);
            if (min == 0) return 1.0;
            int common = 0;
            foreach (var w in sa)
                if (sb.Contains(w)) common++;
            return (double)common / min;
        }

        private static System.Collections.Generic.HashSet<string> Tokens(string s)
        {
            var set = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(s)) return set;
            var sb = new System.Text.StringBuilder();
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else if (sb.Length > 0)
                {
                    if (sb.Length >= 2) set.Add(sb.ToString());
                    sb.Length = 0;
                }
            }
            if (sb.Length >= 2) set.Add(sb.ToString());
            return set;
        }

        /// <summary>Écrivain déterministe (APPELÉ UNIQUEMENT par l'endpoint
        /// d'approbation, après validation du pending — jamais par le
        /// tool).</summary>
        internal static void SetPrompt(PluginConfiguration cfg, string field, string text)
        {
            switch (field.Trim())
            {
                case "rag_directives": cfg.RagDirectives = text; break;
                case "schedule_task": cfg.ScheduleTask = text; break;
                case "schedule_task_movies": cfg.ScheduleTaskMovies = text; break;
                case "tonight_prompt": cfg.TonightPrompt = text; break;
                case "audit_prompt": cfg.AuditPrompt = text; break;
                default: throw new ArgumentException("champ inconnu : " + field);
            }
        }

        private static string FieldList() =>
            string.Join(", ", Array.ConvertAll(FieldIds, f => f.Id));

        private static string ArgString(JsonElement args, string name)
        {
            try
            {
                if (args.ValueKind != JsonValueKind.Object) return null;
                if (!args.TryGetProperty(name, out var v)) return null;
                return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            }
            catch { return null; }
        }

        private static string Json(object o)
        {
            try { return System.Text.Json.JsonSerializer.Serialize(o); }
            catch { return "{\"status\":\"failed\",\"detail\":\"sérialisation\"}"; }
        }
    }
}