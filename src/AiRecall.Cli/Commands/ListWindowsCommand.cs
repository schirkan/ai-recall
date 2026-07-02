using AiRecall.ScreenCapture.Windows;

namespace AiRecall.Cli.Commands;

internal static class ListWindowsCommand
{
    public static int Run(string[] args)
    {
        bool includeInvisible = false;
        bool includeUntitled = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--all":
                    includeInvisible = true;
                    includeUntitled = true;
                    break;
                case "--include-invisible":
                    includeInvisible = true;
                    break;
                case "--include-untitled":
                    includeUntitled = true;
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return 0;
            }
        }

        var windows = WindowEnumerator.Enumerate(includeInvisible, includeUntitled);

        Console.WriteLine($"{"PID",-7} {"HWND",-12} {"Visible",-7}  {"Process",-24}  Title");
        Console.WriteLine(new string('-', 100));

        foreach (var w in windows.OrderBy(w => w.ProcessName).ThenBy(w => w.Title))
        {
            string vis = w.IsVisible ? "yes" : "no";
            string title = string.IsNullOrEmpty(w.Title) ? "<untitled>" : w.Title;
            Console.WriteLine($"{w.ProcessId,-7} 0x{w.Handle.ToInt64():X10} {vis,-7}  {Truncate(w.ProcessName, 24),-24}  {Truncate(title, 60)}");
        }

        Console.WriteLine();
        Console.WriteLine($"Total: {windows.Count} window(s)");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: recall list-windows [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --all                   Include invisible and untitled windows.");
        Console.WriteLine("  --include-invisible    Include invisible windows.");
        Console.WriteLine("  --include-untitled     Include windows with empty titles.");
        Console.WriteLine("  -h, --help             Show this help.");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}