using System.Net;
using Unclassified.Modbus.Tests.Fakes;

namespace Unclassified.Modbus.Tests;

/// <summary>
/// Tests starting and stopping the <see cref="ModbusTcpListener"/> class.
/// </summary>
[TestClass]
public class ModbusTcpListenerTests
{
	#region Private data

	/// <summary>
	/// The maximum time to wait for the listener to stop.
	/// </summary>
	private static readonly TimeSpan stopTimeout = TimeSpan.FromSeconds(5);

	#endregion Private data

	#region Listener lifecycle

	/// <summary>
	/// Checks that an available port is used and provided if the port is 0.
	/// </summary>
	[TestMethod]
	public async Task Start_Port0_ProvidesLocalEndPoint()
	{
		using var listener = await StartListener(new MemoryRequestHandler());
		var localEndPoint = listener.LocalEndPoint;
		await listener.StopAsync(CancellationToken.None).WaitAsync(stopTimeout);

		Assert.IsNotNull(localEndPoint);
		Assert.AreNotEqual(0, localEndPoint.Port);
		Assert.AreEqual(IPAddress.IPv6Loopback, localEndPoint.Address);
		Assert.IsNull(listener.LocalEndPoint);
	}

	/// <summary>
	/// Checks that the listener can be stopped right after starting it, before it accepts
	/// connections.
	/// </summary>
	[TestMethod]
	public async Task Stop_ImmediatelyAfterStart_Completes()
	{
		for (int i = 0; i < 10; i++)
		{
			using var listener = await StartListener(new MemoryRequestHandler());
			await listener.StopAsync(CancellationToken.None).WaitAsync(stopTimeout);
		}
	}

	#endregion Listener lifecycle

	#region Helper methods

	/// <summary>
	/// Starts a listener on the IPv6 loopback address and an available port.
	/// </summary>
	internal static async Task<ModbusTcpListener> StartListener(
		ModbusRequestHandler requestHandler,
		TimeSpan? incompleteRequestTimeout = null)
	{
		var listener = new ModbusTcpListener(requestHandler, null)
		{
			IPAddress = IPAddress.IPv6Loopback,
			Port = 0
		};
		if (incompleteRequestTimeout != null)
			listener.IncompleteRequestTimeout = incompleteRequestTimeout.Value;
		await listener.StartAsync(CancellationToken.None);
		return listener;
	}

	#endregion Helper methods
}
