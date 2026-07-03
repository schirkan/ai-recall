using AiRecall.Core.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace AiRecall.Cli.Logging;

/// <summary>
/// Builds a Serilog <see cref="Logger"/> from <see cref="LoggingConfig"/>.
/// Always writes to console. Adds a daily-rolling file sink when a path is set.
/// </summary>
public static class SerilogSetup
{
    public static Logger Create(LoggingConfig config)
    {
        var level = ParseLevel(config.Level);
        var lc = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");

        if (!string.IsNullOrWhiteSpace(config.Path))
        {
            var logDir = Path.IsPathRooted(config.Path)
                ? config.Path
                : Path.Combine(AppContext.BaseDirectory, config.Path);
            Directory.CreateDirectory(logDir);
            lc = lc.WriteTo.File(
                Path.Combine(logDir, "ai-recall-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:O} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        }

        return lc.CreateLogger();
    }

    public static LogEventLevel ParseLevel(string? level) => level?.ToLowerInvariant() switch
    {
        "verbose" or "trace" => LogEventLevel.Verbose,
        "debug" => LogEventLevel.Debug,
        "info" or "information" or null => LogEventLevel.Information,
        "warn" or "warning" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        "fatal" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information
    };
}
