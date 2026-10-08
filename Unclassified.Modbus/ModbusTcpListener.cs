using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Unclassified.Modbus.Util;

namespace Unclassified.Modbus;

/// <summary>
/// Listens for client TCP connections and runs the communication with each client.
/// </summary>
public class ModbusTcpListener : IDisposable, IHostedService
{
	#region Private data

	private readonly ModbusRequestHandler requestHandler;
	private readonly ILogger? logger;
	private TcpListener? tcpListener;
	private Task? runTask;
	private CancellationTokenSource? runListenerCts;

	#endregion Private data

	#region Constructors

	/// <summary>
	/// Initializes a new instance of the <see cref="ModbusTcpListener"/> class.
	/// </summary>
	/// <param name="requestHandler">An instance that handles Modbus requests and provides a response.</param>
	/// <param name="logger">The logger instance.</param>
	public ModbusTcpListener(ModbusRequestHandler requestHandler, ILogger? logger)
	{
		this.requestHandler = requestHandler;
		this.logger = logger;
	}

	#endregion Constructors

	#region Properties

	/// <summary>
	/// Gets or sets the local IP address to listen on. Default is all network interfaces.
	/// </summary>
	public IPAddress IPAddress { get; set; } = IPAddress.IPv6Any;

	/// <summary>
	/// Gets or sets the port on which to listen for incoming connection attempts. Set to 0 to use
	/// any available port; <see cref="LocalEndPoint"/> provides it after starting.
	/// </summary>
	public int Port { get; set; }

	/// <summary>
	/// Gets the local IP address and port on which the listener accepts connections, or null if
	/// it is not running.
	/// </summary>
	public IPEndPoint? LocalEndPoint => (IPEndPoint?)tcpListener?.LocalEndpoint;

	/// <summary>
	/// Gets or sets the maximum time to wait for the remaining data of a request after its first
	/// byte has been received. The connection is closed if the request is not complete in time.
	/// Default is 2 seconds. Set to <see cref="Timeout.InfiniteTimeSpan"/> to wait infinitely.
	/// </summary>
	public TimeSpan IncompleteRequestTimeout { get; set; } = TimeSpan.FromSeconds(2);

	#endregion Properties

	#region IHostedService implementation

	/// <summary>
	/// Called when the application host is ready to start the service. This starts the TCP listener
	/// and accepts client connections.
	/// </summary>
	/// <param name="cancellationToken">Indicates that the start process has been aborted.</param>
	/// <returns><see cref="Task.CompletedTask"/>, always.</returns>
	public Task StartAsync(CancellationToken cancellationToken)
	{
		if (tcpListener != null)
			throw new InvalidOperationException("The listener service is already running.");
		if (Port < 0 || Port > ushort.MaxValue)
			throw new ArgumentException($"The port number is not in the valid range: {Port}");

		tcpListener = new TcpListener(IPAddress, Port);
		if (IPAddress.AddressFamily == AddressFamily.InterNetworkV6)
			tcpListener.Server.DualMode = true;
		tcpListener.Start();
		logger?.LogInformation("Listening for TCP connections");

		runListenerCts = new CancellationTokenSource();
		runTask = Task.Run(() => RunListener(runListenerCts.Token), CancellationToken.None);
		return Task.CompletedTask;
	}

	/// <summary>
	/// Called when the application host is performing a graceful shutdown. This stops the TCP
	/// listener and waits for the client connections to close.
	/// </summary>
	/// <param name="cancellationToken">Indicates that the shutdown process should no longer be
	///   graceful. Remaining client connections will be closed then.</param>
	public async Task StopAsync(CancellationToken cancellationToken)
	{
		if (runTask == null)
			throw new InvalidOperationException("The listener service was not started.");

		tcpListener!.Stop();
		using (cancellationToken.Register(() => runListenerCts!.Cancel(), useSynchronizationContext: false))
		{
			await runTask.NoSync();
		}
	}

	#endregion IHostedService implementation

	#region Communication methods

