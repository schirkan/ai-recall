using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AiRecall.AppReader.Teams;

/// <summary>
/// CDP-Implementation fuer Teams App-Reader (Spec 0011).
/// Verbindet sich per WebSocket mit dem Chromium DevTools Protocol, das
/// Modern Teams als Electron-App automatisch exponiert (wenn mit
/// <c>--remote-debugging-port=PORT</c> gestartet).
///
/// <para>Pattern: analog Browser-Reader Iter. 3 (Spec 0004). Verwendet
/// ausschliesslich .NET-8-builtin <see cref="ClientWebSocket"/>
/// (kein NuGet-Paket noetig).</para>
///
/// <para>Bei Fehlern (Endpoint nicht erreichbar, WebSocket-Fehler, CDP-RPC-Error)
/// wird <c>null</c> zurueckgegeben. Caller fallen auf UIA-Pfad zurueck.</para>
/// </summary>
internal static class TeamsCdpReader
{
    /// <summary>
    /// HTTP-Discovery: GET /json/version auf dem konfigurierten Endpoint.
    /// Liefert <c>true</c>, wenn der Endpoint erreichbar ist (CDP-Server laeuft).
    /// </summary>
    public static async Task<bool> TryFindEndpointAsync(string endpoint, int timeoutMs)
    {
        if (string.IsNullOrEmpty(endpoint)) return false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            var versionUrl = endpoint.TrimEnd('/') + "/json/version";
            var resp = await http.GetAsync(versionUrl).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Hauptpfad: aktiv-Chat via CDP lesen.
    /// <list type="number">
    ///   <item>GET <c>/json/list</c> → Targets-Liste</item>
    ///   <item>Filter auf <c>type == "page"</c></item>
    ///   <item>WebSocket-Connect zum ersten Match</item>
    ///   <item><see cref="RuntimeEvaluateAsync"/> fuer Title + Body + Senders</item>
    /// </list>
    /// </summary>
    public static async Task<TeamsContent?> TryGetActiveChatAsync(string endpoint, int timeoutMs)
    {
        if (string.IsNullOrEmpty(endpoint)) return null;

        CdpTarget? target;
        try
        {
            target = await FindTeamsTargetAsync(endpoint, timeoutMs).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        if (target == null) return null;

        try
        {
            using var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(timeoutMs);

            await ws.ConnectAsync(new Uri(target.WebSocketDebuggerUrl), cts.Token).ConfigureAwait(false);

            // Title via document.title
            var titleJson = await RuntimeEvaluateAsync(ws, cts.Token, "document.title").ConfigureAwait(false);
            var title = ParseStringResult(titleJson) ?? string.Empty;

            // Body via document.body.innerText -- Plain-Text-Extraktion
            var bodyJson = await RuntimeEvaluateAsync(ws, cts.Token,
                "document.body.innerText").ConfigureAwait(false);
            var bodyMd = ConvertBodyToMarkdown(ParseStringResult(bodyJson) ?? string.Empty);

            // Sender-Heuristik via DOM-Daten-Attribute (wenn vorhanden)
            var senderJson = await RuntimeEvaluateAsync(ws, cts.Token,
                BuildSenderExtractionScript()).ConfigureAwait(false);
            var senders = ParseStringArrayResult(senderJson);

            return new TeamsContent(
                BodyMarkdown: bodyMd,
                SenderSet: senders,
                Source: "teams-cdp")
            { ChatTitleHint = title };
        }
        catch
        {
            return null;
        }
    }

    // ============================================================================
    // CDP-Discovery (HTTP-Phase)
    // ============================================================================

    private static async Task<CdpTarget?> FindTeamsTargetAsync(string endpoint, int timeoutMs)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
        var listUrl = endpoint.TrimEnd('/') + "/json/list";
        using var stream = await http.GetStreamAsync(listUrl).ConfigureAwait(false);
        var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var type = el.GetProperty("type").GetString();
            if (type != "page") continue;

            var title = el.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var wsUrl = el.GetProperty("webSocketDebuggerUrl").GetString() ?? string.Empty;

            // Modern Teams: window-title enthaelt oft "Microsoft Teams" oder Substrings,
            // aber Electron setzt Title oft erst spaet. Wir nehmen jeden page-Type.
            if (string.IsNullOrEmpty(wsUrl)) continue;
            return new CdpTarget(title, wsUrl);
        }

        return null;
    }

    // ============================================================================
    // CDP-RPC (WebSocket-Phase)
    // ============================================================================

    /// <summary>
    /// Single-RPC-Call via WebSocket: <c>Runtime.evaluate(expression)</c>.
    /// Liefert die JSON-Antwort als String.
    /// </summary>
    private static async Task<string?> RuntimeEvaluateAsync(
        ClientWebSocket ws,
        CancellationToken ct,
        string expression)
    {
        var id = Interlocked.Increment(ref _msgId);
        var req = new
        {
            id,
            method = "Runtime.evaluate",
            @params = new { expression, returnByValue = true, awaitPromise = false }
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(req);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);

        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }

        var responseBytes = ms.ToArray();
        var responseText = Encoding.UTF8.GetString(responseBytes);
        return responseText;
    }

    private static int _msgId;

    // ============================================================================
    // Response-Parsing
    // ============================================================================

    private static string? ParseStringResult(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var doc = JsonDocument.Parse(json);
            return doc.RootElement
                .GetProperty("result")
                .GetProperty("result")
                .GetProperty("value")
                .GetString();
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyCollection<string> ParseStringArrayResult(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<string>();
        try
        {
            var doc = JsonDocument.Parse(json);
            var value = doc.RootElement
                .GetProperty("result")
                .GetProperty("result")
                .GetProperty("value");

            if (value.ValueKind != JsonValueKind.Array) return Array.Empty<string>();

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var el in value.EnumerateArray())
            {
                var s = el.GetString();
                if (!string.IsNullOrEmpty(s)) set.Add(s);
            }
            return set;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // ============================================================================
    // Body-Conversion + Sender-Extraction
    // ============================================================================

    /// <summary>
    /// Plain-Text -> Markdown. Minimal-Conversion: nur Tabs ersetzen,
    /// doppelte Newlines als Message-Boundaries beibehalten.
    /// </summary>
    private static string ConvertBodyToMarkdown(string plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText)) return string.Empty;
        return plainText.TrimEnd().Replace("\t", "    ");
    }

    /// <summary>
    /// JS-Snippet fuer CDP Runtime.evaluate: liest aus dem Teams-DOM
    /// die sichtbaren Sender-Namen (heuristisch -- Teams-spezifische
    /// Selektoren sind versionsabhaengig).
    /// </summary>
    private static string BuildSenderExtractionScript()
    {
        return """
            (function() {
                var selectors = [
                    '[data-tid="message-author-name"]',
                    '.ui-chat__message__author',
                    '[class*="author"]',
                ];
                var seen = {};
                for (var i = 0; i < selectors.length; i++) {
                    var elList = document.querySelectorAll(selectors[i]);
                    for (var j = 0; j < elList.length; j++) {
                        var txt = (elList[j].innerText || '').trim();
                        if (txt && txt.length < 50) seen[txt] = true;
                    }
                }
                return Object.keys(seen);
            })()
            """;
    }

    private sealed record CdpTarget(string Title, string WebSocketDebuggerUrl);
}
