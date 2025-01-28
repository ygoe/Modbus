using Microsoft.Extensions.Logging;

namespace Unclassified.Modbus;

/// <summary>
/// A factory class that creates new <see cref="IModbusConnection"/> instances.
/// </summary>
public interface IModbusConnectionFactory
{
	/// <summary>
	/// Creates and opens a new <see cref="IModbusConnection"/> instance.
	/// </summary>
	/// <param name="logger">The logger instance.</param>
	/// <param name="cancellationToken">A cancellation token used to propagate notification that
	///   this operation should be canceled.</param>
	/// <returns>The new connection in the open state.</returns>
	Task<IModbusConnection> GetConnection(ILogger? logger = null, CancellationToken cancellationToken = default);
}
