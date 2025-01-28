using System.Globalization;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ModbusClientDemo.Util;
using Unclassified.Modbus;

namespace ModbusClientDemo;

public class Program
{
	private static ConsoleLogger? logger;
	private static LogLevel minLogLevel = LogLevel.None;
	private static IModbusConnectionFactory? connectionFactory;
	private static int deviceId;
	private static CancellationTokenSource? currentCommandCts;
	private static bool ctrlCPressed;
	private static string? lastCommand;
	private static bool isLooping;

	/// <summary>
	/// Application entry point.
	/// </summary>
	public static async Task<int> Main(string[] args)
	{
		try
		{
			ParseArguments(args);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine(ex.Message);
			return 1;
		}

		logger = new(minLogLevel);
		var client = new ModbusClient(connectionFactory!, logger);

		// 80 columns:     ---------+---------+---------+---------+---------+---------+---------+---------+
		Console.WriteLine("Simple Modbus client");
		Console.WriteLine("");
		Console.WriteLine("Connection: " + connectionFactory!.ToString());
		Console.WriteLine("");
		Console.WriteLine("Commands:");
		Console.WriteLine("  Set device ID: to [(he)x] <deviceid>");
		Console.WriteLine("  Read request: <objtype> <range> [:<datatype>]");
		Console.WriteLine("  Write request: <objtype> <address> [:<datatype>] = <value> [, <value> ...]");
		Console.WriteLine("  Read device ID: id");
		Console.WriteLine("  Loop last command: loop <interval ms>");
		Console.WriteLine("  Exit: exit");
		Console.WriteLine("");
		Console.WriteLine("Object types: c(oil) | d(iscrete input) | h(olding reg.) | i(nput reg.)");
		Console.WriteLine("Address range: [(he)x] <start> [-<end>] [, <range>]");
		Console.WriteLine("Data types: u16|i16|u32|i32|u64|i64|f32|f64|str8.<len>|str16.<len>");
		Console.WriteLine("");
		Console.WriteLine("Examples:");
		Console.WriteLine("  Read 6 single precision float from holding registers starting at 19000");
		Console.WriteLine("    h19000-19010:f32");
		Console.WriteLine("  Read 2 16-bit unsigned integers from input registers 1000 and hex 2000");
		Console.WriteLine("    i1000,x2000:u16");
		Console.WriteLine("  Read 10-char ASCII string from holding registers starting at 20");
		Console.WriteLine("    h20:str8.10");
		Console.WriteLine("");

		Console.CancelKeyPress += OnCancelKeyPress;

		while (true)
		{
			Console.Write(deviceId + "> ");
			string? line = Console.ReadLine();

			if (line == null)   // Ctrl+C on Windows, Ctrl+D on Linux
			{
				// Wait for ctrlCPressed to be updated by the CancelKeyPress handler
				await Task.Delay(50);
				if (ctrlCPressed)
				{
					// Don't terminate the program when pressing Ctrl+C in the moment when the command completes
					Console.WriteLine("Hint: Type 'exit' to terminate the program.");
					ctrlCPressed = false;
					continue;
				}
				else
				{
					Console.WriteLine();
					break;
				}
			}
			line = line.Trim();
			if (line == "")
				continue;

			currentCommandCts = new CancellationTokenSource();
			var currentCancellationToken = currentCommandCts.Token;
			try
			{
				if (!await ParseCommand(line, client, currentCancellationToken))
					break;
			}
			catch (OperationCanceledException ex) when (ex.CancellationToken == currentCancellationToken)
			{
				Console.Error.WriteLine("Cancelled");
			}
			catch (TimeoutException)
			{
				Console.Error.WriteLine("Timeout");
			}
			catch (SocketException ex)
			{
				Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
			}
			catch (ModbusException ex)
			{
				Console.Error.WriteLine("Modbus exception response: " + ex.Message);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine(ex);
			}
			currentCommandCts.Dispose();
			currentCommandCts = null;
			ctrlCPressed = false;
		}
		return 0;
	}

