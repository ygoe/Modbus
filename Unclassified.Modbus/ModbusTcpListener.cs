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
	/// Gets or sets the port on which to listen for incoming connection attempts.
	/// </summary>
	public int Port { get; set; }

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
		if (Port <= 0 || Port > ushort.MaxValue)
			throw new ArgumentException($"The port number is not in the valid range: {Port}");

		tcpListener = new TcpListener(IPAddress, Port);
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
				var endpoint = tcpClient.Client.RemoteEndPoint;
				logger?.LogInformation("Client connected from {Endpoint}", endpoint);
				clients.TryAdd(tcpClient, true);
				var clientTask = Task.Run(async () =>
				{
					await RunClientConnection(tcpClient).NoSync();
					tcpClient.Dispose();
					logger?.LogInformation("Client disconnected from {Endpoint}", endpoint);
					clients.TryRemove(tcpClient, out _);
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
		var readTask = ReadFromClient(stream, byteBuffer, stopReadCts.Token);

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
			byteBuffer.Enqueue(buffer[..readLength]);
		}

		stopReadCts.Cancel();
		await readTask.IgnoreCanceled().NoSync();
	}

	/// <summary>
	/// Reads data from a client and processes full received request messages.
	/// </summary>
	private async Task ReadFromClient(NetworkStream stream, ByteBuffer byteBuffer, CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[260];
		while (true)
		{
			await byteBuffer.DequeueAsync(buffer, 0, 6, cancellationToken).NoSync();
			int transactionId = buffer[0] << 8 | buffer[1];
			int length = buffer[4] << 8 | buffer[5];
			if (length > 254)
			{
				// TODO: Message frame is too long: read and ignore, read and respond with error, or close connection
				return;
			}
			// TODO: Timeout for the remaining data
			await byteBuffer.DequeueAsync(buffer, 0, length, cancellationToken).NoSync();
			int responseLength = await requestHandler.HandleRequest(buffer.AsMemory()[..length], buffer.AsMemory(6)).NoSync();
			if (responseLength < 0)
			{
				// Close the connection
				return;
			}
			if (responseLength > 0)
			{
				// Send a response
				buffer[0] = (byte)(transactionId >> 8);   // Transaction ID
				buffer[1] = (byte)(transactionId & 0xff);   // Transaction ID
				buffer[2] = 0;
				buffer[3] = 0;
				buffer[4] = (byte)(responseLength >> 8);
				buffer[5] = (byte)(responseLength & 0xff);
				await stream.WriteAsync(buffer.AsMemory()[..(6 + responseLength)], cancellationToken).NoSync();
			}
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
