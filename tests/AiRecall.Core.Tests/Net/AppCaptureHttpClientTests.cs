using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiRecall.Core.Net;
using Xunit;

namespace AiRecall.Core.Tests.Net;

/// <summary>
/// Tests fuer <see cref="AppCaptureHttpClient"/> (Spec 0017 — Martin-Direktive 2026-07-13 22:46).
/// Verwendet einen lokalen HttpListener als Mini-Test-Server, weil
/// HttpClientFactory-Tests nur die Handler-Properties pruefen (kein
/// echtes HTTP noetig), AppCaptureHttpClient aber echte HTTP-Roundtrips
/// macht.
/// </summary>
public class AppCaptureHttpClientTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _baseUrl;
    private readonly CancellationTokenSource _cts = new();

    public AppCaptureHttpClientTests()
    {
        // Zufaelligen freien Port finden (Loopback), damit parallel laufende
        // Test-Klassen sich nicht in die Quere kommen.
        var port = GetFreeLoopbackPort();
        _baseUrl = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUrl);
        _listener.Start();
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { /* ignore */ }
        try { _listener.Close(); } catch { /* ignore */ }
        try { _cts.Cancel(); } catch { /* ignore */ }
        _cts.Dispose();
    }

    private static int GetFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// Antwortet auf den naechsten Request mit dem uebergebenen Body +
    /// Content-Type. Blockiert nicht (fire-and-forget Task), damit der
    /// Test selbst gegen den Listener racen kann.
    /// </summary>
    private Task ServeNextAsync(byte[] body, string contentType = "text/plain")
    {
        return Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token)
                .ConfigureAwait(false);
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body, _cts.Token)
                .ConfigureAwait(false);
            ctx.Response.Close();
        }, _cts.Token);
    }

    // --- DownloadAsync ---

    [Fact]
    public async Task DownloadAsync_ValidUrl_ReturnsExpectedBytes()
    {
        var payload = Encoding.UTF8.GetBytes("hello from test server");
        var serverTask = ServeNextAsync(payload);

        var result = await AppCaptureHttpClient.DownloadAsync(_baseUrl + "ping", _cts.Token);

        Assert.Equal(payload, result);
        await serverTask;
    }

    [Fact]
    public async Task DownloadAsync_EmptyUrl_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            AppCaptureHttpClient.DownloadAsync(string.Empty, _cts.Token));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            AppCaptureHttpClient.DownloadAsync("   ", _cts.Token));
    }

    // --- DownloadToFileAsync ---

    [Fact]
    public async Task DownloadToFileAsync_ValidUrl_WritesExpectedContent()
    {
        var payload = Encoding.UTF8.GetBytes("file payload bytes");
        var serverTask = ServeNextAsync(payload);

        var tempFile = Path.Combine(
            Path.GetTempPath(),
            $"appcapture-test-{Guid.NewGuid():N}.bin");

        try
        {
            await AppCaptureHttpClient.DownloadToFileAsync(
                _baseUrl + "asset", tempFile, _cts.Token);

            Assert.True(File.Exists(tempFile));
            Assert.Equal(payload, await File.ReadAllBytesAsync(tempFile, _cts.Token));
            await serverTask;
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { /* ignore */ }
            }
        }
    }
}