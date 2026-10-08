using Unclassified.Modbus.Tests.Fakes;

namespace Unclassified.Modbus.Tests;

/// <summary>
/// Tests the <see cref="ModbusClient"/> class with a fake connection.
/// </summary>
[TestClass]
public class ModbusClientTests
{
	#region Read

	/// <summary>
	/// Checks the request and the decoding of register values.
	/// </summary>
	[TestMethod]
	public async Task Read_HoldingRegisters()
	{
		var connection = new FakeConnection(_ => [1, 3, 4, 0x12, 0x34, 0xab, 0xcd]);
		using var client = CreateClient(connection);

		var range = new ModbusRange(0x0102, 0x0103);

		var objects = await client.Read(ModbusObjectType.HoldingRegister, 1, range, TestContext.CancellationToken);

		byte[] expectedRequest = [1, 3, 0x01, 0x02, 0x00, 0x02];
		CollectionAssert.AreEqual(expectedRequest, connection.Requests.Single());
		Assert.AreEqual((ushort)0x1234, objects.GetUInt16(0x0102));
		Assert.AreEqual((ushort)0xabcd, objects.GetUInt16(0x0103));
	}

	/// <summary>
	/// Checks the decoding of coil states in address order from the least significant bit.
	/// </summary>
	[TestMethod]
	public async Task Read_Coils_LeastSignificantBitFirst()
	{
		var connection = new FakeConnection(_ => [1, 1, 2, 0x05, 0x02]);
		using var client = CreateClient(connection);

		var objects = await client.Read(ModbusObjectType.Coil, 1, new ModbusRange(0, 9), TestContext.CancellationToken);

		byte[] expectedRequest = [1, 1, 0x00, 0x00, 0x00, 10];
		CollectionAssert.AreEqual(expectedRequest, connection.Requests.Single());
		Assert.AreEqual(10, objects.Count);
		bool[] states = [.. Enumerable.Range(0, 10).Select(objects.GetState)];
		CollectionAssert.AreEqual(
			new[] { true, false, true, false, false, false, false, false, false, true },
			states);
	}

	/// <summary>
	/// Checks that a range longer than the protocol limit is read with multiple requests.
	/// </summary>
	[TestMethod]
	public async Task Read_LongRange_SplitsAtProtocolLimit()
	{
		var connection = new FakeConnection(request => MakeRegisterReadResponse(request));
		using var client = CreateClient(connection);

		var objects = await client.Read(
			ModbusObjectType.InputRegister, 1, new ModbusRange(0, 199), TestContext.CancellationToken);

		CollectionAssert.AreEqual(new[] { (0, 125), (125, 75) }, GetStartAndCounts(connection));
		Assert.AreEqual(200, objects.Count);
		Assert.AreEqual((ushort)150, objects.GetUInt16(150));
	}

	/// <summary>
	/// Checks that the configured maximum request length is used if it is below the limit.
	/// </summary>
	[TestMethod]
	public async Task Read_MaxRequestLength_LimitsRequests()
	{
		var connection = new FakeConnection(request => MakeRegisterReadResponse(request));
		using var client = CreateClient(connection, maxRequestLength: 10);

		await client.Read(ModbusObjectType.HoldingRegister, 1, new ModbusRange(0, 24), TestContext.CancellationToken);

		CollectionAssert.AreEqual(
			new[] { (0, 10), (10, 10), (20, 5) },
			GetStartAndCounts(connection));
	}

	/// <summary>
	/// Checks that ranges are read in one request if the gap is within the allowed waste.
	/// </summary>
	[TestMethod]
	public async Task Read_GapWithinAllowedWaste_SingleRequest()
	{
		var connection = new FakeConnection(request => MakeRegisterReadResponse(request));
		using var client = CreateClient(connection, allowedRequestWaste: 5);

		ModbusRange[] ranges = [new(0, 1), new(5, 6)];

		await client.Read(ModbusObjectType.HoldingRegister, 1, ranges, TestContext.CancellationToken);

		CollectionAssert.AreEqual(new[] { (0, 7) }, GetStartAndCounts(connection));
	}

