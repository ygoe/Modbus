namespace Unclassified.Modbus;

/// <summary>
/// Defines a range of Modbus objects to retrieve.
/// </summary>
public readonly struct ModbusRange
{
	#region Constructors

	/// <summary>
	/// Initializes a new instance of the <see cref="ModbusRange"/> class for a single address.
	/// </summary>
	/// <param name="address">The address that the range covers.</param>
	public ModbusRange(ushort address)
	{
		StartAddress = address;
		EndAddress = address;
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="ModbusRange"/> class for an address range.
	/// </summary>
	/// <param name="startAddress">The address of the first object.</param>
	/// <param name="endAddress">The address of the last object.</param>
	public ModbusRange(ushort startAddress, ushort endAddress)
	{
		if (endAddress < startAddress)
			throw new ArgumentException($"The end address ({endAddress}) must not be smaller than the start address ({startAddress}).");

		StartAddress = startAddress;
		EndAddress = endAddress;
	}

	#endregion Constructors

	#region Properties

	/// <summary>
	/// Gets or sets the address of the first object.
	/// </summary>
	public ushort StartAddress { get; }

	/// <summary>
	/// Gets or sets the address of the last object.
	/// </summary>
	public ushort EndAddress { get; }

	/// <summary>
	/// Gets the number of objects.
	/// </summary>
	public ushort Length => (ushort)(EndAddress - StartAddress + 1);

	#endregion Properties

	#region Public methods

	/// <summary>
	/// Creates a subrange of the current range with a different start address.
	/// </summary>
	/// <param name="offset">The offset to the current start address.</param>
	/// <returns>The subrange instance.</returns>
	public ModbusRange Subrange(int offset) => new((ushort)(StartAddress + offset), EndAddress);

	/// <summary>
	/// Returns a string representation of the range.
	/// </summary>
	/// <returns></returns>
	public override string ToString()
	{
		if (StartAddress == EndAddress)
			return $"{StartAddress}";
		return $"{StartAddress}-{EndAddress}";
	}

	#endregion Public methods

	#region Instance creation methods

	/// <summary>
	/// Creates a new <see cref="ModbusRange"/> for 16-bit integer values.
	/// </summary>
	/// <param name="startAddress">The address of the first object.</param>
	/// <param name="count">The number of 16-bit integer values to cover.</param>
	/// <returns>The <see cref="ModbusRange"/> instance.</returns>
	public static ModbusRange ForInt16(ushort startAddress, int count) =>
		new(startAddress, (ushort)(startAddress + count - 1));

	/// <summary>
	/// Creates a new <see cref="ModbusRange"/> for 32-bit integer values.
	/// </summary>
	/// <param name="startAddress">The address of the first object.</param>
	/// <param name="count">The number of 32-bit integer values to cover.</param>
	/// <returns>The <see cref="ModbusRange"/> instance.</returns>
	public static ModbusRange ForInt32(ushort startAddress, int count) =>
		new(startAddress, (ushort)(startAddress + count * 2 - 1));

	/// <summary>
	/// Creates a new <see cref="ModbusRange"/> for 64-bit integer values.
	/// </summary>
	/// <param name="startAddress">The address of the first object.</param>
	/// <param name="count">The number of 64-bit integer values to cover.</param>
	/// <returns>The <see cref="ModbusRange"/> instance.</returns>
	public static ModbusRange ForInt64(ushort startAddress, int count) =>
		new(startAddress, (ushort)(startAddress + count * 4 - 1));

	/// <summary>
	/// Creates a new <see cref="ModbusRange"/> for single precision floating point values.
	/// </summary>
	/// <param name="startAddress">The address of the first object.</param>
	/// <param name="count">The number of single precision floating point values to cover.</param>
	/// <returns>The <see cref="ModbusRange"/> instance.</returns>
	public static ModbusRange ForSingle(ushort startAddress, int count) =>
		new(startAddress, (ushort)(startAddress + count * 2 - 1));

	/// <summary>
	/// Creates a new <see cref="ModbusRange"/> for double precision floating point values.
	/// </summary>
	/// <param name="startAddress">The address of the first object.</param>
	/// <param name="count">The number of double precision floating point values to cover.</param>
	/// <returns>The <see cref="ModbusRange"/> instance.</returns>
	public static ModbusRange ForDouble(ushort startAddress, int count) =>
		new(startAddress, (ushort)(startAddress + count * 4 - 1));

	/// <summary>
	/// Creates a new <see cref="ModbusRange"/> for a single-byte character string value.
	/// </summary>
	/// <param name="startAddress">The address of the first object.</param>
	/// <param name="length">The length of the string to cover.</param>
	/// <returns>The <see cref="ModbusRange"/> instance.</returns>
	public static ModbusRange ForString8(ushort startAddress, int length) =>
		new(startAddress, (ushort)(startAddress + (length + 1) / 2 - 1));

	/// <summary>
	/// Creates a new <see cref="ModbusRange"/> for a double-byte character string value.
	/// </summary>
	/// <param name="startAddress">The address of the first object.</param>
	/// <param name="length">The length of the string to cover.</param>
	/// <returns>The <see cref="ModbusRange"/> instance.</returns>
	public static ModbusRange ForString16(ushort startAddress, int length) =>
		new(startAddress, (ushort)(startAddress + length - 1));

	#endregion Instance creation methods

	#region Range combining

	/// <summary>
	/// Combines multiple ranges into a minimum set of ranges by merging adjacent ranges and filling
	/// smaller gaps.
	/// </summary>
	/// <param name="ranges">The requested ranges.</param>
	/// <param name="maxLength">The maximum length of a single range.</param>
	/// <param name="allowedWaste">The maximum allowed objects to add to merge ranges.</param>
	/// <returns>A set of ranges to request from the server.</returns>
	public static IList<ModbusRange> Combine(IEnumerable<ModbusRange> ranges, int maxLength, int allowedWaste)
	{
		if (maxLength <= 0)
			maxLength = int.MaxValue;

		var orderedRanges = ranges.OrderBy(r => r.StartAddress).ThenByDescending(r => r.EndAddress).ToList();
		for (int i = 0; i < orderedRanges.Count; i++)
		{
			for (int j = i + 1; j < orderedRanges.Count; j++)
			{
				bool overlapOrAdjacent = orderedRanges[j].StartAddress <= orderedRanges[i].EndAddress + 1;

				int lastSplitAddress = orderedRanges[i].StartAddress;
				while (lastSplitAddress + maxLength <= orderedRanges[i].EndAddress)
				{
					lastSplitAddress += maxLength;
				}
				bool nearEnough = orderedRanges[j].EndAddress - lastSplitAddress + 1 <= maxLength &&
					orderedRanges[j].StartAddress <= orderedRanges[i].EndAddress + 1 + allowedWaste;

				if (overlapOrAdjacent || nearEnough)
				{
					// Following range overlaps or directly follows current range,
					// or follows within allowed gap and combined range (after expected splitting)
					// does not exceed max length:
					// extend current and remove next range
					orderedRanges[i] = new(orderedRanges[i].StartAddress, orderedRanges[j].EndAddress);
					orderedRanges.RemoveAt(j);
					j--;
				}
			}

			while (orderedRanges[i].Length > maxLength)
			{
				// Current range is longer than allowed:
				// split and continue with next chunk
				var range = orderedRanges[i];
				orderedRanges[i] = new(range.StartAddress, (ushort)(range.StartAddress + maxLength - 1));
				orderedRanges.Insert(i + 1, new((ushort)(range.StartAddress + maxLength), range.EndAddress));
				i++;
			}
		}
		return orderedRanges;
	}

	#endregion Range combining

	#region Static helper functions

	/// <summary>
	/// Returns the maximum range length for requests about the specified Modbus object type.
	/// </summary>
	/// <param name="objectType">The Modbus object type.</param>
	/// <returns></returns>
	public static int GetMaxLength(ModbusObjectType objectType) => objectType switch
	{
		ModbusObjectType.Coil or ModbusObjectType.DiscreteInput => 2008,
		ModbusObjectType.HoldingRegister or ModbusObjectType.InputRegister => 123,
		_ => throw new ArgumentException("Invalid object type.")
	};

	#endregion Static helper functions
}
