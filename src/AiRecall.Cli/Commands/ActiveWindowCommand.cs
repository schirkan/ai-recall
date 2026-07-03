using System.Diagnostics;
using AiRecall.AppReader.Base;
using AiRecall.Cli.Logging;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using AiRecall.Core.Persistence;
using AiRecall.Core.Util;
using AiRecall.ScreenCapture.Screenshot;
using AiRecall.ScreenCapture.Text;
using AiRecall.ScreenCapture.Windows;
using Serilog;

namespace AiRecall.Cli.Commands;

internal static class ActiveWindowCommand
{
    public static int Run(string[] args)
    {
        bool noOcr = false;
        bool includeIgnored = false;
        string? configPath = null;
        long? hwndOverride = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--no-ocr":
                    noOcr = true;
                    break;
                case "--include-ignored":
                    includeIgnored = true;
                    break;
                case "--hwnd":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--hwnd requires a hex value (e.g. 0x0000090068)");
                        return 2;
                    }
                    var raw = args[++i];
                    if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) raw = raw[2..];
                    if (!long.TryParse(raw, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    {
                        Console.Error.WriteLine($"--hwnd: invalid hex value '{args[i]}'");
                        return 2;
                    }
                    hwndOverride = parsed;
                    break;
                case "--config":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--config requires a path argument");
                        return 2;
                    }
                    configPath = args[++i];
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return 0;
            }
        }

        var config = ConfigLoader.Load(configPath);
        Log.Logger = SerilogSetup.Create(config.Logging);
        try
        {
            return Execute(config, noOcr, includeIgnored, hwndOverride);
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "recall active-window failed");
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static int Execute(AppConfig config, bool noOcr, bool includeIgnored, long? hwndOverride)
    {
        var logger = Log.Logger;
        var sw = Stopwatch.StartNew();

        WindowInfo? window;
        if (hwndOverride.HasValue)
        {
            window = WindowInfoLookup.Get(hwndOverride.Value);
            if (window is null)
            {
                Console.Error.WriteLine($"No window found for HWND 0x{hwndOverride.Value:X}.");
                return 3;
            }
        }
        else
        {
            window = ActiveWindowDetector.GetActive();
            if (window is null)
            {
                Console.Error.WriteLine("No active window detected.");
                return 3;
            }
        }

        Console.WriteLine($"Active window: {window.ProcessName} (PID {window.ProcessId}) — \"{window.Title}\"");
        Console.WriteLine($"  HWND: 0x{window.Handle.ToInt64():X}, Bounds: {window.Bounds.Width}x{window.Bounds.Height} @ ({window.Bounds.X},{window.Bounds.Y})");

        if (IgnoreMatcher.IsIgnored(window, null, config.ScreenRecorder) && !includeIgnored)
        {
            var reason = "process or title matches the ignore list";
            Console.WriteLine($"Skipped ({reason}). Use --include-ignored to force capture.");
            logger.Information("Skipped window {Process} ({Title}): {Reason}", window.ProcessName, window.Title, reason);
            return 0;
        }

        byte[] pngBytes;
        try
        {
            pngBytes = WindowScreenshot.CapturePng(window);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Screenshot failed: {ex.Message}");
            logger.Error(ex, "PrintWindow capture failed");
            return 4;
        }
        var hash = Hashing.Sha256(pngBytes);
        logger.Debug("Screenshot captured ({Bytes} bytes, hash {Hash})", pngBytes.Length, hash);

        string contentText = string.Empty;
        if (!noOcr)
        {
            try
            {
                using var ocr = new OcrEngine(config.Ocr);
                contentText = ocr.ExtractText(pngBytes);
                logger.Debug("OCR extracted {Chars} characters", contentText.Length);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "OCR skipped: {Message}", ex.Message);
                Console.Error.WriteLine($"OCR skipped: {ex.Message}");
            }
        }
        else
        {
            logger.Debug("OCR skipped (--no-ocr)");
        }

        var item = CaptureWriter.Write(
            window,
            pngBytes,
            contentText,
            hash,
            config.Capture.RootPath);

        // App-Reader: vor OCR oder parallel — liefert strukturierten Content, der als
        // *.content.md neben dem Capture-MD persistiert wird (Spec 0004).
        AppReaderResult? appReaderResult = null;
        string? contentPath = null;
        if (config.AppReader.Enabled)
        {
            var pluginDir = ResolvePluginDir(config.AppReader.PluginPath);
            var registry = AppReaderRegistry.LoadFromDirectory(pluginDir, logger);
            var context = new AppReaderContext
            {
                Config = config,
                Logger = logger,
                CancellationToken = default
            };
            appReaderResult = registry.TryRead(window, context);
            if (appReaderResult is not null)
            {
                var coreRecord = new AppContentRecord(
                    appReaderResult.ContentMarkdown,
                    appReaderResult.ContextLabel,
                    appReaderResult.ContextKind,
                    appReaderResult.ReaderName,
                    appReaderResult.ReaderVersion,
                    appReaderResult.Extra);
                contentPath = CaptureWriter.WriteContent(
                    coreRecord,
                    window,
                    item.Timestamp,
                    config.Capture.RootPath,
                    appReaderResult.ContextLabel);
                logger.Information(
                    "AppReader {Reader} produced content for {Process}/{Title} -> {Path}",
                    appReaderResult.ReaderName, window.ProcessName, window.Title, contentPath);
            }
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine("Capture written:");
        Console.WriteLine($"  Screenshot:  {item.ScreenshotPath}");
        Console.WriteLine($"  Markdown:    {item.MarkdownPath}");
        if (contentPath is not null)
            Console.WriteLine($"  App-Reader:  {contentPath}  ({appReaderResult?.ReaderName}, kind={appReaderResult?.ContextKind})");
        Console.WriteLine($"  Hash:        {item.ContentHash}");
        Console.WriteLine($"  Text chars:  {item.ContentText.Length}");
        Console.WriteLine($"  Took:        {sw.ElapsedMilliseconds} ms");

        logger.Information(
            "Captured {Process}/{Title} -> {Path} (hash={Hash}, chars={Chars}, appReader={AppReader}, elapsedMs={Elapsed})",
            window.ProcessName, window.Title, item.ScreenshotPath, hash, contentText.Length,
            appReaderResult?.ReaderName ?? "none", sw.ElapsedMilliseconds);

        return 0;
    }

    private static string ResolvePluginDir(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) configuredPath = ".";
        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: recall active-window [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --no-ocr              Skip OCR (faster, no text in the Markdown).");
        Console.WriteLine("  --include-ignored     Capture even if the window matches the ignore list.");
        Console.WriteLine("  --hwnd <hex>          Capture a specific window by HWND (skips GetForegroundWindow).");
        Console.WriteLine("  --config <path>       Override the config JSON path.");
        Console.WriteLine("  -h, --help            Show this help.");
        Console.WriteLine();
        Console.WriteLine("App-Reader (auto-loaded from <appBase>/AiRecall.AppReader.*.dll):");
        Console.WriteLine("  Browser (msedge, chrome)  Tab-Titel + URL via UIA");
        Console.WriteLine("  Notepad                   Buffer + Dateiname via Win32");
    }
}
