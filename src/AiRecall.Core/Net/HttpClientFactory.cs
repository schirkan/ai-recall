using System.Net;

namespace AiRecall.Core.Net;

/// <summary>
/// Factory für <see cref="HttpClientHandler"/> mit AiRecall-Default-Konfiguration
/// (Spec 0015 — Default-Credentials für HTTP-Downloads).
///
/// <para>
/// In Corporate-Netzwerken mit NTLM-/Kerberos-Proxy versucht der HttpClient ohne
/// Credentials zunächst eine anonyme Anfrage, scheitert dann mit
/// <c>HTTP 407 Proxy Authentication Required</c> und bricht ab.
/// </para>
///
/// <para>
/// Dieser Helper aktiviert
/// <list type="bullet">
///   <item><see cref="HttpClientHandler.UseDefaultCredentials"/> — schickt die
///         Windows-Anmeldeinformationen für die Ziel-Server-Auth mit.</item>
///   <item><see cref="HttpClientHandler.DefaultProxyCredentials"/> — schickt
///         Credentials speziell für den Proxy (NTLM/Kerberos-Verhandlung).</item>
/// </list>
/// <see cref="HttpClientHandler.UseProxy"/> bleibt auf dem .NET-Default
/// <c>true</c>, damit System-Proxy-Discovery (WPAD/PAC, manuelle
/// <c>netsh winhttp</c>-Settings) weiterhin aktiv ist.
/// </para>
///
/// <para>
/// <b>Wichtig:</b> API-Key-Auth (Deepgram, Azure, GitHub-Token) wird via
/// <c>Authorization</c>-Header gesetzt und hat Vorrang vor Default-Credentials.
/// Bei localhost-Calls (CDP) ist Default-Credentials zwar harmlos, aber unnötig.
/// </para>
/// </summary>
internal static class HttpClientFactory
{
    /// <summary>
    /// Erzeugt einen <see cref="HttpClientHandler"/> mit aktivierten
    /// Default-Credentials (Zielserver + Proxy).
    /// </summary>
    /// <returns>Frischer Handler. Caller bestimmt, ob er ihn disposen will.</returns>
    public static HttpClientHandler CreateDefaultHandler()
    {
        return new HttpClientHandler
        {
            // UseProxy = true ist bereits Default — System-Proxy-Discovery bleibt an.
            // Explizit gesetzt für Klarheit/Doku:
            UseProxy = true,
            UseDefaultCredentials = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        };
    }
}