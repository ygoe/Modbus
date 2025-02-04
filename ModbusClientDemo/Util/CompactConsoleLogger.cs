using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ModbusClientDemo.Util;

/// <summary>
/// A simple <see cref="ILogger"/> implementation that logs to the console in a compact format, with
/// different colors per level and an asynchronous queue for improved performance.
/// </summary>
internal class CompactConsoleLogger : ILogger
{
	#region Static members

	private static readonly AsyncQueueSlim<LogEntry> queue = new();
	private static readonly bool showPropertyNames = true;

	private static DateTime? lastLogTime;
	private static bool? isColorEnabled;

	static CompactConsoleLogger()
	{
		// Hack for static destructor which is not available in C#/.NET
		// Source: https://stackoverflow.com/a/13258842/143684
		AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

		Task.Run(() => queue.RunDequeueOneForever(PrintLogEntry));
	}

	private static void OnProcessExit(object? sender, EventArgs args)
	{
		for (int i = 0; i < 50 && queue.Count > 0; i++)
		{
			Thread.Sleep(10);
		}
	}

	public static void Log(LogLevel logLevel, string message, Exception? exception = null)
	{
		queue.Enqueue(new(logLevel, null, message, exception, null));
	}

	#endregion Static members

	#region Private data

	private readonly string? name;
	private readonly Func<CompactConsoleLoggerConfiguration>? getCurrentConfig;
	private readonly LogLevel minLevel;

	#endregion Private data

	#region Constructors

	/// <summary>
	/// Initializes a new instance of the <see cref="CompactConsoleLogger"/> class.
	/// </summary>
	/// <param name="name">The logger name.</param>
	/// <param name="getCurrentConfig">A function that returns the current logger configuration.</param>
	/// <param name="minLevel">The minimum log level to print to the console. Log entries with a
	///   level below that will not be printed.</param>
	public CompactConsoleLogger(string? name, Func<CompactConsoleLoggerConfiguration>? getCurrentConfig = null, LogLevel minLevel = LogLevel.Trace)
	{
		this.name = name;
		this.getCurrentConfig = getCurrentConfig;
		this.minLevel = minLevel;
	}

	#endregion Constructors

	#region ILogger implementation

	public IDisposable? BeginScope<TState>(TState state)
		where TState : notnull
	{
		return default;
	}

	public bool IsEnabled(LogLevel logLevel)
	{
		return logLevel >= minLevel;
	}

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
	{
		if (!IsEnabled(logLevel))
			return;

		string? shortName = name;
		string[]? trimNamePrefixes = getCurrentConfig?.Invoke().TrimNamePrefixes;
		if (name != null && trimNamePrefixes != null)
		{
			foreach (string prefix in trimNamePrefixes)
			{
				if (name.StartsWith(prefix))
				{
					shortName = name[prefix.Length..];
					break;
				}
			}
		}

		if (state is IReadOnlyList<KeyValuePair<string, object?>> properties &&
			properties[^1].Key == "{OriginalFormat}" && properties[^1].Value is string originalFormat)
		{
			queue.Enqueue(new(logLevel, shortName, originalFormat, exception, properties));
		}
		else
		{
			string formattedMessage = formatter(state, null);
			queue.Enqueue(new(logLevel, shortName, formattedMessage, exception, null));
		}
	}

	#endregion ILogger implementation

	#region Log output function

