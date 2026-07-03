using AiRecall.Cli.Logging;
using AiRecall.Core.Configuration;
using AiRecall.Core.Util;
using AiRecall.ScreenCapture.Trigger;
using Serilog;

namespace AiRecall.Cli.Commands;

internal static class RecordCommand
{
    public static int Run(string[] args)
    {
        bool noOcr = false;
        string? configPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--no-ocr":
                    noOcr = true;
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
        if (noOcr)
        {
            // Überschreibe OCR-Engine in der In-Memory-Config (Persistenz nicht nötig).
            config.Ocr.Engine = "";
        }

        Log.Logger = SerilogSetup.Create(config.Logging);
        var logger = Log.Logger;
        try
        {
            logger.Information("recall record: starting (Ctrl+C to stop)");
            using var pipeline = new CapturePipeline(config, logger);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                logger.Information("Shutdown signal received");
            };

            pipeline.Start();

            // Stats-Reporter: alle 30s einen Tick loggen.
            var lastStats = DateTimeOffset.Now;
            while (!cts.Token.IsCancellationRequested)
            {
                Thread.Sleep(500);
                if ((DateTimeOffset.Now - lastStats).TotalSeconds >= 30)
                {
                    logger.Information(
                        "Stats: captures={Captures}, skipped={Skipped}, duplicates={Duplicates}",
                        pipeline.CaptureCount, pipeline.SkippedCount, pipeline.DuplicateCount);
                    lastStats = DateTimeOffset.Now;
                }
            }

            pipeline.Stop();
            Console.WriteLine();
            Console.WriteLine($"Stopped. Captures: {pipeline.CaptureCount}, Skipped: {pipeline.SkippedCount}, Duplicates: {pipeline.DuplicateCount}");
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "recall record failed");
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: recall record [options]");
        Console.WriteLine();
        Console.WriteLine("Continuous capture mode with trigger pipeline (Spec 0002 TR-1..6).");
        Console.WriteLine("Captures the foreground window on Activate / Scroll / Click events,");
        Console.WriteLine("throttled to screenRecorder.throttleMs (default 1000 ms), deduped by SHA-256.");
        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --no-ocr              Disable Tesseract OCR (faster, no text in MD).");
        Console.WriteLine("  --config <path>       Override the config JSON path.");
        Console.WriteLine("  -h, --help            Show this help.");
    }
}