	/// <summary>
	/// Checks that the remaining registers are requested if a device returns fewer than
	/// requested.
	/// </summary>
	[TestMethod]
	public async Task Read_ShorterResponse_RequestsRemainder()
	{
		var connection = new FakeConnection(
			request => MakeRegisterReadResponse(request, maxCount: 4));
		using var client = CreateClient(connection);

		var objects = await client.Read(
			ModbusObjectType.HoldingRegister, 1, new ModbusRange(0, 9), TestContext.CancellationToken);

		CollectionAssert.AreEqual(new[] { (0, 10), (4, 6), (8, 2) }, GetStartAndCounts(connection));
		Assert.AreEqual(10, objects.Count);
		Assert.AreEqual((ushort)9, objects.GetUInt16(9));
	}

	/// <summary>
	/// Checks that a request is repeated after a busy response.
	/// </summary>
	[TestMethod]
	public async Task Read_Busy_Retries()
	{
		int requestCount = 0;
		var connection = new FakeConnection(request =>
			++requestCount == 1 ?
				[1, 0x83, (byte)ModbusError.ServerDeviceBusy] :
				MakeRegisterReadResponse(request));
		using var client = CreateClient(connection, retryCount: 1);

		var objects = await client.Read(
			ModbusObjectType.HoldingRegister, 1, new ModbusRange(0, 1), TestContext.CancellationToken);

		Assert.HasCount(2, connection.Requests);
		Assert.AreEqual(2, objects.Count);
	}

	/// <summary>
	/// Checks that the busy error is thrown after all retries.
	/// </summary>
	[TestMethod]
	public async Task Read_BusyTooOften_Throws()
	{
		var connection = new FakeConnection(_ => [1, 0x83, (byte)ModbusError.ServerDeviceBusy]);
		using var client = CreateClient(connection, retryCount: 1);

		var ex = await Assert.ThrowsExactlyAsync<ModbusException>(
			() => client.Read(ModbusObjectType.HoldingRegister, 1, new ModbusRange(0), TestContext.CancellationToken));

		Assert.AreEqual(ModbusError.ServerDeviceBusy, ex.Code);
		Assert.HasCount(2, connection.Requests);
	}

	/// <summary>
	/// Checks that other exception responses are thrown without retrying.
	/// </summary>
	[TestMethod]
	public async Task Read_ExceptionResponse_ThrowsWithoutRetry()
	{
		var connection = new FakeConnection(_ => [1, 0x83, (byte)ModbusError.IllegalDataAddress]);
		using var client = CreateClient(connection, retryCount: 3);

		var ex = await Assert.ThrowsExactlyAsync<ModbusException>(
			() => client.Read(ModbusObjectType.HoldingRegister, 1, new ModbusRange(0), TestContext.CancellationToken));

		Assert.AreEqual(ModbusError.IllegalDataAddress, ex.Code);
		Assert.HasCount(1, connection.Requests);
	}

	/// <summary>
	/// Checks that a failing connection is retried and its error is thrown after all retries.
	/// </summary>
	[TestMethod]
	public async Task Read_ConnectionError_RetriesThenThrows()
	{
		var connection = new FakeConnection(_ => throw new IOException());
		using var client = CreateClient(connection, retryCount: 2);

		await Assert.ThrowsExactlyAsync<IOException>(
			() => client.Read(ModbusObjectType.HoldingRegister, 1, new ModbusRange(0), TestContext.CancellationToken));

		Assert.HasCount(3, connection.Requests);
	}

