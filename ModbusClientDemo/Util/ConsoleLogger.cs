using Microsoft.Extensions.Logging;

namespace ModbusClientDemo.Util;

/// <summary>
/// A quick-and-dirty implementation of the <see cref="ILogger"/> interface to use in a sample
/// console application.
/// </summary>
internal class ConsoleLogger : ILogger
{
	private static readonly object locker = new();

	private readonly LogLevel minLevel;

	/// <summary>
	/// Initializes a new instance of the <see cref="ConsoleLogger"/> class.
	/// </summary>
	/// <param name="minLevel">The minimum log level to print to the console. Log entries with a
	///   level below that will not be printed.</param>
	public ConsoleLogger(LogLevel minLevel)
	{
		this.minLevel = minLevel;
	}

	public IDisposable? BeginScope<TState>(TState state)
		where TState : notnull
	{
		throw new NotImplementedException();
	}

	public bool IsEnabled(LogLevel logLevel)
	{
		return logLevel >= minLevel;
	}

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
	{
		if (!IsEnabled(logLevel))
			return;

		lock (locker)
		{
			var color = Console.ForegroundColor;
			switch (logLevel)
			{
				case LogLevel.Trace:
				case LogLevel.Debug:
					Console.ForegroundColor = ConsoleColor.DarkGray;
					break;
				case LogLevel.Information:
					Console.ForegroundColor = ConsoleColor.DarkGreen;
					break;
				case LogLevel.Warning:
					Console.ForegroundColor = ConsoleColor.DarkYellow;
					break;
				case LogLevel.Error:
				case LogLevel.Critical:
					Console.ForegroundColor = ConsoleColor.DarkMagenta;
					break;
			}
			if (state != null)
				Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {GetLevelText(logLevel)}: {state}");
			Console.ForegroundColor = color;
		}
	}

	private static string GetLevelText(LogLevel logLevel) => logLevel switch
	{
		LogLevel.Trace => "T",
		LogLevel.Debug => "D",
		LogLevel.Information => "I",
		LogLevel.Warning => "W",
		LogLevel.Error => "E",
		LogLevel.Critical => "C",
		_ => logLevel.ToString()
	};
}
