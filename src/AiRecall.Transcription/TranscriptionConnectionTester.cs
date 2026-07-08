using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using NAudio.Wave;

using Serilog;

namespace AiRecall.Transcription;

/// <summary>
/// Ergebnis eines Connection-Tests (Spec 0013 v0.3 §5.4 Settings-Tab "Test-Connection").
/// </summary>
public sealed record ConnectionTestResult(
    bool Success,
    string? ErrorMessage,
    string ProviderName,
    TimeSpan ResponseTime)
{
    public string Display =>
        Success
            ? $"Verbindung OK ({ProviderName}, {ResponseTime.TotalMilliseconds:F0} ms)"
            : $"Verbindung fehlgeschlagen ({ProviderName}): {ErrorMessage ?? "unknown"}";
}

/// <summary>
/// Connection-Tester fuer Transkriptions-Provider (Spec 0013 v0.3 §5.4
/// Settings-Tab „Test-Connection"-Button). Generiert 1-Sekunden-Silent-Audio
/// (PCM-16, 16 kHz, Mono), schreibt es in eine temp-Datei und ruft
/// <see cref="ITranscriptionProvider.TranscribeAsync"/> damit auf.
/// </summary>
/// <remarks>
/// Der Test deckt drei Fehlerquellen ab:
/// <list type="bullet">
///   <item>Netzwerk/Endpoint erreichbar?</item>
///   <item>API-Key gueltig? (HTTP 401 / 403)</item>
///   <item>Provider akzeptiert das Audio-Format?</item>
/// </list>
/// Die temp-Audiodatei wird bei Disposal aufgeraeumt.
/// </remarks>
public sealed class TranscriptionConnectionTester : IAsyncDisposable
{
    private readonly ITranscriptionProvider _provider;
    private readonly ILogger? _logger;
    private readonly string _silentWavPath;

    /// <summary>Provider-Name (fuer Anzeige).</summary>
    public string ProviderName => _provider.Name;

    public TranscriptionConnectionTester(ITranscriptionProvider provider, ILogger? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _logger = logger;
        _silentWavPath = Path.Combine(
            Path.GetTempPath(),
            $"transcription-test-{Guid.NewGuid():N}.wav");
    }

    /// <summary>
    /// Sendet 1-Sekunden-Silent-Audio an den Provider und liefert das Ergebnis.
    /// </summary>
    public async Task<ConnectionTestResult> TestAsync(
        TranscriptionOptions options, CancellationToken cancellationToken)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        var sw = Stopwatch.StartNew();
        try
        {
            // 1-Sekunden-Silent-Audio in temp-File
            await WriteSilentWavAsync(_silentWavPath, cancellationToken).ConfigureAwait(false);

            // Provider-Transkription (Silent-Audio → leeres Result, aber Erfolg zeigt, dass alles geht)
            var result = await _provider
                .TranscribeAsync(_silentWavPath, options, progress: null, cancellationToken)
                .ConfigureAwait(false);

            sw.Stop();
            return new ConnectionTestResult(
                Success: result.IsSuccess,
                ErrorMessage: result.ErrorMessage,
                ProviderName: result.ProviderName,
                ResponseTime: sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.Warning(ex, "TranscriptionConnectionTester: {Provider} Test fehlgeschlagen", _provider.Name);
            return new ConnectionTestResult(
                Success: false,
                ErrorMessage: ex.Message,
                ProviderName: _provider.Name,
                ResponseTime: sw.Elapsed);
        }
    }

    /// <summary>
    /// Schreibt 1-Sekunden-Silent-Audio (PCM-16, 16 kHz, Mono) in <paramref name="path"/>.
    /// </summary>
    public static async Task WriteSilentWavAsync(string path, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            const int sampleRate = 16000;
            const int samples = sampleRate; // 1 Sekunde
            var format = new WaveFormat(sampleRate, 16, 1);
            using var writer = new WaveFileWriter(path, format);
            var buf = new short[Math.Min(samples, 4096)];
            int written = 0;
            while (written < samples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int chunk = Math.Min(buf.Length, samples - written);
                // buf ist mit 0 initialisiert (= Silence)
                writer.WriteSamples(buf, 0, chunk);
                written += chunk;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        try { if (File.Exists(_silentWavPath)) File.Delete(_silentWavPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }
}
