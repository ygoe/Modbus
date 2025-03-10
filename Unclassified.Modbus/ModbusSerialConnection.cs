using System.IO.Ports;
using Microsoft.Extensions.Logging;
using Unclassified.Modbus.Util;

namespace Unclassified.Modbus;

/// <summary>
/// A client connection to a Modbus server over a serial line.
/// </summary>
internal class ModbusSerialConnection : IModbusConnection
{
	private readonly ILogger? logger;
	private SerialPort? serialPort;
	private RS485? rs485;
	private readonly byte[] buffer = new byte[270];   // Protocol allows 256 = 254 + RTU-specific header

	public ModbusSerialConnection(ILogger? logger)
	{
		this.logger = logger;
	}

	public bool IsOpen => serialPort?.IsOpen == true;

	internal void Connect(string portName, int baudRate, Parity parity, StopBits? stopBitsOverride)
	{
		try
		{
			rs485 = new RS485(portName);
		}
		catch (Exception ex)
		{
			logger?.LogWarning(ex, "Serial driver could not be set to RS-485.");
		}

		var stopBits = stopBitsOverride ?? (parity == Parity.None ? StopBits.Two : StopBits.One);
		serialPort = new SerialPort(portName, baudRate, parity, 8, stopBits);
		if (logger?.IsEnabled(LogLevel.Debug) == true)
			logger?.LogDebug("Opening {PortName} at {Config}...", portName, serialPort.GetParamDescription());
		serialPort.OpenClean();
		logger?.LogDebug("Serial port opened");
	}

	public async Task<ReadOnlyMemory<byte>> SendRequest(ReadOnlyMemory<byte> requestBody, CancellationToken cancellationToken = default)
	{
		if (serialPort == null)
			throw new InvalidOperationException("Connection not opened.");

		// Clear all data (ignore excess unread data from previous responses)
		await serialPort.BaseStream.FlushAsync(cancellationToken).NoSync();
		serialPort.DiscardInBuffer();
		serialPort.DiscardOutBuffer();

		var bytes = MakeMessageFrame(requestBody.Span);
		if (logger?.IsEnabled(LogLevel.Trace) == true)
			logger?.LogTrace("Sending: {HexData}", bytes.Span.ToHexString());
		await serialPort.WriteAsync(bytes, cancellationToken).NoSync();

		// Wait for a response
		int bufferUsed = 0;
		while (true)
		{
			int readBytes = await serialPort.ReadAsync(buffer.AsMemory(bufferUsed), cancellationToken).NoSync();
			if (readBytes <= 0)
				throw new IOException($"The serial port received {readBytes} bytes while waiting for a response, got {bufferUsed} bytes so far.");
			bufferUsed += readBytes;
			if (logger?.IsEnabled(LogLevel.Trace) == true)
				logger?.LogTrace("Received: {HexData}", buffer.AsSpan(0, bufferUsed).ToHexString());

			if (bufferUsed >= 3)
			{
				// There is no universal frame length indication in the RTU format, so we need to
				// interpret the body data to the extent that we can determine its length. Full
				// decoding and processing is performed at a later stage.
				int responseLength = -1;

				byte functionCode = buffer[1];
				if ((functionCode & 0x80) == 0)
				{
					switch (functionCode)
					{
						case (byte)FunctionCode.ReadCoils:
						case (byte)FunctionCode.ReadDiscreteInputs:
						case (byte)FunctionCode.ReadHoldingRegisters:
						case (byte)FunctionCode.ReadInputRegisters:
							responseLength = 2 + buffer[2];
							break;
						case (byte)FunctionCode.WriteCoil:
						case (byte)FunctionCode.WriteHoldingRegister:
						case (byte)FunctionCode.WriteCoils:
						case (byte)FunctionCode.WriteHoldingRegisters:
							responseLength = 2 + 4;
							break;
						case (byte)FunctionCode.ReadDeviceIdentification:
							if (bufferUsed < 8)
								break;
							responseLength = 2 + 6;
							byte count = buffer[7];
							// NOTE: This count value is underspecified and might be wrong.
							//       See ModbusClient.DecodeReadIdResponse method's comments.
							//       But for the serial transport, it's all we have.
							// Now expect count entries of the following structure
							for (int i = 0; i < count; i++)
							{
								// ID (1 byte)
								// Length (1 byte)
								// Value (length bytes)
								if (bufferUsed < responseLength + 1)
								{
									responseLength = -1;
									break;
								}
								responseLength += 2 + buffer[responseLength + 1];
							}
							break;
						default:
							throw new NotImplementedException($"The Modbus function {functionCode} is not implemented.");
					}
				}
				else
				{
					// Error response
					responseLength = 3;
				}

				if (responseLength != -1 && bufferUsed >= responseLength + 2)
					break;
			}
		}
		return ExtractResponseBody(buffer.AsSpan(0, bufferUsed)).ToArray();
	}

	public void Close()
	{
		var localSerialPort = serialPort;
		if (localSerialPort != null)
		{
			localSerialPort.Close();
			localSerialPort.Dispose();
			serialPort = null;
			logger?.LogDebug("Connection closed");

			try
			{
				rs485?.Dispose();
			}
			catch (Exception ex)
			{
				logger?.LogWarning(ex, "Serial driver state could not be reset.");
			}
		}
	}

	private static ReadOnlyMemory<byte> MakeMessageFrame(ReadOnlySpan<byte> requestBody)
	{
		byte[] bytes = new byte[requestBody.Length + 2];
		requestBody.CopyTo(bytes);
		ushort crc = Crc16.ComputeChecksum(bytes.AsSpan(0, requestBody.Length));
		// CRC bytes are little-endian
		bytes[requestBody.Length + 0] = (byte)(crc & 0xff);
		bytes[requestBody.Length + 1] = (byte)(crc >> 8);
		return bytes;
	}

	private ReadOnlySpan<byte> ExtractResponseBody(ReadOnlySpan<byte> response)
	{
		var responseBody = response[..^2];
		// CRC bytes are little-endian
		ushort crc = (ushort)(response[^1] << 8 | response[^2]);
		ushort computedCrc = Crc16.ComputeChecksum(responseBody);
		if (crc != computedCrc)
		{
			if (logger?.IsEnabled(LogLevel.Trace) == true)
				logger?.LogTrace("Data CRC: {Crc}, computed CRC: {ComputedCrc}", $"0x{crc:x4}", $"0x{computedCrc:x4}");
			throw new ModbusException(ModbusError.CrcMismatch);
		}
		return responseBody;
	}
}
