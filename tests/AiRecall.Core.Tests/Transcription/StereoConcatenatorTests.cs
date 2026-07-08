using System;
using System.IO;
using System.Threading.Tasks;

using AiRecall.Core.Audio;
using AiRecall.Transcription;

using NAudio.Wave;

namespace AiRecall.Core.Tests.Transcription;

/// <summary>
/// Tests fuer <see cref="StereoConcatenator"/> (Spec 0013 v0.3 §5.4).
/// Validation-Tests + Bit-genau-Round-Trip + Concurrency.
/// </summary>
public class StereoConcatenatorTests : IDisposable
{
    private readonly string _root;

    public StereoConcatenatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "stereo-concat-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    // =============================================================================
    // Helpers
    // =============================================================================

    /// <summary>
    /// Schreibt eine deterministische Mono-PCM-16-WAV-Datei.
    /// <paramref name="sampleFunc"/>(sampleIndex) liefert den Sample-Wert als short.
    /// </summary>
    private string WriteMonoWav(string subdir, string fileName, int sampleRate, int sampleCount, Func<int, short> sampleFunc)
    {
        var dir = Path.Combine(_root, subdir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        // NAudio 2.x: WaveFileWriter hat WriteSamples(float[]), nicht short[].
        // Wir schreiben einen IEEE-Float-Stream und kodieren manuell in 16-bit PCM.
        var format = new WaveFormat(sampleRate, 16, 1);
        using var writer = new WaveFileWriter(path, format);
        var shortBuf = new short[Math.Min(sampleCount, 4096)];
        var floatBuf = new float[shortBuf.Length];
        int written = 0;
        while (written < sampleCount)
        {
            int chunk = Math.Min(shortBuf.Length, sampleCount - written);
            for (int i = 0; i < chunk; i++) shortBuf[i] = sampleFunc(written + i);
            for (int i = 0; i < chunk; i++) floatBuf[i] = shortBuf[i] / (float)short.MaxValue;
            writer.WriteSamples(floatBuf, 0, chunk);
            written += chunk;
        }
        return path;
    }

    /// <summary>Schreibt ein konstant-Sample Mono-PCM-16-WAV.</summary>
    private string WriteConstantMonoWav(string subdir, string fileName, int sampleRate, int durationMs, short value)
        => WriteMonoWav(subdir, fileName, sampleRate, sampleRate * durationMs / 1000, _ => value);

    private MeetingRecordingPaths NewMeetingFolder(string subdir, string micPath, string loopPath)
    {
        var folder = Path.Combine(_root, subdir);
        Directory.CreateDirectory(folder);
        return new MeetingRecordingPaths(
            Folder: folder,
            MicPath: micPath,
            LoopbackPath: loopPath,
            MetadataPath: Path.Combine(folder, "meta.md"));
    }

    // =============================================================================
    // Tests (Spec 0013 §5.4)
    // =============================================================================

    [Fact]
    public void Concatenate_ValidMonoFiles_ProducesStereo()
    {
        // 16 kHz, mono, 100 ms
        var mic = WriteConstantMonoWav("t1", "mic.wav", 16000, 100, 1000);
        var loop = WriteConstantMonoWav("t1", "loopback.wav", 16000, 100, 2000);
        var paths = NewMeetingFolder("t1", mic, loop);

        var stereoPath = new StereoConcatenator().Concatenate(paths);

        Assert.True(File.Exists(stereoPath));
        Assert.Equal(Path.Combine(paths.Folder, "combined-stereo.wav"), stereoPath);
        using var reader = new WaveFileReader(stereoPath);
        Assert.Equal(2, reader.WaveFormat.Channels);
        Assert.Equal(16000, reader.WaveFormat.SampleRate);
        // 100 ms * 16 kHz = 1600 Frames (NAudio zählt SampleCount in Frames,
        // nicht in interleavten Samples — bei Stereo sind das 3200 Samples).
        Assert.Equal(1600L, reader.SampleCount);
    }

    [Fact]
    public void Concatenate_SampleRateMismatch_Throws()
    {
        var mic = WriteConstantMonoWav("t2", "mic.wav", 16000, 100, 1000);
        var loop = WriteConstantMonoWav("t2", "loopback.wav", 8000, 50, 2000); // andere Rate
        var paths = NewMeetingFolder("t2", mic, loop);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new StereoConcatenator().Concatenate(paths));
        Assert.Contains("Sample-Rate mismatch", ex.Message);
    }

