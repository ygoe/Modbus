using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Unclassified.Modbus.Util;

namespace Unclassified.Modbus;

/// <summary>
/// A client connection to a Modbus server over an IP network.
/// </summary>
internal class ModbusTcpConnection : IModbusConnection
{
	private readonly ILogger? logger;
	private TcpClient? tcpClient;
	private int lastTransactionId = 0;
	private readonly byte[] buffer = new byte[270];   // Protocol allows 260 = 254 + TCP-specific header

	public ModbusTcpConnection(ILogger? logger)
	{
		this.logger = logger;
	}

	public bool IsOpen => tcpClient?.Client.Connected == true;

	internal async Task Connect(string hostName, int port, CancellationToken cancellationToken = default)
	{
		if (logger?.IsEnabled(LogLevel.Debug) == true)
			logger?.LogDebug("Connecting to {HostName}:{Port}...", hostName, port);
		tcpClient = new TcpClient(AddressFamily.InterNetworkV6);
		tcpClient.Client.DualMode = true;
		await tcpClient.ConnectAsync(hostName, port, cancellationToken).ConfigureAwait(false);
		if (logger?.IsEnabled(LogLevel.Debug) == true)
			logger?.LogDebug("Connected to {HostName}:{Port}", hostName, port);
	}

	public async Task<ReadOnlyMemory<byte>> SendRequest(ReadOnlyMemory<byte> requestBody, CancellationToken cancellationToken = default)
	{
		if (tcpClient == null)
			throw new InvalidOperationException("Connection not opened.");

		var bytes = MakeMessageFrame(requestBody.Span, out ushort transactionId);
		if (logger?.IsEnabled(LogLevel.Trace) == true)
			logger?.LogTrace("Sending: {HexData}", bytes.Span.ToHexString());
		var stream = tcpClient.GetStream();
		await stream.WriteAsync(bytes, cancellationToken);

		// Wait for a response
		int bufferUsed = 0;
		while (true)
		{
			int readBytes = await stream.ReadAsync(buffer.AsMemory(bufferUsed), cancellationToken);
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

			if (bufferUsed >= 6)
			{
				int responseLength = buffer[4] << 8 | buffer[5];
				if (bufferUsed >= 6 + responseLength)
					break;
			}
		}
		return ExtractResponseBody(buffer.AsSpan(0, bufferUsed), transactionId).ToArray();
	}

	public void Close()
	{
		var localTcpClient = tcpClient;
		if (localTcpClient != null)
		{
			localTcpClient.Close();
			localTcpClient.Dispose();
			tcpClient = null;
			logger?.LogDebug("Connection closed");
		}
	}

	private ReadOnlyMemory<byte> MakeMessageFrame(ReadOnlySpan<byte> requestBody, out ushort transactionId)
	{
		transactionId = (ushort)(Interlocked.Increment(ref lastTransactionId) & 0xffff);
		byte[] bytes = new byte[6 + requestBody.Length];
		bytes[0] = (byte)(transactionId >> 8);   // Transaction ID
		bytes[1] = (byte)(transactionId & 0xff);   // Transaction ID
		bytes[2] = 0;
		bytes[3] = 0;
		ushort length = (ushort)requestBody.Length;
		bytes[4] = (byte)(length >> 8);
		bytes[5] = (byte)(length & 0xff);
		requestBody.CopyTo(bytes.AsSpan(6));
		return bytes;
	}

	private ReadOnlySpan<byte> ExtractResponseBody(ReadOnlySpan<byte> response, ushort transactionId)
	{
		int rcvdTransactionId = response[0] << 8 | response[1];
		if (rcvdTransactionId != transactionId)
		{
			if (logger?.IsEnabled(LogLevel.Debug) == true)
				logger?.LogDebug("Response transaction ID {RcvdTransactionId} does not match request transaction ID {TransactionId}.", rcvdTransactionId, transactionId);
		}
		int responseLength = response[4] << 8 | response[5];
		return response.Slice(6, responseLength);
	}
}
