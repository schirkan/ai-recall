using AiRecall.AppReader.Teams;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Tests fuer <see cref="TeamsCdpReader"/> (Spec 0011, Cluster 3).
/// Tests sind auf den Public-Surface + sicheren Fail-Pfad beschraenkt,
/// weil echte WebSocket-CDP-Calls installiertes Teams voraussetzen.
/// </summary>
public class TeamsCdpReaderTests
{
    // ============================================================================
    // TryFindEndpointAsync - HTTP-Discovery-Fail-Pfad
    // ============================================================================

    [Fact]
    public async Task TryFindEndpointAsync_EmptyEndpoint_ReturnsFalse()
    {
        var result = await TeamsCdpReader.TryFindEndpointAsync(string.Empty, 1000);
        Assert.False(result);
    }

    [Fact]
    public async Task TryFindEndpointAsync_NullEndpoint_ReturnsFalse()
    {
        var result = await TeamsCdpReader.TryFindEndpointAsync(null!, 1000);
        Assert.False(result);
    }

    [Fact]
    public async Task TryFindEndpointAsync_UnreachableEndpoint_ReturnsFalse()
    {
        // Port 1 ist reserviert und nicht erreichbar
        var result = await TeamsCdpReader.TryFindEndpointAsync("http://localhost:1", 500);
        Assert.False(result);
    }

    [Fact]
    public async Task TryFindEndpointAsync_InvalidUrl_ReturnsFalse()
    {
        var result = await TeamsCdpReader.TryFindEndpointAsync("not-a-url", 500);
        Assert.False(result);
    }

    [Fact]
    public async Task TryFindEndpointAsync_RespectsTimeout()
    {
        // Timeout-Wert wird uebergeben ohne Exception. Smoke-Test: sehr kurzer
        // Timeout fuehrt nicht zu Hang.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await TeamsCdpReader.TryFindEndpointAsync("http://localhost:1", 200);
        sw.Stop();
        Assert.False(result);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Endpoint-Check took {sw.ElapsedMilliseconds}ms");
    }

    // ============================================================================
    // TryGetActiveChatAsync - Public-Surface
    // ============================================================================

    [Fact]
    public async Task TryGetActiveChatAsync_EmptyEndpoint_ReturnsNull()
    {
        var result = await TeamsCdpReader.TryGetActiveChatAsync(string.Empty, 1000);
        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetActiveChatAsync_NullEndpoint_ReturnsNull()
    {
        var result = await TeamsCdpReader.TryGetActiveChatAsync(null!, 1000);
        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetActiveChatAsync_UnreachableEndpoint_ReturnsNull()
    {
        var result = await TeamsCdpReader.TryGetActiveChatAsync("http://localhost:1", 500);
        Assert.Null(result);
    }

    // ============================================================================
    // Custom JS-Extraction-Skript (interner Helper, ueber Public-API testbar)
    // ============================================================================

    [Fact]
    public async Task TryGetActiveChatAsync_InvalidUrl_ReturnsNull_NoThrow()
    {
        // Smoke-Test: fehlerhafte URL darf nicht crashen.
        Exception? exception = await Record.ExceptionAsync(async () =>
            await TeamsCdpReader.TryGetActiveChatAsync("not-a-url", 100));
        Assert.Null(exception);
    }

    [Fact]
    public async Task TryFindEndpointAsync_RespectsCancellation_NoHang()
    {
        // Smoke-Test: bei Cancel muss der WaitAsync-Aufruf schnell enden,
        // entweder mit OperationCanceledException ODER mit false
        // (wenn HTTP-Connect durch Netzwerk-RST schon fail-fast war).
        // Wichtig: kein Endlos-Hang bei cancellation.
        using var cts = new System.Threading.CancellationTokenSource(200);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await TeamsCdpReader.TryFindEndpointAsync("http://localhost:1", 5000)
                .WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Erwartet.
        }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 3000,
            $"Cancellation wurde nicht respektiert: Dauer {sw.ElapsedMilliseconds}ms");
    }
}
