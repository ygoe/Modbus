namespace Unclassified.Modbus;

/// <summary>
/// A client connection to a Modbus server.
/// </summary>
public interface IModbusConnection
{
	/// <summary>
	/// Gets a value indicating whether the connection is currently open.
	/// </summary>
	public bool IsOpen { get; }

	/// <summary>
	/// Sends a request in a connection-type-specific message frame to the Modbus server and waits
	/// for the complete response.
	/// </summary>
	/// <param name="requestBody">The request body bytes to send.</param>
	/// <param name="cancellationToken">A cancellation token used to propagate notification that
	///   this operation should be canceled.</param>
	/// <returns>The bytes of a complete response received from the server.</returns>
	public Task<ReadOnlyMemory<byte>> SendRequest(ReadOnlyMemory<byte> requestBody, CancellationToken cancellationToken = default);

	/// <summary>
	/// Closes the connection.
	/// </summary>
	public void Close();

	/// <summary>
	/// Returns the connection busy time, in microseconds, since the last call of the method.
	/// </summary>
	public long GetBusyTimeUs() => 0;
}
