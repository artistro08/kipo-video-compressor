namespace Kipo.Core;

/// <summary>Writes failures to Kipo's log file.</summary>
public static class Log
{
    /// <summary>Logs an error.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="exception">Optional exception.</param>
    public static void Error(string message, Exception? exception = null)
    {
    }
}