	private static void PrintLogEntry(LogEntry logEntry)
	{
		int pauseLength = 0;
		if (logEntry.Time >= lastLogTime?.AddHours(10))
		{
			pauseLength = 12;
		}
		else if (logEntry.Time >= lastLogTime?.AddHours(1))
		{
			pauseLength = 11;
		}
		else if (logEntry.Time >= lastLogTime?.AddMinutes(10))
		{
			pauseLength = 9;
		}
		else if (logEntry.Time >= lastLogTime?.AddMinutes(1))
		{
			pauseLength = 8;
		}
		else if (logEntry.Time >= lastLogTime?.AddSeconds(10))
		{
			pauseLength = 6;
		}
		else if (logEntry.Time >= lastLogTime?.AddSeconds(1))
		{
			pauseLength = 5;
		}
		else if (logEntry.Time >= lastLogTime?.AddMilliseconds(100))
		{
			pauseLength = 3;
		}

		if (lastLogTime != null &&
			logEntry.Time.Date != ((DateTime)lastLogTime).Date)
		{
			WriteLogLine($"{Style(AnsiEscape.ForegroundBrightWhite)}--- {logEntry.Time:yyyy-MM-dd} ---{Style(AnsiEscape.ForegroundDefault)}{Environment.NewLine}");
		}
		lastLogTime = logEntry.Time;

		bool exceptionInColor = false;
		string textColor = "";
		switch (logEntry.LogLevel)
		{
			case LogLevel.Trace:
			case LogLevel.Debug:
				textColor = AnsiEscape.ForegroundBrightBlack;
				exceptionInColor = true;
				break;
			case LogLevel.Information:
				textColor = AnsiEscape.ForegroundGreen;
				break;
			case LogLevel.Warning:
				textColor = AnsiEscape.ForegroundYellow;
				break;
			case LogLevel.Error:
			case LogLevel.Critical:
				textColor = AnsiEscape.ForegroundRed;
				break;
		}

		var sb = new StringBuilder();
		sb.Append(Style(textColor));

		sb.Append(logEntry.Time.ToString("HH:mm:ss.fff"));
		if (pauseLength > 0)
		{
			sb.Insert(sb.Length - pauseLength, Style(AnsiEscape.Overline));
			sb.Append(Style(AnsiEscape.NoOverline));
		}

		sb.Append(' ');
		sb.Append(GetLevelText(logEntry.LogLevel));
		sb.Append(": ");
		if (!string.IsNullOrEmpty(logEntry.Name))
			sb.Append('[').Append(logEntry.Name).Append("] ");

		string? message = logEntry.Text?.Replace("\r", "").Replace("\n", " ");
		if (message != null && logEntry.Properties != null)
		{
			int startPos = 0;
			int propIndex = 0;
			while (true)
			{
				if (propIndex >= logEntry.Properties.Count - 1)
				{
					// Running out of properties, don't resolve the remaining braces
					sb.AppendLine(message[startPos..]);
					break;
				}
				int bracePos = message.IndexOf('{', startPos);
				if (bracePos == -1)
				{
					sb.AppendLine(message[startPos..]);
					break;
				}
				sb.Append(message[startPos..bracePos]);
				int endPos = message.IndexOf('}', bracePos + 1);
				if (endPos == -1)
				{
					sb.AppendLine(message[startPos..]);
					break;
				}
				string name = message[(bracePos + 1)..endPos];
				sb.Append(Style(AnsiEscape.Italic));
				if (showPropertyNames)
				{
					sb.Append(Style(AnsiEscape.Dim));
					sb.Append(name);
					sb.Append(':');
					sb.Append(Style(AnsiEscape.NoBoldOrDim));
				}
				try
				{
					sb.Append(FormatValue(logEntry.Properties[propIndex].Value));
				}
				catch (IndexOutOfRangeException)
				{
					sb.Append("<missing!>");
				}
				sb.Append(Style(AnsiEscape.NoItalic));
				startPos = endPos + 1;
				propIndex++;
			}
		}
		else
		{
			sb.AppendLine(message);
		}

		if (!exceptionInColor)
			sb.Append(Style(AnsiEscape.Reset));
		// TODO: Disabled here, needs more support code
		//if (logEntry.Exception != null)
		//	sb.Append("| ").AppendLine(logEntry.Exception.GetStackTrace(ExceptionStackTraceOptions.IncludeData).TrimEnd().Replace("\n", "\n| "));
		if (exceptionInColor)
			sb.Append(Style(AnsiEscape.Reset));
		WriteLogLine(sb.ToString());
	}

