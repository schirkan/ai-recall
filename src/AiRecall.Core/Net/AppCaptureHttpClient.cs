using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AiRecall.Core.Net;

/// <summary>
/// HTTP-Download-Helper fuer App-Capture-Workflows (Spec 0017 — Martin-Direktive 2026-07-13 22:46).
///
/// <para>
/// Kapselt <see cref="HttpClient"/>-Erzeugung und Default-Credentials-Setup
/// (siehe <see cref="HttpClientFactory"/>, Spec 0015) hinter zwei simplen
/// Methoden. Hintergrund: mehrere geplante Capture-Features (Browser-Source
/// Pull, Favicon-Download, externe Asset-Pipeline) brauchen HTTP-GET ohne
/// Boilerplate.
/// </para>
///
/// <para>
/// Privacy-First-Gate: KEINE automatische Credential-Uebermittlung an
/// private/localhost-Adressen wuerde hier Sinn ergeben, aber
/// <see cref="HttpClientFactory.CreateDefaultHandler"/> laesst das via
/// System-Proxy-Discovery ohnehin nur fuer nicht-localhost-Ziele zu.
/// </para>
/// </summary>
public static class AppCaptureHttpClient
{
    /// <summary>
    /// Laedt den Inhalt der URL als Byte-Array. Wirft
    /// <see cref="ArgumentException"/> bei leerer URL und
    /// <see cref="HttpRequestException"/> bei HTTP-/Netzwerkfehlern.
    /// </summary>
    /// <param name="url">Absolute URL (http/https).</param>
    /// <param name="ct">CancellationToken fuer Abbruch.</param>
    public static async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL must not be null or empty.", nameof(url));

        using var handler = HttpClientFactory.CreateDefaultHandler();
        using var client = new HttpClient(handler);
        return await client.GetByteArrayAsync(url, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Laedt den Inhalt der URL und schreibt ihn in eine Datei. Ueberschreibt
    /// eine existierende Datei am Zielpfad. Wirft <see cref="ArgumentException"/>
    /// bei leerer URL oder leerem Pfad, <see cref="HttpRequestException"/> bei
    /// HTTP-Fehlern, <see cref="IOException"/> bei Datei-Schreibfehlern.
    /// </summary>
    /// <param name="url">Absolute URL (http/https).</param>
    /// <param name="path">Absoluter Zielpfad (Verzeichnis muss existieren).</param>
    /// <param name="ct">CancellationToken fuer Abbruch.</param>
    public static async Task DownloadToFileAsync(string url, string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL must not be null or empty.", nameof(url));
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must not be null or empty.", nameof(path));

        var bytes = await DownloadAsync(url, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
    }
}