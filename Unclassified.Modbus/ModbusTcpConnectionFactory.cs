using Microsoft.Extensions.Logging;

namespace Unclassified.Modbus;

/// <summary>
/// A factory class that creates new <see cref="ModbusTcpConnection"/> instances.
/// </summary>
public class ModbusTcpConnectionFactory : IModbusConnectionFactory
{
	/// <summary>
	/// Initializes a new instance of the <see cref="ModbusTcpConnectionFactory"/> class.
	/// </summary>
	/// <param name="hostName">The name of the network host to connect to.</param>
	/// <param name="port">The TCP port number to connect to.</param>
	public ModbusTcpConnectionFactory(string hostName, int port)
	{
		HostName = hostName;
		Port = port;
	}

	/// <summary>
	/// Gets the name of the network host to connect to.
	/// </summary>
	public string HostName { get; }

	/// <summary>
	/// Gets the TCP port number to connect to.
	/// </summary>
	public int Port { get; }

	/// <inheritdoc/>
	public async Task<IModbusConnection> GetConnection(ILogger? logger = null, CancellationToken cancellationToken = default)
	{
		var connection = new ModbusTcpConnection(logger);
		await connection.Connect(HostName, Port, cancellationToken);
		return connection;
	}

	/// <summary>
	/// Returns a summary of the connection data.
	/// </summary>
	/// <returns></returns>
	public override string ToString()
	{
		return $"TCP {HostName}:{Port}";
	}
}
