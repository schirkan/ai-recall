using System.Net;
using AiRecall.Core.Configuration;

namespace AiRecall.Core.Tessdata;

/// <summary>
/// Verwaltet das tessdata-Verzeichnis für Tesseract-OCR (Spec 0012).
/// Findet fehlende Sprachpakete und lädt sie aus dem
/// <c>tesseract-ocr/tessdata_fast</c>-GitHub-Repository herunter.
///
/// **Status:** Skeleton-Stub v0.1 (Bug-Bash 2026-07-06).
/// Auto-Download-Logik ist minimal gehalten, Retry + Progress-Reporting
/// aber voll testbar. Spec-Detail-Iteration folgt in eigenem Cluster.
/// </summary>
public sealed class TessdataManager
{
    /// <summary>
    /// Default Base-URL für tessdata-Downloads.
    /// <c>tessdata_fast</c> ist die kleinere Variante (Apache-2.0, ca. 1-4 MB pro Sprache).
    /// </summary>
    public const string DefaultBaseUrl =
        "https://github.com/tesseract-ocr/tessdata_fast/raw/main/";

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    /// <summary>Default-Konstruktor: verwendet <see cref="DefaultBaseUrl"/> und einen neuen <see cref="HttpClient"/>.</summary>
    public TessdataManager() : this(new HttpClientHandler(), DefaultBaseUrl)
    {
    }

    /// <summary>DI-Konstruktor für Tests (Mock-Handler + eigene BaseUrl).</summary>
    /// <param name="handler">HTTP-Handler (in Production: <c>new HttpClientHandler()</c>).</param>
    /// <param name="baseUrl">Base-URL für Downloads.</param>
    public TessdataManager(HttpMessageHandler handler, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(baseUrl);
        _http = new HttpClient(handler, disposeHandler: false);
        _baseUrl = baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
    }

    /// <summary>
    /// Liefert alle konfigurierten Sprachen, deren <c>*.traineddata</c>-Datei
    /// im Ziel-Verzeichnis fehlt. Berücksichtigt:
    /// <list type="bullet">
    ///   <item>Engine != "tesseract" → leer (andere Engines haben kein tessdata).</item>
    ///   <item>AutoDownloadTessdata == false → leer (User hat deaktiviert).</item>
    ///   <item>"osd" wird ignoriert (Script-Code, nicht per First-Run-Download).</item>
    /// </list>
    /// </summary>
    public IReadOnlyList<MissingLanguage> FindMissingLanguages(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!string.Equals(config.Ocr.Engine, "tesseract", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<MissingLanguage>();

        if (!config.Ocr.AutoDownloadTessdata)
            return Array.Empty<MissingLanguage>();

        var targetDir = ResolveTargetDirectory(config.Ocr);
        var missing = new List<MissingLanguage>();
        foreach (var lang in config.Ocr.Languages)
        {
            // osd (Orientation/Script Detection) wird ignoriert — Script-Code, nicht Sprachdatei.
            if (string.Equals(lang, "osd", StringComparison.OrdinalIgnoreCase))
                continue;

            var fileName = $"{lang}.traineddata";
            var fullPath = Path.Combine(targetDir, fileName);
            if (!File.Exists(fullPath))
                missing.Add(new MissingLanguage(lang, fileName));
        }
        return missing;
    }

    /// <summary>
    /// Lädt die angegebenen Sprachpakete in <paramref name="targetDir"/> herunter.
    /// Implementiert 3-Retry-Logik für 5xx-Antworten; 4xx-Antworten werfen
    /// sofort <see cref="TessdataDownloadException"/>.
    /// </summary>
    public async Task DownloadAsync(
        IEnumerable<string> languages,
        string targetDir,
        IProgress<TessdataDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentNullException.ThrowIfNull(targetDir);

        Directory.CreateDirectory(targetDir);
        var langs = languages.ToList();
        long totalBytes = 0;
        int completed = 0;

        progress?.Report(new TessdataDownloadProgress(
            CompletedCount: 0,
            TotalCount: langs.Count,
            TotalBytesReceived: 0,
            CurrentLanguage: null));

        foreach (var lang in langs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = $"{lang}.traineddata";
            var targetPath = Path.Combine(targetDir, fileName);

            progress?.Report(new TessdataDownloadProgress(
                CompletedCount: completed,
                TotalCount: langs.Count,
                TotalBytesReceived: totalBytes,
                CurrentLanguage: lang));

            const int maxAttempts = 3;
            HttpResponseMessage? lastResponse = null;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                lastResponse?.Dispose();
                lastResponse = await _http.GetAsync(
                    _baseUrl + fileName,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (lastResponse.IsSuccessStatusCode)
                    break;

                // 4xx: kein Retry
                var statusCode = lastResponse.StatusCode;
                if ((int)statusCode >= 400 && (int)statusCode < 500)
                {
                    throw new TessdataDownloadException(
                        $"tessdata download for '{lang}' failed: HTTP {(int)statusCode}",
                        statusCode);
                }

                // 5xx: retry (nach kurzem Backoff)
                if (attempt < maxAttempts)
                {
                    try { await Task.Delay(200 * attempt, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { lastResponse.Dispose(); throw; }
                }
            }

            if (lastResponse is null || !lastResponse.IsSuccessStatusCode)
            {
                throw new TessdataDownloadException(
                    $"tessdata download for '{lang}' failed after {maxAttempts} attempts: HTTP {(int?)lastResponse?.StatusCode}",
                    lastResponse?.StatusCode ?? HttpStatusCode.ServiceUnavailable);
            }

            // Download Body und schreiben.
            var bytes = await lastResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken).ConfigureAwait(false);
            totalBytes += bytes.LongLength;
            completed++;
            lastResponse.Dispose();

            progress?.Report(new TessdataDownloadProgress(
                CompletedCount: completed,
                TotalCount: langs.Count,
                TotalBytesReceived: totalBytes,
                CurrentLanguage: null));
        }
    }

    /// <summary>
    /// Ermittelt das Ziel-Verzeichnis für tessdata. Verwendet
    /// <c>OcrConfig.TessDataPath</c>, falls das Verzeichnis existiert.
    /// Sonst Fallback auf <c>%LOCALAPPDATA%\AiRecall\tessdata</c>.
    /// </summary>
    public string ResolveTargetDirectory(OcrConfig ocrConfig)
    {
        ArgumentNullException.ThrowIfNull(ocrConfig);

        var configured = ocrConfig.TessDataPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // Relativ zu AppContext.BaseDirectory (analog zu AiRecall-OcrEngine-Pfad-Logik)
            var resolved = Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(AppContext.BaseDirectory, configured);
            if (Directory.Exists(resolved))
                return resolved;
        }

        // Fallback: %LOCALAPPDATA%\AiRecall\tessdata
        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AiRecall", "tessdata");
        Directory.CreateDirectory(fallback);
        return fallback;
    }
}