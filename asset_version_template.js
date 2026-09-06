// TEMPLATE du module de cache-busting — NE PAS ÉDITER asset_version.js :
// ce fichier est généré à chaque build par la cible GenerateAssetVersionJs
// (LLM_AI.csproj) qui remplace __PLUGIN_VERSION__ par $(Version) du csproj,
// puis est embarqué comme ressource LLM_AI.asset_version.js (servi sur
// web/ConfigurationPage?name=LLMAIAssetVersion, chargé via require() par
// config.js / recommendations.js / chat.js — même pattern que LLMAII18n).
//
// PROBLÈME : Emby sert les ressources web/ConfigurationPage avec
// Cache-Control: public, sans max-age ni Last-Modified — le navigateur peut
// resservir un JS périmé depuis son cache disque sans jamais revalider
// (d'où le « hard reset » (Ctrl+Shift+R) nécessaire après chaque déploiement
// de DLL). L'ETag présent est correct (il suit l'assembly) mais rien ne
// force le navigateur à l'envoyer ; il n'est en outre PAS dérivé du contenu
// (même ETag pour des ressources de contenus différents, v1.13.4.3).
//
// SOLUTION (1re couche, v1.13.4.3) : config.js / chat.js /
// recommendations.js chargent les ressources dont ILS ont le contrôle
// (LLMAII18n, LLMAIAssetVersion, LLMAIBg) avec un paramètre ?v= de bust PAR
// SESSION navigateur (jeton sessionStorage) → lecture réseau garantie à
// chaque nouvelle session, sans dépendre de l'ETag.
// SOLUTION (couche de recours, ce module) : version du build QUI L'A GÉNÉRÉ
// + logique de vérification pour les ressources chargées PAR LE DASHBOARD
// (config.html, config.js — URLs getConfigurationResourceUrl, hors de notre
// contrôle). La page interroge GET /Plugins/LLMAI/Version (route plugin
// authentifiée standard) : si la version serveur CHANGE au cours d'une
// session, on réécrit les entrées de cache HTTP de TOUTES nos pages
// (fetch cache:'reload' = requête réseau inconditionnelle qui REMPLACE
// l'entrée en cache), puis on recharge le dashboard : la passe suivante
// charge la version fraîche.
define([], function () {
    "use strict";

    // Stamp du build (généré) — source de vérité : <Version> du .csproj.
    var VERSION = "__PLUGIN_VERSION__";

    // Toutes les ressources plugin servies par web/ConfigurationPage
    // (voir GetPages dans Plugin.cs) : le fetch cache:'reload' doit couvrir
    // HTML + JS + i18n + fond + ce module, sinon la recharge retombe sur une
    // ressource sœur périmée.
    var PAGE_NAMES = [
        "LLMAIConfigPage", "LLMAIConfigPageJS", "LLMAII18n",
        "LLMAIRecommendationsPage", "LLMAIRecommendationsPageJS",
        "LLMAIChatPage", "LLMAIChatPageJS", "LLMAIBg", "LLMAIAssetVersion"
    ];

    // Garde anti-boucle : une seule passe d'auto-correction par version
    // serveur et par session. La valeur (version serveur) fait expirer le
    // drapeau dès qu'une NOUVELLE version est déployée.
    var RELOADED_FLAG = "LLMAI.versionReloaded";

    // Compare la version serveur mémorisée à celle du serveur. Retourne une
    // promesse : false = rien à faire (ou vérification impossible —
    // silencieux), "reloading" = page sur le point d'être rechargée.
    // Best-effort : AUCUNE erreur ne doit perturber le rendu de la page hôte.
    function checkForUpdate(apiClient, opts) {
        opts = opts || {};
        var req;
        try {
            req = apiClient.ajax({
                url: apiClient.getUrl("Plugins/LLMAI/Version"),
                type: "GET",
                dataType: "json"
            });
        } catch (e) { return Promise.resolve(false); }
        return req.then(function (srv) {
            var serverVersion = srv && srv.Version ? String(srv.Version) : "";
            // Pas de version côté serveur (endpoint absent/ancien, erreur) :
            // on ne fait rien — la page reste pleinement fonctionnelle.
            if (!serverVersion) return false;

            // Détection par version serveur mémorisée (v1.13.4.3) : depuis le
            // bust ?v= par session, ce module est lui-même toujours frais —
            // comparer VERSION (gravée) au serveur ne détecterait plus rien.
            // On mémorise la version serveur vue : un déploiement pendant la
            // session (ou entre deux sessions) fait changer serverVersion →
            // une passe de réécriture des caches + reload.
            var seen;
            try {
                seen = sessionStorage.getItem(RELOADED_FLAG); // null à la 1re visite
                if (seen == null) {
                    // Première mémorisation : on enregistre sans recharger —
                    // évite un reload inutile à l'ouverture d'une session.
                    sessionStorage.setItem(RELOADED_FLAG, serverVersion);
                    return false;
                }
            } catch (e) {
                // sessionStorage indisponible : impossible de mémoriser → on
                // renonce au heal, sinon window.location.reload() bouclerait.
                return false;
            }

            if (seen === serverVersion) return false;

            // Réécriture des entrées de cache HTTP de toutes nos pages.
            // web/ConfigurationPage répond à un GET simple, sans auth (le
            // require() du dashboard fait de même) — pas de token ici.
            var refreshes = PAGE_NAMES.map(function (n) {
                var u = apiClient.getUrl("web/ConfigurationPage", { name: n });
                return fetch(u, { cache: "reload" }).catch(function () {});
            });
            return Promise.all(refreshes).then(function () {
                try { sessionStorage.setItem(RELOADED_FLAG, serverVersion); } catch (e) {}
                if (opts.noReload) return "reloading"; // test : pas de reload
                window.location.reload();
                return "reloading";
            });
        }, function () { return false; });
    }

    return { version: VERSION, checkForUpdate: checkForUpdate };
});