	/// <summary>
	/// Accepts new incoming connections and starts a client task for each. The listener is stopped
	/// by the <see cref="StopAsync"/> method, then all open client connections are closed with the
	/// <paramref name="cancellationToken"/> of this method.
	/// </summary>
	private async Task RunListener(CancellationToken cancellationToken)
	{
		var clients = new ConcurrentDictionary<TcpClient, bool>();   // bool is dummy, never regarded
		var clientTasks = new List<Task>();
		try
		{
			while (true)
			{
				TcpClient tcpClient;
				try
				{
					// Cancel this with ObjectDisposedException; our CancellationToken is for
					// closing all remaining client connections afterwards.
					tcpClient = await tcpListener!.AcceptTcpClientAsync(CancellationToken.None).NoSync();
				}
				catch (ObjectDisposedException)
				{
					// Listener was stopped
					break;
				}
				catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
				{
					// Listener was stopped (Windows)
					break;
				}
				catch (InvalidOperationException)
				{
					// Listener was stopped before accepting
					break;
				}
				var endpoint = tcpClient.Client.RemoteEndPoint;
				logger?.LogInformation("Client connected from {Endpoint}", endpoint);
				clients.TryAdd(tcpClient, true);
				var clientTask = Task.Run(async () =>
				{
					try
					{
						await RunClientConnection(tcpClient).NoSync();
					}
					catch (Exception ex)
					{
						logger?.LogError(ex, "Error in connection from {Endpoint}: {Message}", endpoint, ex.Message);
					}
					finally
					{
						tcpClient.Dispose();
						logger?.LogInformation("Client disconnected from {Endpoint}", endpoint);
						clients.TryRemove(tcpClient, out _);
					}
				}, CancellationToken.None);
				clientTasks.RemoveAll(t => t.IsCompleted);
				clientTasks.Add(clientTask);
			}
		}
		finally
		{
			// Wait for all client connections to close
			logger?.LogInformation("Shutting down, waiting for client connections...");
			await Task.WhenAny(Task.WhenAll(clientTasks), cancellationToken.WaitAsync()).NoSync();

			// If cancellationToken has been signalled, disconnect the clients
			if (cancellationToken.IsCancellationRequested)
			{
				logger?.LogInformation("Closing all client connections...");
				foreach (var tcpClient in clients.Keys)
				{
					tcpClient.Dispose();
				}
			}
			await Task.WhenAll(clientTasks).NoSync();
			logger?.LogInformation("All client connections closed");
			clientTasks.Clear();
			tcpListener = null;
		}
	}

	/// <summary>
	/// Runs a connection from a client. This method returns when the connection was closed either
	/// way.
	/// </summary>
	private async Task RunClientConnection(TcpClient tcpClient)
	{
		var byteBuffer = new ByteBuffer();
		var stream = tcpClient.GetStream();

		// Read until the connection is closed. A closed connection can only be detected while
		// reading, so we need to read permanently, not only when we might use received data.

		// Start a separate asynchronous task that just pulls out as much received data of the
		// buffer as needed for the next message. It will continue each time a complete new request
		// message was received.
		using var stopReadCts = new CancellationTokenSource();
		var readTask = ReadFromClient(tcpClient, stream, byteBuffer, stopReadCts.Token);

		try
		{
			// 10 KiB should be enough for every Ethernet packet
			byte[] buffer = new byte[10240];
			while (true)
			{
				int readLength;
				try
				{
					readLength = await stream.ReadAsync(buffer).NoSync();
					if (readLength == 0)
						logger?.LogInformation("Connection closed remotely");
				}
				catch (IOException ex) when (ex.InnerException is SocketException { ErrorCode: (int)SocketError.OperationAborted } ||
					ex.InnerException is SocketException { ErrorCode: 125 } /* Operation canceled (Linux) */)
				{
					// Warning: This error code number (995) may change.
					// See https://docs.microsoft.com/en-us/windows/desktop/winsock/windows-sockets-error-codes-2
					// Note: NativeErrorCode and ErrorCode 125 observed on Linux.
					logger?.LogInformation("Connection closed locally");
					readLength = -1;
				}
				catch (ObjectDisposedException)
				{
					logger?.LogInformation("Connection closed locally");
					readLength = -1;
				}
				catch (IOException ex) when (ex.InnerException is SocketException { ErrorCode: (int)SocketError.ConnectionAborted })
				{
					logger?.LogWarning("Connection aborted");
					readLength = -1;
				}
				catch (IOException ex) when (ex.InnerException is SocketException { ErrorCode: (int)SocketError.ConnectionReset })
				{
					logger?.LogWarning("Connection reset remotely");
					readLength = -2;
				}
				if (readLength <= 0)
					break;
				byteBuffer.Enqueue(buffer, 0, readLength);
			}
		}
		finally
		{
			stopReadCts.Cancel();
			await readTask.IgnoreCanceled().NoSync();
		}
	}

