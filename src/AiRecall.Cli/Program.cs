using AiRecall.Cli.Commands;

if (args.Length == 0)
{
    PrintRootUsage();
    return 1;
}

return args[0] switch
{
    "list-windows" or "lsw" => ListWindowsCommand.Run(args[1..]),
    "active-window" or "aw" => ActiveWindowCommand.Run(args[1..]),
    "-h" or "--help" or "help" => PrintRootUsage(returnCode: 0),
    _ => UnknownCommand(args[0])
};

static int UnknownCommand(string cmd)
{
    Console.Error.WriteLine($"Unknown command: {cmd}");
    Console.Error.WriteLine("Run 'recall help' for usage.");
    return 2;
}

static int PrintRootUsage(int returnCode = 1)
{
    Console.WriteLine("AI Recall CLI");
    Console.WriteLine();
    Console.WriteLine("Usage: recall <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  list-windows (lsw)    List all open top-level windows.");
    Console.WriteLine("  active-window (aw)    Capture the current foreground window as PNG + MD.");
    Console.WriteLine();
    Console.WriteLine("Run 'recall <command> --help' for command-specific help.");
    return returnCode;
}
