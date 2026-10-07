using Microsoft.Extensions.Logging;
using Unclassified.Modbus.Util;

namespace Unclassified.Modbus;

/// <summary>
/// Decodes and processes a Modbus request sent by a client and generates a response or other
/// reaction. Derived classes provide and accept the data by overriding the
/// <see cref="ProvideReadData"/> and <see cref="ProcessWriteData"/> methods.
/// </summary>
public class ModbusRequestHandler
{
	#region Private data

	private readonly ILogger? logger;

	#endregion Private data

	#region Constructors

	/// <summary>
	/// Initializes a new instance of the <see cref="ModbusRequestHandler"/> class.
	/// </summary>
	/// <param name="logger">The logger instance.</param>
	public ModbusRequestHandler(ILogger? logger = null)
	{
		this.logger = logger;
	}

	#endregion Constructors

	#region Request handling

	/// <summary>
	/// Handles a request from a client and generates a response or other action.
	/// </summary>
	/// <param name="requestBody">The body of the request, starting with the device ID.</param>
	/// <param name="responseBody">The place to write the generated response, starting with the
	///   device ID. It must not overlap with <paramref name="requestBody"/> and must provide at
	///   least 254 bytes.</param>
	/// <param name="cancellationToken">A cancellation token used to propagate notification that
	///   this operation should be canceled.</param>
	/// <returns>
	/// The length of the generated response body, in bytes. If 0, no response will be sent. If -1,
	/// the connection will be closed in response to the request.
	/// </returns>
	public virtual async Task<int> HandleRequest(ReadOnlyMemory<byte> requestBody, Memory<byte> responseBody, CancellationToken cancellationToken = default)
	{
		if (requestBody.Length < 2)
		{
			// No device ID and/or function code set, ignore this message
			return 0;
		}
		byte deviceId = requestBody.Span[0];
		var functionCode = (FunctionCode)requestBody.Span[1];

		try
		{
			switch (functionCode)
			{
				case FunctionCode.ReadCoils:
				case FunctionCode.ReadDiscreteInputs:
				case FunctionCode.ReadHoldingRegisters:
				case FunctionCode.ReadInputRegisters:
					return await HandleReadRequest(deviceId, functionCode, requestBody, responseBody, cancellationToken).NoSync();
				case FunctionCode.WriteCoil:
				case FunctionCode.WriteHoldingRegister:
					return await HandleSingleWriteRequest(deviceId, functionCode, requestBody, responseBody, cancellationToken).NoSync();
				case FunctionCode.WriteCoils:
				case FunctionCode.WriteHoldingRegisters:
					return await HandleMultipleWriteRequest(deviceId, functionCode, requestBody, responseBody, cancellationToken).NoSync();
				default:
					logger?.LogDebug("Unsupported function code {FunctionCode} for device {DeviceId}", (int)functionCode, deviceId);
					return MakeExceptionResponse(deviceId, functionCode, ModbusError.IllegalFunction, responseBody);
			}
		}
		catch (ModbusException ex) when ((int)ex.Code is > 0 and <= 0xff)
		{
			logger?.LogDebug("Responding to {FunctionCode} for device {DeviceId} with exception {ErrorCode}: {Message}", functionCode, deviceId, ex.Code, ex.Message);
			return MakeExceptionResponse(deviceId, functionCode, ex.Code, responseBody);
		}
		catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
		{
			logger?.LogError(ex, "Error handling {FunctionCode} for device {DeviceId}: {Message}", functionCode, deviceId, ex.Message);
			return MakeExceptionResponse(deviceId, functionCode, ModbusError.ServerDeviceFailure, responseBody);
		}
	}

