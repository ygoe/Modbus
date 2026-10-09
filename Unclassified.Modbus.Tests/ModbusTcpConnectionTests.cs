using System.Net;
using System.Net.Sockets;

namespace Unclassified.Modbus.Tests;

/// <summary>
/// Tests the <see cref="ModbusTcpConnection"/> class through a <see cref="ModbusClient"/> with a
/// scripted server over the loopback interface.
/// </summary>
[TestClass]
public class ModbusTcpConnectionTests
{
	#region Private data

	/// <summary>
	/// The maximum time to wait for a network operation that is expected to complete.
	/// </summary>
	private static readonly TimeSpan networkTimeout = TimeSpan.FromSeconds(5);

	/// <summary>
	/// The response timeout of the client for tests that let a request time out.
	/// </summary>
	private static readonly TimeSpan shortResponseTimeout = TimeSpan.FromMilliseconds(500);

	/// <summary>
	/// The response body to reading input register 5 that the tests expect.
	/// </summary>
	private static readonly byte[] responseBody = [1, 4, 2, 0x05, 0x05];

	/// <summary>
	/// The response body to reading input register 5 that the client must not accept.
	/// </summary>
	private static readonly byte[] lateResponseBody = [1, 4, 2, 0x09, 0x09];

	/// <summary>
	/// The listener that the client connects to.
	/// </summary>
	private TcpListener listener = null!;

	#endregion Private data

	#region Test initialization and cleanup

	/// <summary>
	/// Starts the listener on an available port.
	/// </summary>
	[TestInitialize]
	public void StartListener()
	{
		listener = new TcpListener(IPAddress.IPv6Loopback, 0);
		listener.Start();
	}

	/// <summary>
	/// Stops the listener.
	/// </summary>
	[TestCleanup]
	public void StopListener()
	{
		listener.Dispose();
	}

	#endregion Test initialization and cleanup

	#region Transaction IDs

	/// <summary>
	/// Checks that a response with another transaction ID that arrives together with the matching
	/// response is discarded.
	/// </summary>
	[TestMethod]
	public async Task MismatchingTransactionId_SameSegment_Discarded()
	{
		using var client = CreateClient();
		var readTask = ReadRegister(client);
		using var server = await Accept();
		var stream = server.GetStream();
		ushort transactionId = await ReadRequest(stream);

		await stream.WriteAsync(
			(byte[])[
				.. ModbusTcpTests.MakeFrame(unchecked((ushort)(transactionId - 1)), lateResponseBody),
				.. ModbusTcpTests.MakeFrame(transactionId, responseBody)
			],
			TestContext.CancellationToken);

		Assert.AreEqual((ushort)0x0505, await readTask);
	}

	/// <summary>
	/// Checks that a response with another transaction ID is discarded when the matching response
	/// follows separately and split into multiple segments.
	/// </summary>
	[TestMethod]
	public async Task MismatchingTransactionId_SplitSegments_Discarded()
	{
		using var client = CreateClient();
		var readTask = ReadRegister(client);
		using var server = await Accept();
		var stream = server.GetStream();
		ushort transactionId = await ReadRequest(stream);
		byte[] frame = ModbusTcpTests.MakeFrame(transactionId, responseBody);

		await WriteSegments(
			stream,
			ModbusTcpTests.MakeFrame(unchecked((ushort)(transactionId - 1)), lateResponseBody),
			frame[..3],
			frame[3..8],
			frame[8..]);

		Assert.AreEqual((ushort)0x0505, await readTask);
	}

	/// <summary>
	/// Checks that only responses with another transaction ID lead to a transaction ID mismatch
	/// error instead of a timeout, without switching the write mode.
	/// </summary>
	[TestMethod]
	public async Task NoMatchingResponse_ThrowsTransactionIdMismatch()
	{
		using var client = CreateClient(responseTimeout: shortResponseTimeout, retryCount: 1);
		var objects = new ModbusCollection(ModbusObjectType.HoldingRegister);
		objects.SetUInt16(7, 0x1234);
		var writeTask = client.Write(1, objects, TestContext.CancellationToken);
		using var server = await Accept();
		var stream = server.GetStream();

		for (int i = 0; i < 2; i++)
		{
			ushort transactionId = await ReadRequest(stream);
			await stream.WriteAsync(
				ModbusTcpTests.MakeFrame(unchecked((ushort)(transactionId - 1)), [1, 6, 0, 7, 0x12, 0x34]),
				TestContext.CancellationToken);
		}

		var ex = await Assert.ThrowsExactlyAsync<ModbusException>(() => writeTask);
		Assert.AreEqual(ModbusError.TransactionIdMismatch, ex.Code);
		Assert.IsFalse(client.AlwaysWriteMultiple);
	}

