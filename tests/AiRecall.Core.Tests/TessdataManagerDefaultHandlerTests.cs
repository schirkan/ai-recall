using System.Net;
using System.Net.Http;
using System.Reflection;
using AiRecall.Core.Tessdata;
using Xunit;

namespace AiRecall.Core.Tests;

/// <summary>
/// Tests, die nachweisen, dass der Default-Konstruktor von
/// <see cref="TessdataManager"/> tatsächlich einen Handler mit Default-Credentials
/// verwendet (Spec 0015).
///
/// Der HttpClient selbst hat keinen öffentlichen Zugriff auf den Handler;
/// Reflection auf <see cref="HttpMessageInvoker"/>._handler ist die einzige
/// Möglichkeit, den injizierten Handler-Stack zu verifizieren.
/// </summary>
public class TessdataManagerDefaultHandlerTests
{
    [Fact]
    public void DefaultConstructor_HandlerHasUseDefaultCredentials()
    {
        var manager = new TessdataManager();
        var handler = ExtractHandler(manager);

        Assert.True(handler.UseDefaultCredentials,
            "Default-Konstruktor von TessdataManager muss einen Handler mit "
            + "UseDefaultCredentials=true erzeugen (Spec 0015).");
    }

    [Fact]
    public void DefaultConstructor_HandlerHasDefaultProxyCredentials()
    {
        var manager = new TessdataManager();
        var handler = ExtractHandler(manager);

        Assert.NotNull(handler.DefaultProxyCredentials);
        Assert.Same(CredentialCache.DefaultCredentials, handler.DefaultProxyCredentials);
    }

    /// <summary>
    /// Holt den internen <see cref="HttpClientHandler"/> aus einem
    /// <see cref="TessdataManager"/> via Reflection.
    /// </summary>
    private static HttpClientHandler ExtractHandler(TessdataManager manager)
    {
        // TessdataManager._http (privates readonly HttpClient-Feld)
        var httpField = typeof(TessdataManager).GetField(
            "_http", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(httpField);
        var http = (HttpClient)httpField.GetValue(manager)!;

        // HttpClient erbt von HttpMessageInvoker, das einen privaten
        // _handler (HttpMessageHandler) hält.
        var invokerField = typeof(HttpMessageInvoker).GetField(
            "_handler", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(invokerField);
        var handler = (HttpMessageHandler)invokerField.GetValue(http)!;

        var typed = Assert.IsType<HttpClientHandler>(handler);
        return typed;
    }
}