	/// <summary>
	/// Checks that the connection is closed after a CRC error that suggests it is out of sync.
	/// </summary>
	[TestMethod]
	public async Task Read_CrcError_ClosesConnection()
	{
		var connection = new FakeConnection(
			_ => throw new ModbusException(ModbusError.CrcMismatch));
		using var client = CreateClient(connection);

		await Assert.ThrowsExactlyAsync<ModbusException>(
			() => client.Read(ModbusObjectType.HoldingRegister, 1, new ModbusRange(0), TestContext.CancellationToken));

		Assert.IsFalse(connection.IsOpen);
	}

	/// <summary>
	/// Checks that the connection is closed after each request without an idle timeout.
	/// </summary>
	[TestMethod]
	public async Task Read_NoIdleTimeout_ClosesAfterEachRequest()
	{
		var connection = new FakeConnection(request => MakeRegisterReadResponse(request));
		using var client = CreateClient(connection, idleTimeout: TimeSpan.Zero);

		await client.Read(ModbusObjectType.HoldingRegister, 1, new ModbusRange(0), TestContext.CancellationToken);
		await client.Read(ModbusObjectType.HoldingRegister, 1, new ModbusRange(0), TestContext.CancellationToken);

		Assert.AreEqual(2, connection.OpenCount);
		Assert.IsFalse(connection.IsOpen);
	}

	/// <summary>
	/// Checks that the error of a failing connection attempt is thrown without an idle timeout.
	/// </summary>
	[TestMethod]
	public async Task Read_ConnectFailsWithoutIdleTimeout_ThrowsConnectError()
	{
		var connection = new FakeConnection(request => MakeRegisterReadResponse(request))
		{
			ConnectException = new IOException()
		};
		using var client = CreateClient(connection, idleTimeout: TimeSpan.Zero);

		await Assert.ThrowsExactlyAsync<IOException>(
			() => client.Read(ModbusObjectType.HoldingRegister, 1, new ModbusRange(0), TestContext.CancellationToken));
	}

	/// <summary>
	/// Checks that the request events contain the request and response data.
	/// </summary>
	[TestMethod]
	public async Task Read_RaisesEvents()
	{
		var connection = new FakeConnection(request => MakeRegisterReadResponse(request));
		using var client = CreateClient(connection);
		ModbusClient.RequestEventArgs? requestArgs = null;
		ModbusClient.ResponseEventArgs? responseArgs = null;
		client.SendingRequest += (sender, args) => requestArgs = args;
		client.ResponseReceived += (sender, args) => responseArgs = args;

		await client.Read(ModbusObjectType.HoldingRegister, 5, new ModbusRange(10, 11), TestContext.CancellationToken);

		Assert.IsNotNull(requestArgs);
		Assert.AreEqual((byte)5, requestArgs.DeviceId);
		Assert.AreEqual("ReadHoldingRegisters", requestArgs.FunctionCode);
		Assert.AreEqual((ushort)10, requestArgs.StartAddress);
		Assert.AreEqual((ushort)2, requestArgs.Count);
		Assert.IsNotNull(responseArgs);
		Assert.AreEqual((byte)5, responseArgs.DeviceId);
		CollectionAssert.AreEqual(new ushort[] { 10, 11 }, responseArgs.DataArray);
	}

	/// <summary>
	/// Checks that the response event contains one state value for each read coil.
	/// </summary>
	[TestMethod]
	public async Task Read_Coils_RaisesEventWithStates()
	{
		var connection = new FakeConnection(_ => [1, 1, 2, 0x05, 0x02]);
		using var client = CreateClient(connection);
		ModbusClient.ResponseEventArgs? responseArgs = null;
		client.ResponseReceived += (sender, args) => responseArgs = args;

		await client.Read(ModbusObjectType.Coil, 1, new ModbusRange(0, 9), TestContext.CancellationToken);

		Assert.IsNotNull(responseArgs);
		CollectionAssert.AreEqual(
			new ushort[] { 1, 0, 1, 0, 0, 0, 0, 0, 0, 1 },
			responseArgs.DataArray);
	}

	#endregion Read

	#region Write

