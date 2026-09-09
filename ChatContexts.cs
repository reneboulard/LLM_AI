using System;
using System.Linq;
using MediaBrowser.Controller;

namespace LLM_AI
{
    /// <summary>
    /// Un contexte prédéfini du chat admin (v1.13.8, portage du pattern
    /// « contextes » de llm_core) : un mode de conversation que l'usager
    /// choisit dans une liste déroulante de la page chat. Le contexte est
    /// injecté dans le system prompt <b>à chaque tour</b> (le serveur est
    /// stateless et le reconstruit de toute façon) — le changer n'exige
    /// donc JAMAIS de réinitialiser la conversation, et le texte injecté
    /// est toujours frais (rechargé de la config à chaque tour).
    /// </summary>
    /// <remarks>
    /// Les contextes fournis d'origine sont les cinq <b>modes d'édition de
    /// prompt</b> : chacun injecte le texte courant d'un prompt éditable +
    /// son guide d'édition (rôle, invariants, conventions de rédaction).
    /// Ajouter un contexte futur = UNE entrée dans <see cref="ChatContexts.All"/>
    /// — la liste servie à la page et la validation <c>context_id</c> en
    /// dérivent automatiquement (registre unique en code, pas de fichier à
    /// surveiller : le plugin tourne in-process chez Emby).
    /// </remarks>
    internal sealed class ChatContextDef
    {
        /// <summary>Identifiant stable (envoyé par la page à chaque tour).</summary>
        public string Id { get; }
        /// <summary>Libellé du menu déroulant (FR : l'admin francophone voit
        /// la liste ; les guides eux-mêmes sont FR).</summary>
        public string Label { get; }
        /// <summary>Guide d'édition du prompt (FR) : rôle, invariants,
        /// conventions — injecté avec le texte courant.</summary>
        public string Guide { get; }
        /// <summary>Lecteur du texte courant du prompt dans la config.</summary>
        public Func<PluginConfiguration, string> GetPrompt { get; }
        /// <summary>Champ de prompt couvert par ce mode (liste blanche
        /// <see cref="ChatPromptsTool.FieldIds"/>) : cible de la validation
        /// croisée champ ↔ mode du <c>set</c> (v1.13.9 — la sauvegarde ne
        /// peut viser que le prompt du mode actif).</summary>
        public string Field { get; }

        public ChatContextDef(string id, string label, string guide,
            Func<PluginConfiguration, string> getPrompt, string field)
        {
            Id = id ?? "";
            Label = label ?? "";
            Guide = guide ?? "";
            GetPrompt = getPrompt ?? (_ => "");
            Field = field ?? "";
        }
    }

