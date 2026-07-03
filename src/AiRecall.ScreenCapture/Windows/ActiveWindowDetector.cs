using System.Runtime.InteropServices;
using System.Text;
using AiRecall.Core.Models;

namespace AiRecall.ScreenCapture.Windows;

/// <summary>
/// Detects the currently active (foreground) window via the Win32
/// <c>GetForegroundWindow</c> API.
/// </summary>
public static class ActiveWindowDetector
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>Returns the foreground window info, or <c>null</c> if none is set.</summary>
    public static WindowInfo? GetActive()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return null;

        var sb = new StringBuilder(512);
        GetWindowText(hWnd, sb, sb.Capacity);
        var title = sb.ToString();

        GetWindowThreadProcessId(hWnd, out uint pid);
        var processName = "<unknown>";
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            processName = p.ProcessName;
        }
        catch
        {
            // Process may have exited between calls; keep the placeholder.
        }

        if (!GetWindowRect(hWnd, out var rect)) return null;

        var bounds = new WindowRect(
            rect.Left,
            rect.Top,
            Math.Max(0, rect.Right - rect.Left),
            Math.Max(0, rect.Bottom - rect.Top));

        return new WindowInfo(
            hWnd,
            title,
            (int)pid,
            processName,
            IsWindowVisible(hWnd),
            bounds);
    }
}
