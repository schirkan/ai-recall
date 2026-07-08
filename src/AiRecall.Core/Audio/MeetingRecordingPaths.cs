namespace AiRecall.Core.Audio;

/// <summary>
/// Pfade zu allen Artefakten eines aufgezeichneten Meetings.
/// Wird von <see cref="RecordingSession.StopAsync"/> zurueckgegeben
/// und vom Transcription-Worker als Eingabe verwendet.
/// </summary>
public sealed record MeetingRecordingPaths(
    string Folder,
    string MicPath,
    string LoopbackPath,
    string MetadataPath)
{
    /// <summary>True, wenn alle Pfade existieren.</summary>
    public bool AllExist =>
        Directory.Exists(Folder) &&
        File.Exists(MicPath) &&
        File.Exists(LoopbackPath) &&
        File.Exists(MetadataPath);
}