    /// <summary>
    /// Registre statique des contextes de chat + constructeur du bloc
    /// injecté dans le system prompt. Le bloc porte : le guide, le texte
    /// COURANT du prompt (règle read-modify-write — ne jamais régénérer de
    /// mémoire : des règles de l'usager peuvent n'exister que dans sa
    /// config live) et la langue cible, résolue côté serveur
    /// (<c>ResponseLanguage</c>, repli langue d'affichage Emby) — le LLM
    /// ne demande JAMAIS la langue : c'est une contrainte injectée, pas une
    /// préférence à négocier.
    /// </summary>
    internal static class ChatContexts
    {
        /// <summary>Conventions de rédaction communes aux cinq modes
        /// d'édition (règles de la maison, cf. mémoires du projet).</summary>
        private const string CommonRules =
            "### RÈGLES DE RÉVISION (obligatoires)\n" +
            "- CANAL DE LIVRAISON (obligatoire) : vous ne pouvez PAS écrire la configuration du " +
            "plugin vous-même — le SEUL canal par lequel votre texte révisé parvient à l'interface " +
            "est un bloc de code clôturé ```text … ``` (même mécanique qu'une commande bash proposée " +
            "dans un bloc : sans le bloc affiché, rien ne peut être exécuté). Dès que vous proposez " +
            "une révision, TERMINEZ votre réponse par ce bloc portant le texte COMPLET du prompt " +
            "révisé — explication en prose PUIS la clôture en tout dernier. Sans lui, votre " +
            "proposition n'existe pas pour l'interface (le bouton de sauvegarde n'apparaît pas).\n" +
            "- PAS DE CONFIRMATION AVANT LIVRAISON : ne demandez JAMAIS une validation ou un choix " +
            "de pistes au lieu de livrer (pas de « voulez-vous que je prépare le texte ? », " +
            "« que pensez-vous ? » puis attente). La confirmation existe, mais elle porte sur " +
            "l'EXÉCUTION du bloc déjà livré — la carte de diff Approuver/Refuser de la page — jamais " +
            "sur sa production : livrez le bloc, l'usager approuve ou refuse la carte.\n" +
            "- INVERSE : n'enveloppez JAMAIS un tableau JSON d'appels d'outils dans une clôture " +
            "Markdown (une clôture = contenu destiné à l'admin, un appel d'outil = toujours brut).\n" +
            "- READ-MODIFY-WRITE : la source de vérité est le TEXTE ACTUEL ci-dessus. " +
            "Ne régénérez JAMAIS le prompt de mémoire : modifiez le texte actuel, en le " +
            "préservant mot à mot sauf demande explicite de l'usager (des règles ne pouvant " +
            "être retrouvées ailleurs peuvent y vivre).\n" +
            "- Conventions de rédaction : en-tête de règle en MAJUSCULES ; une règle par bloc ; " +
            "références exactes aux champs réels fournis par les outils (jamais de champ " +
            "inventé) ; un garde-fou anti-mauvaise-interprétation par règle (ex. « ne décale " +
            "jamais l'heure », « ne pénalise jamais une donnée absente ») ; défaut défini par " +
            "la règle elle-même (bornes inclusives explicites, jours nommés).\n" +
            "- LANGUES : répondez dans la langue de l'usager ; le texte du prompt révisé reste " +
            "dans la LANGUE CIBLE ci-dessous (elle est fixée par la configuration du serveur, " +
            "les runs du plugin la lisent). Si l'usager demande une autre langue pour le " +
            "prompt, refusez poliment et proposez de changer d'abord la « Langue de réponse » " +
            "dans la page de configuration — ne créez jamais une divergence config/runs.\n" +
            "- SAUVEGARDE : l'écriture passe par le tool plugin_prompts (action=\"set\") — " +
            "elle n'est appliquée qu'après que l'admin a cliqué « Approuver » sur la carte de " +
            "diff de la page. Ne dites jamais qu'un prompt est enregistré avant cette " +
            "approbation ; après approbation, rappelez de recharger la page de configuration " +
            "(un enregistrement ultérieur de la page avec des valeurs affichées périmées " +
            "écraserait la modification).\n" +
            "- MODE EXCLUSIF : ce mode ne couvre que le prompt présenté ci-dessus. Si l'usager " +
            "demande de modifier un AUTRE prompt (ou de « sauvegarder dans » un autre champ), " +
            "refusez : rappelez le mode actif et invitez-le à changer de mode dans la liste " +
            "déroulante de la page chat. Ne proposez JAMAIS un set d'un autre champ — le " +
            "serveur le refuserait de toute façon (vécu 2026-09-09 : une règle demandée pour " +
            "les Directives RAG a été écrite dans la tâche séries sans que rien ne le signale).";

        /// <summary>Les cinq modes d'édition (un par prompt éditable de la
        /// page de configuration).</summary>
        internal static readonly ChatContextDef[] All =
        {
            new ChatContextDef("edit_rag",
                "Éditer — Directives RAG",
                "### RÔLE DE CE PROMPT\n" +
                "Les « Directives RAG » sont injectées dans le system prompt de TOUTES les tâches " +
                "LLM du plugin (tâche quotidienne de séries, tâche films, run « ce soir »). Elles " +
                "portent les invariants transverses : ne jamais recommander un titre déjà possédé " +
                "ou déjà programmé, ne jamais deviner une donnée absente (synopsis, année, id), " +
                "préférence légère aux productions récentes SANS pénaliser une année inconnue, " +
                "pas de doublon dans une même réponse. Toute révision doit préserver ces " +
                "garde-fous — ils protègent contre les erreurs classiques des LLM.\n",
                cfg => cfg.RagDirectives, "rag_directives"),

            new ChatContextDef("edit_schedule_series",
                "Éditer — Tâche séries (enregistrements)",
                "### RÔLE DE CE PROMPT\n" +
                "Directive de la tâche planifiée quotidienne de SÉRIES. Contrat de sortie : un " +
                "tableau JSON [{title, kind, reason, priority, channel, start, showbizz_match}] " +
                "avec kind=\"series\" — le moteur du plugin parse ce format : toute révision doit " +
                "le préserver tel quel. Le préfixe « Daily 03:00 | » est l'horaire de planification " +
                "lu par la config : ne l'altérez que sur demande explicite et sans ambiguïté. " +
                "Champs EPG réels disponibles : ceux de get_emby_info (action=epg_series — " +
                "premieres_only, new_seasons) ; ne référencez jamais un champ inventé.\n",
                cfg => cfg.ScheduleTask, "schedule_task"),

            new ChatContextDef("edit_schedule_movies",
                "Éditer — Tâche films (enregistrements)",
                "### RÔLE DE CE PROMPT\n" +
                "Directive de la tâche de FILMS. Contrat de sortie : tableau JSON [{title, kind, " +
                "reason, priority, channel, start, showbizz_match}] avec kind=\"movie\" — à " +
                "préserver tel quel. Champs EPG réels : ceux de get_emby_info (action=epg_movies). " +
                "ATTENTION : cette directive porte souvent des règles PERSONNALISÉES de l'usager " +
                "(ex. une règle d'horaire d'exclusion) qui n'existent NULLE PART ailleurs — la " +
                "règle read-modify-write ci-dessous est critique pour ce prompt.\n",
                cfg => cfg.ScheduleTaskMovies, "schedule_task_movies"),

            new ChatContextDef("edit_tonight",
                "Éditer — Run « À regarder ce soir »",
                "### RÔLE DE CE PROMPT\n" +
                "Prompt du run interactif « À regarder ce soir » (page Recommandations, " +
                "collection/playlist, déclenchement chat). Il croise le profil de goût, l'EPG du " +
                "soir (get_emby_info action=epg_tonight) et les enregistrements récents non " +
                "visionnés. Contrat de sortie : kind=\"series\"|\"movie\", priority " +
                "high/medium/low, source=\"live\" (champs title/channel/start repris tels quels " +
                "d'epg_tonight) ou source=\"recording\" (id repris de la liste des " +
                "enregistrements). Les directives de session one-shot du chat s'injectent APRÈS " +
                "ce prompt : ne les dupliquez pas dedans.\n",
                cfg => cfg.TonightPrompt, "tonight_prompt"),

            new ChatContextDef("edit_audit",
                "Éditer — Prompt d'audit santé",
                "### RÔLE DE CE PROMPT\n" +
                "Prompt de l'audit santé (« Lancer l'audit santé ») : orchestre system_audit " +
                "(actions server_info, host_metrics, disk_storage, active_sessions, " +
                "scheduled_tasks, transcode, gpu_transcode, security_check, upnp_check, " +
                "list_logs, inspect_log) et produit le rapport Markdown par gravité (🔴/⚠️/✅). " +
                "Les GARDE-FOUS DE SÉCURITÉ vivent en CODE (workflow d'audit + interrupteur " +
                "« remédiation » de la config) : une révision de ce prompt ne peut ni les " +
                "modifier ni croire devoir les dupliquer — ne réécrivez pas l'interdiction de " +
                "remédiation autonome en prose différente, laissez le code la porter.\n",
                cfg => cfg.AuditPrompt, "audit_prompt"),
        };

