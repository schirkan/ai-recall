using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;

namespace AiRecall.TrayApp;

/// <summary>
/// Rastert ein Emoji in ein <see cref="Bitmap"/> fuer
/// <see cref="ToolStripMenuItem.Image"/>. Wir hatten hier auch eine
/// Icon-Version fuer <see cref="NotifyIcon"/>, aber GDI+ und WinForms
/// NotifyIcon rendern Color-Emoji auf diesem System unzuverlaessig
/// (leere Bitmaps oder monochrome Outlines). Deshalb wird im TrayIcon
/// weiterhin <see cref="SystemIcons.Application"/> verwendet; das Menu
/// rendert die Glyphen ueber <see cref="ToolStripMenuItem.Image"/>
/// zuverlaessig.
/// </summary>
internal static class EmojiIconFactory
{
    /// <summary>
    /// Erzeugt ein <see cref="Bitmap"/> fuer <see cref="ToolStripMenuItem.Image"/>.
    /// Caller ist fuer das Dispose verantwortlich (oder ueber
    /// <see cref="MenuImageCache"/> mit AutoDispose).
    /// </summary>
    public static Bitmap RenderBitmap(string emoji, int size, Color? color = null)
    {
        // Wichtiger Render-Pfad-Wechsel: GDI+ Graphics.DrawString rendert
        // Color-Emoji (COLR/CPAL) auf einem 32bppArgb-Bitmap mit
        // Clear(Color.Transparent) ZU LEER. TextRenderer.DrawText benutzt
        // die Win32-Text-Pipeline, die Color-Fonts korrekt zeichnet.
        //
        // Trick: Hintergrund opak weiss fuellen, dann mit TextRenderer die
        // Color-Glyphe drueber, dann weisse Pixel auf Alpha=0 maskieren.
        // So bleibt die echte Glyphen-Farbe erhalten und der Hintergrund
        // ist transparent (wichtig fuer Tray/Menu-Rendering).
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            const string fontName = "Segoe UI Emoji";
            // Glyphe bewusst kleiner rendern als das Bitmap (0.7 statt
            // 0.85): TextRenderer fuegt intern ein Font-Linegap hinzu, das
            // bei 0.85f dazu fuehrt, dass der untere Teil der Glyphe im
            // weissen Mask-Bereich landet und dann weggeschnitten wird.
            // Bei 0.7f sitzt die Glyphe sicher im sichtbaren Bereich mit
            // symmetrischem Padding oben/unten.
            float fontSize = size * 0.7f;
            using var font = new Font(fontName, fontSize, FontStyle.Regular, GraphicsUnit.Pixel);

            // TextRenderer nutzt die Uniscribe/DirectWrite-Pipeline und
            // unterstuetzt Color-Fonts. Color.Empty faellt auf Outline-
            // Rendering zurueck (monochrom schwarz), darum wird hier
            // explizit Color.Black gesetzt — das triggert auf Win10/11
            // die COLR/CPAL-Color-Font-Pipeline.
            var format = TextFormatFlags.HorizontalCenter
                       | TextFormatFlags.VerticalCenter
                       | TextFormatFlags.NoPadding
                       | TextFormatFlags.NoPrefix;
            var fg = color ?? Color.Black;
            TextRenderer.DrawText(g, emoji, font, new Rectangle(0, 0, size, size), fg, format);
        }
        // Hintergrund-Pixel (weiss-nahe) auf Alpha=0 setzen, damit das
        // Bitmap im Tray/Menu transparent wirkt. Schwelle 240 verhindert,
        // dass Glyphen-Antialiasing weggeschnitten wird.
        MaskBackgroundToTransparent(bmp, threshold: 240);
        return bmp;
    }

    /// <summary>
    /// Setzt Pixel mit Alpha &gt; 0 und annaehernd weisser Farbe auf
    /// voll transparent. So bleibt das Color-Emoji erhalten, der
    /// Hintergrund verschwindet fuer Menu-Renderer.
    /// </summary>
    private static void MaskBackgroundToTransparent(Bitmap bmp, byte threshold)
    {
        var w = bmp.Width;
        var h = bmp.Height;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.A == 0) continue;
                bool isNearWhite = c.R >= threshold && c.G >= threshold && c.B >= threshold;
                if (isNearWhite)
                {
                    bmp.SetPixel(x, y, Color.FromArgb(0, c.R, c.G, c.B));
                }
            }
        }
    }
}

/// <summary>
/// Cache fuer Menu-Item-Bilder. Verhindert, dass beim wiederholten Oeffnen
/// des ContextMenuStrip jedes Mal ein neuer <see cref="Bitmap"/> erzeugt
/// wird (Dispose-Lawine). Dispose am Controller-Lifetime koppeln.
/// </summary>
internal sealed class MenuImageCache : IDisposable
{
    private readonly Dictionary<string, Image> _cache = new();
    private readonly Dictionary<string, Icon> _iconCache = new();
    private bool _disposed;

    public Image GetOrAdd(string key, Func<Image> factory)
    {
        if (_disposed) return factory();
        if (_cache.TryGetValue(key, out var existing)) return existing;
        var img = factory();
        _cache[key] = img;
        return img;
    }

    /// <summary>
    /// Laedt ein <see cref="Icon"/> aus einer Embedded Resource
    /// (Resources/Icons/&lt;name&gt;.ico). Die Resource wird einmal
    /// geladen und im Cache gehalten, damit bei jedem State-Wechsel
    /// kein neuer Stream geoeffnet werden muss. Dispose am
    /// Controller-Lifetime koppelt.
    /// </summary>
    public Icon GetOrAddEmbeddedIcon(string name)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MenuImageCache));
        if (_iconCache.TryGetValue(name, out var existing)) return existing;
        var asm = typeof(MenuImageCache).Assembly;
        // Manifest-Name = AssemblyName.PfadMitPunkten
        var resourceName = $"{asm.GetName().Name}.Resources.Icons.{name}";
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {resourceName}. " +
                "Did AiRecall.TrayApp.csproj include Resources\\Icons\\*.ico?");
        var icon = new Icon(stream);
        _iconCache[name] = icon;
        return icon;
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var img in _cache.Values) img.Dispose();
        _cache.Clear();
        foreach (var icon in _iconCache.Values) icon.Dispose();
        _iconCache.Clear();
        _disposed = true;
    }
}
