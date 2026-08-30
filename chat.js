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

    // Module i18n (FR/EN) : chargé comme ressource plugin via require() sur
    // « web/ConfigurationPage?name=LLMAII18n » (même mécanisme que
    // recommendations.js). `i18n` est renseigné au viewshow avant toute
    // utilisation de t() / translateView().
    var i18n = null;
    var _i18nPromise = null;
    function i18nReady() {
        if (i18n) return Promise.resolve(i18n);
        if (_i18nPromise) return _i18nPromise;
        var url = ApiClient.getUrl("web/ConfigurationPage", { name: "LLMAII18n" });
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
                        History: chatHistory.slice(0, -1).slice(-40)
                    };

                    ApiClient.ajax({
                        url: ApiClient.getUrl("Plugins/LLMAI/Chat"),
                        type: "POST",
                        data: JSON.stringify(payload),
                        contentType: "application/json"
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
                        appendChatTurn("assistant", renderMarkdown(reply));
                        chatHistory.push({ role: "assistant", content: reply });
                    }, function (err) {
                        chatBusy = false;
                        chatSendBtn.disabled = false;
                        chatHistory.pop();
                        removePendingTurn();
                        appendChatTurn("assistant",
                            '<p class="chatHint">' + esc(String(err)) + '</p>');
                        if (chatInput) chatInput.value = msg;
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
                        if (chatLog) {
                            chatLog.innerHTML = '<div class="chatHint">' +
                                esc(i18n.t("cfg.chat.hint")) + '</div>';
                        }
                    });
                }
            }); // fin i18nReady().then(...).then(...)
        });
    };
});