        /// <summary>Résout un contexte par identifiant (null si inconnu —
        /// la page n'envoie que des ids de la liste servie, mais la requête
        /// est entrante : jamais de confiance).</summary>
        internal static ChatContextDef Find(string contextId) =>
            string.IsNullOrWhiteSpace(contextId)
                ? null
                : All.FirstOrDefault(c => string.Equals(c.Id, contextId.Trim(), StringComparison.Ordinal));

        /// <summary>Champ de prompt couvert par un mode (vide si mode
        /// inconnu/absent) — la validation croisée du <c>set</c> s'appuie
        /// dessus : un mode d'édition ne couvre que SON prompt.</summary>
        internal static string FieldFor(string contextId) => Find(contextId)?.Field ?? "";

        /// <summary>
        /// Construit le bloc contexte injecté dans le system prompt du tour
        /// (vide si <paramref name="contextId"/> absent/inconnu ou si le
        /// prompt courant est vide — fail-open : jamais de bloc dégénéré).
        /// La langue cible est résolue serveur : <c>ResponseLanguage</c>
        /// renseignée, sinon langue d'affichage Emby (même cascade que
        /// l'endpoint DefaultPrompts).
        /// </summary>
        internal static string BuildBlock(PluginConfiguration cfg, string contextId,
            IServerApplicationHost host)
        {
            var def = Find(contextId);
            string current = def == null || cfg == null ? null : def.GetPrompt(cfg);
            if (def == null || string.IsNullOrWhiteSpace(current)) return "";

            string lang = I18n.ParseLangName(cfg.ResponseLanguage);
            if (string.IsNullOrEmpty(lang))
                lang = I18n.ResolveDisplayLangKey(host);
            string langLabel = string.Equals(lang, I18n.Fr, StringComparison.Ordinal)
                ? "français"
                : "anglais";

            var sb = new System.Text.StringBuilder();
            sb.Append("\n\n### MODE ACTIF : ").Append(def.Label).Append("\n");
            sb.Append(def.Guide);
            sb.Append("### LANGUE CIBLE\n");
            sb.Append("Le texte du prompt révisé doit rester en ").Append(langLabel)
              .Append(" (langue de réponse configurée du serveur — les runs la lisent).\n");
            sb.Append("### TEXTE ACTUEL DU PROMPT\n");
            sb.Append(current.Trim());
            sb.Append("\n### FIN DU TEXTE ACTUEL\n");
            // Règles de révision (dont la clôture ```text qui active le
            // bouton de sauvegarde) : injectées en TOUT DERNIER du bloc —
            // v1.13.9.10 : elles étaient définies mais jamais injectées
            // (vécu : le LLM présentait la révision en prose et l'usager
            // devait le rappeler à chaque tour). En fin de bloc pour
            // l'effet de récence sur les petits modèles locaux.
            sb.Append(CommonRules);
            return sb.ToString();
        }
    }
}