using System.Runtime.InteropServices;
using AiRecall.AppReader.Base;
using AiRecall.Core.Configuration;
using AiRecall.Trigger;
using Serilog;

namespace AiRecall.TrayApp;

/// <summary>Offline verification of the shipping EXE, without starting the recorder.</summary>
internal static class PackageSmokeTest
{
    public static int Run()
    {
        try
        {
            ApplicationConfiguration.Initialize();
            using var form = new Form();
            using var logger = new LoggerConfiguration().CreateLogger();
            var assets = Path.GetDirectoryName(typeof(AppReaderRegistry).Assembly.Location)
                ?? throw new InvalidOperationException("The packaged assemblies were not extracted.");
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath)!;
            if (!string.Equals(AppPaths.ResolveDataPath("logs"), Path.Combine(exeDir, "logs"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("User data would be written into the bundle cache.");
            var readers = AppReaderRegistry.LoadFromDirectory(assets, logger);
            string[] expected = ["BrowserAppReader", "ExcelAppReader", "ExplorerAppReader", "NotepadAppReader",
                "OneNoteAppReader", "OutlookAppReader", "PdfViewerAppReader", "PowerPointAppReader", "TeamsAppReader", "WordAppReader"];
            var actual = readers.Readers.Select(reader => reader.GetType().Name).ToHashSet(StringComparer.Ordinal);
            if (!expected.All(actual.Contains))
                throw new InvalidOperationException("Missing readers: " + string.Join(", ", expected.Where(reader => !actual.Contains(reader))));

            var config = new AppConfig { Capture = { RootPath = "package-smoke" } };
            if (ConfigSerializer.Deserialize(ConfigSerializer.Serialize(config)).Capture.RootPath != config.Capture.RootPath)
                throw new InvalidOperationException("Config serialization failed.");

            var ocrDirectory = Path.Combine(assets, "x64");
            var leptonica = NativeLibrary.Load(Path.Combine(ocrDirectory, "leptonica-1.82.0.dll"));
            try
            {
                var tesseract = NativeLibrary.Load(Path.Combine(ocrDirectory, "tesseract50.dll"));
                NativeLibrary.Free(tesseract);
            }
            finally { NativeLibrary.Free(leptonica); }
            var speech = Directory.EnumerateFiles(assets, "Microsoft.CognitiveServices.Speech.core.dll", SearchOption.AllDirectories).Single();
            NativeLibrary.Free(NativeLibrary.Load(speech));
            if (Directory.Exists(Path.Combine(assets, "x86")))
                throw new InvalidOperationException("x86 payload in the x64 build.");

            Console.WriteLine($"Package OK: {actual.Count} readers, WinForms, config, x64 OCR and Speech SDK; app base: {AppContext.BaseDirectory}; bundle assets: {assets}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
