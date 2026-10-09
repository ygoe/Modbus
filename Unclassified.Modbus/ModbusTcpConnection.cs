using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Unclassified.Modbus.Util;

namespace Unclassified.Modbus;

/// <summary>
/// A client connection to a Modbus server over an IP network.
/// </summary>
internal class ModbusTcpConnection : IModbusConnection
{
	/// <summary>
	/// The length of the MBAP header.
	/// </summary>
	private const int HeaderLength = 6;

	/// <summary>
	/// The minimum length value in the header: the device ID and the function code.
	/// </summary>
	private const int MinBodyLength = 2;

	/// <summary>
	/// The maximum length value in the header: the device ID and the maximum PDU of 253 bytes.
	/// </summary>
	private const int MaxBodyLength = 254;

	/// <summary>
	/// The transaction ID of the last request on any connection.
	/// </summary>
	/// <remarks>
	/// Some devices send a response after its request has timed out, even on a new connection.
	/// Continuing the IDs across connections lets such a response be recognised by its ID. The IDs
	/// start at 1 rather than a random value to stay predictable for debugging and testing.
	/// </remarks>
	private static int lastTransactionId;

	private readonly ILogger? logger;
	private TcpClient? tcpClient;
	private bool ignoreTransactionIdMismatch;

	/// <summary>
	/// The received bytes that have not been consumed yet, starting at a frame header.
	/// </summary>
	/// <remarks>
	/// The bytes are kept across requests because a read can return more than one frame, and a
	/// frame can be incomplete when a request times out. More bytes are only received while no
	/// complete frame is buffered, so there is always space for at least one more frame.
	/// </remarks>
	private readonly byte[] buffer = new byte[2 * (HeaderLength + MaxBodyLength)];

	/// <summary>
	/// The number of bytes in <see cref="buffer"/>.
	/// </summary>
	private int bufferUsed;

	public ModbusTcpConnection(ILogger? logger)
	{
		this.logger = logger;
	}

	public bool IsOpen => tcpClient?.Client.Connected == true;

	internal async Task Connect(
		string hostName,
		int port,
		bool ignoreTransactionIdMismatch,
		CancellationToken cancellationToken = default)
	{
		if (logger?.IsEnabled(LogLevel.Debug) == true)
			logger?.LogDebug("Connecting to {HostName}:{Port}...", hostName, port);
		tcpClient = new TcpClient(AddressFamily.InterNetworkV6);
		tcpClient.Client.DualMode = true;
		await tcpClient.ConnectAsync(hostName, port, cancellationToken).NoSync();
		if (logger?.IsEnabled(LogLevel.Debug) == true)
			logger?.LogDebug("Connected to {HostName}:{Port}", hostName, port);
		this.ignoreTransactionIdMismatch = ignoreTransactionIdMismatch;
	}

	/// <summary>
	/// Sends a request in a Modbus TCP message frame to the server and waits for the response with
	/// the same transaction ID.
	/// </summary>
	/// <remarks>
	/// Responses that were received before the request was sent are discarded. Responses with
	/// another transaction ID are discarded while waiting for the matching response, unless
	/// transaction IDs are ignored. If no matching response follows within the time of the
	/// <paramref name="cancellationToken"/>, a <see cref="ModbusException"/> with
	/// <see cref="ModbusError.TransactionIdMismatch"/> is thrown instead of the
	/// <see cref="OperationCanceledException"/>.
	/// </remarks>
	public async Task<ReadOnlyMemory<byte>> SendRequest(
		ReadOnlyMemory<byte> requestBody,
		CancellationToken cancellationToken = default)
	{
		if (tcpClient == null)
			throw new InvalidOperationException("Connection not opened.");

		var stream = tcpClient.GetStream();
		await DiscardReceivedFrames(stream, cancellationToken).NoSync();
		// The rest of a partial frame left in the buffer also belongs to an earlier request
		bool isStaleFrameBuffered = bufferUsed > 0;

		var bytes = MakeMessageFrame(requestBody.Span, out ushort transactionId);
		if (logger?.IsEnabled(LogLevel.Trace) == true)
			logger?.LogTrace("Sending: {HexData}", bytes.Span.ToHexString());
		await stream.WriteAsync(bytes, cancellationToken).NoSync();

		// Wait for a response
		int? discardedTransactionId = null;
		try
		{
			while (true)
			{
				int frameLength = GetBufferedFrameLength();
				if (frameLength == 0)
				{
					await Receive(stream, cancellationToken).NoSync();
					continue;
				}

				int rcvdTransactionId = buffer[0] << 8 | buffer[1];
				if (isStaleFrameBuffered)
				{
					if (logger?.IsEnabled(LogLevel.Debug) == true)
						logger?.LogDebug(
							"Discarding response with transaction ID {RcvdTransactionId} that was partially received before the request.",
							rcvdTransactionId);
					RemoveFrame(frameLength);
					isStaleFrameBuffered = false;
					continue;
				}
				if (rcvdTransactionId != transactionId)
				{
					if (ignoreTransactionIdMismatch)
					{
						if (logger?.IsEnabled(LogLevel.Debug) == true)
							logger?.LogDebug(
								"Response transaction ID {RcvdTransactionId} does not match request transaction ID {TransactionId}.",
								rcvdTransactionId,
								transactionId);
					}
					else
					{
						// Probably the late response to an earlier request, the matching response may follow
						if (logger?.IsEnabled(LogLevel.Debug) == true)
							logger?.LogDebug(
								"Discarding response with transaction ID {RcvdTransactionId} that does not match request transaction ID {TransactionId}.",
								rcvdTransactionId,
								transactionId);
						RemoveFrame(frameLength);
						discardedTransactionId = rcvdTransactionId;
						continue;
					}
				}
				byte[] responseBody = buffer.AsSpan(HeaderLength, frameLength - HeaderLength).ToArray();
				RemoveFrame(frameLength);
				return responseBody;
			}
		}
		catch (OperationCanceledException ex) when (discardedTransactionId != null)
		{
			// The mismatch is the more specific reason. A timeout would also make ModbusClient
			// assume that the device does not support the requested write function.
			throw new ModbusException(
				ModbusError.TransactionIdMismatch,
				$"Response transaction ID ({discardedTransactionId}) does not match request ({transactionId}), and no matching response followed.",
				ex);
		}
	}