	// TODO: File writing is a prototype
	public static string? LogFileName { get; set; }

	private static void WriteLogLine(string line)
	{
		Console.Write(line);

		if (!string.IsNullOrWhiteSpace(LogFileName))
		{
			if (!File.Exists(LogFileName))
			{
				File.WriteAllLines(
					LogFileName,
					[
						"<!doctype html>",
						"<html>",
						"<head>",
						"<style>",
						"  pre { margin: 0; }",
						"</style>",
						"</head>",
						"<body onload='updateSearch()'>",
						"<script>",
						"function updateSearch() {",
						"  let search = document.querySelector('#searchText').value.toLowerCase();",
						"  document.querySelectorAll('pre').forEach(pre => {",
						"    pre.style.display = (search === '' || pre.textContent.toLowerCase().includes(search)) ? '' : 'none';",
						"  });",
						"}",
						"</script>",
						"<div>",
						"Search: <input id='searchText' oninput='updateSearch()' size='60'>",
						"</div>",
					]);
			}
			File.AppendAllText(LogFileName, "<pre>" + ConvertAnsiEscapeToHtml(line).TrimEnd().Replace("\r", "").Replace("\n", "<br>" + Environment.NewLine) + "</pre>" + Environment.NewLine);
		}
	}

	private static string ConvertAnsiEscapeToHtml(string str)
	{
		str = Regex.Replace(str, @"\x1b\[.+?m", "");
		return str;
	}

	#endregion Log output function

	#region Color and formatting

	#region WinAPI

	// Source: https://www.pinvoke.net/default.aspx/kernel32/GetStdHandle.html
	private const int STD_OUTPUT_HANDLE = -11;
	// Source: https://www.pinvoke.net/default.aspx/Constants/INVALID_HANDLE_VALUE.html
	private const nint INVALID_HANDLE_VALUE = -1;
	// Source: https://learn.microsoft.com/en-us/windows/console/setconsolemode
	private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x4;

	// Source: https://www.pinvoke.net/default.aspx/kernel32/ConsoleFunctions.html
	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint GetStdHandle(int nStdHandle);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

	private static bool EnableVirtualTerminalProcessing()
	{
		// Source: https://learn.microsoft.com/en-us/windows/console/console-virtual-terminal-sequences#samples
		nint hOut = GetStdHandle(STD_OUTPUT_HANDLE);
		if (hOut != INVALID_HANDLE_VALUE)
		{
			if (GetConsoleMode(hOut, out uint dwMode))
			{
				if ((dwMode & ENABLE_VIRTUAL_TERMINAL_PROCESSING) != 0)
				{
					return true;
				}
				dwMode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING;
				if (SetConsoleMode(hOut, dwMode))
				{
					return true;
				}
			}
		}
		return false;
	}

	#endregion WinAPI

	private static string Style(string ansiEscape) => IsColorEnabled() ? ansiEscape : "";

	private static bool IsColorEnabled()
	{
		if (isColorEnabled == null)
		{
			if (Environment.OSVersion.Platform == PlatformID.Win32NT)
			{
				isColorEnabled = EnableVirtualTerminalProcessing();
			}
			else
			{
				string? term = Environment.GetEnvironmentVariable("TERM");
				isColorEnabled = term == "xterm-color" || term?.EndsWith("-256color") == true;
			}
		}
		return (bool)isColorEnabled;
	}

	private static string GetLevelText(LogLevel logLevel) => logLevel switch
	{
		LogLevel.Trace => "T",
		LogLevel.Debug => "D",
		LogLevel.Information => "I",
		LogLevel.Warning => "W",
		LogLevel.Error => "E",
		LogLevel.Critical => "C",
		LogLevel.None => "N",
		_ => logLevel.ToString()
	};

