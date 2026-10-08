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
	/// Checks that the listener accepts connections on an IPv4 address.
	/// </summary>
	[TestMethod]
	public async Task Start_IPv4Address_AcceptsConnections()
	{
		var handler = new MemoryRequestHandler();
		handler.HoldingRegisters[0] = 0x1234;
		using var listener = await StartListener(handler, address: IPAddress.Loopback);
		int port = listener.LocalEndPoint!.Port;
		var connectionFactory = new ModbusTcpConnectionFactory("127.0.0.1", port, false);
		ModbusCollection objects;
		using (var client = new ModbusClient(connectionFactory, null))
		{
			objects = await client.Read(ModbusObjectType.HoldingRegister, 1, new ModbusRange(0));
		}
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
		await listener.StopAsync(cts.Token).WaitAsync(stopTimeout);

		Assert.AreEqual((ushort)0x1234, objects.GetUInt16(0));
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
	/// Starts a listener on an available port.
	/// </summary>
	/// <param name="requestHandler">The request handler of the listener.</param>
	/// <param name="incompleteRequestTimeout">The incomplete request timeout, or null for the
	///   default.</param>
	/// <param name="address">The IP address to listen on, or null for the IPv6 loopback
	///   address.</param>
	/// <returns>The started listener.</returns>
	internal static async Task<ModbusTcpListener> StartListener(
		ModbusRequestHandler requestHandler,
		TimeSpan? incompleteRequestTimeout = null,
		IPAddress? address = null)
	{
		var listener = new ModbusTcpListener(requestHandler, null)
		{
			IPAddress = address ?? IPAddress.IPv6Loopback,
			Port = 0
		};
		if (incompleteRequestTimeout != null)
			listener.IncompleteRequestTimeout = incompleteRequestTimeout.Value;
		await listener.StartAsync(CancellationToken.None);
		return listener;
	}

	#endregion Helper methods
}
