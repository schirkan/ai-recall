using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AiRecall.Core.Configuration;

namespace AiRecall.AppReader.Browser;

/// <summary>
/// Minimaler, synchroner Chrome DevTools Protocol (CDP) Client.
///
/// Voraussetzung: Chrome/Edge muss mit <c>--remote-debugging-port=PORT</c>
/// gestartet werden. Funktioniert NICHT out-of-the-box.
///
/// Pro Aufruf:
///   1. HTTP GET <c>{endpoint}/json/version</c> um die WebSocket-URL des Browser-Targets zu bekommen
///      (oder <c>/json</c> für alle offenen Tabs)
///   2. WebSocket-Connect auf die Browser-Target-WS-URL
///   3. Sende <c>Runtime.evaluate("document.documentElement.outerHTML")</c>
///   4. Parse Response, gib URL + HTML zurück
///
/// Bei jedem Fehler (Port nicht offen, Timeout, Browser blockiert CDP) → <c>null</c>.
/// </summary>
internal static class ChromeDevToolsProtocolClient
{
    public sealed record CdpResult(string Url, string Html);

    /// <summary>
    /// Versucht, die aktive Browser-Tab-URL + outerHTML via CDP zu lesen.
    /// Werte stammen aus <see cref="CdpConfig"/>; ohne aktives CDP liefert <c>null</c>.
    /// </summary>
    public static CdpResult? TryReadActivePage(CdpConfig config)
    {
        if (config is null || !config.Enabled) return null;
        return TryReadActivePage(config.Endpoint, config.TimeoutMs);
    }

    /// <summary>
    /// Direkter Aufruf mit explizitem Endpoint + Timeout (für Tests / Smoke-Tests).
    /// </summary>
    public static CdpResult? TryReadActivePage(string endpoint, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        if (timeoutMs <= 0) return null;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };

            // 1. Liste der Targets holen
            var listJson = http.GetStringAsync($"{endpoint.TrimEnd('/')}/json").GetAwaiter().GetResult();
            using var listDoc = JsonDocument.Parse(listJson);
            var firstPage = listDoc.RootElement.EnumerateArray()
                .FirstOrDefault(e =>
                    e.TryGetProperty("type", out var t) &&
                    t.GetString() == "page");

            if (firstPage.ValueKind == JsonValueKind.Undefined) return null;

            var wsUrl = firstPage.TryGetProperty("webSocketDebuggerUrl", out var wsu)
                ? wsu.GetString()
                : null;
            var pageUrl = firstPage.TryGetProperty("url", out var pu)
                ? pu.GetString() ?? ""
                : "";

            if (wsUrl is null) return null;

            // 2. WebSocket verbinden
            using var ws = new ClientWebSocket();
            ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None).GetAwaiter().GetResult();

            // 3. Runtime.evaluate senden
            var cmd = JsonSerializer.Serialize(new
            {
                id = 1,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression = "document.documentElement.outerHTML",
                    returnByValue = true
                }
            });
            var cmdBytes = Encoding.UTF8.GetBytes(cmd);
            ws.SendAsync(cmdBytes, WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();

            // 4. Response lesen (mit Timeout)
            var buffer = new byte[8 * 1024 * 1024]; // 8 MB Puffer für große Pages
            using var cts = new CancellationTokenSource(timeoutMs);
            var receiveTask = ws.ReceiveAsync(buffer, cts.Token);
            receiveTask.Wait(cts.Token);
            var result = receiveTask.GetAwaiter().GetResult();
            if (result.MessageType != WebSocketMessageType.Text) return null;

            var response = Encoding.UTF8.GetString(buffer, 0, result.Count);
            using var respDoc = JsonDocument.Parse(response);

            var html = respDoc.RootElement
                .GetProperty("result")
                .GetProperty("result")
                .GetProperty("value")
                .GetString();

            if (html is null) return null;
            return new CdpResult(pageUrl, html);
        }
        catch
        {
            return null;
        }
    }
}