	/// <summary>
	/// Checks that transaction IDs are not reset by a new connection of a new client.
	/// </summary>
	[TestMethod]
	public async Task TransactionIds_ContinueAcrossConnections()
	{
		ushort firstTransactionId = await ReadWithNewClient();
		ushort secondTransactionId = await ReadWithNewClient();

		Assert.IsGreaterThan(firstTransactionId, secondTransactionId);
	}

	#endregion Transaction IDs

	#region Late responses

	/// <summary>
	/// Checks that a late response that was received before the next request is discarded, even
	/// if transaction IDs are ignored.
	/// </summary>
	[TestMethod]
	public async Task LateResponse_ReceivedBeforeRequest_Discarded()
	{
		using var client = CreateClient(
			ignoreTransactionIdMismatch: true, responseTimeout: shortResponseTimeout);
		var readTask = ReadRegister(client);
		using var server = await Accept();
		var stream = server.GetStream();
		ushort lateTransactionId = await ReadRequest(stream);
		await Assert.ThrowsExactlyAsync<TimeoutException>(() => readTask);
		await stream.WriteAsync(
			ModbusTcpTests.MakeFrame(lateTransactionId, lateResponseBody),
			TestContext.CancellationToken);
		await Task.Delay(100, TestContext.CancellationToken);   // Arrive before the next request

		readTask = ReadRegister(client);
		ushort transactionId = await ReadRequest(stream);
		await stream.WriteAsync(
			ModbusTcpTests.MakeFrame(transactionId, responseBody),
			TestContext.CancellationToken);

		Assert.AreEqual((ushort)0x0505, await readTask);
	}

	/// <summary>
	/// Checks that a late response that was partially received before the next request is
	/// discarded, even if transaction IDs are ignored.
	/// </summary>
	[TestMethod]
	public async Task LateResponse_PartiallyReceivedBeforeRequest_Discarded()
	{
		using var client = CreateClient(
			ignoreTransactionIdMismatch: true, responseTimeout: shortResponseTimeout);
		var readTask = ReadRegister(client);
		using var server = await Accept();
		var stream = server.GetStream();
		ushort lateTransactionId = await ReadRequest(stream);
		await Assert.ThrowsExactlyAsync<TimeoutException>(() => readTask);
		byte[] lateFrame = ModbusTcpTests.MakeFrame(lateTransactionId, lateResponseBody);
		await stream.WriteAsync(lateFrame.AsMemory(0, 4), TestContext.CancellationToken);
		await Task.Delay(100, TestContext.CancellationToken);   // Arrive before the next request

		readTask = ReadRegister(client);
		ushort transactionId = await ReadRequest(stream);
		await stream.WriteAsync(
			(byte[])[.. lateFrame[4..], .. ModbusTcpTests.MakeFrame(transactionId, responseBody)],
			TestContext.CancellationToken);

		Assert.AreEqual((ushort)0x0505, await readTask);
	}

	/// <summary>
	/// Checks that the rest of a response that was incomplete when its request timed out is not
	/// mistaken for the start of the next response.
	/// </summary>
	[TestMethod]
	public async Task IncompleteResponse_AfterTimeout_Discarded()
	{
		using var client = CreateClient(responseTimeout: shortResponseTimeout);
		var readTask = ReadRegister(client);
		using var server = await Accept();
		var stream = server.GetStream();
		ushort lateTransactionId = await ReadRequest(stream);
		byte[] lateFrame = ModbusTcpTests.MakeFrame(lateTransactionId, lateResponseBody);
		await stream.WriteAsync(lateFrame.AsMemory(0, 8), TestContext.CancellationToken);
		await Assert.ThrowsExactlyAsync<TimeoutException>(() => readTask);

		readTask = ReadRegister(client);
		ushort transactionId = await ReadRequest(stream);
		await WriteSegments(stream, lateFrame[8..], ModbusTcpTests.MakeFrame(transactionId, responseBody));

		Assert.AreEqual((ushort)0x0505, await readTask);
	}

