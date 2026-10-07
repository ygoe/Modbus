namespace Unclassified.Modbus.Tests.Fakes;

/// <summary>
/// A request handler that provides and stores the Modbus objects in dictionaries.
/// </summary>
/// <remarks>
/// Addresses that are not in the dictionaries are not provided, so reading them results in an
/// <see cref="ModbusError.IllegalDataAddress"/> response.
/// </remarks>
internal class MemoryRequestHandler : ModbusRequestHandler
{
	#region Properties

	/// <summary>
	/// Gets the coil states by address.
	/// </summary>
	public Dictionary<ushort, bool> Coils { get; } = [];

	/// <summary>
	/// Gets the discrete input states by address.
	/// </summary>
	public Dictionary<ushort, bool> DiscreteInputs { get; } = [];

	/// <summary>
	/// Gets or sets an exception to throw instead of reading or writing data.
	/// </summary>
	public Exception? Exception { get; set; }

	/// <summary>
	/// Gets the holding register values by address.
	/// </summary>
	public Dictionary<ushort, ushort> HoldingRegisters { get; } = [];

	/// <summary>
	/// Gets the input register values by address.
	/// </summary>
	public Dictionary<ushort, ushort> InputRegisters { get; } = [];

	/// <summary>
	/// Gets the device ID of the last read or write request.
	/// </summary>
	public byte? LastDeviceId { get; private set; }

	/// <summary>
	/// Gets the address ranges of all write requests.
	/// </summary>
	public List<ModbusRange> WrittenRanges { get; } = [];

	#endregion Properties

	#region Data provider methods

	/// <inheritdoc/>
	protected override Task ProvideReadData(
		byte deviceId,
		ModbusRange range,
		ModbusCollection objects,
		CancellationToken cancellationToken)
	{
		LastDeviceId = deviceId;
		if (Exception != null)
			throw Exception;

		for (int i = range.StartAddress; i <= range.EndAddress; i++)
		{
			ushort address = (ushort)i;
			switch (objects.ObjectType)
			{
				case ModbusObjectType.Coil:
					if (Coils.TryGetValue(address, out bool coil))
						objects.SetState(address, coil);
					break;
				case ModbusObjectType.DiscreteInput:
					if (DiscreteInputs.TryGetValue(address, out bool discreteInput))
						objects.SetState(address, discreteInput);
					break;
				case ModbusObjectType.HoldingRegister:
					if (HoldingRegisters.TryGetValue(address, out ushort holdingRegister))
						objects.SetUInt16(address, holdingRegister);
					break;
				case ModbusObjectType.InputRegister:
					if (InputRegisters.TryGetValue(address, out ushort inputRegister))
						objects.SetUInt16(address, inputRegister);
					break;
			}
		}
		return Task.CompletedTask;
	}

	/// <inheritdoc/>
	protected override Task ProcessWriteData(
		byte deviceId,
		ModbusRange range,
		ModbusCollection objects,
		CancellationToken cancellationToken)
	{
		LastDeviceId = deviceId;
		if (Exception != null)
			throw Exception;

		WrittenRanges.Add(range);
		foreach (var obj in objects)
		{
			if (obj is IModbusStateObject stateObject)
				Coils[obj.Address] = stateObject.State;
			else if (obj is IModbusValueObject valueObject)
				HoldingRegisters[obj.Address] = valueObject.Value;
		}
		return Task.CompletedTask;
	}

	#endregion Data provider methods

	#region Public methods

	/// <summary>
	/// Sets each register in the address range to a value that equals its address.
	/// </summary>
	/// <param name="registers">The register dictionary to fill.</param>
	/// <param name="range">The address range to fill.</param>
	public static void FillWithAddresses(Dictionary<ushort, ushort> registers, ModbusRange range)
	{
		for (int i = range.StartAddress; i <= range.EndAddress; i++)
		{
			registers[(ushort)i] = (ushort)i;
		}
	}

	#endregion Public methods
}