	/// <summary>
	/// Checks the request to write a single register.
	/// </summary>
	[TestMethod]
	public async Task Write_SingleRegister()
	{
		var connection = new FakeConnection(request => request);
		using var client = CreateClient(connection);
		var objects = new ModbusCollection(ModbusObjectType.HoldingRegister);
		objects.SetUInt16(0x10, 0xbeef);

		await client.Write(1, objects, TestContext.CancellationToken);

		byte[] expectedRequest = [1, 6, 0x00, 0x10, 0xbe, 0xef];
		CollectionAssert.AreEqual(expectedRequest, connection.Requests.Single());
	}

	/// <summary>
	/// Checks the request to write a single coil.
	/// </summary>
	[TestMethod]
	public async Task Write_SingleCoil()
	{
		var connection = new FakeConnection(request => request);
		using var client = CreateClient(connection);
		var objects = new ModbusCollection(ModbusObjectType.Coil);
		objects.SetState(3, true);

		await client.Write(1, objects, TestContext.CancellationToken);

		byte[] expectedRequest = [1, 5, 0x00, 0x03, 0xff, 0x00];
		CollectionAssert.AreEqual(expectedRequest, connection.Requests.Single());
	}

	/// <summary>
	/// Checks the request to write multiple registers.
	/// </summary>
	[TestMethod]
	public async Task Write_MultipleRegisters()
	{
		var connection = new FakeConnection(request => request[..6]);
		using var client = CreateClient(connection);
		var objects = new ModbusCollection(ModbusObjectType.HoldingRegister);
		objects.SetUInt32(0x20, 0x12345678);

		await client.Write(1, objects, TestContext.CancellationToken);

		CollectionAssert.AreEqual(
			new byte[] { 1, 16, 0x00, 0x20, 0x00, 0x02, 4, 0x12, 0x34, 0x56, 0x78 },
			connection.Requests.Single());
	}

	/// <summary>
	/// Checks the request to write multiple coils in address order from the least significant
	/// bit.
	/// </summary>
	[TestMethod]
	public async Task Write_MultipleCoils()
	{
		var connection = new FakeConnection(request => request[..6]);
		using var client = CreateClient(connection);
		var objects = new ModbusCollection(ModbusObjectType.Coil);
		for (int address = 0; address < 10; address++)
		{
			objects.SetState(address, address is 0 or 2 or 9);
		}

		await client.Write(1, objects, TestContext.CancellationToken);

		CollectionAssert.AreEqual(
			new byte[] { 1, 15, 0x00, 0x00, 0x00, 10, 2, 0x05, 0x02 },
			connection.Requests.Single());
	}

	/// <summary>
	/// Checks that the request event contains one state value for each written coil.
	/// </summary>
	[TestMethod]
	public async Task Write_MultipleCoils_RaisesEventWithStates()
	{
		var connection = new FakeConnection(request => request[..6]);
		using var client = CreateClient(connection);
		ModbusClient.RequestEventArgs? requestArgs = null;
		client.SendingRequest += (sender, args) => requestArgs = args;
		var objects = new ModbusCollection(ModbusObjectType.Coil);
		for (int address = 0; address < 10; address++)
		{
			objects.SetState(address, address is 0 or 2 or 9);
		}

		await client.Write(1, objects, TestContext.CancellationToken);

		Assert.IsNotNull(requestArgs);
		CollectionAssert.AreEqual(
			new ushort[] { 1, 0, 1, 0, 0, 0, 0, 0, 0, 1 },
			requestArgs.DataArray);
	}

	/// <summary>
	/// Checks that a range longer than the protocol limit is written with multiple requests.
	/// </summary>
	[TestMethod]
	public async Task Write_LongRange_SplitsAtProtocolLimit()
	{
		var connection = new FakeConnection(request => request[..6]);
		using var client = CreateClient(connection);
		var objects = new ModbusCollection(ModbusObjectType.HoldingRegister);
		for (int address = 0; address < 130; address++)
		{
			objects.SetUInt16(address, (ushort)address);
		}

		await client.Write(1, objects, TestContext.CancellationToken);

		CollectionAssert.AreEqual(new[] { (0, 123), (123, 7) }, GetStartAndCounts(connection));
	}