	#endregion Late responses

	#region Frame header

	/// <summary>
	/// Checks that a response with an invalid protocol ID or length closes the connection.
	/// </summary>
	[TestMethod]
	[DataRow(1, 5)]
	[DataRow(0, 1)]
	[DataRow(0, 255)]
	public async Task InvalidFrameHeader_ClosesConnection(int protocolId, int length)
	{
		using var client = CreateClient();
		var readTask = ReadRegister(client);
		using var server = await Accept();
		var stream = server.GetStream();
		ushort transactionId = await ReadRequest(stream);

		await stream.WriteAsync(
			(byte[])[
				(byte)(transactionId >> 8),
				(byte)(transactionId & 0xff),
				(byte)(protocolId >> 8),
				(byte)(protocolId & 0xff),
				(byte)(length >> 8),
				(byte)(length & 0xff),
				.. responseBody
			],
			TestContext.CancellationToken);

		var ex = await Assert.ThrowsExactlyAsync<ModbusException>(() => readTask);
		Assert.AreEqual(ModbusError.InvalidFrameHeader, ex.Code);
		await ModbusTcpTests.AssertClosed(stream);
	}

	#endregion Frame header

	#region Helper methods

	/// <summary>
	/// Accepts the next connection to the listener.
	/// </summary>
	private async Task<TcpClient> Accept()
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
		cts.CancelAfter(networkTimeout);
		var server = await listener.AcceptTcpClientAsync(cts.Token);
		// Send each segment written by a test immediately
		server.NoDelay = true;
		return server;
	}

	/// <summary>
	/// Creates a client that connects to the listener.
	/// </summary>
	private ModbusClient CreateClient(
		bool ignoreTransactionIdMismatch = false,
		TimeSpan? responseTimeout = null,
		int retryCount = 0)
	{
		int port = ((IPEndPoint)listener.LocalEndpoint).Port;
		var connectionFactory = new ModbusTcpConnectionFactory("::1", port, ignoreTransactionIdMismatch);
		return new ModbusClient(connectionFactory, null)
		{
			ResponseTimeout = responseTimeout ?? networkTimeout,
			ExceptionRetryDelay = TimeSpan.Zero,
			RetryCount = retryCount
		};
	}

	/// <summary>
	/// Reads input register 5 of device 1.
	/// </summary>
	private async Task<ushort> ReadRegister(ModbusClient client)
	{
		var objects = await client.Read(
			ModbusObjectType.InputRegister, 1, new ModbusRange(5), TestContext.CancellationToken);
		return objects.GetUInt16(5);
	}

	/// <summary>
	/// Reads a request frame from the stream and returns its transaction ID.
	/// </summary>
	private static async Task<ushort> ReadRequest(NetworkStream stream)
	{
		byte[] header = await ModbusTcpTests.ReadExactly(stream, 6);
		await ModbusTcpTests.ReadExactly(stream, header[4] << 8 | header[5]);
		return (ushort)(header[0] << 8 | header[1]);
	}

	/// <summary>
	/// Reads input register 5 with a new client and returns the transaction ID of the request.
	/// </summary>
	private async Task<ushort> ReadWithNewClient()
	{
		using var client = CreateClient();
		var readTask = ReadRegister(client);
		using var server = await Accept();
		var stream = server.GetStream();
		ushort transactionId = await ReadRequest(stream);
		await stream.WriteAsync(
			ModbusTcpTests.MakeFrame(transactionId, responseBody),
			TestContext.CancellationToken);
		await readTask;
		return transactionId;
	}

	/// <summary>
	/// Writes the segments to the stream with a pause after each, so that the client receives them
	/// separately.
	/// </summary>
	private async Task WriteSegments(NetworkStream stream, params byte[][] segments)
	{
		foreach (byte[] segment in segments)
		{
			await stream.WriteAsync(segment, TestContext.CancellationToken);
			await Task.Delay(50, TestContext.CancellationToken);
		}
	}

	#endregion Helper methods

	#region Properties

	/// <summary>
	/// Gets or sets the context of the current test.
	/// </summary>
	public TestContext TestContext { get; set; }

	#endregion Properties
}
