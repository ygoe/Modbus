using Microsoft.Extensions.Logging;
using ModbusClientDemo.Util;
using Unclassified.Modbus;

namespace ModbusServerDemo;

/// <summary>
/// A simple Modbus TCP server that keeps all objects in memory.
/// </summary>
public class Program
{
	/// <summary>
	/// Application entry point.
	/// </summary>
	/// <remarks>
	/// Supported arguments:
	/// <list type="bullet">
	/// <item>
	/// -port port<br/>
	/// port: default is 502
	/// </item>
	/// <item>
	/// -log level<br/>
	/// level: trace|debug|info|warn|error|critical|none (case-insensitive, can be abbreviated to
	/// first character), default is info
	/// </item>
	/// </list>
	/// </remarks>
	public static async Task<int> Main(string[] args)
	{
		int port = 502;
		var minLogLevel = LogLevel.Information;
		try
		{
			for (int i = 0; i < args.Length; i++)
			{
				if (args[i] == "-port" && i + 1 < args.Length)
				{
					port = int.Parse(args[++i]);
				}
				else if (args[i] == "-log" && i + 1 < args.Length)
				{
					i++;
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
					};
				}
				else
				{
					throw new ArgumentException("Usage: [-port <port>] [-log t|d|i|w|e|c|n]");
				}
			}
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine(ex.Message);
			return 1;
		}

		var logger = new ConsoleLogger(minLogLevel);
		var listener = new ModbusTcpListener(new MemoryRequestHandler(logger), logger)
		{
			Port = port
		};

		// 80 columns:     ---------+---------+---------+---------+---------+---------+---------+---------+
		Console.WriteLine("Simple Modbus server");
		Console.WriteLine("");
		Console.WriteLine($"Listening on TCP port {port} for any device ID. All objects are kept in");
		Console.WriteLine("memory, coils and holding registers can be written.");
		Console.WriteLine("  Input registers 0-1: seconds since start (u32)");
		Console.WriteLine("  Discrete input 0: toggles every second");
		Console.WriteLine("");
		Console.WriteLine("Press Ctrl+C to exit.");
		Console.WriteLine("");

		using var stopCts = new CancellationTokenSource();
		Console.CancelKeyPress += (sender, args) =>
		{
			args.Cancel = true;
			stopCts.Cancel();
		};

		await listener.StartAsync(default);
		try
		{
			await Task.Delay(Timeout.Infinite, stopCts.Token);
		}
		catch (OperationCanceledException)
		{
		}
		// Close all client connections after 2 seconds
		using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
		await listener.StopAsync(shutdownCts.Token);
		return 0;
	}
}

/// <summary>
/// Provides all Modbus objects from memory.
/// </summary>
internal class MemoryRequestHandler : ModbusRequestHandler
{
	/// <summary>
	/// Synchronizes access to the object arrays.
	/// </summary>
	private readonly Lock syncObj = new();

	/// <summary>
	/// The coil states by address.
	/// </summary>
	private readonly bool[] coils = new bool[0x10000];

	/// <summary>
	/// The discrete input states by address.
	/// </summary>
	private readonly bool[] discreteInputs = new bool[0x10000];

	/// <summary>
	/// The holding register values by address.
	/// </summary>
	private readonly ushort[] holdingRegisters = new ushort[0x10000];

	/// <summary>
	/// The input register values by address.
	/// </summary>
	private readonly ushort[] inputRegisters = new ushort[0x10000];

	/// <summary>
	/// The time when the handler was created, for the uptime in the input registers.
	/// </summary>
	private readonly DateTime startTime = DateTime.UtcNow;

	/// <summary>
	/// Initializes a new instance of the <see cref="MemoryRequestHandler"/> class.
	/// </summary>
	/// <param name="logger">The logger instance.</param>
	public MemoryRequestHandler(ILogger? logger)
		: base(logger)
	{
	}

	/// <inheritdoc/>
	protected override Task ProvideReadData(
		byte deviceId,
		ModbusRange range,
		ModbusCollection objects,
		CancellationToken cancellationToken)
	{
		lock (syncObj)
		{
			// Update the dynamic objects
			uint seconds = (uint)(DateTime.UtcNow - startTime).TotalSeconds;
			inputRegisters[0] = (ushort)(seconds >> 16);
			inputRegisters[1] = (ushort)(seconds & 0xffff);
			discreteInputs[0] = seconds % 2 != 0;

			for (int address = range.StartAddress; address <= range.EndAddress; address++)
			{
				switch (objects.ObjectType)
				{
					case ModbusObjectType.Coil:
						objects.SetState(address, coils[address]);
						break;
					case ModbusObjectType.DiscreteInput:
						objects.SetState(address, discreteInputs[address]);
						break;
					case ModbusObjectType.HoldingRegister:
						objects.SetUInt16(address, holdingRegisters[address]);
						break;
					case ModbusObjectType.InputRegister:
						objects.SetUInt16(address, inputRegisters[address]);
						break;
				}
			}
		}
		return Task.CompletedTask;
	}

	/// <inheritdoc/>
	protected override Task ProcessWriteData(
		byte deviceId,
		ModbusRange range,
		ModbusCollection objects,
		CancellationToken cancellationToken)
	{
		lock (syncObj)
		{
			foreach (var obj in objects)
			{
				if (obj is IModbusStateObject stateObj)
					coils[obj.Address] = stateObj.State;
				else if (obj is IModbusValueObject valueObj)
					holdingRegisters[obj.Address] = valueObj.Value;
			}
		}
		return Task.CompletedTask;
	}
}