	private static string FormatValue(object? value)
	{
		if (value is DateTime dt)
		{
			return dt.ToString("yyyy-MM-dd HH:mm:ss");
		}
		return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
	}

	#endregion Color and formatting

	private class LogEntry
	{
		public LogEntry(LogLevel logLevel, string? name, string? text, Exception? exception, IReadOnlyList<KeyValuePair<string, object?>>? properties)
		{
			LogLevel = logLevel;
			Name = name;
			Text = text;
			Exception = exception;
			Properties = properties;
		}

		public DateTime Time { get; } = DateTime.Now;

		public LogLevel LogLevel { get; }

		public string? Name { get; }

		public string? Text { get; }

		public Exception? Exception { get; }

		public IReadOnlyList<KeyValuePair<string, object?>>? Properties { get; }
	}
}

/// <summary>
/// Represents a type that can create instances of <see cref="CompactConsoleLogger"/>.
/// </summary>
public sealed class CompactConsoleLoggerProvider : ILoggerProvider
{
	private CompactConsoleLoggerConfiguration currentConfig;
	private readonly IDisposable? onChangeToken;

	/// <summary>
	/// Initializes a new instance of the <see cref="CompactConsoleLoggerProvider"/> class.
	/// </summary>
	/// <param name="config">Provides the logger configuration and notifies about changes.</param>
	public CompactConsoleLoggerProvider(IOptionsMonitor<CompactConsoleLoggerConfiguration> config)
	{
		currentConfig = config.CurrentValue;
		onChangeToken = config.OnChange(updatedConfig => currentConfig = updatedConfig);
	}

	/// <summary>
	/// Creates a new <see cref="CompactConsoleLogger"/> instance.
	/// </summary>
	/// <param name="categoryName">The category name for messages produced by the logger.</param>
	/// <returns>The instance of <see cref="CompactConsoleLogger"/> that was created.</returns>
	public ILogger CreateLogger(string categoryName)
	{
		return new CompactConsoleLogger(categoryName, GetCurrentConfig);
	}

	/// <summary>
	/// Does nothing.
	/// </summary>
	public void Dispose()
	{
		onChangeToken?.Dispose();
		GC.SuppressFinalize(this);
	}

	private CompactConsoleLoggerConfiguration GetCurrentConfig() => currentConfig;
}

/// <summary>
/// Provides extension methods for using the <see cref="CompactConsoleLogger"/> class.
/// </summary>
public static class CompactConsoleLoggerExtensions
{
	/// <summary>
	/// Adds a console logger named 'CompactConsole' to the factory.
	/// </summary>
	/// <param name="builder">The <see cref="ILoggingBuilder"/> to use.</param>
	/// <returns>The <paramref name="builder"/>.</returns>
	public static ILoggingBuilder AddCompactConsoleLogger(this ILoggingBuilder builder)
	{
		builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, CompactConsoleLoggerProvider>());
		return builder;
	}

	/// <summary>
	/// Adds a logger named 'File' to the factory.
	/// </summary>
	/// <param name="builder">The <see cref="ILoggingBuilder"/> to use.</param>
	/// <param name="configure">A delegate to configure the <see cref="CompactConsoleLogger"/>.</param>
	/// <returns>The <paramref name="builder"/>.</returns>
	public static ILoggingBuilder AddCompactConsoleLogger(this ILoggingBuilder builder, Action<CompactConsoleLoggerConfiguration> configure)
	{
		builder.AddCompactConsoleLogger();
		builder.Services.Configure(configure);
		return builder;
	}
}

/// <summary>
/// Options for a <see cref="CompactConsoleLogger"/>.
/// </summary>
public class CompactConsoleLoggerConfiguration
{
	/// <summary>
	/// Gets or sets the logger name prefixes to remove from the beginning of a logger name. These
	/// can be application namespaces that contain unambiguous class names and improves readability
	/// of the log output.
	/// </summary>
	public string[]? TrimNamePrefixes { get; set; }
}
