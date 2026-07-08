using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AiRecall.Transcription;

/// <summary>
/// Schreibt Transkriptions-Ergebnisse in die <c>meta.md</c> eines Meetings
/// (Spec 0013 v0.3 §7 — Transcription in MD-Datei schreiben).
/// <list type="bullet">
///   <li>Erfolg: ersetzt/ergaenzt <c>transcript_status: done</c> +
///         <c>## Transcription</c>-Block mit Segmenten</li>
///   <li>Fehler: setzt <c>transcript_status: failed</c> + ErrorMessage im Frontmatter</li>
/// </list>
/// Atomar: schreibt in temp-File und <see cref="File.Replace(string, string, string?)"/>.
/// </summary>
public sealed class MetadataUpdater
{
    /// <summary>Max. Laenge des in MD geschriebenen Transkripts (Sicherheits-Cap).</summary>
    public int MaxTranscriptChars { get; init; } = 200_000;

    /// <summary>
    /// Schreibt das Transkriptions-Result in <paramref name="metadataPath"/>.
    /// Existiert die Datei nicht, wird sie neu angelegt (mit minimalem Frontmatter).
    /// </summary>
    public async Task UpdateAsync(
        string metadataPath,
        TranscriptionResult result,
        CancellationToken cancellationToken)
    {
        if (metadataPath is null) throw new ArgumentNullException(nameof(metadataPath));
        if (result is null) throw new ArgumentNullException(nameof(result));

        var original = File.Exists(metadataPath)
            ? await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false)
            : BuildInitialMeta();
        var updated = result.IsSuccess
            ? AppendSuccess(original, result, MaxTranscriptChars)
            : AppendFailure(original, result);
        await AtomicWriteAsync(metadataPath, updated, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Setzt <c>transcript_status: failed</c> + ErrorMessage (bei Worker-Crash).</summary>
    public async Task MarkFailedAsync(
        string metadataPath,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        if (metadataPath is null) throw new ArgumentNullException(nameof(metadataPath));
        var original = File.Exists(metadataPath)
            ? await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false)
            : BuildInitialMeta();
        var updated = AppendFailure(original, new TranscriptionResult(
            Segments: Array.Empty<TranscriptionSegment>(),
            ProviderName: "worker",
            AudioDuration: TimeSpan.Zero,
            SpeakerCount: 0,
            SpeakerLabels: Array.Empty<string>(),
            ErrorMessage: errorMessage));
        await AtomicWriteAsync(metadataPath, updated, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildInitialMeta()
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("transcript_status: pending");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("# Meeting Transcript");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string AppendSuccess(string original, TranscriptionResult result, int maxChars)
    {
        var sb = new StringBuilder(original);

        // Frontmatter-Status setzen/ersetzen
        sb = ReplaceFrontmatterField(sb, "transcript_status", "done");
        sb = ReplaceFrontmatterField(sb, "transcript_provider", result.ProviderName);
        sb = ReplaceFrontmatterField(sb, "transcript_speaker_count", result.SpeakerCount.ToString());
        sb = ReplaceFrontmatterField(sb, "transcript_audio_duration_seconds",
            result.AudioDuration.TotalSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

        // Transcript-Block: ersetze existierenden, falls vorhanden
        const string startMarker = "<!-- transcript:begin -->";
        const string endMarker = "<!-- transcript:end -->";
        var transcript = BuildTranscriptMarkdown(result, maxChars);
        var block = $"{startMarker}\n{transcript}\n{endMarker}\n";

        var existingStart = original.IndexOf(startMarker, StringComparison.Ordinal);
        var existingEnd = original.IndexOf(endMarker, StringComparison.Ordinal);
        if (existingStart >= 0 && existingEnd > existingStart)
        {
            // ersetzen
            var before = original[..existingStart];
            var after = original[(existingEnd + endMarker.Length)..];
            return before + block + after.TrimStart('\n', '\r');
        }
        else
        {
            // anhaengen
            if (sb.Length > 0 && sb[^1] != '\n') sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("## Transcription");
            sb.AppendLine();
            sb.Append(block);
            return sb.ToString();
        }
    }

    private static string AppendFailure(string original, TranscriptionResult result)
    {
        var sb = new StringBuilder(original);
        sb = ReplaceFrontmatterField(sb, "transcript_status", "failed");
        sb = ReplaceFrontmatterField(sb, "transcript_error",
            (result.ErrorMessage ?? "unknown").Replace("\n", " "));
        return sb.ToString();
    }

    private static StringBuilder ReplaceFrontmatterField(StringBuilder sb, string key, string value)
    {
        var text = sb.ToString();
        var pattern = $"^{key}:.*$";
        var lines = text.Split('\n');
        var replaced = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(lines[i].TrimStart(), pattern))
            {
                lines[i] = $"{key}: {value}";
                replaced = true;
                break;
            }
        }
        if (!replaced)
        {
            // in Frontmatter einfuegen (zwischen erste "---" und zweite "---")
            var firstDash = Array.FindIndex(lines, l => l.Trim() == "---");
            var secondDash = firstDash >= 0
                ? Array.FindIndex(lines, firstDash + 1, l => l.Trim() == "---")
                : -1;
            if (firstDash >= 0 && secondDash > firstDash)
            {
                var insertLine = secondDash;
                var newLines = new string[lines.Length + 1];
                Array.Copy(lines, newLines, insertLine);
                newLines[insertLine] = $"{key}: {value}";
                Array.Copy(lines, insertLine, newLines, insertLine + 1, lines.Length - insertLine);
                lines = newLines;
            }
        }
        return new StringBuilder(string.Join('\n', lines));
    }

    private static string BuildTranscriptMarkdown(TranscriptionResult result, int maxChars)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Provider: {result.ProviderName}");
        sb.AppendLine($"Speaker-Count: {result.SpeakerCount}");
        sb.AppendLine($"Duration: {result.AudioDuration:hh\\:mm\\:ss\\.fff}");
        sb.AppendLine();
        var total = 0;
        foreach (var seg in result.Segments)
        {
            var line = $"**[{seg.Speaker}] {seg.Start:hh\\:mm\\:ss}–{seg.End:hh\\:mm\\:ss}**  {seg.Text}";
            if (total + line.Length + 1 > maxChars)
            {
                sb.AppendLine();
                sb.AppendLine($"_… truncated at {maxChars} chars ({result.Segments.Count - CountRendered(result, total)} segments omitted)_");
                break;
            }
            sb.AppendLine(line);
            total += line.Length + 1;
        }
        return sb.ToString();
    }

    private static int CountRendered(TranscriptionResult result, int currentTotal)
    {
        // grobe Schaetzung
        return result.Segments.Count;
    }

    private static async Task AtomicWriteAsync(
        string path, string content, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        if (File.Exists(path))
        {
            File.Replace(tmp, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tmp, path);
        }
    }
}
