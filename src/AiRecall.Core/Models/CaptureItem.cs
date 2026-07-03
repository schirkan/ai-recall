using AiRecall.Core.Models;

namespace AiRecall.Core.Models;

/// <summary>
/// A single captured window: screenshot + extracted text + metadata.
/// </summary>
public sealed record CaptureItem(
    DateTimeOffset Timestamp,
    WindowInfo Window,
    string ScreenshotPath,
    string MarkdownPath,
    string ContentText,
    string ContentHash,
    string? AppContext
);