	/// <summary>
	/// Parses the command line arguments into the class fields.
	/// </summary>
	/// <remarks>
	/// Supported arguments:
	/// <list type="bullet">
	/// <item>
	/// -tcp host[:port]<br/>
	/// port: default is 502
	/// </item>
	/// <item>
	/// -serial serialport[@baudrate[parity[stopbits]]]<br/>
	/// baudrate: default is 9600<br/>
	/// parity: none|even|odd|mark|space (case-insensitive, can be abbreviated to first character), default is even<br/>
	/// stopbits: 0|1|1.5|2, default depends on parity (1 or 2)
	/// </item>
	/// <item>
	/// -log level<br/>
	/// level: trace|debug|info|warn|error|critical|none (case-insensitive, can be abbreviated to first character), default is none
	/// </item>
	/// </list>
	/// </remarks>
	private static void ParseArguments(string[] args)
	{
		Match match;
		for (int i = 0; i < args.Length; i++)
		{
			if (args[i] == "-tcp")
			{
				i++;
				if (i >= args.Length)
				{
					throw new ArgumentException("Missing TCP argument");
				}

				if ((match = Regex.Match(args[i], @"^(\S+?):(\d+)$")).Success)
				{
					connectionFactory = new ModbusTcpConnectionFactory(match.Groups[1].Value, int.Parse(match.Groups[2].Value));
				}
				else if ((match = Regex.Match(args[i], @"^(\S+?)$")).Success)
				{
					connectionFactory = new ModbusTcpConnectionFactory(match.Groups[1].Value, 502);
				}
				else
				{
					throw new ArgumentException("Invalid TCP argument: " + args[i]);
				}
			}
			else if (args[i] == "-serial")
			{
				i++;
				if (i >= args.Length)
				{
					throw new ArgumentException("Missing serial argument");
				}

				if ((match = Regex.Match(args[i], @"^([^@]+?)@(\d+)([neoms][a-z]*)(0|1|1.5|2)?$")).Success)
				{
					var parity = char.ToLowerInvariant(match.Groups[3].Value[0]) switch
					{
						'n' => Parity.None,
						'e' => Parity.Even,
						'o' => Parity.Odd,
						'm' => Parity.Mark,
						's' => Parity.Space,
						_ => throw new ArgumentException("Invalid serial parity argument: " + match.Groups[3].Value)
					};
					if (match.Groups[4].Success)
					{
						var stopBitsOverride = match.Groups[4].Value switch
						{
							"0" => StopBits.None,
							"1" => StopBits.One,
							"1.5" => StopBits.OnePointFive,
							"2" => StopBits.Two,
							_ => throw new ArgumentException("Invalid serial stop bits argument: " + match.Groups[4].Value)
						};
						connectionFactory = new ModbusSerialConnectionFactory(match.Groups[1].Value, int.Parse(match.Groups[2].Value), parity, stopBitsOverride);
					}
					else
					{
						connectionFactory = new ModbusSerialConnectionFactory(match.Groups[1].Value, int.Parse(match.Groups[2].Value), parity);
					}
				}
				else if ((match = Regex.Match(args[i], @"^([^@]+?)@(\d+)$")).Success)
				{
					connectionFactory = new ModbusSerialConnectionFactory(match.Groups[1].Value, int.Parse(match.Groups[2].Value));
				}
				else if ((match = Regex.Match(args[i], @"^([^@]+?)$")).Success)
				{
					connectionFactory = new ModbusSerialConnectionFactory(match.Groups[1].Value);
				}
				else
				{
					throw new ArgumentException("Invalid serial argument: " + args[i]);
				}
			}
			else if (args[i] == "-log")
			{
				i++;
				if (i >= args.Length)
				{
					throw new ArgumentException("Missing log argument");
				}

				minLogLevel = char.ToLowerInvariant(args[i][0]) switch
				{
					't' => LogLevel.Trace,
					'd' => LogLevel.Debug,
					'i' => LogLevel.Information,
					'w' => LogLevel.Warning,
					'e' => LogLevel.Error,
					'c' => LogLevel.Critical,
					'n' => LogLevel.None,
					_ => throw new ArgumentException("Invalid log argument: " + args[i])
				}; ;
			}
			else
			{
				throw new ArgumentException("Invalid argument: " + args[i]);
			}
		}

		if (connectionFactory == null)
		{
			throw new ArgumentException("Missing arguments: -tcp <host>[:<port>] | -serial <serialport>[@<baudrate>[<parity>[<stopbits>]]] [-log t|d|i|w|e|c|n]");
		}
	}

