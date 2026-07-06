// filepath: tools/EmojiIconGen/Program.cs
// Generiert Multi-Resolution .ico-Dateien (16/24/32/48) aus Emojis.
// Die Glyphe wird in jeder Aufloesung frisch gerendert (TextRenderer +
// anschliessendes Hintergrund-Masking), damit Color-Fonts sauber bleiben.
//
// Aufruf:
//   dotnet run --project tools/EmojiIconGen -- <output-dir>
// Standard-Output: src/AiRecall.TrayApp/Resources/Icons/

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace AiRecall.Tools.EmojiIconGen;

internal static class Program
{
    private static readonly Dictionary<string, string> Icons = new()
    {
        // Tray-Icon (NotifyIcon): zwei States, mehr braucht es nicht.
        // Eye = Recording aktiv, Black Circle = Idle.
        ["tray-idle"]         = "⚫",
        ["tray-recording"]    = "👁\uFE0F",
        // Menu-Items
        ["status-stopped"]    = "🔴",
        ["status-starting"]   = "🟡",
        ["status-running"]    = "🟢",
        ["status-stopping"]   = "🟡",
        ["status-crashed"]    = "⚠",
        ["start"]             = "▶",
        ["stop"]              = "⏸",
        ["logs"]              = "📋",
        ["settings"]          = "⚙",
        ["quit"]              = "🚪",
    };

    private static readonly int[] Sizes = { 16, 24, 32, 48 };

    private static int Main(string[] args)
    {
        var outDir = args.Length > 0
            ? args[0]
            : Path.Combine("src", "AiRecall.TrayApp", "Resources", "Icons");
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"Output: {Path.GetFullPath(outDir)}");

        foreach (var (key, emoji) in Icons)
        {
            var path = Path.Combine(outDir, $"{key}.ico");
            WriteMultiResIco(path, emoji);
            Console.WriteLine($"  {key,-18} {emoji}  -> {Path.GetFileName(path)}");
        }
        Console.WriteLine("Done.");
        return 0;
    }

    private static void WriteMultiResIco(string path, string emoji)
    {
        // Multi-Resolution .ico besteht aus mehreren PNG/PNG-komprimierten
        // Bitmaps, jeweils mit eigenem ICONDIRENTRY. Wir nutzen das
        // ICONDIR (6 Bytes) + ICONDIRENTRY (16 Bytes pro Bild) + die
        // rohen PNG-Daten. PNG in .ico wird seit Vista nativ unterstuetzt.
        var pngPerSize = Sizes.Select(s => RenderToPngBytes(emoji, s)).ToArray();

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        // ICONDIR
        bw.Write((ushort)0);            // Reserved
        bw.Write((ushort)1);            // Type = Icon
        bw.Write((ushort)Sizes.Length);  // Count

        // Offset-Plan: Header (6) + Entries (16 * Count) + Daten
        var dataOffset = 6 + 16 * Sizes.Length;
        var entryOffsets = new int[Sizes.Length];

        // ICONDIRENTRY[]
        for (int i = 0; i < Sizes.Length; i++)
        {
            entryOffsets[i] = dataOffset;
            var s = (byte)Sizes[i]; // ICONDIRENTRY erlaubt nur 0..255; 0 = 256
            bw.Write(s);                              // Width
            bw.Write(s);                              // Height
            bw.Write((byte)0);                        // ColorCount (0 = >=256)
            bw.Write((byte)0);                        // Reserved
            bw.Write((ushort)1);                      // Planes
            bw.Write((ushort)32);                     // BitCount
            bw.Write((uint)pngPerSize[i].Length);     // SizeInBytes
            bw.Write((uint)entryOffsets[i]);          // Offset
            dataOffset += pngPerSize[i].Length;
        }

        // Daten
        foreach (var png in pngPerSize)
        {
            bw.Write(png);
        }
    }

    private static byte[] RenderToPngBytes(string emoji, int size)
    {
        // Glyphe mit TextRenderer-Pipeline rendern (Color-Fonts), dann
        // Weiss-Hintergrund auf Alpha=0 maskieren. Gleiche Logik wie
        // EmojiIconFactory.RenderBitmap, hier nur lokal fuer das Tool.
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var font = new Font("Segoe UI Emoji", size * 0.7f, FontStyle.Regular, GraphicsUnit.Pixel);
            var format = TextFormatFlags.HorizontalCenter
                       | TextFormatFlags.VerticalCenter
                       | TextFormatFlags.NoPadding
                       | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, emoji, font, new Rectangle(0, 0, size, size), Color.Black, format);
        }
        MaskBackgroundToTransparent(bmp, threshold: 240);

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        bmp.Dispose();
        return ms.ToArray();
    }

    private static void MaskBackgroundToTransparent(Bitmap bmp, byte threshold)
    {
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.A == 0) continue;
                if (c.R >= threshold && c.G >= threshold && c.B >= threshold)
                {
                    bmp.SetPixel(x, y, Color.FromArgb(0, c.R, c.G, c.B));
                }
            }
        }
    }
}