using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace ObsidianDB.Logging;

/// <summary>
/// Provides logging services for the ObsidianDB application.
/// </summary>
public static class LoggerService
{
    private static ILoggerFactory? _loggerFactory;
    private static ILoggerFactory LoggerFactory => _loggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
    {
        builder.AddConsole();
        builder.SetMinimumLevel(LogLevel.Information);
    });

    /// <summary>
    /// Configures the logging level and providers.
    /// </summary>
    /// <param name="configure">Action to configure the logging builder.</param>
    public static void ConfigureLogging(Action<ILoggingBuilder> configure)
    {
        _loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(configure);
    }

    /// <summary>
    /// Gets a logger for the specified type.
    /// </summary>
    /// <typeparam name="T">The type to create a logger for.</typeparam>
    /// <returns>An ILogger instance.</returns>
    public static ILogger<T> GetLogger<T>() => LoggerFactory.CreateLogger<T>();

    /// <summary>
    /// Gets a logger for the specified category name.
    /// </summary>
    /// <param name="categoryName">The category name for the logger.</param>
    /// <returns>An ILogger instance.</returns>
    public static ILogger GetLogger(string categoryName) => LoggerFactory.CreateLogger(categoryName);
} 