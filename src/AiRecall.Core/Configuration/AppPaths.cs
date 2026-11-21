namespace AiRecall.Core.Configuration;

/// <summary>Stable location for relative user paths, separate from extracted bundle assets.</summary>
public static class AppPaths
{
    public static string DataDirectory { get; private set; } = AppContext.BaseDirectory;

    public static void UseExecutableDirectory()
    {
        var process = Environment.ProcessPath;
        if (process is null || string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase))
            return;
        DataDirectory = Path.GetDirectoryName(process) ?? AppContext.BaseDirectory;
    }

    public static string ResolveDataPath(string path) => Path.GetFullPath(path, DataDirectory);
}
