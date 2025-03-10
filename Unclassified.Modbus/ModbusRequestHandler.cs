using Unclassified.Modbus.Util;

namespace Unclassified.Modbus;

/// <summary>
/// Decodes and processes a Modbus request sent by a client and generates a response or other
/// reaction.
/// </summary>
public class ModbusRequestHandler
{
	/// <summary>
	/// Handles a request from a client and generates a response or other action.
	/// </summary>
	/// <param name="requestBody">The body of the request.</param>
	/// <param name="responseBody">The place to write the generated response.</param>
	/// <returns>
	/// The length of the generated response body, in bytes. If 0, no response will be sent. If -1,
	/// the connection will be closed in response to the request.
	/// </returns>
	public virtual async Task<int> HandleRequest(ReadOnlyMemory<byte> requestBody, Memory<byte> responseBody)
	{
		if (requestBody.Length < 2)
		{
			// No device ID and/or function code set, ignore this message
			return 0;
		}
		byte deviceId = requestBody.Span[0];
		var functionCode = (FunctionCode)requestBody.Span[1];

		switch (functionCode)
		{
			case FunctionCode.ReadCoils:
			case FunctionCode.ReadDiscreteInputs:
			case FunctionCode.ReadHoldingRegisters:
			case FunctionCode.ReadInputRegisters:
				return await HandleReadRequest(deviceId, functionCode, requestBody, responseBody).NoSync();
			default:
				return MakeExceptionResponse(deviceId, functionCode, ModbusError.IllegalFunction, responseBody);
		}
	}

	private async Task<int> HandleReadRequest(byte deviceId, FunctionCode functionCode, ReadOnlyMemory<byte> requestBody, Memory<byte> responseBody)
	{
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
		var objects = new ModbusCollection(objectType);

		await ProvideReadData(deviceId, objects).NoSync();
		if (objects.Count > ModbusRange.GetMaxLength(objectType))
			throw new InvalidOperationException("Trying to respond with too many Modbus objects.");

		responseBody.Span[0] = deviceId;
		responseBody.Span[1] = (byte)functionCode;
		if (objectType == ModbusObjectType.Coil || objectType == ModbusObjectType.DiscreteInput)
		{
			responseBody.Span[2] = (byte)((objects.Count + 7) / 8);
			// TODO
		}
		else if (objectType == ModbusObjectType.HoldingRegister || objectType == ModbusObjectType.InputRegister)
		{
			responseBody.Span[2] = (byte)(objects.Count * 2);
			// TODO
		}
		return 3 + responseBody.Span[2];
	}

	/// <summary>
	/// Fills the provided <see cref="ModbusCollection"/> with the data to send to the client in
	/// response to a read request.
	/// </summary>
	/// <param name="deviceId">The requested device ID.</param>
	/// <param name="objects">A collection that will contain the objects to send to the client.</param>
	/// <returns></returns>
	protected virtual Task ProvideReadData(byte deviceId, ModbusCollection objects)
	{
		return Task.CompletedTask;
	}

	private static int MakeExceptionResponse(byte deviceId, FunctionCode functionCode, ModbusError error, Memory<byte> responseBody)
	{
		responseBody.Span[0] = deviceId;
		responseBody.Span[1] = (byte)((int)functionCode | 0x80);
		responseBody.Span[2] = (byte)error;
		return 3;
	}
}
