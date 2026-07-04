using System.Text;
using System.Threading.Channels;
using AiRecall.Core.Configuration;
using AiRecall.Core.Persistence;
using Serilog;

namespace AiRecall.Conversion;

/// <summary>
/// Async Conversion-Worker (Spec 0007).
///
/// Liest Capture-MD-Pfade aus einem in-process <see cref="Channel{T}"/>,
/// fuehrt die Document-Konvertierung + (spaeter) OCR durch und schreibt
/// das Ergebnis als <c>*.content.md</c>. Updated das Frontmatter der
/// Capture-MD-Datei mit Status-Feldern.
///
/// Lifecycle:
///   ctor  -> Background-Task startet automatisch
///   Stop  -> Channel schliessen, Task sauber beenden
///   Dispose -> ruft Stop
///
/// Idempotent: Start/Stop koennen mehrfach aufgerufen werden.
///
/// Spec 0007 v0.4 (Martin 2026-07-04 20:01):
///   - Pandoc raus, Performance wichtiger
///   - Channel-Queue statt FileSystemWatcher
///   - kein Legacy-Handling
/// </summary>
public sealed class ConversionWorker : IDisposable
{
    private readonly Channel<string> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _workerTask;
    private readonly ILogger _logger;
    private readonly AppConfig _config;
    private readonly IOcrEngine _ocrEngine;

    private int _pendingCount;
    private int _completedCount;
    private int _failedCount;
    private int _ocrSkippedCount;
    private int _partialCount;
    private int _ocrErrorCount;
    private bool _disposed;

    /// <summary>Anzahl Captures in der Channel-Queue (noch nicht verarbeitet).</summary>
    public int PendingCount => _pendingCount;

    /// <summary>Anzahl erfolgreich konvertierter Captures.</summary>
    public int CompletedCount => _completedCount;

    /// <summary>Anzahl komplett fehlgeschlagener Konvertierungen.</summary>
    public int FailedCount => _failedCount;

    /// <summary>Anzahl Captures ohne OCR-Screenshot (Skip).</summary>
    public int OcrSkippedCount => _ocrSkippedCount;

    /// <summary>Anzahl Captures mit partieller Konvertierung (ein Schritt failed, anderer ok).</summary>
    public int PartialCount => _partialCount;

    /// <summary>Anzahl OCR-Fehler (Tesseract nicht verfuegbar, leeres Bild, etc.).</summary>
    public int OcrErrorCount => _ocrErrorCount;

    public ConversionWorker(AppConfig config, ILogger logger, IOcrEngine? ocrEngine = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ocrEngine = ocrEngine ?? new NullOcrEngine();

        _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(() => RunWorkerAsync(_cts.Token));
        _logger.Information("ConversionWorker started");
    }

    /// <summary>
    /// Enqueue eine Capture-MD-Datei zur Konvertierung.
    /// Fire-and-forget: Rueckgabewert ist Task.CompletedTask, aber die Methode ist async
    /// fuer API-Konsistenz.
    /// </summary>
    public ValueTask EnqueueAsync(string captureMdPath, CancellationToken ct = default)
    {
        if (_disposed) return ValueTask.CompletedTask;
        if (string.IsNullOrWhiteSpace(captureMdPath)) return ValueTask.CompletedTask;

        Interlocked.Increment(ref _pendingCount);
        return _channel.Writer.WriteAsync(captureMdPath, ct);
    }

    /// <summary>
    /// Synchroner Enqueue (fire-and-forget ohne await).
    /// </summary>
    public bool TryEnqueue(string captureMdPath)
    {
        if (_disposed || string.IsNullOrWhiteSpace(captureMdPath)) return false;
        Interlocked.Increment(ref _pendingCount);
        return _channel.Writer.TryWrite(captureMdPath);
    }

