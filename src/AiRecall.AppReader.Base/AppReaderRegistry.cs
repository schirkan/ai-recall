using System.Reflection;
using AiRecall.Core.Models;
using Serilog;

namespace AiRecall.AppReader.Base;

/// <summary>
/// Lädt <c>AiRecall.AppReader.*.dll</c> aus einem Plugin-Verzeichnis,
/// instanziiert alle <see cref="IAppReader"/>-Implementierungen und
/// indiziert sie nach Prozessname. Beim Lookup für ein Fenster wird der
/// erste passende Reader zurückgegeben (Best Match: Reihenfolge der DLLs
/// im Verzeichnis).
/// </summary>
public sealed class AppReaderRegistry
{
    private readonly List<IAppReader> _readers;
    private readonly Dictionary<string, IAppReader> _byProcess;
    private readonly ILogger _logger;

    public IReadOnlyList<IAppReader> Readers => _readers;

    private AppReaderRegistry(IEnumerable<IAppReader> readers, ILogger logger)
    {
        _readers = readers.ToList();
        _byProcess = new Dictionary<string, IAppReader>(StringComparer.OrdinalIgnoreCase);
        foreach (var reader in _readers)
        {
            foreach (var process in reader.SupportedProcesses)
            {
                if (!_byProcess.ContainsKey(process))
                {
                    _byProcess[process] = reader;
                }
            }
        }
        _logger = logger;
    }

    /// <summary>
    /// Scannt <paramref name="pluginDirectory"/> nach <c>AiRecall.AppReader.*.dll</c>,
    /// lädt sie und sammelt alle <see cref="IAppReader"/>-Implementierungen.
    /// Fehler beim Laden einzelner DLLs werden geloggt und übersprungen.
    /// </summary>
    public static AppReaderRegistry LoadFromDirectory(string pluginDirectory, ILogger logger)
    {
        var readers = new List<IAppReader>();

        if (!Directory.Exists(pluginDirectory))
        {
            logger.Warning("AppReader plugin directory not found: {Dir}", pluginDirectory);
            return new AppReaderRegistry(readers, logger);
        }

        var dlls = Directory.GetFiles(pluginDirectory, "AiRecall.AppReader.*.dll", SearchOption.TopDirectoryOnly);
        logger.Information("Scanning {Count} AppReader plugin DLL(s) in {Dir}", dlls.Length, pluginDirectory);

        foreach (var dll in dlls)
        {
            try
            {
                var asm = Assembly.LoadFrom(dll);
                var types = asm.GetTypes().Where(t =>
                    typeof(IAppReader).IsAssignableFrom(t) &&
                    t is { IsClass: true, IsAbstract: false } &&
                    t.GetConstructor(Type.EmptyTypes) is not null);

                foreach (var type in types)
                {
                    try
                    {
                        var instance = (IAppReader)Activator.CreateInstance(type)!;
                        readers.Add(instance);
                        logger.Information("Loaded AppReader: {Reader} ({Type}) from {Dll}",
                            instance.DisplayName, type.FullName, Path.GetFileName(dll));
                    }
                    catch (Exception ex)
                    {
                        logger.Warning(ex, "Failed to instantiate AppReader {Type} from {Dll}", type.FullName, dll);
                    }
                }
            }
            catch (ReflectionTypeLoadException rtlEx)
            {
                foreach (var ex in rtlEx.LoaderExceptions)
                {
                    logger.Warning(ex, "Loader exception for {Dll}", dll);
                }
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Failed to load AppReader DLL {Dll}", dll);
            }
        }

        return new AppReaderRegistry(readers, logger);
    }

    /// <summary>
    /// Leerer Registry für Tests oder wenn AppReader komplett deaktiviert ist.
    /// </summary>
    public static AppReaderRegistry Empty(ILogger logger) => new(Array.Empty<IAppReader>(), logger);

    /// <summary>
    /// Factory für Tests: konstruiert einen Registry aus bereits instanziierten Readern.
    /// </summary>
    public static AppReaderRegistry FromReaders(IEnumerable<IAppReader> readers, ILogger logger)
        => new(readers, logger);

    /// <summary>
    /// Liefert den ersten Reader, der <see cref="IAppReader.CanRead"/> für
    /// das Fenster zurückgibt, oder <c>null</c>.
    /// </summary>
    public IAppReader? FindForWindow(WindowInfo window)
    {
        // First try fast O(1) lookup by process name.
        if (_byProcess.TryGetValue(window.ProcessName, out var cached))
        {
            return cached.CanRead(window) ? cached : null;
        }
        // Fallback: full scan (für Reader mit zusätzlicher Title-Heuristik).
        foreach (var reader in _readers)
        {
            if (reader.CanRead(window)) return reader;
        }
        return null;
    }

    /// <summary>
    /// Convenience: Findet einen passenden Reader und ruft <see cref="IAppReader.Read"/>
    /// auf. Liefert <c>null</c> wenn kein Reader matched oder <c>Read</c>
    /// selbst <c>null</c> liefert.
    /// </summary>
    public AppReaderResult? TryRead(WindowInfo window, AppReaderContext context)
    {
        var reader = FindForWindow(window);
        if (reader is null) return null;

        try
        {
            var result = reader.Read(window, context);
            if (result is not null)
            {
                _logger.Debug("AppReader {Reader} returned content for {Process}/{Title}",
                    reader.DisplayName, window.ProcessName, window.Title);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "AppReader {Reader} threw on {Process}/{Title}",
                reader.DisplayName, window.ProcessName, window.Title);
            return null;
        }
    }
}