	/// <summary>
	/// Checks that a different confirmed value is reported.
	/// </summary>
	[TestMethod]
	public async Task Write_DifferentValueConfirmed_Throws()
	{
		var connection = new FakeConnection(_ => [1, 6, 0x00, 0x10, 0x00, 0x00]);
		using var client = CreateClient(connection);
		var objects = new ModbusCollection(ModbusObjectType.HoldingRegister);
		objects.SetUInt16(0x10, 0xbeef);

		var ex = await Assert.ThrowsExactlyAsync<ModbusException>(
			() => client.Write(1, objects, TestContext.CancellationToken));

		Assert.AreEqual(ModbusError.WriteMismatch, ex.Code);
	}

	/// <summary>
	/// Checks that the multiple write function is used if a device rejects the single write
	/// function.
	/// </summary>
	[TestMethod]
	public async Task Write_SingleRejected_SwitchesToMultiple()
	{
		var connection = new FakeConnection(request =>
			request[1] == 6 ? [1, 0x86, (byte)ModbusError.IllegalFunction] : request[..6]);
		using var client = CreateClient(connection);
		var objects = new ModbusCollection(ModbusObjectType.HoldingRegister);
		objects.SetUInt16(0x10, 0xbeef);

		await client.Write(1, objects, TestContext.CancellationToken);

		byte[] functionCodes = [.. connection.Requests.Select(r => r[1])];
		CollectionAssert.AreEqual(new byte[] { 6, 16 }, functionCodes);
		Assert.IsTrue(client.AlwaysWriteMultiple);
	}

	/// <summary>
	/// Checks that each register is written individually if a device rejects the multiple write
	/// function.
	/// </summary>
	[TestMethod]
	public async Task Write_MultipleRejected_WritesEachRegister()
	{
		var connection = new FakeConnection(request =>
			request[1] == 16 ? [1, 0x90, (byte)ModbusError.IllegalFunction] : request);
		using var client = CreateClient(connection);
		var objects = new ModbusCollection(ModbusObjectType.HoldingRegister);
		objects.SetUInt16(0x10, 0x1111);
		objects.SetUInt16(0x11, 0x2222);
		objects.SetUInt16(0x12, 0x3333);

		await client.Write(1, objects, TestContext.CancellationToken);

		byte[][] expectedSingleRequests =
		[
			[1, 6, 0x00, 0x10, 0x11, 0x11],
			[1, 6, 0x00, 0x11, 0x22, 0x22],
			[1, 6, 0x00, 0x12, 0x33, 0x33]
		];
		Assert.IsTrue(client.AlwaysWriteSingle);
		Assert.HasCount(4, connection.Requests);
		Assert.AreEqual((byte)16, connection.Requests[0][1]);
		for (int i = 0; i < expectedSingleRequests.Length; i++)
		{
			CollectionAssert.AreEqual(expectedSingleRequests[i], connection.Requests[i + 1]);
		}
	}

	/// <summary>
	/// Checks that the remaining registers are written if a device confirms fewer than
	/// requested.
	/// </summary>
	[TestMethod]
	public async Task Write_FewerConfirmed_WritesRemainder()
	{
		var connection = new FakeConnection(request =>
		{
			// Confirm at most 2 registers
			byte[] response = request[..6];
			response[4] = 0;
			response[5] = Math.Min(request[5], (byte)2);
			return response;
		});
		using var client = CreateClient(connection);
		var objects = new ModbusCollection(ModbusObjectType.HoldingRegister);
		objects.SetUInt32(0x10, 0x11112222);
		objects.SetUInt32(0x12, 0x33334444);

		await client.Write(1, objects, TestContext.CancellationToken);

		CollectionAssert.AreEqual(new[] { (0x10, 4), (0x12, 2) }, GetStartAndCounts(connection));
		byte[] expectedRequest = [1, 16, 0x00, 0x12, 0x00, 0x02, 4, 0x33, 0x33, 0x44, 0x44];
		CollectionAssert.AreEqual(expectedRequest, connection.Requests[1]);
	}

