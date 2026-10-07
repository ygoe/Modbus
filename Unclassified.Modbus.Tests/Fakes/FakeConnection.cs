using Microsoft.Extensions.Logging;

namespace Unclassified.Modbus.Tests.Fakes;

/// <summary>
/// A Modbus connection and its factory that record the request bodies and generate the response
/// bodies with a function instead of communicating with a server.
/// </summary>
internal class FakeConnection : IModbusConnection, IModbusConnectionFactory
{
	#region Private data

	/// <summary>
	/// Generates the response body for a request body, or throws an exception.
	/// </summary>
	private readonly Func<byte[], byte[]> respond;

	#endregion Private data

	#region Constructors

	/// <summary>
	/// Initializes a new instance of the <see cref="FakeConnection"/> class.
	/// </summary>
	/// <param name="respond">Generates the response body for a request body, or throws an
	///   exception.</param>
	public FakeConnection(Func<byte[], byte[]> respond)
	{
		this.respond = respond;
	}

	#endregion Constructors

	#region Properties

	/// <summary>
	/// Gets or sets an exception to throw when a connection is requested.
	/// </summary>
	public Exception? ConnectException { get; set; }

	/// <summary>
	/// Gets a value indicating whether the connection is currently open.
	/// </summary>
	public bool IsOpen { get; private set; }

	/// <summary>
	/// Gets the number of times the connection was opened.
	/// </summary>
	public int OpenCount { get; private set; }

	/// <summary>
	/// Gets the request bodies in the order they were sent.
	/// </summary>
	public List<byte[]> Requests { get; } = [];

	#endregion Properties

	#region IModbusConnectionFactory implementation

	/// <summary>
	/// Opens the connection and returns this instance.
	/// </summary>
	public Task<IModbusConnection> GetConnection(
		ILogger? logger = null,
		CancellationToken cancellationToken = default)
	{
		if (ConnectException != null)
			return Task.FromException<IModbusConnection>(ConnectException);
		IsOpen = true;
		OpenCount++;
		return Task.FromResult<IModbusConnection>(this);
	}

	#endregion IModbusConnectionFactory implementation

	#region IModbusConnection implementation

	/// <summary>
	/// Records the request body and returns the generated response body.
	/// </summary>
	public Task<ReadOnlyMemory<byte>> SendRequest(
		ReadOnlyMemory<byte> requestBody,
		CancellationToken cancellationToken = default)
	{
		byte[] request = requestBody.ToArray();
		Requests.Add(request);
		try
		{
			return Task.FromResult<ReadOnlyMemory<byte>>(respond(request));
		}
		catch (Exception ex)
		{
			return Task.FromException<ReadOnlyMemory<byte>>(ex);
		}
	}

	/// <summary>
	/// Closes the connection.
	/// </summary>
	public void Close()
	{
		IsOpen = false;
	}

	#endregion IModbusConnection implementation
}
