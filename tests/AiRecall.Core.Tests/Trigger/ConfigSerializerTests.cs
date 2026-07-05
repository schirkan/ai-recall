using System.Text.Json;
using AiRecall.Core.Configuration;
using AiRecall.Trigger;
using Xunit;

namespace AiRecall.Core.Tests.Trigger;

public class ConfigSerializerTests
{
    [Fact]
    public void Serialize_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ConfigSerializer.Serialize(null!));
    }

    [Fact]
    public void Serialize_DefaultConfig_ProducesCamelCaseJson()
    {
        var json = ConfigSerializer.Serialize(new AppConfig());
        Assert.Contains("\"capture\"", json);
        Assert.Contains("\"screenRecorder\"", json);
        Assert.Contains("\"appReader\"", json);
        Assert.Contains("\"trigger\"", json);
    }

    [Fact]
    public void RoundTrip_PreservesAllValues()
    {
        var original = new AppConfig
        {
            Ocr = new OcrConfig
            {
                Engine = "paddle-ocr",
                Languages = new List<string> { "deu", "eng", "fra" },
                TessDataPath = "tessdata-prod"
            },
            Trigger = new TriggerConfig
            {
                ThrottleMs = 1000,
                HeartbeatIntervalSeconds = 60,
                WinEvents = new WinEventSubscription
                {
                    Foreground = true,
                    Focus = false,
                    Scroll = true
                }
            }
        };

        var json = ConfigSerializer.Serialize(original);
        var roundTripped = ConfigSerializer.Deserialize(json);

        Assert.Equal(original.Ocr.Engine, roundTripped.Ocr.Engine);
        Assert.Equal(original.Ocr.Languages, roundTripped.Ocr.Languages);
        Assert.Equal(original.Ocr.TessDataPath, roundTripped.Ocr.TessDataPath);
        Assert.Equal(original.Trigger.ThrottleMs, roundTripped.Trigger.ThrottleMs);
        Assert.Equal(original.Trigger.HeartbeatIntervalSeconds, roundTripped.Trigger.HeartbeatIntervalSeconds);
        Assert.Equal(original.Trigger.WinEvents.Foreground, roundTripped.Trigger.WinEvents.Foreground);
        Assert.Equal(original.Trigger.WinEvents.Focus, roundTripped.Trigger.WinEvents.Focus);
        Assert.Equal(original.Trigger.WinEvents.Scroll, roundTripped.Trigger.WinEvents.Scroll);
    }

    [Fact]
    public void Deserialize_NullJson_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ConfigSerializer.Deserialize(null!));
    }

    [Fact]
    public void Deserialize_MalformedJson_Throws()
    {
        Assert.Throws<JsonException>(() => ConfigSerializer.Deserialize("{not valid"));
    }

    [Fact]
    public void Deserialize_EmptyObject_ReturnsAllDefaults()
    {
        var config = ConfigSerializer.Deserialize("{}");
        Assert.NotNull(config);
        Assert.Equal("tesseract", config.Ocr.Engine);
    }

    [Fact]
    public void SaveAtomic_CreatesFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"config-test-{Guid.NewGuid():N}.json");
        try
        {
            ConfigSerializer.SaveAtomic(new AppConfig(), tmp);
            Assert.True(File.Exists(tmp));
            var content = File.ReadAllText(tmp);
            Assert.Contains("\"capture\"", content);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            var bak = tmp + ".bak";
            if (File.Exists(bak)) File.Delete(bak);
        }
    }

    [Fact]
    public void SaveAtomic_OverwritesExisting_CreatesBackup()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"config-test-{Guid.NewGuid():N}.json");
        try
        {
            // Initial file
            File.WriteAllText(tmp, "{\"initial\":true}");
            ConfigSerializer.SaveAtomic(new AppConfig(), tmp);

            Assert.True(File.Exists(tmp));
            Assert.True(File.Exists(tmp + ".bak"));
            var bakContent = File.ReadAllText(tmp + ".bak");
            Assert.Contains("initial", bakContent);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            var bak = tmp + ".bak";
            if (File.Exists(bak)) File.Delete(bak);
        }
    }

    [Fact]
    public void SaveAtomic_CreatesDirectoryIfMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"config-test-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "config.json");
        try
        {
            ConfigSerializer.SaveAtomic(new AppConfig(), path);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Bug-Bash 2026-07-05 I-7 Regressions-Test: Wenn der Backup-Pfad nicht
    /// geschrieben werden kann (z. B. weil ein Read-only-Handle den Pfad
    /// blockiert), soll der eigentliche Save trotzdem gelingen.
    /// </summary>
    [Fact]
    public void SaveAtomic_BackupFailure_DoesNotPreventSave()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"config-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            // Initial file schreiben
            File.WriteAllText(path, "{\"initial\":true}");

            // Backup-Pfad als Read-only-Datei anlegen — File.Copy mit overwrite
            // wirft dann UnauthorizedAccessException
            var bakPath = path + ".bak";
            File.WriteAllText(bakPath, "old-backup");
            File.SetAttributes(bakPath, FileAttributes.ReadOnly);

            // Save sollte trotzdem gelingen, ohne dass die Exception propagiert
            ConfigSerializer.SaveAtomic(new AppConfig(), path);

            Assert.True(File.Exists(path));
            // Save selbst hat stattgefunden — Datei existiert und enthaelt aktuelle Config
            var content = File.ReadAllText(path);
            Assert.Contains("\"capture\"", content);
        }
        finally
        {
            // Read-only-Attribut zuruecksetzen vor dem Cleanup
            var bakPath = path + ".bak";
            if (File.Exists(bakPath))
            {
                File.SetAttributes(bakPath, FileAttributes.Normal);
            }
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}