	public void Close()
	{
		var localTcpClient = tcpClient;
		if (localTcpClient != null)
		{
			localTcpClient.Close();
			localTcpClient.Dispose();
			tcpClient = null;
			bufferUsed = 0;
			logger?.LogDebug("Connection closed");
		}
	}

	private static ReadOnlyMemory<byte> MakeMessageFrame(ReadOnlySpan<byte> requestBody, out ushort transactionId)
	{
		transactionId = (ushort)(Interlocked.Increment(ref lastTransactionId) & 0xffff);
		byte[] bytes = new byte[HeaderLength + requestBody.Length];
		bytes[0] = (byte)(transactionId >> 8);   // Transaction ID
		bytes[1] = (byte)(transactionId & 0xff);   // Transaction ID
		bytes[2] = 0;
		bytes[3] = 0;
		ushort length = (ushort)requestBody.Length;
		bytes[4] = (byte)(length >> 8);
		bytes[5] = (byte)(length & 0xff);
		requestBody.CopyTo(bytes.AsSpan(HeaderLength));
		return bytes;
	}

	/// <summary>
	/// Receives the bytes that are already available without waiting, and discards all complete
	/// frames in the buffer.
	/// </summary>
	/// <remarks>
	/// These frames were received before the next request is sent, so they cannot be its
	/// response. This is the only defence against late responses if transaction IDs are ignored.
	/// </remarks>
	private async Task DiscardReceivedFrames(NetworkStream stream, CancellationToken cancellationToken)
	{
		while (true)
		{
			int frameLength;
			while ((frameLength = GetBufferedFrameLength()) > 0)
			{
				if (logger?.IsEnabled(LogLevel.Debug) == true)
					logger?.LogDebug(
						"Discarding response with transaction ID {RcvdTransactionId} that was received before the request.",
						buffer[0] << 8 | buffer[1]);
				RemoveFrame(frameLength);
			}
			if (stream.Socket.Available <= 0)
				break;
			await Receive(stream, cancellationToken).NoSync();
		}
	}

	/// <summary>
	/// Waits for bytes from the network and appends them to the buffer.
	/// </summary>
	private async Task Receive(NetworkStream stream, CancellationToken cancellationToken)
	{
		int readBytes = await stream.ReadAsync(buffer.AsMemory(bufferUsed), cancellationToken).NoSync();
		if (readBytes <= 0)
		{
			// We need to close the stream manually or more trouble is coming that way...
			stream.Close();
			logger?.LogWarning("Connection closed by remote request");
			throw new IOException($"The network stream was closed unexpectedly by the Modbus server while waiting for a response, got {bufferUsed} bytes so far.");
		}
		bufferUsed += readBytes;
		if (logger?.IsEnabled(LogLevel.Trace) == true)
			logger?.LogTrace("Received: {HexData}", buffer.AsSpan(0, bufferUsed).ToHexString());
	}

	/// <summary>
	/// Returns the length of the frame at the start of the buffer if it is complete.
	/// </summary>
	/// <returns>The frame length including the header, or 0 if the frame is incomplete.</returns>
	/// <exception cref="ModbusException">With <see cref="ModbusError.InvalidFrameHeader"/> after
	///   closing the connection.</exception>
	private int GetBufferedFrameLength()
	{
		if (bufferUsed < HeaderLength)
			return 0;

		int protocolId = buffer[2] << 8 | buffer[3];
		int bodyLength = buffer[4] << 8 | buffer[5];
		if (protocolId != 0 || bodyLength < MinBodyLength || bodyLength > MaxBodyLength)
		{
			// The start of the next frame is unknown, so no further received data can be trusted
			Close();
			throw new ModbusException(
				ModbusError.InvalidFrameHeader,
				$"Response frame header has protocol ID {protocolId} and length {bodyLength}, expected protocol ID 0 and length {MinBodyLength} to {MaxBodyLength}.");
		}
		int frameLength = HeaderLength + bodyLength;
		return bufferUsed >= frameLength ? frameLength : 0;
	}

	/// <summary>
	/// Removes a frame from the start of the buffer.
	/// </summary>
	/// <param name="frameLength">The length of the frame including the header.</param>
	private void RemoveFrame(int frameLength)
	{
		bufferUsed -= frameLength;
		buffer.AsSpan(frameLength, bufferUsed).CopyTo(buffer);
	}
}