	private async Task<int> HandleReadRequest(byte deviceId, FunctionCode functionCode, ReadOnlyMemory<byte> requestBody, Memory<byte> responseBody, CancellationToken cancellationToken)
	{
		if (requestBody.Length < 6)
			throw new ModbusException(ModbusError.IllegalDataValue, $"Request is too short: {requestBody.Length} bytes starting at device ID");
		int startAddress = requestBody.Span[2] << 8 | requestBody.Span[3];
		int count = requestBody.Span[4] << 8 | requestBody.Span[5];

		var objectType = functionCode switch
		{
			FunctionCode.ReadCoils => ModbusObjectType.Coil,
			FunctionCode.ReadDiscreteInputs => ModbusObjectType.DiscreteInput,
			FunctionCode.ReadHoldingRegisters => ModbusObjectType.HoldingRegister,
			FunctionCode.ReadInputRegisters => ModbusObjectType.InputRegister,
			_ => throw new InvalidOperationException("Invalid function code.")   // Should never happen
		};
		bool isBitType = objectType == ModbusObjectType.Coil || objectType == ModbusObjectType.DiscreteInput;
		int maxCount = ModbusRange.GetMaxReadLength(objectType);
		if (count < 1 || count > maxCount)
			throw new ModbusException(ModbusError.IllegalDataValue, $"Requested count ({count}) is not in the valid range 1-{maxCount}.");
		if (startAddress + count > 0x10000)
			throw new ModbusException(ModbusError.IllegalDataAddress, "Requested range exceeds the address space.");

		var range = new ModbusRange((ushort)startAddress, (ushort)(startAddress + count - 1));
		var objects = new ModbusCollection(objectType);
		await ProvideReadData(deviceId, range, objects, cancellationToken).NoSync();
		var objectsByAddress = new Dictionary<ushort, IModbusObject>();
		foreach (var obj in objects)
		{
			objectsByAddress[obj.Address] = obj;
		}

		var span = responseBody.Span;
		span[0] = deviceId;
		span[1] = (byte)functionCode;
		if (isBitType)
		{
			// State bits are written in address order from least to most significant bit, one byte after the other
			int byteCount = (count + 7) / 8;
			span.Slice(3, byteCount).Clear();
			for (int i = 0; i < count; i++)
			{
				var obj = (IModbusStateObject)GetObject(objectsByAddress, objectType, startAddress + i);
				if (obj.State)
					span[3 + i / 8] |= (byte)(1 << (i % 8));
			}
			span[2] = (byte)byteCount;
		}
		else
		{
			// Values are written in 2 bytes each
			for (int i = 0; i < count; i++)
			{
				var obj = (IModbusValueObject)GetObject(objectsByAddress, objectType, startAddress + i);
				span[3 + i * 2] = (byte)(obj.Value >> 8);
				span[3 + i * 2 + 1] = (byte)(obj.Value & 0xff);
			}
			span[2] = (byte)(count * 2);
		}
		return 3 + span[2];
	}

	private async Task<int> HandleSingleWriteRequest(byte deviceId, FunctionCode functionCode, ReadOnlyMemory<byte> requestBody, Memory<byte> responseBody, CancellationToken cancellationToken)
	{
		if (requestBody.Length < 6)
			throw new ModbusException(ModbusError.IllegalDataValue, $"Request is too short: {requestBody.Length} bytes starting at device ID");
		ushort address = (ushort)(requestBody.Span[2] << 8 | requestBody.Span[3]);
		ushort value = (ushort)(requestBody.Span[4] << 8 | requestBody.Span[5]);

		ModbusCollection objects;
		if (functionCode == FunctionCode.WriteCoil)
		{
			if (value != 0x0000 && value != 0xff00)
				throw new ModbusException(ModbusError.IllegalDataValue, $"Invalid coil value: 0x{value:x4}");
			objects = new ModbusCollection(ModbusObjectType.Coil);
			objects.SetState(address, value != 0);
		}
		else
		{
			objects = new ModbusCollection(ModbusObjectType.HoldingRegister);
			objects.SetUInt16(address, value);
		}
		await ProcessWriteData(deviceId, new ModbusRange(address), objects, cancellationToken).NoSync();

		// The response is an echo of the request
		requestBody.Span[..6].CopyTo(responseBody.Span);
		return 6;
	}

	private async Task<int> HandleMultipleWriteRequest(byte deviceId, FunctionCode functionCode, ReadOnlyMemory<byte> requestBody, Memory<byte> responseBody, CancellationToken cancellationToken)
	{
		if (requestBody.Length < 7)
			throw new ModbusException(ModbusError.IllegalDataValue, $"Request is too short: {requestBody.Length} bytes starting at device ID");
		int startAddress = requestBody.Span[2] << 8 | requestBody.Span[3];
		int count = requestBody.Span[4] << 8 | requestBody.Span[5];
		int byteCount = requestBody.Span[6];

		bool isCoil = functionCode == FunctionCode.WriteCoils;
		int maxCount = ModbusRange.GetMaxWriteLength(isCoil ? ModbusObjectType.Coil : ModbusObjectType.HoldingRegister);
		if (count < 1 || count > maxCount)
			throw new ModbusException(ModbusError.IllegalDataValue, $"Requested count ({count}) is not in the valid range 1-{maxCount}.");
		int expectedByteCount = isCoil ? (count + 7) / 8 : count * 2;
		if (byteCount != expectedByteCount)
			throw new ModbusException(ModbusError.IllegalDataValue, $"Byte count ({byteCount}) does not match the requested count ({count}).");
		if (requestBody.Length < 7 + byteCount)
			throw new ModbusException(ModbusError.IllegalDataValue, $"Request data ends unexpectedly after {requestBody.Length - 7} instead of {byteCount} bytes.");
		if (startAddress + count > 0x10000)
			throw new ModbusException(ModbusError.IllegalDataAddress, "Requested range exceeds the address space.");

		var objects = DecodeWriteData(isCoil, startAddress, count, requestBody.Span[7..]);
		var range = new ModbusRange((ushort)startAddress, (ushort)(startAddress + count - 1));
		await ProcessWriteData(deviceId, range, objects, cancellationToken).NoSync();

		// The response repeats the device ID, function code, start address and count
		requestBody.Span[..6].CopyTo(responseBody.Span);
		return 6;
	}

