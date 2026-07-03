namespace AiRecall.Core.Models;

/// <summary>
/// Information about a top-level window.
/// </summary>
public sealed record WindowInfo(
    IntPtr Handle,
    string Title,
    int ProcessId,
    string ProcessName,
    bool IsVisible,
    WindowRect Bounds
);

/// <summary>
/// Window rectangle in screen coordinates (logical pixels).
/// </summary>
public sealed record WindowRect(int X, int Y, int Width, int Height);