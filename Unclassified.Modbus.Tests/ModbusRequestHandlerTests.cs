using Unclassified.Modbus.Tests.Fakes;

namespace Unclassified.Modbus.Tests;

/// <summary>
/// Tests the <see cref="ModbusRequestHandler"/> class with request and response bodies.
/// </summary>
[TestClass]
public class ModbusRequestHandlerTests
{
	#region Read requests

	/// <summary>
	/// Checks that register values are sent with the high byte first.
	/// </summary>
	[TestMethod]
	public async Task Read_HoldingRegisters()
	{
		var handler = new MemoryRequestHandler();
		handler.HoldingRegisters[0x10] = 0x1234;
		handler.HoldingRegisters[0x11] = 0xabcd;

		byte[] response = await Handle(handler, [7, 3, 0x00, 0x10, 0x00, 0x02]);

		CollectionAssert.AreEqual(new byte[] { 7, 3, 4, 0x12, 0x34, 0xab, 0xcd }, response);
		Assert.AreEqual((byte)7, handler.LastDeviceId);
	}

	/// <summary>
	/// Checks that coil states are sent in address order from the least significant bit.
	/// </summary>
	[TestMethod]
	public async Task Read_Coils_LeastSignificantBitFirst()
	{
		var handler = new MemoryRequestHandler();
		for (ushort address = 0; address < 10; address++)
		{
			handler.Coils[address] = address is 0 or 2 or 9;
		}

		byte[] response = await Handle(handler, [1, 1, 0x00, 0x00, 0x00, 10]);

		CollectionAssert.AreEqual(new byte[] { 1, 1, 2, 0x05, 0x02 }, response);
	}

	/// <summary>
	/// Checks that the maximum number of registers can be read.
	/// </summary>
	[TestMethod]
	public async Task Read_MaxRegisterCount()
	{
		var handler = new MemoryRequestHandler();
		MemoryRequestHandler.FillWithAddresses(handler.InputRegisters, new(0, 124));

		byte[] response = await Handle(handler, [1, 4, 0x00, 0x00, 0x00, 125]);

		Assert.HasCount(3 + 250, response);
		Assert.AreEqual((byte)250, response[2]);
		Assert.AreEqual((byte)124, response[^1]);
	}

	/// <summary>
	/// Checks that the maximum number of discrete inputs can be read.
	/// </summary>
	[TestMethod]
	public async Task Read_MaxBitCount()
	{
		var handler = new MemoryRequestHandler();
		for (ushort address = 0; address < 2000; address++)
		{
			handler.DiscreteInputs[address] = true;
		}

		byte[] response = await Handle(handler, [1, 2, 0x00, 0x00, 0x07, 0xd0]);

		Assert.HasCount(3 + 250, response);
		Assert.AreEqual((byte)250, response[2]);
	}

	/// <summary>
	/// Checks that a count outside the protocol limits is rejected.
	/// </summary>
	[TestMethod]
	[DataRow((byte)3, 0)]
	[DataRow((byte)3, 126)]
	[DataRow((byte)4, 126)]
	[DataRow((byte)1, 2001)]
	[DataRow((byte)2, 2001)]
	public async Task Read_InvalidCount_IllegalDataValue(byte functionCode, int count)
	{
		byte[] response = await Handle(
			new MemoryRequestHandler(),
			[1, functionCode, 0x00, 0x00, (byte)(count >> 8), (byte)(count & 0xff)]);

		AssertException(response, functionCode, ModbusError.IllegalDataValue);
	}

	/// <summary>
	/// Checks that the last address can be read but a range beyond it is rejected.
	/// </summary>
	[TestMethod]
	public async Task Read_EndOfAddressSpace()
	{
		var handler = new MemoryRequestHandler();
		handler.HoldingRegisters[0xffff] = 1;

		byte[] lastAddressResponse = await Handle(handler, [1, 3, 0xff, 0xff, 0x00, 0x01]);
		byte[] beyondResponse = await Handle(handler, [1, 3, 0xff, 0xff, 0x00, 0x02]);

		CollectionAssert.AreEqual(new byte[] { 1, 3, 2, 0x00, 0x01 }, lastAddressResponse);
		AssertException(beyondResponse, 3, ModbusError.IllegalDataAddress);
	}

	/// <summary>
	/// Checks that a request without the count is rejected.
	/// </summary>
	[TestMethod]
	public async Task Read_ShortRequest_IllegalDataValue()
	{
		byte[] response = await Handle(new MemoryRequestHandler(), [1, 3, 0x00, 0x00]);

		AssertException(response, 3, ModbusError.IllegalDataValue);
	}

	/// <summary>
	/// Checks that an address that the handler does not provide is rejected.
	/// </summary>
	[TestMethod]
	public async Task Read_MissingObject_IllegalDataAddress()
	{
		var handler = new MemoryRequestHandler();
		handler.HoldingRegisters[0] = 1;

		byte[] response = await Handle(handler, [1, 3, 0x00, 0x00, 0x00, 0x02]);

		AssertException(response, 3, ModbusError.IllegalDataAddress);
	}

