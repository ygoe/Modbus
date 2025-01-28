namespace Unclassified.Modbus;

/// <summary>
/// A client connection to a Modbus server.
/// </summary>
public interface IModbusConnection
{
	/// <summary>
	/// Gets a value indicating whether the connection is currently open.
	/// </summary>
	bool IsOpen { get; }

	/// <summary>
	/// Sends a request in a connection-type-specific message frame to the Modbus server and waits
	/// for the complete response.
	/// </summary>
	/// <param name="requestBody">The request body bytes to send.</param>
	/// <param name="cancellationToken">A cancellation token used to propagate notification that
	///   this operation should be canceled.</param>
	/// <returns>The bytes of a complete response received from the server.</returns>
	Task<ReadOnlyMemory<byte>> SendRequest(ReadOnlyMemory<byte> requestBody, CancellationToken cancellationToken = default);

	/// <summary>
	/// Closes the connection.
	/// </summary>
	void Close();
}
