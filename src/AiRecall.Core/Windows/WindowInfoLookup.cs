using System.Runtime.InteropServices;
using System.Text;
using AiRecall.Core.Models;

namespace AiRecall.Core.Windows;

/// <summary>
/// Looks up a single <see cref="WindowInfo"/> for a specific HWND.
/// Used by <c>recall active-window --hwnd &lt;hex&gt;</c> for scripted captures
/// and headless/automated testing.
/// </summary>
public static class WindowInfoLookup
{
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>Returns info for the given HWND, or <c>null</c> if it is not a valid window.</summary>
    public static WindowInfo? Get(long hwndValue)
    {
        var hWnd = new IntPtr(hwndValue);
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return null;

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
            // Process may have exited; keep the placeholder.
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