	#endregion Read requests

	#region Write requests

	/// <summary>
	/// Checks that the coil values ON and OFF are accepted and the request is echoed.
	/// </summary>
	[TestMethod]
	[DataRow((byte)0xff, true)]
	[DataRow((byte)0x00, false)]
	public async Task WriteSingleCoil(byte valueHighByte, bool expected)
	{
		var handler = new MemoryRequestHandler();
		byte[] request = [1, 5, 0x00, 0x03, valueHighByte, 0x00];

		byte[] response = await Handle(handler, request);

		CollectionAssert.AreEqual(request, response);
		Assert.AreEqual(expected, handler.Coils[3]);
	}

	/// <summary>
	/// Checks that coil values other than ON and OFF are rejected.
	/// </summary>
	[TestMethod]
	public async Task WriteSingleCoil_InvalidValue_IllegalDataValue()
	{
		var handler = new MemoryRequestHandler();

		byte[] response = await Handle(handler, [1, 5, 0x00, 0x03, 0x12, 0x34]);

		AssertException(response, 5, ModbusError.IllegalDataValue);
		Assert.IsEmpty(handler.Coils);
	}

	/// <summary>
	/// Checks that a single register is written and the request is echoed.
	/// </summary>
	[TestMethod]
	public async Task WriteSingleRegister()
	{
		var handler = new MemoryRequestHandler();
		byte[] request = [1, 6, 0x00, 0x10, 0xbe, 0xef];

		byte[] response = await Handle(handler, request);

		CollectionAssert.AreEqual(request, response);
		Assert.AreEqual((ushort)0xbeef, handler.HoldingRegisters[0x10]);
	}

	/// <summary>
	/// Checks that coil states are read in address order from the least significant bit.
	/// </summary>
	[TestMethod]
	public async Task WriteMultipleCoils()
	{
		var handler = new MemoryRequestHandler();

		byte[] response = await Handle(handler, [1, 15, 0x00, 0x00, 0x00, 10, 2, 0x05, 0x02]);

		CollectionAssert.AreEqual(new byte[] { 1, 15, 0x00, 0x00, 0x00, 10 }, response);
		CollectionAssert.AreEqual(new[] { new ModbusRange(0, 9) }, handler.WrittenRanges);
		bool[] states = [.. Enumerable.Range(0, 10).Select(a => handler.Coils[(ushort)a])];
		CollectionAssert.AreEqual(
			new[] { true, false, true, false, false, false, false, false, false, true },
			states);
	}

	/// <summary>
	/// Checks that register values are read with the high byte first.
	/// </summary>
	[TestMethod]
	public async Task WriteMultipleRegisters()
	{
		var handler = new MemoryRequestHandler();

		byte[] response = await Handle(
			handler,
			[1, 16, 0x00, 0x20, 0x00, 0x02, 4, 0x12, 0x34, 0x56, 0x78]);

		CollectionAssert.AreEqual(new byte[] { 1, 16, 0x00, 0x20, 0x00, 0x02 }, response);
		Assert.AreEqual((ushort)0x1234, handler.HoldingRegisters[0x20]);
		Assert.AreEqual((ushort)0x5678, handler.HoldingRegisters[0x21]);
	}

	/// <summary>
	/// Checks that the maximum number of coils can be written but no more.
	/// </summary>
	[TestMethod]
	public async Task WriteMultipleCoils_CountLimit()
	{
		var handler = new MemoryRequestHandler();

		byte[] maxResponse = await Handle(handler, MakeWriteCoilsRequest(1968));
		byte[] beyondResponse = await Handle(handler, MakeWriteCoilsRequest(1969));

		CollectionAssert.AreEqual(new byte[] { 1, 15, 0x00, 0x00, 0x07, 0xb0 }, maxResponse);
		AssertException(beyondResponse, 15, ModbusError.IllegalDataValue);
	}

	/// <summary>
	/// Checks that a byte count that does not match the register count is rejected.
	/// </summary>
	[TestMethod]
	public async Task WriteMultipleRegisters_ByteCountMismatch_IllegalDataValue()
	{
		var handler = new MemoryRequestHandler();

		byte[] response = await Handle(handler, [1, 16, 0x00, 0x00, 0x00, 0x02, 3, 1, 2, 3]);

		AssertException(response, 16, ModbusError.IllegalDataValue);
		Assert.IsEmpty(handler.WrittenRanges);
	}

	/// <summary>
	/// Checks that a request with less data than indicated is rejected.
	/// </summary>
	[TestMethod]
	public async Task WriteMultipleRegisters_MissingData_IllegalDataValue()
	{
		byte[] response = await Handle(
			new MemoryRequestHandler(),
			[1, 16, 0x00, 0x00, 0x00, 0x02, 4, 1, 2]);

		AssertException(response, 16, ModbusError.IllegalDataValue);
	}