	#endregion Request handling

	#region Data provider methods

	/// <summary>
	/// Fills the provided <see cref="ModbusCollection"/> with the data to send to the client in
	/// response to a read request.
	/// </summary>
	/// <param name="deviceId">The requested device ID.</param>
	/// <param name="range">The requested address range. An object must be added to
	///   <paramref name="objects"/> for each address in this range. Objects outside the range are
	///   ignored.</param>
	/// <param name="objects">A collection that will contain the objects to send to the client. Its
	///   <see cref="ModbusCollection.ObjectType"/> indicates the requested object type.</param>
	/// <param name="cancellationToken">A cancellation token used to propagate notification that
	///   this operation should be canceled.</param>
	/// <returns>The task object representing the asynchronous operation.</returns>
	/// <remarks>
	/// <para>
	/// Throw a <see cref="ModbusException"/> to respond with its exception code, e.g.
	/// <see cref="ModbusError.IllegalDataAddress"/> for unsupported addresses. Other exceptions are
	/// responded with <see cref="ModbusError.ServerDeviceFailure"/>. If an address in the
	/// <paramref name="range"/> is missing in <paramref name="objects"/>, the response is
	/// <see cref="ModbusError.IllegalDataAddress"/>.
	/// </para>
	/// <para>
	/// The default implementation responds with <see cref="ModbusError.IllegalFunction"/>.
	/// </para>
	/// </remarks>
	protected virtual Task ProvideReadData(byte deviceId, ModbusRange range, ModbusCollection objects, CancellationToken cancellationToken)
	{
		throw new ModbusException(ModbusError.IllegalFunction);
	}

	/// <summary>
	/// Processes the data received from the client in a write request.
	/// </summary>
	/// <param name="deviceId">The requested device ID.</param>
	/// <param name="range">The address range of the written objects.</param>
	/// <param name="objects">The objects to write, one for each address in the
	///   <paramref name="range"/>. Its <see cref="ModbusCollection.ObjectType"/> is
	///   <see cref="ModbusObjectType.Coil"/> or <see cref="ModbusObjectType.HoldingRegister"/>.</param>
	/// <param name="cancellationToken">A cancellation token used to propagate notification that
	///   this operation should be canceled.</param>
	/// <returns>The task object representing the asynchronous operation.</returns>
	/// <remarks>
	/// <para>
	/// Throw a <see cref="ModbusException"/> to respond with its exception code, e.g.
	/// <see cref="ModbusError.IllegalDataAddress"/> for unsupported addresses. Other exceptions are
	/// responded with <see cref="ModbusError.ServerDeviceFailure"/>.
	/// </para>
	/// <para>
	/// The default implementation responds with <see cref="ModbusError.IllegalFunction"/>.
	/// </para>
	/// </remarks>
	protected virtual Task ProcessWriteData(byte deviceId, ModbusRange range, ModbusCollection objects, CancellationToken cancellationToken)
	{
		throw new ModbusException(ModbusError.IllegalFunction);
	}

	#endregion Data provider methods

	#region Private helper methods

	private static ModbusCollection DecodeWriteData(bool isCoil, int startAddress, int count, ReadOnlySpan<byte> data)
	{
		ModbusCollection objects;
		if (isCoil)
		{
			// State bits are read in address order from least to most significant bit, one byte after the other
			objects = new ModbusCollection(ModbusObjectType.Coil);
			for (int i = 0; i < count; i++)
			{
				objects.SetState(startAddress + i, (data[i / 8] & (1 << (i % 8))) != 0);
			}
		}
		else
		{
			// Values are read in 2 bytes each
			objects = new ModbusCollection(ModbusObjectType.HoldingRegister);
			for (int i = 0; i < count; i++)
			{
				objects.SetUInt16(startAddress + i, (ushort)(data[i * 2] << 8 | data[i * 2 + 1]));
			}
		}
		return objects;
	}

	private static IModbusObject GetObject(Dictionary<ushort, IModbusObject> objectsByAddress, ModbusObjectType objectType, int address)
	{
		if (!objectsByAddress.TryGetValue((ushort)address, out var obj))
			throw new ModbusException(ModbusError.IllegalDataAddress, $"No {objectType} provided for address {address}.");
		return obj;
	}

	private static int MakeExceptionResponse(byte deviceId, FunctionCode functionCode, ModbusError error, Memory<byte> responseBody)
	{
		responseBody.Span[0] = deviceId;
		responseBody.Span[1] = (byte)((int)functionCode | 0x80);
		responseBody.Span[2] = (byte)error;
		return 3;
	}

	#endregion Private helper methods
}