	/// <summary>
	/// Handles a Ctrl+C key press to cancel the current Modbus command.
	/// </summary>
	private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs args)
	{
		ctrlCPressed = true;
		Console.WriteLine("^C");
		// Cancel the current command
		currentCommandCts?.Cancel();
		// Don't terminate the program
		args.Cancel = true;
	}

	/// <summary>
	/// Parses an interactive command that the user has entered.
	/// </summary>
	private static async Task<bool> ParseCommand(string line, ModbusClient client, CancellationToken cancellationToken)
	{
		Match match;

		// Exit
		if ((match = Regex.Match(line, @"^(exit|quit|bye)$", RegexOptions.IgnoreCase)).Success)
		{
			return false;
		}

		// Set device id
		else if ((match = Regex.Match(line, @"^to\s*(\d+)$", RegexOptions.IgnoreCase)).Success)
		{
			deviceId = int.Parse(match.Groups[1].Value);

			Console.WriteLine($"New device ID: {deviceId}");
		}
		else if ((match = Regex.Match(line, @"^to\s*x\s*([0-9a-f]+)$", RegexOptions.IgnoreCase)).Success)
		{
			deviceId = int.Parse(match.Groups[1].Value, NumberStyles.AllowHexSpecifier);

			Console.WriteLine($"New device ID: {deviceId}");
		}

		// Read device identification
		else if ((match = Regex.Match(line, @"^(id)$", RegexOptions.IgnoreCase)).Success)
		{
			Console.WriteLine($"Reading device identification");

			var values = await client.ReadDeviceIdentification(deviceId, cancellationToken);

			// Show results
			foreach (var kvp in values)
			{
				Console.WriteLine($"{kvp.Key}: {kvp.Value}");
			}
		}

		// Bit write access
		else if ((match = Regex.Match(line, @"^([c])([^=]+)\s*=\s*(.+)$", RegexOptions.IgnoreCase)).Success)
		{
			var objectType = ParseObjectType(match.Groups[1].Value);
			ushort address = ParseRangeValue(match.Groups[2].Value);
			string value = match.Groups[3].Value;

			if (!Regex.IsMatch(value, @"^[01, ]*[01][01, ]*$"))
			{
				Console.Error.WriteLine("Invalid bit value(s): " + value);
				return true;
			}
			value = Regex.Replace(value, @"[^01]", "");
			ushort endAddress = (ushort)(address + value.Length - 1);

			if (!isLooping)
				Console.WriteLine($"Writing {objectType} to {address}{(endAddress > address ? $"-{endAddress}" : "")} = {value}");

			var objects = new ModbusCollection(objectType);
			foreach (char ch in value)
			{
				objects.SetState(address++, ch != '0');
			}
			await client.Write(deviceId, objects, cancellationToken);
			Console.WriteLine($"{objects.Count} bits written");
		}

		// Register write access
		else if ((match = Regex.Match(line, @"^([h])([^:=]+)(?::\s*([ui](?:16|32|64)|f32|f64|str(?:8|16)\s*(?:\.\s*(\d+))?))\s*=\s*(.+)$", RegexOptions.IgnoreCase)).Success)
		{
			var objectType = ParseObjectType(match.Groups[1].Value);
			ushort address = ParseRangeValue(match.Groups[2].Value);
			string dataType = Regex.Replace(match.Groups[3].Value, @":.*$", "").Trim().ToLowerInvariant();
			int stringLength = !string.IsNullOrWhiteSpace(match.Groups[4].Value) ? int.Parse(match.Groups[4].Value) : 0;
			string value = match.Groups[5].Value;

			if (!isLooping)
				Console.WriteLine($"Writing {objectType} to {address} as {dataType}{(stringLength > 0 ? ":" + stringLength : "")} = {value}");

			var objects = new ModbusCollection(objectType);
			switch (dataType)
			{
				case "u16":
					foreach (string v in value.Split(','))
					{
						objects.SetUInt16(address, ushort.Parse(v));
						address++;
					}
					break;
				case "i16":
					foreach (string v in value.Split(','))
					{
						objects.SetInt16(address, short.Parse(v));
						address++;
					}
					break;
				case "u32":
					foreach (string v in value.Split(','))
					{
						objects.SetUInt32(address, uint.Parse(v));
						address += 2;
					}
					break;
				case "i32":
					foreach (string v in value.Split(','))
					{
						objects.SetInt32(address, int.Parse(v));
						address += 2;
					}
					break;
				case "u64":
					foreach (string v in value.Split(','))
					{
						objects.SetUInt64(address, ulong.Parse(v));
						address += 4;
					}
					break;
				case "i64":
					foreach (string v in value.Split(','))
					{
						objects.SetInt64(address, long.Parse(v));
						address += 4;
					}
					break;
				case "f32":
					foreach (string v in value.Split(','))
					{
						objects.SetSingle(address, float.Parse(v, CultureInfo.InvariantCulture));
						address += 2;
					}
					break;
				case "f64":
					foreach (string v in value.Split(','))
					{
						objects.SetDouble(address, double.Parse(v, CultureInfo.InvariantCulture));
						address += 4;
					}
					break;
				case "str8":
				case "str16":
					if (stringLength > 0)
					{
						if (stringLength < value.Length)
							value = value[..stringLength];
						else if (stringLength > value.Length)
							value = value.PadRight(stringLength, ' ');
					}
					if (dataType == "str8")
						objects.SetString8(address, value);
					if (dataType == "str16")
						objects.SetString16(address, value);
					break;
			}
			await client.Write(deviceId, objects, cancellationToken);
			Console.WriteLine($"{objects.Count} values written");
		}

		// Bit read access
		else if ((match = Regex.Match(line, @"^([cd])(.+)$", RegexOptions.IgnoreCase)).Success)
		{
			var objectType = ParseObjectType(match.Groups[1].Value);
			var ranges = ParseRanges(match.Groups[2].Value);

			if (!isLooping)
				Console.WriteLine($"Reading {objectType} from {string.Join(',', ranges.Select(r => r.ToString()))}");

			var objects = await client.Read(objectType, deviceId, ranges, cancellationToken);

			// Show results
			foreach (var range in ranges)
			{
				for (int address = range.StartAddress; address <= range.EndAddress; address++)
				{
					bool state = objects.GetState(address);
					Console.WriteLine($"{address}: {(state ? 1 : 0)}");
				}
			}
		}

		// Register read access
		else if ((match = Regex.Match(line, @"^([hi])([^:]+)(?::\s*([ui](?:16|32|64)|f32|f64|str(?:8|16)\s*\.\s*(\d+)))?$", RegexOptions.IgnoreCase)).Success)
		{
			var objectType = ParseObjectType(match.Groups[1].Value);
			var ranges = ParseRanges(match.Groups[2].Value);
			string dataType = Regex.Replace(match.Groups[3].Value, @"\..*$", "").Trim().ToLowerInvariant();
			int stringLength = !string.IsNullOrWhiteSpace(match.Groups[4].Value) ? int.Parse(match.Groups[4].Value) : 0;

			if (!isLooping)
				Console.WriteLine($"Reading {objectType} from {string.Join(',', ranges.Select(r => r.ToString()))} as {(dataType != "" ? dataType : "raw")}{(stringLength > 0 ? "." + stringLength : "")}");

			// Extend each range for the specified data type
			int registerCount = 1;
			if (dataType == "str8")
				registerCount = (stringLength + 1) / 2;
			else if (dataType == "str16")
				registerCount = stringLength;
			else if (dataType.EndsWith("32"))
				registerCount = 2;
			else if (dataType.EndsWith("64"))
				registerCount = 4;
			if (registerCount > 1)
			{
				for (int i = 0; i < ranges.Count; i++)
				{
					int mod = ranges[i].Length % registerCount;
					if (mod != 0)
						ranges[i] = new(ranges[i].StartAddress, (ushort)(ranges[i].EndAddress + registerCount - mod));
				}
			}

			var objects = await client.Read(objectType, deviceId, ranges, cancellationToken);

			// Show results
			foreach (var range in ranges)
			{
				for (int address = range.StartAddress; address <= range.EndAddress; address += registerCount)
				{
					object value = dataType switch
					{
						"u16" => objects.GetUInt16(address),
						"i16" => objects.GetInt16(address),
						"u32" => objects.GetUInt32(address),
						"i32" => objects.GetInt32(address),
						"u64" => objects.GetUInt64(address),
						"i64" => objects.GetInt64(address),
						"f32" => objects.GetSingle(address),
						"f64" => objects.GetDouble(address),
						"str8" => objects.GetString8(address, stringLength),
						"str16" => objects.GetString16(address, stringLength),
						_ => "",
					};
					Console.WriteLine($"{address}: {value}");
				}
			}
		}

		// Loop last command
		else if ((match = Regex.Match(line, @"^loop\s*(\d+)$", RegexOptions.IgnoreCase)).Success)
		{
			int interval = int.Parse(match.Groups[1].Value);
			if (lastCommand != null)
			{
				Console.WriteLine($"Repeating last command every {interval} ms, press Ctrl+C to break...");
				try
				{
					isLooping = true;
					while (true)
					{
						if (!await ParseCommand(lastCommand, client, cancellationToken))
							return false;
						await Task.Delay(interval, cancellationToken);
					}
					// Never leaves this method regularly, so won't affect lastCommand
				}
				finally
				{
					isLooping = false;
				}
			}
		}

		else
		{
			Console.Error.WriteLine("Command not understood: " + line);
		}
		lastCommand = line;
		return true;
	}

	/// <summary>
	/// Parses a Modbus object type from the command that the user has entered.
	/// Accepted values: the first character of the Modbus object name (case-insensitive).
	/// </summary>
	private static ModbusObjectType ParseObjectType(string str) => str.Trim().ToLowerInvariant() switch
	{
		"c" => ModbusObjectType.Coil,
		"d" => ModbusObjectType.DiscreteInput,
		"h" => ModbusObjectType.HoldingRegister,
		"i" => ModbusObjectType.InputRegister,
		_ => throw new FormatException("Invalid object type: " + str),
	};

	/// <summary>
	/// Parses Modbus object address ranges from the command that the user has entered.
	/// Supported formats: start and end address separated by "-", and multiple ranges separated by ",".
	/// </summary>
	private static IList<ModbusRange> ParseRanges(string str)
	{
		var ranges = new List<ModbusRange>();
		foreach (string rangeStr in str.Split(','))
		{
			string[] parts = rangeStr.Split('-');
			if (parts.Length == 1)
			{
				ranges.Add(new(ParseRangeValue(parts[0])));
			}
			else if (parts.Length == 2)
			{
				ranges.Add(new(ParseRangeValue(parts[0]), ParseRangeValue(parts[1])));
			}
			else if (parts.Length > 2)
			{
				throw new FormatException("Invalid address range specification: " + rangeStr.Trim());
			}
		}
		return ranges;
	}

	/// <summary>
	/// Parses a Modbus object address value from the command that the user has entered.
	/// Supported formats: decimal numbers or hexadecimal numbers with the "x" prefix (case-insensitive).
	/// </summary>
	private static ushort ParseRangeValue(string str)
	{
		Match match;
		if ((match = Regex.Match(str, @"^\s*([0-9]+)\s*$", RegexOptions.IgnoreCase)).Success)
		{
			return ushort.Parse(match.Groups[1].Value);
		}
		if ((match = Regex.Match(str, @"^\s*x\s*([0-9a-f]+)\s*$", RegexOptions.IgnoreCase)).Success)
		{
			return ushort.Parse(match.Groups[1].Value, NumberStyles.AllowHexSpecifier);
		}
		throw new FormatException("Invalid address specification: " + str);
	}
}