	/// <summary>
	/// Checks that a range beyond the last address is rejected.
	/// </summary>
	[TestMethod]
	public async Task WriteMultipleRegisters_BeyondAddressSpace_IllegalDataAddress()
	{
		byte[] response = await Handle(
			new MemoryRequestHandler(),
			[1, 16, 0xff, 0xff, 0x00, 0x02, 4, 0, 0, 0, 0]);

		AssertException(response, 16, ModbusError.IllegalDataAddress);
	}

	#endregion Write requests

	#region Errors and other requests

	/// <summary>
	/// Checks that a <see cref="ModbusException"/> from the handler is sent with its code.
	/// </summary>
	[TestMethod]
	public async Task HandlerModbusException_SendsCode()
	{
		var handler = new MemoryRequestHandler
		{
			Exception = new ModbusException(ModbusError.ServerDeviceBusy)
		};

		byte[] response = await Handle(handler, [1, 3, 0x00, 0x00, 0x00, 0x01]);

		AssertException(response, 3, ModbusError.ServerDeviceBusy);
	}

	/// <summary>
	/// Checks that other exceptions from the handler are sent as a server device failure.
	/// </summary>
	[TestMethod]
	public async Task HandlerOtherException_ServerDeviceFailure()
	{
		var handler = new MemoryRequestHandler
		{
			Exception = new InvalidOperationException()
		};

		byte[] response = await Handle(handler, [1, 6, 0x00, 0x00, 0x00, 0x01]);

		AssertException(response, 6, ModbusError.ServerDeviceFailure);
	}

	/// <summary>
	/// Checks that client-side error codes, which cannot be sent, result in a server device
	/// failure.
	/// </summary>
	[TestMethod]
	public async Task HandlerClientSideErrorCode_ServerDeviceFailure()
	{
		var handler = new MemoryRequestHandler
		{
			Exception = new ModbusException(ModbusError.CrcMismatch)
		};

		byte[] response = await Handle(handler, [1, 3, 0x00, 0x00, 0x00, 0x01]);

		AssertException(response, 3, ModbusError.ServerDeviceFailure);
	}

	/// <summary>
	/// Checks that a cancellation of the request is passed on instead of responded to.
	/// </summary>
	[TestMethod]
	public async Task HandlerCancelled_Throws()
	{
		using var cts = new CancellationTokenSource();
		cts.Cancel();
		var handler = new MemoryRequestHandler
		{
			Exception = new OperationCanceledException(cts.Token)
		};
		byte[] request = [1, 3, 0x00, 0x00, 0x00, 0x01];
		byte[] response = new byte[254];

		await Assert.ThrowsAsync<OperationCanceledException>(
			() => handler.HandleRequest(request, response, cts.Token));
	}

	/// <summary>
	/// Checks that unsupported function codes are rejected.
	/// </summary>
	[TestMethod]
	public async Task UnsupportedFunction_IllegalFunction()
	{
		byte[] response = await Handle(new MemoryRequestHandler(), [1, 43, 14, 1, 0]);

		AssertException(response, 43, ModbusError.IllegalFunction);
	}

	/// <summary>
	/// Checks that the base class rejects reading and writing.
	/// </summary>
	[TestMethod]
	public async Task BaseHandler_IllegalFunction()
	{
		var handler = new ModbusRequestHandler();

		byte[] readResponse = await Handle(handler, [1, 1, 0x00, 0x00, 0x00, 0x01]);
		byte[] writeResponse = await Handle(handler, [1, 6, 0x00, 0x00, 0x00, 0x01]);

		AssertException(readResponse, 1, ModbusError.IllegalFunction);
		AssertException(writeResponse, 6, ModbusError.IllegalFunction);
	}

	/// <summary>
	/// Checks that a request without a function code is not responded to.
	/// </summary>
	[TestMethod]
	public async Task NoFunctionCode_NoResponse()
	{
		byte[] response = await Handle(new MemoryRequestHandler(), [1]);

		Assert.IsEmpty(response);
	}

	#endregion Errors and other requests

	#region Helper methods

	/// <summary>
	/// Passes the request body to the handler and returns the response body.
	/// </summary>
	private static async Task<byte[]> Handle(ModbusRequestHandler handler, byte[] request)
	{
		byte[] response = new byte[254];
		int length = await handler.HandleRequest(request, response);
		return response[..length];
	}

	/// <summary>
	/// Checks that the response body is an exception response.
	/// </summary>
	private static void AssertException(byte[] response, byte functionCode, ModbusError error)
	{
		byte[] expected = [1, (byte)(functionCode | 0x80), (byte)error];
		CollectionAssert.AreEqual(expected, response);
	}

	/// <summary>
	/// Creates a request body that writes OFF to the specified number of coils from address 0.
	/// </summary>
	private static byte[] MakeWriteCoilsRequest(int count)
	{
		int byteCount = (count + 7) / 8;
		byte[] request = new byte[7 + byteCount];
		request[0] = 1;
		request[1] = 15;   // WriteCoils
		request[4] = (byte)(count >> 8);
		request[5] = (byte)(count & 0xff);
		request[6] = (byte)byteCount;
		return request;
	}

	#endregion Helper methods
}
