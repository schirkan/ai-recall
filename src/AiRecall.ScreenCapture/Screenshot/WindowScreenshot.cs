using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using AiRecall.Core.Models;

namespace AiRecall.ScreenCapture.Screenshot;

/// <summary>
/// Captures a single window's contents via the Win32 <c>PrintWindow</c> API
/// (with <c>PW_RENDERFULLCONTENT</c> to handle DWM-composited windows).
/// </summary>
public static class WindowScreenshot
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    /// <summary>Capture the window as a PNG byte array.</summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the window has zero/negative bounds or <c>PrintWindow</c> returns false.
    /// </exception>
    public static byte[] CapturePng(WindowInfo window)
    {
        var bounds = window.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException(
                $"Window has invalid size ({bounds.Width}x{bounds.Height}). " +
                "Cannot capture a hidden or zero-area window.");
        }

        using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            try
            {
                if (!PrintWindow(window.Handle, hdc, PW_RENDERFULLCONTENT))
                {
                    throw new InvalidOperationException(
                        $"PrintWindow failed for HWND 0x{window.Handle.ToInt64():X}.");
                }
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
