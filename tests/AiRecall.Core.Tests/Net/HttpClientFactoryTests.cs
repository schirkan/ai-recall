using System.Net;
using AiRecall.Core.Net;
using Xunit;

namespace AiRecall.Core.Tests.Net;

/// <summary>
/// Tests für <see cref="HttpClientFactory"/> (Spec 0015).
/// Verifiziert, dass der Default-Handler Default-Credentials aktiviert hat,
/// damit in Corporate-Umgebungen mit NTLM-/Kerberos-Proxy die Auth ohne
/// User-Interaktion läuft.
/// </summary>
public class HttpClientFactoryTests
{
    [Fact]
    public void CreateDefaultHandler_ReturnsHttpClientHandler()
    {
        var handler = HttpClientFactory.CreateDefaultHandler();

        Assert.NotNull(handler);
    }

    [Fact]
    public void CreateDefaultHandler_UseDefaultCredentials_IsTrue()
    {
        var handler = HttpClientFactory.CreateDefaultHandler();

        Assert.True(handler.UseDefaultCredentials,
            "UseDefaultCredentials=true ist Pflicht für NTLM/Kerberos-Zielserver-Auth.");
    }

    [Fact]
    public void CreateDefaultHandler_DefaultProxyCredentials_IsNotNull()
    {
        var handler = HttpClientFactory.CreateDefaultHandler();

        Assert.NotNull(handler.DefaultProxyCredentials);
        // CredentialCache.DefaultCredentials liefert auf Windows genau diese Instanz.
        Assert.Same(CredentialCache.DefaultCredentials, handler.DefaultProxyCredentials);
    }

    [Fact]
    public void CreateDefaultHandler_UseProxy_RemainsTrue()
    {
        var handler = HttpClientFactory.CreateDefaultHandler();

        // UseProxy muss an bleiben, damit System-Proxy-Discovery (WPAD/PAC)
        // und manuelle netsh-winhttp-Settings greifen.
        Assert.True(handler.UseProxy);
    }

    [Fact]
    public void CreateDefaultHandler_EachCallReturnsFreshHandler()
    {
        // Caller muss Handler selbst disposen können — keine Singleton-Semantik.
        var h1 = HttpClientFactory.CreateDefaultHandler();
        var h2 = HttpClientFactory.CreateDefaultHandler();

        Assert.NotSame(h1, h2);
    }
}