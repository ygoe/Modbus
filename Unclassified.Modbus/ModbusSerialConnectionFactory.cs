using System.IO.Ports;
using Microsoft.Extensions.Logging;

namespace Unclassified.Modbus;

/// <summary>
/// A factory class that creates new <see cref="ModbusSerialConnection"/> instances.
/// </summary>
public class ModbusSerialConnectionFactory : IModbusConnectionFactory
{
	/// <summary>
	/// Initializes a new instance of the <see cref="ModbusTcpConnectionFactory"/> class.
	/// </summary>
	/// <param name="portName">The name of the serial port to open.</param>
	public ModbusSerialConnectionFactory(string portName)
	{
		PortName = portName;
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="ModbusTcpConnectionFactory"/> class.
	/// </summary>
	/// <param name="portName">The name of the serial port to open.</param>
	/// <param name="baudRate">The serial communication baud rate.</param>
	public ModbusSerialConnectionFactory(string portName, int baudRate)
	{
		PortName = portName;
		BaudRate = baudRate;
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="ModbusTcpConnectionFactory"/> class.
	/// </summary>
	/// <param name="portName">The name of the serial port to open.</param>
	/// <param name="baudRate">The serial communication baud rate.</param>
	/// <param name="parity">The parity of the serial communication.</param>
	public ModbusSerialConnectionFactory(string portName, int baudRate, Parity parity)
	{
		PortName = portName;
		BaudRate = baudRate;
		Parity = parity;
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="ModbusTcpConnectionFactory"/> class.
	/// </summary>
	/// <param name="portName">The name of the serial port to open.</param>
	/// <param name="baudRate">The serial communication baud rate.</param>
	/// <param name="parity">The parity of the serial communication.</param>
	/// <param name="stopBitsOverride">The stop bits of the serial communication.</param>
	public ModbusSerialConnectionFactory(string portName, int baudRate, Parity parity, StopBits stopBitsOverride)
	{
		PortName = portName;
		BaudRate = baudRate;
		Parity = parity;
		StopBitsOverride = stopBitsOverride;
	}

	/// <summary>
	/// Gets the name of the serial port to open.
	/// </summary>
	public string PortName { get; }

	/// <summary>
	/// Gets the serial communication baud rate. Default is 19200.
	/// </summary>
	public int BaudRate { get; } = 19200;

	/// <summary>
	/// Gets the parity of the serial communication. Default is <see cref="Parity.Even"/>.
	/// As per the Modbus standard, if <see cref="Parity.None"/> is set, two stop bits are used;
	/// otherwise, one. This can be overridden with <see cref="StopBitsOverride"/>.
	/// </summary>
	public Parity Parity { get; } = Parity.Even;

	/// <summary>
	/// Gets the override stop bits of the serial communication. Default is automatic and depends on
	/// <see cref="Parity"/>.
	/// </summary>
	public StopBits? StopBitsOverride { get; }

	/// <inheritdoc/>
	public Task<IModbusConnection> GetConnection(ILogger? logger = null, CancellationToken cancellationToken = default)
	{
		var connection = new ModbusSerialConnection(logger);
		connection.Connect(PortName, BaudRate, Parity, StopBitsOverride);
		return Task.FromResult((IModbusConnection)connection);
	}

	/// <summary>
	/// Returns a summary of the connection data.
	/// </summary>
	/// <returns></returns>
	public override string ToString()
	{
		var stopBits = StopBitsOverride ?? (Parity == Parity.None ? StopBits.Two : StopBits.One);
		string stopBitsStr = stopBits == StopBits.OnePointFive ?
			"1.5" :
			stopBits.ToString("D");
		string parityStr = "8" + Parity.ToString()[0] + stopBitsStr;
		return $"Serial {PortName}@{BaudRate},{parityStr}";
	}
}
