// Page « Chat LLM AI » — module AMD chargé par le dashboard via
// data-controller="__plugin/LLMAIChatPageJS". Conversation interactive
// multi-tours avec l'agent LLM : POST /Plugins/LLMAI/Chat avec le message
// courant + l'historique complet de la conversation.
//
// Le serveur est stateless — l'historique vit dans cette closure (page) et
// n'est jamais persisté ; seuls les tours user/assistant (textes finaux)
// sont stockés, pas les appels d'outils intermédiaires. Le system prompt
// (doc outils + directives) est construit serveur-side, une seule fois par
// conversation — le client ne le stocke ni ne le renvoie.
//
// Porté depuis la section chat de config.js (v1) ; les clés i18n cfg.chat.*
// sont partagées par le module LLMAII18n.
define([], function () {
    "use strict";

    // Cache-busting : compare la version du build qui a servi ce JS à celle
    // du serveur (module généré asset_version.js, chargé via require() comme
    // LLMAII18n). Tâche de fond non bloquante : si le JS servi est périmé
    // (cache disque), les entrées de cache HTTP sont réécrites puis la page
    // rechargée — une seule fois par session (voir asset_version_template.js).
    // Toute erreur est silencieuse : la page reste pleinement fonctionnelle.

    // Jeton de bust de cache PAR SESSION navigateur. Emby sert
    // web/ConfigurationPage avec Cache-Control: public, sans max-age ni
    // Last-Modified, et un ETag NON dérivé du contenu (même ETag pour des
    // ressources de contenus différents) : la revalidation peut répondre
    // 304 sur un contenu périmé. Le jeton rend l'URL distincte à chaque
    // session → lecture réseau garantie au moins une fois par session,
    // sans dépendre de l'ETag (v1.13.4.3).
    function bustToken() {
        try {
            var k = sessionStorage.getItem("LLMAI.bust");
            if (!k) {
                k = Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
                sessionStorage.setItem("LLMAI.bust", k);
            }
            return k;
        } catch (e) { return String(Date.now()); }
    }
    function resourceUrl(name) {
        return ApiClient.getUrl("web/ConfigurationPage", { name: name, v: bustToken() });
    }

    (function checkAssetVersion() {
        try {
            var url = resourceUrl("LLMAIAssetVersion");
            require([url], function (av) {
                if (av && typeof av.checkForUpdate === "function") av.checkForUpdate(ApiClient);
            }, function () { /* module absent : silencieux */ });
        } catch (e) { /* require/ApiClient indisponible : silencieux */ }
    })();

    // Module i18n (FR/EN) : chargé comme ressource plugin via require() sur
    // « web/ConfigurationPage?name=LLMAII18n » (même mécanisme que
    // recommendations.js). `i18n` est renseigné au viewshow avant toute
    // utilisation de t() / translateView().
    var i18n = null;
    var _i18nPromise = null;
    function i18nReady() {
        if (i18n) return Promise.resolve(i18n);
        if (_i18nPromise) return _i18nPromise;
        var url = resourceUrl("LLMAII18n");
        _i18nPromise = new Promise(function (resolve, reject) {
            require([url], function (mod) { i18n = mod; resolve(mod); }, reject);
        });
        return _i18nPromise;
    }

    function esc(s) {
        // Échappe le HTML pour éviter toute injection depuis la sortie du LLM.
        return String(s == null ? "" : s)
            .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;");
    }

    // Mini-rendu Markdown -> HTML sûr (identique à config.js / audit) : on
    // échappe d'abord tout le texte, puis on convertit les constructions
    // supportées (titres #/##/###, listes -/*, gras **…**, `code` inline,
    // paragraphes, lignes de code séparées). Pas de dépendance externe.
    function renderLines(lines) {
        var out = [];
        var inUl = false;
        var para = [];
        function flushPara() {
            if (para.length) {
                out.push("<p>" + para.join(" ") + "</p>");
                para = [];
            }
        }
        function closeUl() {
            if (inUl) { out.push("</ul>"); inUl = false; }
        }
        function inline(s) {
            var h = esc(s);
            // v1.13.9.12 : liens Markdown [texte](url) vers la fiche Emby —
            // même origine uniquement (l'URL doit commencer par « / » : la page
            // chat est servie par Emby lui-même, tout lien absolu du LLM est
            // rendu comme texte brut — pas de redirection contrôlée par le
            // modèle). Ouvre dans un nouvel onglet (demande explicite de
            // l'usager : « consulter la fiche directement sur Emby »).
            h = h.replace(/\[([^\]]+)\]\(([^()\s][^()]*?)\)/g, function (m, txt, url) {
                if (!/^\/[^/]/.test(url) && url !== "/") return m;
                return '<a href="' + url + '" target="_blank" rel="noopener noreferrer">' + txt + '</a>';
            });
            h = h.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
            h = h.replace(/`([^`]+)`/g, "<code>$1</code>");
            return h;
        }
        for (var i = 0; i < lines.length; i++) {
            var line = lines[i].replace(/^\s+|\s+$/g, "");
            if (!line) { flushPara(); closeUl(); continue; }
            var h = /^(#{1,3})\s+(.*)$/.exec(line);
            if (h) { flushPara(); closeUl(); var lvl = h[1].length; out.push("<h" + lvl + ">" + inline(h[2]) + "</h" + lvl + ">"); continue; }
            if (/^[-*]\s+/.test(line)) { flushPara(); if (!inUl) { out.push("<ul>"); inUl = true; } out.push("<li>" + inline(line.replace(/^[-*]\s+/, "")) + "</li>"); continue; }
            if (/^>|^\|/.test(line)) { flushPara(); closeUl(); out.push("<p><code>" + esc(line) + "</code></p>"); continue; }
            para.push(inline(line));
        }
        flushPara();
        closeUl();
        return out.join("\n");
    }

    // Extension v1.13.9.5 : blocs clôturés ```lang … ``` rendus en conteneur
    // avec boutons Copier / Demander l'enregistrement (portage du pattern
    // _renderCodeBlock de llm_core, repli execCommand pour HTTP non-localhost).
    var CODE_EXT = {
        javascript: "js", js: "js", python: "py", py: "py", php: "php",
        html: "html", css: "css", bash: "sh", sh: "sh", shell: "sh",
        json: "json", xml: "xml", yaml: "yaml", yml: "yml", sql: "sql",
        markdown: "md", md: "md", csharp: "cs", cs: "cs", cpp: "cpp",
        "c++": "cpp", java: "java", go: "go", rust: "rs", rs: "rs",
        ruby: "rb", rb: "rb", text: "txt"
    };

    function codeBlockHtml(lang, code, allowSave) {
        var l = (lang || "").trim().toLowerCase();
        var ext = CODE_EXT[l] || "txt";
        var label = "📄 code." + ext + (l ? " (" + l + ")" : "");
        var btns = '<button type="button" class="chatCodeBtn chatCodeCopy" data-code="' + esc(code) +
            '" title="' + esc(i18n ? i18n.t("chat.code.copy") : "Copier") + '">📋</button>';
        // Bouton de sauvegarde : uniquement en mode d'édition (un contexte
        // est sélectionné) — le message prérédigé vise le « mode actif ».
        if (allowSave) {
            btns += '<button type="button" class="chatCodeBtn chatCodeSave" data-code="' + esc(code) +
                '" title="' + esc(i18n ? i18n.t("chat.code.save") : "") + '">💾</button>';
        }
        return '<div class="chatCode"><div class="chatCodeHead">' +
            '<span class="chatCodeName">' + esc(label) + '</span>' +
            '<span class="chatCodeBtns">' + btns + '</span></div>' +
            '<pre class="chatCodePre"><code>' + esc(code) + '</code></pre></div>';
    }

    function renderMarkdown(md, allowSave) {
        var lines = String(md == null ? "" : md).replace(/\r\n?/g, "\n").split("\n");
        var out = [];
        var buf = [];
        var i = 0;
        function flushBuf() {
            if (buf.length) { out.push(renderLines(buf)); buf = []; }
        }
        while (i < lines.length) {
            var open = /^\s*```([A-Za-z0-9+#\-]*)\s*$/.exec(lines[i]);
            if (open) {
                flushBuf();
                var code = [];
                i++;
                // Fence non fermé (réponse tronquée) : tout le reste est code.
                while (i < lines.length && !/^\s*```\s*$/.test(lines[i])) { code.push(lines[i]); i++; }
                if (i < lines.length) i++; // saute la clôture
                out.push(codeBlockHtml(open[1], code.join("\n"), allowSave === true));
                continue;
            }
            buf.push(lines[i]);
            i++;
        }
        flushBuf();
        return out.join("\n");
    }

    return function (view) {
        view.addEventListener("viewshow", function () {
            i18nReady().then(function () {
                return i18n.init();
            }).then(function () {
                // Traduit le DOM statique (titre, description, indice, libellés).
                i18n.translateView(view);

                // Historique de la conversation (stateless serveur) : tours
                // user/assistant uniquement, bornés côté page ET côté serveur.
                var chatHistory = [];
                var chatBusy = false;
                // Mémoire de conversation : identifiant de session retourné
                // par la première réponse du serveur puis rejoué à chaque
                // tour (chaîne vide = nouvelle conversation).
                var chatSessionId = "";

                var chatLog = view.querySelector("#chatLog");
                var chatInput = view.querySelector("#txtChatInput");
                var chatSendBtn = view.querySelector("#btnSendChat");
                var chatClearBtn = view.querySelector("#btnClearChat");
                var chatContextSel = view.querySelector("#selChatContext");

                // ----------------------------------------------------------------
                //  Blocs de code des réponses (v1.13.9.5) : Copier (repli
                //  execCommand — navigator.clipboard n'existe pas en contexte
                //  non sécurisé, ex. HTTP sur IP LAN) et Demander
                //  l'enregistrement (envoie un message prérédigé : le LLM
                //  reste l'émetteur du set, la carte diff reste
                //  l'autorisation). Délégation sur #chatLog : aucun
                //  gestionnaire par bouton.
                // ----------------------------------------------------------------
                function copyCodeText(txt, btn) {
                    function done() {
                        var o = btn.textContent;
                        btn.textContent = "✅";
                        setTimeout(function () { btn.textContent = o; }, 2000);
                    }
                    function legacy() {
                        var ta = document.createElement("textarea");
                        ta.value = txt;
                        ta.style.position = "fixed";
                        ta.style.opacity = "0";
                        document.body.appendChild(ta);
                        ta.select();
                        try { document.execCommand("copy"); done(); } catch (e) { /* silencieux */ }
                        document.body.removeChild(ta);
                    }
                    if (navigator.clipboard && navigator.clipboard.writeText) {
                        navigator.clipboard.writeText(txt).then(done, legacy);
                    } else legacy();
                }
                if (chatLog) {
                    chatLog.addEventListener("click", function (ev) {
                        var btn = ev.target;
                        while (btn && btn !== chatLog && !(btn.classList && btn.classList.contains("chatCodeBtn"))) {
                            btn = btn.parentNode;
                        }
                        if (!btn || !btn.classList) return;
                        if (btn.classList.contains("chatCodeCopy")) {
                            copyCodeText(btn.getAttribute("data-code") || "", btn);
                        } else if (btn.classList.contains("chatCodeSave")) {
                            if (chatBusy) return;
                            // v1.13.9.7 : message autoporteur — le texte du
                            // bloc cliqué est embarqué dans le message, donc
                            // le clic vise toujours SON bloc (plusieurs
                            // propositions dans la conversation sans
                            // ambiguïté « réponse précédente »). Le bouton de
                            // secours au niveau du tour (réponse en prose)
                            // n'a pas de data-code : message générique.
                            var code = btn.getAttribute("data-code");
                            if (code != null) {
                                sendChatText(i18n.t("chat.code.save_request", code));
                            } else {
                                // v1.13.9.8 : le bouton de secours (réponse en
                                // prose) ne demande plus l'enregistrement
                                // direct — le ciblage « ta dernière réponse »
                                // est ambigu si la conversation a avancé. Il
                                // fait réémettre la révision en bloc ```text :
                                // instruction ponctuelle (suivie, là où la
                                // règle de fond de CommonRules est ignorée),
                                // puis le bloc porte son 💾 autoporteur.
                                sendChatText(i18n.t("chat.code.reemit_request"));
                            }
                        }
                    });
                }

                // ----------------------------------------------------------------
                //  Contextes déroulants (v1.13.8) : modes de conversation servis
                //  par le registre statique serveur. Le choix est un paramètre
                //  de chaque tour — changer de mode n'exige JAMAIS de reset
                //  (le serveur réinjecte le bloc à chaque tour).
                // ----------------------------------------------------------------
                if (chatContextSel) {
                    ApiClient.ajax({
                        url: ApiClient.getUrl("Plugins/LLMAI/ChatContexts"),
                        type: "GET",
                        dataType: "json"
                    }).then(function (data) {
                        data = data || {};
                        if (data.Error || data.Enabled === false || !data.Contexts) return;
                        data.Contexts.forEach(function (c) {
                            if (!c || !c.Id) return;
                            var opt = document.createElement("option");
                            opt.value = c.Id;
                            opt.textContent = c.Label || c.Id;
                            chatContextSel.appendChild(opt);
                        });
                    }, function () { /* indisponible : chat sans modes (fail-open) */ });
                }

                function chatTurnHtml(role, bodyHtml) {
                    var who = i18n.t(role === "user" ? "cfg.chat.you" : "cfg.chat.assistant");
                    return '<div class="chatTurn ' + (role === "user" ? "user" : "bot") + '">' +
                        '<div class="chatWho">' + esc(who) + '</div>' +
                        '<div class="' + (role === "user" ? "chatUser" : "chatMarkdown") + '">' +
                        bodyHtml + '</div></div>';
                }

                function appendChatTurn(role, bodyHtml) {
                    if (!chatLog) return;
                    // Retire l'indice initial (« posez une question… ») au 1er
                    // tour. :scope > : l'indice est un enfant direct de
                    // #chatLog — les bulles « réfléchit… » (.chatHint dans un
                    // .chatTurn) ne sont jamais touchées ici.
                    var hint = chatLog.querySelector(":scope > .chatHint");
                    if (hint) hint.parentNode.removeChild(hint);
                    chatLog.insertAdjacentHTML("beforeend", chatTurnHtml(role, bodyHtml));
                    chatLog.scrollTop = chatLog.scrollHeight;
                }

                // Retire la bulle « réfléchit… » (toujours le dernier enfant
                // de #chatLog) avant d'afficher le résultat réel.
                function removePendingTurn() {
                    if (!chatLog) return;
                    var pending = chatLog.querySelector(".chatTurn:last-child");
                    if (pending) pending.parentNode.removeChild(pending);
                }

                // Message d'erreur lisible depuis une promesse ajax rejetée.
                // L'ajax d'Emby (ApiClient.fetch, requête non-GET) rejette la
                // Response BRUTE pour tout statut ≥ 400 : String(err) rendait
                // « [object Response] » (vécu 2026-09-05). On lit le statut et,
                // si possible, le corps (JSON d'erreur ServiceStack ou texte)
                // pour afficher « HTTP 500 — message ». Repli : AbortError
                // (timeout page), TypeError (connexion coupée), sinon String().
                function describeError(err) {
                    if (err && typeof err.status === "number" && err.status >= 400) {
                        var base = "HTTP " + err.status;
                        if (typeof err.text !== "function") return Promise.resolve(base);
                        return err.text().then(function (body) {
                            var m = "";
                            try {
                                var j = JSON.parse(body);
                                m = (j && (j.Message || j.message)) ||
                                    (j && j.ResponseStatus &&
                                        (j.ResponseStatus.Message || j.ResponseStatus.ErrorCode)) || "";
                            } catch (e) {
                                if (body) m = String(body).slice(0, 200);
                            }
                            return m ? base + " — " + m : base;
                        }, function () { return base; });
                    }
                    if (err && err.name === "AbortError")
                        return Promise.resolve("Requête trop longue — le LLM n'a pas répondu dans le délai imparti. Réessayez.");
                    if (err instanceof TypeError)
                        return Promise.resolve("Serveur injoignable (connexion interrompue).");
                    return Promise.resolve(String(err == null ? "" : err));
                }

                function sendChat() {
                    if (chatBusy || !chatInput || !chatSendBtn) return;
                    var msg = (chatInput.value || "").trim();
                    if (!msg) return;
                    chatInput.value = "";
                    sendChatText(msg);
                }

                // Cœur de l'envoi (v1.13.9) : partagé par le bouton « Envoyer »
                // et par l'annonce automatique au changement de mode.
                function sendChatText(msg) {
                    if (chatBusy || !chatSendBtn) return;

                    chatBusy = true;
                    chatSendBtn.disabled = true;
                    appendChatTurn("user", "<p>" + esc(msg) + "</p>");
                    chatHistory.push({ role: "user", content: msg });
                    appendChatTurn("assistant",
                        '<p class="chatHint">' + esc(i18n.t("cfg.chat.running")) + '</p>');

                    // Borné côté page aussi (le serveur borne de nouveau) :
                    // on ne re-poste que les derniers tours, sans le message
                    // courant (porté par Message).
                    var payload = {
                        Message: msg,
                        History: chatHistory.slice(0, -1).slice(-40),
                        Session: chatSessionId,
                        // Mode de conversation (contexte déroulant) : envoyé à
                        // CHAQUE tour ; vide = assistant général.
                        Context: chatContextSel ? (chatContextSel.value || "") : ""
                    };

                    ApiClient.ajax({
                        url: ApiClient.getUrl("Plugins/LLMAI/Chat"),
                        type: "POST",
                        data: JSON.stringify(payload),
                        contentType: "application/json",
                        // Borne client : un LLM local lent ne doit pas laisser
                        // la bulle « réfléchit… » indéfiniment (le serveur
                        // voit l'annulation et arrête son agent). AbortError
                        // → message dédié dans describeError().
                        timeout: 240000
                    }).then(function (resp) {
                        return resp.json();
                    }).then(function (data) {
                        chatBusy = false;
                        chatSendBtn.disabled = false;
                        if (!data || data.Enabled === false) {
                            // Désactivé côté serveur : on retire le tour en
                            // attente et le message de l'historique pour
                            // permettre un retry.
                            chatHistory.pop();
                            removePendingTurn();
                            appendChatTurn("assistant",
                                '<p class="chatHint">' + esc(i18n.t("cfg.chat.disabled")) + '</p>');
                            if (chatInput) chatInput.value = msg;
                            return;
                        }
                        if (data.Error) {
                            chatHistory.pop();
                            removePendingTurn();
                            appendChatTurn("assistant",
                                '<p class="chatHint">' + esc(data.Error) + '</p>');
                            if (chatInput) chatInput.value = msg;
                            return;
                        }
                        // Remplace le « réfléchit… » par la réponse réelle.
                        removePendingTurn();
                        var reply = (data.Reply || "").trim();
                        var actionsHtml = "";
                        if (data.Actions && data.Actions.length) {
                            // v1.13.4 : actions réussies du tour (le toast
                            // Emby DisplayMessage n'est pas rendu par le
                            // client web sur cette page — le serveur renvoie
                            // donc les libellés ici). Pas rejoué par
                            // l'historique : l'info reste dans le texte.
                            actionsHtml = '<div class="chatActions">' +
                                data.Actions.map(function (a) {
                                    var t = String(a || "").replace(/^Chat : /, "");
                                    return '<div>🤖 ' + esc(t) + '</div>';
                                }).join("") + '</div>';
                        }
                        // v1.13.9.7 : bouton de secours au niveau du tour —
                        // en mode d'édition, si la réponse ne contient AUCUN
                        // bloc clôturé (LLM en prose, vécu 12:52:55) et que
                        // aucun set n'est déjà en attente, un bouton sous la
                        // réponse permet de demander l'enregistrement sans
                        // taper. Exactement un chemin de sauvegarde visible
                        // par tour : bloc 💾 si clôturé, sinon bouton de tour.
                        var bodyHtml = renderMarkdown(reply, !!(chatContextSel && chatContextSel.value)) + actionsHtml;
                        if (chatContextSel && chatContextSel.value && !data.Pending &&
                            reply.indexOf("```") === -1) {
                            bodyHtml += '<div class="chatSaveTurnRow">' +
                                '<button type="button" class="chatCodeBtn chatCodeSave" title="' +
                                esc(i18n.t("chat.code.save_turn")) + '">💾 ' +
                                esc(i18n.t("chat.code.save_turn_short")) + '</button></div>';
                        }
                        appendChatTurn("assistant", bodyHtml);
                        chatHistory.push({ role: "assistant", content: reply });
                        // Proposition de prompt en attente (v1.13.8) : carte
                        // de diff Approuver/Refuser sous la réponse.
                        if (data.Pending && data.Pending.ActionId) {
                            appendPendingCard(data.Pending);
                        }
                        // Identifiant de session (mémoire de conversation) :
                        // retourné à chaque tour, rejoué au suivant.
                        if (data.Session) chatSessionId = data.Session;
                    }, function (err) {
                        return describeError(err).then(function (errText) {
                            chatBusy = false;
                            chatSendBtn.disabled = false;
                            chatHistory.pop();
                            removePendingTurn();
                            appendChatTurn("assistant",
                                '<p class="chatHint">' + esc(errText) + '</p>');
                            if (chatInput) chatInput.value = msg;
                        });
                    });
                }

                if (chatSendBtn) {
                    chatSendBtn.addEventListener("click", sendChat);
                }

                // Annonce au changement de mode (v1.13.9, pattern llm_core) :
                // la page envoie automatiquement une note [Admin] pour que le
                // LLM annonce le champ visé et le texte courant AVANT toute
                // proposition — l'ancrage read-modify-write dès l'entrée en
                // mode (vécu 2026-09-09 : le LLM a soumis le mauvais champ).
                // Rien pour « Aucun » ; si un tour est en cours, l'annonce
                // est sautée (le mode est de toute façon porté par le tour
                // suivant).
                if (chatContextSel) {
                    chatContextSel.addEventListener("change", function () {
                        var mode = chatContextSel.value || "";
                        if (!mode || chatBusy) return;
                        var opt = chatContextSel.options[chatContextSel.selectedIndex];
                        var labelTxt = (opt && opt.textContent) || mode;
                        sendChatText("[Admin] J'ai sélectionné le mode « " + labelTxt + " ». " +
                            "Avant toute chose : indique clairement sur quel prompt tu travailles " +
                            "(champ concerné) et affiche le texte actuel que tu vas modifier.");
                    });
                }

                // ----------------------------------------------------------------
                //  Approbation de prompts (v1.13.8, two-phase) : la carte de
                //  diff rend l'ancien et le nouveau texte ; le clic n'envoie
                //  QUE l'identifiant d'action (les paramètres de l'écriture
                //  restent côté serveur). Le résultat est annoncé au LLM
                //  dans le fil (note usager dans l'historique).
                // ----------------------------------------------------------------
                // Verrouille une carte de diff périmée (v1.13.9) : une seule
                // proposition est actionnable (la plus récente) — les cartes
                // antérieures encore à l'écran sont marquées, leurs boutons
                // cachés (un clic n'aurait de toute façon donné qu'une erreur
                // serveur « expirée/inconnue » ; vécu 2026-09-09 : deux
                // cartes simultanées, laquelle est approuvée ?).
                function lockStaleCard(card) {
                    if (!card || card.dataset.locked) return;
                    card.dataset.locked = "1";
                    var btns = card.querySelector(".chatPendingButtons");
                    if (btns) btns.hidden = true;
                    var res = card.querySelector(".chatPendingResult");
                    if (res && !res.textContent) {
                        res.textContent = i18n.t("chat.pending.stale");
                        res.style.color = "#9a9a9a";
                        res.hidden = false;
                    }
                }

                function appendPendingCard(pending) {
                    if (!chatLog) return;
                    var previous = chatLog.querySelectorAll(".chatPending");
                    for (var i = 0; i < previous.length; i++) lockStaleCard(previous[i]);
                    var card = document.createElement("div");
                    card.className = "chatPending";
                    card.innerHTML =
                        '<div class="chatPendingTitle">⏳ ' + esc(i18n.t("chat.pending.title")) + '</div>' +
                        (pending.Warning
                            ? '<div class="chatPendingWarn">' + esc(pending.Warning) + '</div>'
                            : '') +
                        '<div class="chatPendingLabel">' + esc(i18n.t("chat.pending.field")) + ' : ' +
                            esc(pending.Label || "?") + '</div>' +
                        '<div class="chatPendingLabel">' + esc(i18n.t("chat.pending.before")) + '</div>' +
                        '<pre class="chatPendingText"></pre>' +
                        '<div class="chatPendingLabel">' + esc(i18n.t("chat.pending.after")) + '</div>' +
                        '<pre class="chatPendingText"></pre>' +
                        '<div class="chatPendingButtons">' +
                            '<button is="emby-button" type="button" class="raised btnApprovePrompt" data-i18n="chat.pending.approve">Approuver</button>' +
                            '<button is="emby-button" type="button" class="raised btnRefusePrompt" data-i18n="chat.pending.refuse">Refuser</button>' +
                        '</div>' +
                        '<div class="chatPendingResult" hidden></div>';
                    var texts = card.querySelectorAll(".chatPendingText");
                    // textContent (jamais innerHTML) : le texte du prompt est
                    // affiché brut, quel que soit son contenu.
                    if (texts[0]) texts[0].textContent = pending.OldText || "(vide)";
                    if (texts[1]) texts[1].textContent = pending.NewText || "";
                    chatLog.appendChild(card);
                    chatLog.scrollTop = chatLog.scrollHeight;

                    var actionId = String(pending.ActionId || "");
                    // makeNote(resp) : la note poussée dans le fil (le LLM la
                    // rejoue au tour suivant). Pour l'approbation, elle
                    // embarque l'indication de test du serveur — le LLM peut
                    // alors OFFRIR d'exécuter le test.
                    function decide(url, doneHtml, makeNote) {
                        ApiClient.ajax({
                            url: ApiClient.getUrl(url, { session: chatSessionId || "" }),
                            type: "POST",
                            data: JSON.stringify({ ActionId: actionId }),
                            contentType: "application/json",
                            dataType: "json"
                        }).then(function (resp) {
                            resp = resp || {};
                            var res = card.querySelector(".chatPendingResult");
                            card.querySelector(".chatPendingButtons").hidden = true;
                            if (res) {
                                if (resp.Ok) {
                                    res.textContent = doneHtml;
                                    // Indication de test selon le champ
                                    // (serveur) : ligne dédiée sous le
                                    // verdict d'approbation.
                                    if (resp.TestHint) {
                                        var hint = document.createElement("div");
                                        hint.className = "chatPendingHint";
                                        hint.textContent = resp.TestHint;
                                        res.appendChild(hint);
                                    }
                                } else {
                                    res.textContent = i18n.t("chat.pending.error") +
                                        " : " + (resp.Error || "?");
                                    res.style.color = "#e57373";
                                }
                                res.hidden = false;
                            }
                            if (resp.Ok) {
                                // Annonce au LLM dans le fil (note usager —
                                // re-postée à chaque tour, le serveur la
                                // rejoue comme un tour user).
                                chatHistory.push({ role: "user", content: makeNote(resp) });
                            }
                        }, function (err) {
                            var res = card.querySelector(".chatPendingResult");
                            card.querySelector(".chatPendingButtons").hidden = true;
                            if (res) {
                                res.textContent = i18n.t("chat.pending.error") +
                                    " : " + (err && err.status ? "HTTP " + err.status : "?");
                                res.style.color = "#e57373";
                                res.hidden = false;
                            }
                        });
                    }

                    var approveBtn = card.querySelector(".btnApprovePrompt");
                    var refuseBtn = card.querySelector(".btnRefusePrompt");
                    if (approveBtn) approveBtn.addEventListener("click", function () {
                        if (chatBusy) return;
                        decide("Plugins/LLMAI/ChatPrompt/Approve",
                            i18n.t("chat.pending.approved"),
                            function (resp) {
                                return "[Admin] J'ai approuvé la modification du prompt « " +
                                    (pending.Label || pending.Field) + " » — elle a été enregistrée " +
                                    "dans la configuration." +
                                    (resp && resp.TestHint ? " Façon de tester : " + resp.TestHint : "");
                            });
                    });
                    if (refuseBtn) refuseBtn.addEventListener("click", function () {
                        if (chatBusy) return;
                        decide("Plugins/LLMAI/ChatPrompt/Refuse",
                            i18n.t("chat.pending.refused"),
                            function () {
                                return "[Admin] J'ai refusé la modification du prompt « " +
                                    (pending.Label || pending.Field) + " » — rien n'a été écrit.";
                            });
                    });
                }
                if (chatInput) {
                    chatInput.addEventListener("keydown", function (e) {
                        if (e.key === "Enter") { e.preventDefault(); sendChat(); }
                    });
                }
                if (chatClearBtn) {
                    chatClearBtn.addEventListener("click", function () {
                        if (chatBusy) return;
                        chatHistory = [];
                        // Oubli côté serveur de la session en cours (mémoire
                        // de conversation) — best-effort, le chat continue
                        // sans même si l'appel échoue.
                        if (chatSessionId) {
                            var forgotten = chatSessionId;
                            chatSessionId = "";
                            ApiClient.ajax({
                                url: ApiClient.getUrl("Plugins/LLMAI/ChatMemory/Forget"),
                                type: "POST",
                                data: JSON.stringify({ Session: forgotten }),
                                contentType: "application/json",
                                dataType: "json"
                            }).then(null, function () { /* silencieux */ });
                        }
                        if (chatLog) {
                            chatLog.innerHTML = '<div class="chatHint">' +
                                esc(i18n.t("cfg.chat.hint")) + '</div>';
                        }
                    });
                }

                // ----------------------------------------------------------------
                //  Mémoire de conversation : bannière « Reprendre »
                // ----------------------------------------------------------------

                // GET /Plugins/LLMAI/ChatMemory : la session la plus récente
                // (id, date, résumé, derniers tours). Une bannière propose de
                // la reprendre ; les derniers échanges verbatim sont rejoués
                // dans le log, le résumé (si prêt) est injecté serveur-side.
                function loadChatMemory() {
                    var banner = view.querySelector("#chatResume");
                    if (!banner) return;
                    ApiClient.ajax({
                        url: ApiClient.getUrl("Plugins/LLMAI/ChatMemory"),
                        type: "GET",
                        dataType: "json"
                    }).then(function (data) {
                        data = data || {};
                        if (data.Error || data.Enabled === false || !data.Current) return;
                        var info = data.Current;
                        if (!info.Id || !(info.Turns > 0)) return;

                        var textEl = view.querySelector("#chatResumeText");
                        if (textEl) {
                            textEl.textContent = i18n.t("chat.resume.banner",
                                info.Date || "?", info.Turns || 0);
                        }
                        banner.hidden = false;

                        var resumeBtn = view.querySelector("#btnResumeChat");
                        if (resumeBtn && !resumeBtn._llmaiWired) {
                            resumeBtn._llmaiWired = true;
                            resumeBtn.addEventListener("click", function () {
                                if (chatBusy) return;
                                chatSessionId = info.Id;
                                chatHistory = [];
                                if (chatLog) chatLog.innerHTML = "";
                                var turns = info.Last || [];
                                for (var i = 0; i < turns.length; i++) {
                                    var t = turns[i];
                                    // NB : DTO serveur PascalCase (Role/Content)
                                    // — l'ancien t.role/t.content rendait le
                                    // replay silencieusement vide.
                                    if (!t || !t.Content) continue;
                                    var role = t.Role === "user" ? "user" : "assistant";
                                    chatHistory.push({ role: role, content: t.Content });
                                    appendChatTurn(role, role === "user"
                                        ? "<p>" + esc(t.Content) + "</p>"
                                        : renderMarkdown(t.Content));
                                }
                                banner.hidden = true;
                            });
                        }
                    }, function () { /* indisponible : chat sans mémoire (fail-open) */ });
                }
                loadChatMemory();
            }); // fin i18nReady().then(...).then(...)
        });
    };
});