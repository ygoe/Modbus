using Microsoft.Extensions.Logging;

namespace Unclassified.Modbus.Tests.Fakes;

/// <summary>
/// A logger that records the level and formatted message of all log entries.
/// </summary>
internal class ListLogger : ILogger
{
	#region Properties

	/// <summary>
	/// Gets the recorded log entries in the order they were logged.
	/// </summary>
	public List<(LogLevel Level, string Message)> Entries { get; } = [];

	#endregion Properties

	#region ILogger implementation

	/// <summary>
	/// Does not support scopes.
	/// </summary>
	public IDisposable? BeginScope<TState>(TState state)
		where TState : notnull
	{
		return null;
	}

	/// <summary>
	/// Returns true for all log levels.
	/// </summary>
	public bool IsEnabled(LogLevel logLevel)
	{
		return true;
	}

	/// <summary>
	/// Records the log entry.
	/// </summary>
	public void Log<TState>(
		LogLevel logLevel,
		EventId eventId,
		TState state,
		Exception? exception,
		Func<TState, Exception?, string> formatter)
	{
		lock (Entries)
		{
			Entries.Add((logLevel, formatter(state, exception)));
		}
	}

	#endregion ILogger implementation
}