    private async Task RunWorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var mdPath in _channel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await ProcessAsync(mdPath, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failedCount);
                    _logger.Error(ex, "ConversionWorker: failed to process {MdPath}", mdPath);
                    try
                    {
                        CaptureWriter.UpdateConversionStatus(mdPath, "failed",
                            conversionError: ex.GetType().Name + ": " + ex.Message);
                    }
                    catch
                    {
                        // File evtl. geloescht — ignorieren
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingCount);
                }
            }
        }
        catch (OperationCanceledException) { /* sauberer Stop */ }
        _logger.Information("ConversionWorker stopped");
    }

    /// <summary>
    /// Verarbeitet einen Capture:
    ///   1. Parse Frontmatter
    ///   2. Hole screenshot + filePath + uiaContent
    ///   3. OCR (Schritt 4) + DocumentConverter (Schritt 3)
    ///   4. Schreibe *.content.md
    ///   5. Update Frontmatter mit Status
    /// </summary>
    internal async Task ProcessAsync(string mdPath, CancellationToken ct)
    {
        if (!File.Exists(mdPath))
        {
            _logger.Warning("ConversionWorker: MD file missing {MdPath}", mdPath);
            return;
        }

        var content = await File.ReadAllTextAsync(mdPath, ct);
        var frontmatter = ParseFrontmatter(content);

        var screenshotFile = frontmatter.GetValueOrDefault("screenshot");
        var filePath = frontmatter.GetValueOrDefault("filePath");
        var uiaContent = frontmatter.GetValueOrDefault("uiaContent");

        var sections = new List<string>();
        var steps = new List<string>();
        bool anyStep = false;
        bool anyFailed = false;

        // 1) DocumentConverter (Schritt 3)
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            try
            {
                var converterName = DocumentConverter.GetConverterForFile(filePath);
                var docMd = DocumentConverter.Convert(filePath, maxChars: _config.Conversion?.MaxTextKB ?? 64 * 1024, logger: _logger);
                if (docMd != null)
                {
                    sections.Add($"## Document content (via {converterName})\n\n{docMd}");
                    steps.Add($"doc=ok,{converterName}");
                    anyStep = true;
                }
                else
                {
                    steps.Add($"doc=fail,no-content");
                    anyFailed = true;
                }
            }
            catch (Exception ex)
            {
                steps.Add($"doc=fail,{ex.GetType().Name}");
                anyFailed = true;
                _logger.Warning(ex, "ConversionWorker: DocumentConverter failed for {FilePath}", filePath);
            }
        }
        else
        {
            steps.Add("doc=skip,no-filePath");
        }

        // 2) OCR (Schritt 4 — Tesseract-Adapter)
        if (!string.IsNullOrWhiteSpace(screenshotFile))
        {
            // Screenshot-Pfad ist relativ zum Capture-MD (gleicher Ordner)
            var mdDir = Path.GetDirectoryName(mdPath);
            var screenshotPath = Path.IsPathRooted(screenshotFile)
                ? screenshotFile
                : Path.Combine(mdDir!, screenshotFile);

            if (File.Exists(screenshotPath))
            {
                try
                {
                    var pngBytes = await File.ReadAllBytesAsync(screenshotPath, ct);
                    var ocrText = await _ocrEngine.ExtractTextAsync(pngBytes, ct);
                    if (!string.IsNullOrWhiteSpace(ocrText))
                    {
                        sections.Add($"## OCR Content (via {_ocrEngine.Name})\n\n```\n{ocrText.Trim()}\n```");
                        steps.Add($"ocr=ok,{_ocrEngine.Name}");
                        anyStep = true;
                    }
                    else
                    {
                        steps.Add($"ocr=ok,empty,{_ocrEngine.Name}");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    steps.Add($"ocr=fail,{ex.GetType().Name}");
                    anyFailed = true;
                    Interlocked.Increment(ref _ocrErrorCount);
                    _logger.Warning(ex, "ConversionWorker: OCR failed for {ScreenshotPath}", screenshotPath);
                }
            }
            else
            {
                steps.Add("ocr=skip,file-missing");
                Interlocked.Increment(ref _ocrSkippedCount);
            }
        }
        else
        {
            steps.Add("ocr=skip,no-screenshot");
        }

        // 3) Content-MD schreiben + Frontmatter update
        //    Output-Datei: *.conversion.md (App-Reader schreibt *.content.md sync
        //    in TriggerWorker; Schritt 7 wird App-Reader duenn-refactoren und
        //    beide Quellen in *.content.md mergen).
        if (anyStep)
        {
            var dir = Path.GetDirectoryName(mdPath);
            var baseName = Path.GetFileNameWithoutExtension(mdPath);
            var contentPath = Path.Combine(dir!, baseName + ".conversion.md");
            var contentBody = new StringBuilder();
            contentBody.AppendLine("---");
            contentBody.AppendLine($"timestamp: {DateTimeOffset.Now:O}");
            contentBody.AppendLine($"source: \"conversion-worker\"");
            contentBody.AppendLine($"sourceMd: \"{Path.GetFileName(mdPath)}\"");
            contentBody.AppendLine("---");
            contentBody.AppendLine();
            foreach (var s in sections) contentBody.AppendLine(s);
            await File.WriteAllTextAsync(contentPath, contentBody.ToString(), new UTF8Encoding(false), ct);
        }

        // 4) Frontmatter-Update
        string status;
        if (!anyStep) status = "failed";
        else if (anyFailed) status = "partial";
        else status = "done";

        string? error = null;
        if (!anyStep && filePath != null) error = "no-document-converter-result";
        else if (!anyStep) error = "no-content-source";

        if (status == "partial") Interlocked.Increment(ref _partialCount);
        if (status == "done") Interlocked.Increment(ref _completedCount);
        if (status == "failed" && !anyStep) Interlocked.Increment(ref _failedCount);

        CaptureWriter.UpdateConversionStatus(mdPath, status,
            conversionError: error,
            conversionSteps: string.Join(";", steps));

        _logger.Information("ConversionWorker: {Status} {MdPath} ({Steps})",
            status, Path.GetFileName(mdPath), string.Join(";", steps));
    }

    /// <summary>
    /// Parst das YAML-Frontmatter einer Capture-MD-Datei.
    /// Liefert leeres Dictionary bei Fehler.
    /// </summary>
    internal static Dictionary<string, string> ParseFrontmatter(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(content)) return result;

        var lines = content.Split('\n');
        int startIdx = -1, endIdx = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                if (startIdx == -1) startIdx = i;
                else { endIdx = i; break; }
            }
        }
        if (startIdx == -1 || endIdx == -1) return result;

        for (int i = startIdx + 1; i < endIdx; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;
            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0) continue;
            var key = line[..colonIdx].Trim();
            var value = line[(colonIdx + 1)..].Trim().Trim('"');
            result[key] = value;
        }
        return result;
    }

    /// <summary>
    /// Stoppt den Worker sauber. Idempotent.
    /// </summary>
    public void Stop()
    {
        if (_disposed) return;
        _channel.Writer.TryComplete();
        try
        {
            _workerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException) { /* Task kann gecancelt sein */ }
        _cts.Cancel();
        _logger.Information("ConversionWorker stopped by user");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts.Dispose();
    }
}