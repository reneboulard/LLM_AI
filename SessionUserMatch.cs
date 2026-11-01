using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Session;

namespace LLM_AI
{
    /// <summary>
    /// Appariement session ↔ usager — UNE seule source de vérité (v2.7.4).
    /// </summary>
    /// <remarks>
    /// <para><b>Piège de format (vécu prod 2026-10-29→31, preuve banc
    /// 2026-11-01 sur Emby 4.10.2.0)</b> :
    /// <see cref="SessionInfo.UserId"/> est stocké en forme « N » (Guid
    /// SANS tirets, ex. <c>00c272f6365b4f79866c3db322ed4d1a</c>) alors que
    /// <c>User.Id.ToString()</c> produit la forme « D » (AVEC tirets) — une
    /// comparaison de chaînes stricte entre les deux ne matche JAMAIS. C'est
    /// ce qui a tué silencieusement les toasts d'enregistrement
    /// (<c>TonightRecordingService</c>, v2.4.0→v2.7.3 : « 0 toast(s) » sur
    /// CHAQUE annonce — début et complétion — alors que l'usager regardait
    /// Emby, la ligne de log ne distinguant pas « pas de session » de « pas
    /// de correspondance ») et le mode <c>osd</c> de
    /// <c>ServerRemediation.SendMessageAsync</c> (forme « D » seule).</para>
    /// <para><b>Contrat</b> : comparer <see cref="SessionInfo.UserId"/> à un
    /// usager TOUJOURS par <see cref="Matches"/> (trois formes + casse),
    /// jamais directement contre <c>user.Id.ToString()</c>. La résolution
    /// INVERSE (usager DEPUIS une session) est fiable via
    /// <c>IUserManager.GetUserById(string)</c> (tolérant au format côté
    /// serveur) — voir <c>TonightLoginService.ResolveUser</c> et
    /// <c>PermissionGate.FromSession</c>.</para>
    /// </remarks>
    internal static class SessionUserMatch
    {
        /// <summary>La session appartient-elle à cet usager ? Compare
        /// <see cref="SessionInfo.UserId"/> aux TROIS formes d'id d'usager
        /// (Guid « N » sans tirets — la forme réellement stockée sur
        /// 4.10.x —, forme canonique « D », InternalId numérique),
        /// insensible à la casse. Tolérant aux null (session ou usager) —
        /// ne lève jamais.</summary>
        internal static bool Matches(SessionInfo s, User user)
        {
            var sid = s?.UserId;
            if (string.IsNullOrWhiteSpace(sid) || user == null) return false;
            return string.Equals(sid, user.Id.ToString("N"), StringComparison.OrdinalIgnoreCase)
                || string.Equals(sid, user.Id.ToString(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(sid, user.InternalId.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }
}