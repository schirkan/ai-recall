using System;
using System.IO;

using AiRecall.Core.Audio;

using NAudio.Wave;

namespace AiRecall.Transcription;

/// <summary>
/// Kombiniert zwei Mono-WAV-Dateien (mic + loopback) zu einer Stereo-WAV-Datei
/// (links = mic, rechts = loopback). Spec 0013 v0.3 §5.4 (Update 6: Stereo statt Mono-Mix).
/// <para>
/// Wird vom <c>TranscriptionWorker</c> vor dem ASR-Call aufgerufen.
/// Output-Datei <c>combined-stereo.wav</c> im selben Meeting-Ordner.
/// Wird nach erfolgreicher Transkription im finally-Block geloescht (transient).
/// </para>
/// </summary>
public sealed class StereoConcatenator
{
    /// <summary>
    /// Liest <paramref name="paths"/>.MicPath (Mono) und
    /// <paramref name="paths"/>.LoopbackPath (Mono), schreibt
    /// <c>combined-stereo.wav</c> in <paramref name="paths"/>.Folder.
    /// </summary>
    /// <returns>Absoluter Pfad zur erzeugten Stereo-Datei.</returns>
    /// <exception cref="FileNotFoundException">Mic- oder Loopback-Datei fehlt.</exception>
    /// <exception cref="InvalidOperationException">
    /// Sample-Rate, Channel-Count oder Sample-Count mismatch.
    /// </exception>
    public string Concatenate(MeetingRecordingPaths paths)
    {
        if (paths is null) throw new ArgumentNullException(nameof(paths));
        if (!File.Exists(paths.MicPath))
            throw new FileNotFoundException("Mic-WAV fehlt", paths.MicPath);
        if (!File.Exists(paths.LoopbackPath))
            throw new FileNotFoundException("Loopback-WAV fehlt", paths.LoopbackPath);

        var stereoPath = Path.Combine(paths.Folder, "combined-stereo.wav");

        using var mic = new WaveFileReader(paths.MicPath);
        using var loop = new WaveFileReader(paths.LoopbackPath);

        // Validation: gleiche Sample-Rate, beide Mono
        if (mic.WaveFormat.SampleRate != loop.WaveFormat.SampleRate)
        {
            throw new InvalidOperationException(
                $"Sample-Rate mismatch: mic={mic.WaveFormat.SampleRate}, loop={loop.WaveFormat.SampleRate}");
        }
        if (mic.WaveFormat.Channels != 1 || loop.WaveFormat.Channels != 1)
        {
            throw new InvalidOperationException(
                $"Both files must be mono PCM: mic.Channels={mic.WaveFormat.Channels}, loop.Channels={loop.WaveFormat.Channels}");
        }
        if (mic.SampleCount != loop.SampleCount)
        {
            throw new InvalidOperationException(
                $"Length mismatch: mic={mic.SampleCount}, loop={loop.SampleCount}");
        }

        // Output: IEEE-Float Stereo bei gleicher Sample-Rate
        var stereoFormat = WaveFormat.CreateIeeeFloatWaveFormat(mic.WaveFormat.SampleRate, 2);
        using var writer = new WaveFileWriter(stereoPath, stereoFormat);

        // NAudio 2.x: kein direkter Read(float[], ...) auf WaveFileReader —
        // ToSampleProvider() konvertiert automatisch PCM/IEEE → float.
        var micSamples = mic.ToSampleProvider();
        var loopSamples = loop.ToSampleProvider();

        // 100 ms chunks
        var micBuf = new float[mic.WaveFormat.SampleRate / 10];
        var loopBuf = new float[mic.WaveFormat.SampleRate / 10];
        int read;
        while ((read = micSamples.Read(micBuf, 0, micBuf.Length)) > 0)
        {
            int loopRead = loopSamples.Read(loopBuf, 0, read);
            if (loopRead != read)
            {
                throw new InvalidOperationException(
                    $"Loop-Read lieferte {loopRead} Samples statt {read} (Mic-Read)");
            }
            for (int i = 0; i < read; i++)
            {
                // Left = mic, Right = loopback
                writer.WriteSample(micBuf[i]);
                writer.WriteSample(loopBuf[i]);
            }
        }
        return stereoPath;
    }
}
