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
    function renderMarkdown(md) {
        var lines = String(md == null ? "" : md).replace(/\r\n?/g, "\n").split("\n");
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

                    chatBusy = true;
                    chatSendBtn.disabled = true;
                    chatInput.value = "";
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
                        Session: chatSessionId
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
                        appendChatTurn("assistant", renderMarkdown(reply) + actionsHtml);
                        chatHistory.push({ role: "assistant", content: reply });
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
                                    if (!t || !t.content) continue;
                                    var role = t.role === "user" ? "user" : "assistant";
                                    chatHistory.push({ role: role, content: t.content });
                                    appendChatTurn(role, role === "user"
                                        ? "<p>" + esc(t.content) + "</p>"
                                        : renderMarkdown(t.content));
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