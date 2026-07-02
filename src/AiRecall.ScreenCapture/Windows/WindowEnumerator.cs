using System.Runtime.InteropServices;
using AiRecall.Core.Models;

namespace AiRecall.ScreenCapture.Windows;

/// <summary>
/// Enumerates top-level windows via the Win32 EnumWindows API.
/// </summary>
public static class WindowEnumerator
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static EnumWindowsProc? _callback;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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

    /// <summary>
    /// Enumerates all top-level windows.
    /// </summary>
    /// <param name="includeInvisible">If false, only visible windows are returned.</param>
    /// <param name="includeUntitled">If false, windows with empty titles are skipped.</param>
    public static IReadOnlyList<WindowInfo> Enumerate(bool includeInvisible = false, bool includeUntitled = false)
    {
        var result = new List<WindowInfo>();

        _callback = (hWnd, _) =>
        {
            bool isVisible = IsWindowVisible(hWnd);
            if (!includeInvisible && !isVisible) return true;

            int length = GetWindowTextLength(hWnd);
            string title = string.Empty;
            if (length > 0)
            {
                var sb = new System.Text.StringBuilder(length + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                title = sb.ToString();
            }

            if (!includeUntitled && string.IsNullOrEmpty(title)) return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            string processName = TryGetProcessName((int)pid);

            GetWindowRect(hWnd, out RECT rect);
            var bounds = new WindowRect(
                rect.Left,
                rect.Top,
                rect.Right - rect.Left,
                rect.Bottom - rect.Top);

            result.Add(new WindowInfo(hWnd, title, (int)pid, processName, isVisible, bounds));
            return true;
        };

        try
        {
            EnumWindows(_callback, IntPtr.Zero);
        }
        finally
        {
            _callback = null;
        }

        return result;
    }

    private static string TryGetProcessName(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return "<unknown>";
        }
    }
}