    [Fact]
    public void Concatenate_ChannelsMismatch_Throws()
    {
        // loopback als Stereo schreiben
        var dir = Path.Combine(_root, "t3");
        Directory.CreateDirectory(dir);
        var mic = WriteConstantMonoWav("t3", "mic.wav", 16000, 100, 1000);
        var loopPath = Path.Combine(dir, "loopback.wav");
        var stereo = new WaveFormat(16000, 16, 2);
        using (var w = new WaveFileWriter(loopPath, stereo))
        {
            var buf = new short[1600 * 2];
            for (int i = 0; i < buf.Length; i++) buf[i] = 2000;
            w.WriteSamples(buf, 0, buf.Length);
        }
        var paths = NewMeetingFolder("t3", mic, loopPath);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new StereoConcatenator().Concatenate(paths));
        Assert.Contains("mono PCM", ex.Message);
    }

    [Fact]
    public void Concatenate_LengthMismatch_Throws()
    {
        var mic = WriteConstantMonoWav("t4", "mic.wav", 16000, 200, 1000); // 200 ms
        var loop = WriteConstantMonoWav("t4", "loopback.wav", 16000, 100, 2000); // 100 ms
        var paths = NewMeetingFolder("t4", mic, loop);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new StereoConcatenator().Concatenate(paths));
        Assert.Contains("Length mismatch", ex.Message);
    }

    [Fact]
    public void Concatenate_PreservesAudioContent_LeftMic_RightLoopback()
    {
        // mic: Rampe 0.0 .. 0.99, loop: konstantes 0.5
        // Beide als PCM-16 short, dann als float.
        const int sampleRate = 16000;
        const int durationMs = 50;
        int sampleCount = sampleRate * durationMs / 1000; // 800
        var mic = WriteMonoWav("t5", "mic.wav", sampleRate, sampleCount, i =>
        {
            // i: 0..799, Rampe 0..0.99
            double v = (i / (double)sampleCount);
            return (short)(v * short.MaxValue);
        });
        var loop = WriteConstantMonoWav("t5", "loopback.wav", sampleRate, durationMs, (short)(0.5 * short.MaxValue));
        var paths = NewMeetingFolder("t5", mic, loop);

        var stereoPath = new StereoConcatenator().Concatenate(paths);

        using var reader = new WaveFileReader(stereoPath);
        Assert.Equal(2, reader.WaveFormat.Channels);
        var left = new float[sampleCount];
        var right = new float[sampleCount];
        // NAudio 2.x: ToSampleProvider() liefert interleavte float-Stereo-Samples: L,R,L,R,...
        var interleaved = new float[sampleCount * 2];
        var sampleProvider = reader.ToSampleProvider();
        int totalRead = 0;
        while (totalRead < interleaved.Length)
        {
            int n = sampleProvider.Read(interleaved, totalRead, interleaved.Length - totalRead);
            if (n <= 0) break;
            totalRead += n;
        }
        for (int i = 0; i < sampleCount; i++)
        {
            left[i] = interleaved[2 * i];
            right[i] = interleaved[2 * i + 1];
        }

        // Left = mic-Rampe: jeder Wert ≈ i / sampleCount
        Assert.Equal(0f, left[0], 4);
        Assert.InRange(left[sampleCount / 2], 0.49f, 0.51f);
        Assert.InRange(left[sampleCount - 1], 0.98f, 1.0f);

        // Right = konstantes 0.5
        foreach (var s in right) Assert.InRange(s, 0.49f, 0.51f);
    }

    [Fact]
    public async Task Concatenate_ParallelTasks_NoFilenameCollision()
    {
        // Zwei parallele Tasks in unterschiedlichen Meeting-Ordnern.
        // Beide schreiben ihr eigenes combined-stereo.wav — kein shared Filename,
        // daher keine File-Lock-Kollision.
        var t1Mic = WriteConstantMonoWav("p1", "mic.wav", 16000, 50, 1000);
        var t1Loop = WriteConstantMonoWav("p1", "loopback.wav", 16000, 50, 2000);
        var t1Paths = NewMeetingFolder("p1", t1Mic, t1Loop);

        var t2Mic = WriteConstantMonoWav("p2", "mic.wav", 16000, 50, 3000);
        var t2Loop = WriteConstantMonoWav("p2", "loopback.wav", 16000, 50, 4000);
        var t2Paths = NewMeetingFolder("p2", t2Mic, t2Loop);

        var concat = new StereoConcatenator();
        var task1 = Task.Run(() => concat.Concatenate(t1Paths));
        var task2 = Task.Run(() => concat.Concatenate(t2Paths));
        var results = await Task.WhenAll(task1, task2);

        Assert.True(File.Exists(results[0]));
        Assert.True(File.Exists(results[1]));
        Assert.NotEqual(results[0], results[1]);
        // Inhalte sind unterschiedlich (andere Sample-Werte)
        using var r1 = new WaveFileReader(results[0]);
        using var r2 = new WaveFileReader(results[1]);
        var s1 = new float[100];
        var s2 = new float[100];
        r1.ToSampleProvider().Read(s1, 0, 100);
        r2.ToSampleProvider().Read(s2, 0, 100);
        Assert.NotEqual(s1[0], s2[0]);
    }

    [Fact]
    public void Concatenate_MicFileMissing_Throws()
    {
        var loop = WriteConstantMonoWav("t6", "loopback.wav", 16000, 100, 1000);
        var paths = NewMeetingFolder("t6", Path.Combine(_root, "t6", "missing.wav"), loop);
        Assert.Throws<FileNotFoundException>(() => new StereoConcatenator().Concatenate(paths));
    }
}