	#endregion Write

	#region Read device identification

	/// <summary>
	/// Checks the request and the decoding of the basic device identification objects.
	/// </summary>
	[TestMethod]
	public async Task ReadDeviceIdentification_Basic()
	{
		byte[] response =
		[
			1, 43, 14, 1,
			0x01,   // Conformity level: basic identification, stream access only
			0x00,   // More follows: no
			0x00,   // Next object ID
			3,   // Number of objects
			0x00, 3, (byte)'A', (byte)'B', (byte)'C',
			0x01, 2, (byte)'P', (byte)'1',
			0x02, 3, (byte)'1', (byte)'.', (byte)'0'
		];
		var connection = new FakeConnection(_ => response);
		using var client = CreateClient(connection);

		var values = await client.ReadDeviceIdentification(1, TestContext.CancellationToken);

		CollectionAssert.AreEqual(new byte[] { 1, 43, 14, 1, 0 }, connection.Requests.Single());
		Assert.AreEqual("ABC", values[ModbusDeviceIdentificationObject.VendorName]);
		Assert.AreEqual("P1", values[ModbusDeviceIdentificationObject.ProductCode]);
		Assert.AreEqual("1.0", values[ModbusDeviceIdentificationObject.MajorMinorRevision]);
	}

	#endregion Read device identification

	#region Helper methods

	/// <summary>
	/// Creates a client for the fake connection without retry delays.
	/// </summary>
	private static ModbusClient CreateClient(
		FakeConnection connection,
		int retryCount = 0,
		int maxRequestLength = 0,
		int allowedRequestWaste = 0,
		TimeSpan? idleTimeout = null)
	{
		return new ModbusClient(connection, null)
		{
			RetryCount = retryCount,
			MaxRequestLength = maxRequestLength,
			AllowedRequestWaste = allowedRequestWaste,
			IdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(7),
			BusyRetryDelay = TimeSpan.Zero,
			ExceptionRetryDelay = TimeSpan.Zero
		};
	}

	/// <summary>
	/// Creates the response body to a register read request with values that equal their
	/// addresses.
	/// </summary>
	/// <param name="request">The request body.</param>
	/// <param name="maxCount">The maximum number of registers to return.</param>
	/// <returns>The response body.</returns>
	private static byte[] MakeRegisterReadResponse(byte[] request, int maxCount = int.MaxValue)
	{
		(int startAddress, int count) = GetStartAndCount(request);
		count = Math.Min(count, maxCount);
		byte[] response = new byte[3 + count * 2];
		response[0] = request[0];
		response[1] = request[1];
		response[2] = (byte)(count * 2);
		for (int i = 0; i < count; i++)
		{
			response[3 + i * 2] = (byte)((startAddress + i) >> 8);
			response[3 + i * 2 + 1] = (byte)((startAddress + i) & 0xff);
		}
		return response;
	}

	/// <summary>
	/// Returns the start address and count of a read or multiple write request body.
	/// </summary>
	private static (int StartAddress, int Count) GetStartAndCount(byte[] request) =>
		(request[2] << 8 | request[3], request[4] << 8 | request[5]);

	/// <summary>
	/// Returns the start addresses and counts of all requests sent through the connection.
	/// </summary>
	private static (int StartAddress, int Count)[] GetStartAndCounts(FakeConnection connection) =>
		[.. connection.Requests.Select(GetStartAndCount)];

	public TestContext TestContext { get; set; }

	#endregion Helper methods
}