	/// <summary>
	/// Reads data from a client and processes full received request messages. If processing ends
	/// for any other reason than the <paramref name="cancellationToken"/>, the connection is
	/// closed.
	/// </summary>
	private async Task ReadFromClient(TcpClient tcpClient, NetworkStream stream, ByteBuffer byteBuffer, CancellationToken cancellationToken)
	{
		// MBAP header without the unit ID (6 bytes), then unit ID and PDU (max. 1 + 253 bytes)
		byte[] header = new byte[6];
		byte[] requestBuffer = new byte[254];
		byte[] responseBuffer = new byte[6 + 254];
		try
		{
			while (true)
			{
				// Wait for the next request without time limit
				await byteBuffer.WaitAsync(cancellationToken).NoSync();

				int length;
				using (var timeoutCts = TaskHelper.CreateTimeoutCancellationTokenSource(cancellationToken, IncompleteRequestTimeout))
				{
					try
					{
						await byteBuffer.DequeueAsync(header, 0, 6, timeoutCts.Token).NoSync();
						length = header[4] << 8 | header[5];
						if (length < 2 || length > requestBuffer.Length)
						{
							// The start of the next request cannot be found after an invalid length
							logger?.LogWarning("Invalid request length {Length}, closing connection", length);
							return;
						}
						await byteBuffer.DequeueAsync(requestBuffer, 0, length, timeoutCts.Token).NoSync();
					}
					catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
					{
						logger?.LogWarning("Incomplete request was not received in time, closing connection");
						return;
					}
				}

				int protocolId = header[2] << 8 | header[3];
				if (protocolId != 0)
				{
					logger?.LogDebug("Ignoring request with unsupported protocol ID {ProtocolId}", protocolId);
					continue;
				}

				int responseLength = await requestHandler.HandleRequest(requestBuffer.AsMemory(0, length), responseBuffer.AsMemory(6), cancellationToken).NoSync();
				if (responseLength < 0)
				{
					logger?.LogInformation("Closing connection as requested by the request handler");
					return;
				}
				if (responseLength > 0)
				{
					// Send a response with the transaction ID and protocol ID from the request
					Array.Copy(header, responseBuffer, 4);
					responseBuffer[4] = (byte)(responseLength >> 8);
					responseBuffer[5] = (byte)(responseLength & 0xff);
					await stream.WriteAsync(responseBuffer.AsMemory(0, 6 + responseLength), cancellationToken).NoSync();
				}
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
		{
			logger?.LogError(ex, "Error processing requests, closing connection: {Message}", ex.Message);
		}
		finally
		{
			// Also stop receiving data if processing has ended (no effect if the connection was
			// already closed which cancelled this method)
			tcpClient.Close();
		}
	}

	#endregion Communication methods

	#region IDisposable implementation

	/// <summary>
	/// Gets a value indicating whether this instance is disposed.
	/// </summary>
	public bool IsDisposed { get; private set; }

	/// <summary>
	/// Disposes this <see cref="ModbusTcpListener"/> instance.
	/// </summary>
	public void Dispose()
	{
		if (!IsDisposed)
		{
			tcpListener?.Stop();
			tcpListener = null;
			IsDisposed = true;
			GC.SuppressFinalize(this);
		}
	}

	#endregion IDisposable implementation
}
