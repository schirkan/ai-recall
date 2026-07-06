using AiRecall.Conversion;
using AiRecall.Core.Configuration;
using AiRecall.Trigger;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace AiRecall.Core.Tests.Trigger;

/// <summary>
/// Integration-Tests fuer TriggerService + ConversionWorker (Spec 0007 Schritt 6).
/// Verifiziert Verdrahtung, Eigentuemerschaft und Lifecycle.
/// </summary>
public class TriggerServiceConversionTests
{
    private sealed class TestSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (ILogger logger, TestSink sink) NewLogger()
    {
        var sink = new TestSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink);
    }

    private static AppConfig NewConfig()
    {
        var c = new AppConfig();
        c.Trigger.WinEvents.Foreground = false;
        c.Trigger.WinEvents.Focus = false;
        c.Trigger.WinEvents.NameChange = false;
        c.Trigger.WinEvents.ValueChange = false;
        c.Trigger.WinEvents.Scroll = false;
        c.Trigger.WinEvents.MenuPopup = false;
        c.Trigger.HeartbeatIntervalSeconds = 0;
        return c;
    }

    [Fact]
    public void ConversionWorker_AutoCreated_WhenConversionEnabled()
    {
        // Default-Config: conversion.enabled=true, ocr.engine="tesseract" (ohne tessdata → Fallback NullOcrEngine)
        var (logger, _) = NewLogger();
        using var svc = new TriggerService(NewConfig(), logger,
            enableWinEventHook: false, enableHeartbeat: false);

        Assert.NotNull(svc.ConversionWorker);
    }

    [Fact]
    public void ConversionWorker_Null_WhenConversionDisabled()
    {
        var (logger, _) = NewLogger();
        var c = NewConfig();
        c.Conversion.Enabled = false;

        using var svc = new TriggerService(c, logger,
            enableWinEventHook: false, enableHeartbeat: false);

        Assert.Null(svc.ConversionWorker);
    }

    [Fact]
    public void ConversionWorker_ExternalInjected_IsUsedAndNotDisposed()
    {
        // Wenn extern injiziert, darf TriggerService den Worker NICHT disposen
        var (logger, _) = NewLogger();
        using var externalWorker = new ConversionWorker(NewConfig(), NewLogger().Item1, new NullOcrEngine());

        var svc = new TriggerService(NewConfig(), logger,
            enableWinEventHook: false, enableHeartbeat: false,
            conversionWorker: externalWorker);

        Assert.Same(externalWorker, svc.ConversionWorker);

        // Nach Dispose: externalWorker darf NICHT disposed sein (PendingCount-Abfrage als Smoke-Check)
        svc.Dispose();
        Assert.Equal(0, externalWorker.PendingCount); // funktioniert nur, wenn nicht disposed
    }

    [Fact]
    public void ConversionWorker_Owned_IsDisposedOnServiceDispose()
    {
        var (logger, _) = NewLogger();
        var svc = new TriggerService(NewConfig(), logger,
            enableWinEventHook: false, enableHeartbeat: false);
        var owned = svc.ConversionWorker;
        Assert.NotNull(owned);

        // Dispose: danach darf ein Enqueue nicht mehr annehmen (Disposed-Pfad in ConversionWorker)
        svc.Dispose();
        Assert.False(owned!.TryEnqueue("dummy.md"));
    }

    [Fact]
    public void ConversionWorker_WithOcrDisabled_UsesNullOcrEngine()
    {
        // Ocr.Engine != "tesseract" → NullOcrEngine
        var (logger, _) = NewLogger();
        var c = NewConfig();
        c.Ocr.Engine = "none";

        using var svc = new TriggerService(c, logger,
            enableWinEventHook: false, enableHeartbeat: false);

        Assert.NotNull(svc.ConversionWorker);
    }

    [Fact]
    public void ConversionWorker_TesseractInitFails_FallsBackToNullOcrEngine()
    {
        // Default-Config hat ocr.engine="tesseract" aber kein tessdata → OcrEngine wirft
        // → TriggerService muss auf NullOcrEngine fallen (sonst wirft der ctor).
        // Bug-Bash 2026-07-06 I-14: OcrEngine sucht jetzt in mehreren Pfaden
        // (u. a. %LOCALAPPDATA%\AiRecall\tessdata). Damit der Test auf jeder
        // Maschine deterministisch scheitert (nicht von lokalen tessdata-
        // Ordnern abhaengt), zeigen wir TessDataPath auf einen garantiert
        // nicht existenten Pfad.
        var (logger, sink) = NewLogger();
        var c = NewConfig();
        c.Ocr.TessDataPath = Path.Combine(Path.GetTempPath(), $"tessdata-nonexistent-{Guid.NewGuid():N}");
        using var svc = new TriggerService(c, logger,
            enableWinEventHook: false, enableHeartbeat: false);

        Assert.NotNull(svc.ConversionWorker);
        // Warning wurde geloggt
        Assert.Contains(sink.Events, e =>
            e.MessageTemplate.Text.Contains("Tesseract init failed") ||
            e.MessageTemplate.Text.Contains("NullOcrEngine"));
    }
}
