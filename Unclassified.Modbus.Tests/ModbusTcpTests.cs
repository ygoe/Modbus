using System.Net;
using System.Net.Sockets;
using Unclassified.Modbus.Tests.Fakes;

namespace Unclassified.Modbus.Tests;

/// <summary>
/// Tests the <see cref="ModbusClient"/> with the <see cref="ModbusTcpListener"/> and raw TCP
/// connections over the loopback interface.
/// </summary>
[TestClass]
public class ModbusTcpTests
{
	#region Private data

	/// <summary>
	/// The maximum time to wait for a network operation that is expected to complete.
	/// </summary>
	private static readonly TimeSpan networkTimeout = TimeSpan.FromSeconds(5);

	/// <summary>
	/// The request handler of the listener.
	/// </summary>
	private MemoryRequestHandler handler = null!;

	/// <summary>
	/// The listener that is started for each test.
	/// </summary>
	private ModbusTcpListener listener = null!;

	#endregion Private data

	#region Test initialization and cleanup

	/// <summary>
	/// Starts the listener on an available port.
	/// </summary>
	[TestInitialize]
	public async Task StartListener()
	{
		handler = new MemoryRequestHandler();
		listener = await ModbusTcpListenerTests.StartListener(handler);
	}

	/// <summary>
	/// Stops the listener and closes the remaining connections.
	/// </summary>
	[TestCleanup]
	public async Task StopListener()
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
		await listener.StopAsync(cts.Token);
		listener.Dispose();
	}

	#endregion Test initialization and cleanup

	#region Client and server

	/// <summary>
	/// Checks that typed register values written by the client are read back unchanged.
	/// </summary>
	[TestMethod]
	public async Task HoldingRegisters_RoundTrip()
	{
		using var client = CreateClient();
		var objects = new ModbusCollection(ModbusObjectType.HoldingRegister);
		objects.SetUInt32(100, 0x12345678);
		objects.SetString8(102, "ABCDE");
		objects.SetDouble(105, -1234.5678);

		await client.Write(1, objects, TestContext.CancellationToken);
		var range = new ModbusRange(100, 108);
		var result = await client.Read(ModbusObjectType.HoldingRegister, 1, range, TestContext.CancellationToken);

		Assert.AreEqual(0x12345678u, result.GetUInt32(100));
		Assert.AreEqual("ABCDE", result.GetString8(102, 5));
		Assert.AreEqual(-1234.5678, result.GetDouble(105));
		Assert.AreEqual((ushort)0x1234, handler.HoldingRegisters[100]);
	}

	/// <summary>
	/// Checks that coil states written by the client are read back unchanged.
	/// </summary>
	[TestMethod]
	public async Task Coils_RoundTrip()
	{
		using var client = CreateClient();
		var objects = new ModbusCollection(ModbusObjectType.Coil);
		for (int address = 10; address < 30; address++)
		{
			objects.SetState(address, address % 3 == 0);
		}

		await client.Write(1, objects, TestContext.CancellationToken);
		var result = await client.Read(
			ModbusObjectType.Coil, 1, new ModbusRange(10, 29), TestContext.CancellationToken);

		for (int address = 10; address < 30; address++)
		{
			Assert.AreEqual(address % 3 == 0, result.GetState(address), $"Coil {address}");
		}
	}

	/// <summary>
	/// Checks that ranges beyond the protocol limits are split into requests the server accepts.
	/// </summary>
	[TestMethod]
	public async Task LargeRanges_Split()
	{
		using var client = CreateClient();
		MemoryRequestHandler.FillWithAddresses(handler.InputRegisters, new(0, 299));
		var coils = new ModbusCollection(ModbusObjectType.Coil);
		for (int address = 0; address < 2100; address++)
		{
			coils.SetState(address, address % 5 == 0);
		}

		var range = new ModbusRange(0, 299);
		var registers = await client.Read(ModbusObjectType.InputRegister, 1, range, TestContext.CancellationToken);
		await client.Write(1, coils, TestContext.CancellationToken);

		Assert.AreEqual(300, registers.Count);
		Assert.AreEqual((ushort)299, registers.GetUInt16(299));
		CollectionAssert.AreEqual(
			new[] { new ModbusRange(0, 1967), new ModbusRange(1968, 2099) },
			handler.WrittenRanges);
	}

	/// <summary>
	/// Checks that the device ID is passed to the handler.
	/// </summary>
	[TestMethod]
	public async Task DeviceId_PassedToHandler()
	{
		using var client = CreateClient();
		handler.HoldingRegisters[0] = 1;

		await client.Read(ModbusObjectType.HoldingRegister, 17, new ModbusRange(0), TestContext.CancellationToken);

		Assert.AreEqual((byte)17, handler.LastDeviceId);
	}

	/// <summary>
	/// Checks that an exception response of the server is thrown by the client.
	/// </summary>
	[TestMethod]
	public async Task ExceptionResponse_ThrownByClient()
	{
		using var client = CreateClient();

		var ex = await Assert.ThrowsExactlyAsync<ModbusException>(
			() => client.Read(ModbusObjectType.HoldingRegister, 1, new ModbusRange(0), TestContext.CancellationToken));

		Assert.AreEqual(ModbusError.IllegalDataAddress, ex.Code);
	}

	#endregion Client and server

	#region Raw connections

	/// <summary>
	/// Checks that requests sent together are responded to in order with their transaction IDs.
	/// </summary>
	[TestMethod]
	public async Task PipelinedRequests_RespondedInOrder()
	{
		handler.InputRegisters[5] = 0x0505;
		handler.InputRegisters[6] = 0x0606;
		using var tcpClient = await ConnectRaw();
		var stream = tcpClient.GetStream();

		await stream.WriteAsync(
			(byte[])[
				.. MakeFrame(0x1234, [1, 4, 0x00, 0x05, 0x00, 0x01]),
				.. MakeFrame(0x1235, [1, 4, 0x00, 0x06, 0x00, 0x01])
			],
			TestContext.CancellationToken);
		byte[] responses = await ReadExactly(stream, 2 * 11);

		byte[] expected =
		[
			0x12, 0x34, 0x00, 0x00, 0x00, 0x05, 1, 4, 2, 0x05, 0x05,
			0x12, 0x35, 0x00, 0x00, 0x00, 0x05, 1, 4, 2, 0x06, 0x06
		];
		CollectionAssert.AreEqual(expected, responses);
	}

	/// <summary>
	/// Checks that a request with another protocol ID is ignored and the connection stays usable.
	/// </summary>
	[TestMethod]
	public async Task OtherProtocolId_Ignored()
	{
		handler.InputRegisters[5] = 0x0505;
		using var tcpClient = await ConnectRaw();
		var stream = tcpClient.GetStream();

		byte[] otherProtocolFrame =
			[0x00, 0x01, 0x00, 0x01, 0x00, 0x06, 1, 4, 0x00, 0x05, 0x00, 0x01];
		await stream.WriteAsync(otherProtocolFrame, TestContext.CancellationToken);
		await stream.WriteAsync(MakeFrame(0x0002, [1, 4, 0x00, 0x05, 0x00, 0x01]), TestContext.CancellationToken);
		byte[] response = await ReadExactly(stream, 11);

		Assert.AreEqual((byte)0x02, response[1]);
	}

	/// <summary>
	/// Checks that an invalid length in the header closes the connection.
	/// </summary>
	[TestMethod]
	public async Task InvalidLength_ClosesConnection()
	{
		using var tcpClient = await ConnectRaw();
		var stream = tcpClient.GetStream();

		await stream.WriteAsync((byte[])[0x00, 0x01, 0x00, 0x00, 0x01, 0x00], TestContext.CancellationToken);

		await AssertClosed(stream);
	}

	/// <summary>
	/// Checks that a request that is not completed in time closes the connection.
	/// </summary>
	[TestMethod]
	public async Task IncompleteRequest_ClosesConnectionAfterTimeout()
	{
		await ReplaceListener(handler, incompleteRequestTimeout: TimeSpan.FromMilliseconds(200));
		using var tcpClient = await ConnectRaw();
		var stream = tcpClient.GetStream();

		await stream.WriteAsync((byte[])[0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 1], TestContext.CancellationToken);

		await AssertClosed(stream);
	}

	/// <summary>
	/// Checks that the connection is closed when the handler requests it.
	/// </summary>
	[TestMethod]
	public async Task HandlerRequestsClose_ClosesConnection()
	{
		await ReplaceListener(new ClosingRequestHandler());
		using var tcpClient = await ConnectRaw();
		var stream = tcpClient.GetStream();

		await stream.WriteAsync(MakeFrame(0x0001, [1, 4, 0x00, 0x00, 0x00, 0x01]), TestContext.CancellationToken);

		await AssertClosed(stream);
	}

	/// <summary>
	/// Checks that stopping the listener closes open connections after the graceful period.
	/// </summary>
	[TestMethod]
	public async Task Stop_ClosesOpenConnections()
	{
		handler.InputRegisters[0] = 0;
		using var tcpClient = await ConnectRaw();
		var stream = tcpClient.GetStream();
		await stream.WriteAsync(MakeFrame(0x0001, [1, 4, 0x00, 0x00, 0x00, 0x01]), TestContext.CancellationToken);
		await ReadExactly(stream, 11);
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

		await listener.StopAsync(cts.Token).WaitAsync(networkTimeout, TestContext.CancellationToken);

		await AssertClosed(stream);
		Assert.IsNull(listener.LocalEndPoint);
	}

	#endregion Raw connections

	#region Helper methods

	/// <summary>
	/// Stops the current listener and starts a new one with the specified settings.
	/// </summary>
	private async Task ReplaceListener(
		ModbusRequestHandler requestHandler,
		TimeSpan? incompleteRequestTimeout = null)
	{
		await listener.StopAsync(CancellationToken.None);
		listener.Dispose();
		listener = await ModbusTcpListenerTests.StartListener(
			requestHandler,
			incompleteRequestTimeout);
	}

	/// <summary>
	/// Creates a client that connects to the listener and does not retry.
	/// </summary>
	private ModbusClient CreateClient()
	{
		int port = listener.LocalEndPoint!.Port;
		var connectionFactory = new ModbusTcpConnectionFactory("::1", port, false);
		return new ModbusClient(connectionFactory, null) { RetryCount = 0 };
	}

	/// <summary>
	/// Opens a TCP connection to the listener.
	/// </summary>
	private async Task<TcpClient> ConnectRaw()
	{
		var tcpClient = new TcpClient(AddressFamily.InterNetworkV6);
		await tcpClient.ConnectAsync(
			IPAddress.IPv6Loopback, listener.LocalEndPoint!.Port, TestContext.CancellationToken);
		return tcpClient;
	}

	/// <summary>
	/// Creates a Modbus TCP message frame with the specified transaction ID and body.
	/// </summary>
	private static byte[] MakeFrame(ushort transactionId, byte[] body) =>
	[
		(byte)(transactionId >> 8),
		(byte)(transactionId & 0xff),
		0x00,
		0x00,
		(byte)(body.Length >> 8),
		(byte)(body.Length & 0xff),
		.. body
	];

	/// <summary>
	/// Reads the specified number of bytes from the stream.
	/// </summary>
	private static async Task<byte[]> ReadExactly(NetworkStream stream, int count)
	{
		using var cts = new CancellationTokenSource(networkTimeout);
		byte[] buffer = new byte[count];
		await stream.ReadExactlyAsync(buffer, cts.Token);
		return buffer;
	}

	/// <summary>
	/// Checks that the remote side closes the connection without sending more data.
	/// </summary>
	private static async Task AssertClosed(NetworkStream stream)
	{
		using var cts = new CancellationTokenSource(networkTimeout);
		int length;
		try
		{
			length = await stream.ReadAsync(new byte[16], cts.Token);
		}
		catch (IOException)
		{
			// Connection reset
			return;
		}
		Assert.AreEqual(0, length, "Data received instead of closing the connection");
	}

	#endregion Helper methods

	#region Private classes

	/// <summary>
	/// A request handler that closes the connection in response to every request.
	/// </summary>
	private class ClosingRequestHandler : ModbusRequestHandler
	{
		/// <summary>
		/// Requests to close the connection.
		/// </summary>
		public override Task<int> HandleRequest(
			ReadOnlyMemory<byte> requestBody,
			Memory<byte> responseBody,
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult(-1);
		}
	}

	public TestContext TestContext { get; set; }

	#endregion Private classes
}
