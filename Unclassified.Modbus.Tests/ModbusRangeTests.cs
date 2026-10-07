namespace Unclassified.Modbus.Tests;

/// <summary>
/// Tests the <see cref="ModbusRange"/> struct.
/// </summary>
[TestClass]
public class ModbusRangeTests
{
	#region Construction and properties

	/// <summary>
	/// Checks that the constructor rejects an end address before the start address.
	/// </summary>
	[TestMethod]
	public void Constructor_EndBeforeStart_Throws()
	{
		Assert.ThrowsExactly<ArgumentException>(() => new ModbusRange(5, 4));
	}

	/// <summary>
	/// Checks that the length includes both the start and the end address.
	/// </summary>
	[TestMethod]
	public void Length_IsInclusive()
	{
		Assert.AreEqual(1, new ModbusRange(7).Length);
		Assert.AreEqual(3, new ModbusRange(10, 12).Length);
	}

	/// <summary>
	/// Checks that a subrange starts at the offset and keeps the end address.
	/// </summary>
	[TestMethod]
	public void Subrange_KeepsEndAddress()
	{
		Assert.AreEqual(new ModbusRange(13, 20), new ModbusRange(10, 20).Subrange(3));
	}

	/// <summary>
	/// Checks the string representation of single addresses and ranges.
	/// </summary>
	[TestMethod]
	public void ToString_SingleAddressAndRange()
	{
		Assert.AreEqual("5", new ModbusRange(5).ToString());
		Assert.AreEqual("5-7", new ModbusRange(5, 7).ToString());
	}

	/// <summary>
	/// Checks that the data type factory methods cover the number of registers of each type.
	/// </summary>
	[TestMethod]
	public void DataTypeFactories_CoverRegisters()
	{
		Assert.AreEqual(new ModbusRange(100, 102), ModbusRange.ForInt16(100, 3));
		Assert.AreEqual(new ModbusRange(100, 105), ModbusRange.ForInt32(100, 3));
		Assert.AreEqual(new ModbusRange(0, 7), ModbusRange.ForInt64(0, 2));
		Assert.AreEqual(new ModbusRange(100, 105), ModbusRange.ForSingle(100, 3));
		Assert.AreEqual(new ModbusRange(0, 7), ModbusRange.ForDouble(0, 2));
		Assert.AreEqual(new ModbusRange(10, 12), ModbusRange.ForString8(10, 5));
		Assert.AreEqual(new ModbusRange(10, 11), ModbusRange.ForString8(10, 4));
		Assert.AreEqual(new ModbusRange(10, 14), ModbusRange.ForString16(10, 5));
	}

	#endregion Construction and properties

	#region Combine

	/// <summary>
	/// Checks that adjacent ranges are merged.
	/// </summary>
	[TestMethod]
	public void Combine_Adjacent_Merges()
	{
		AssertCombine([new(0, 4), new(5, 9)], 100, 0, [new(0, 9)]);
	}

	/// <summary>
	/// Checks that overlapping ranges are merged.
	/// </summary>
	[TestMethod]
	public void Combine_Overlapping_Merges()
	{
		AssertCombine([new(0, 6), new(4, 9)], 100, 0, [new(0, 9)]);
	}

	/// <summary>
	/// Checks that a range inside another range does not shorten it.
	/// </summary>
	[TestMethod]
	public void Combine_ContainedRange_KeepsOuterRange()
	{
		AssertCombine([new(0, 10), new(2, 5)], 100, 0, [new(0, 10)]);
	}

	/// <summary>
	/// Checks that of 2 ranges with the same start address, the longer one is kept.
	/// </summary>
	[TestMethod]
	public void Combine_SameStartAddress_KeepsLongerRange()
	{
		AssertCombine([new(0, 5), new(0, 10)], 100, 0, [new(0, 10)]);
	}

	/// <summary>
	/// Checks that a maximum length of 0 merges ranges without limiting their length.
	/// </summary>
	[TestMethod]
	public void Combine_NoMaxLength_MergesWithoutSplitting()
	{
		AssertCombine([new(5, 9), new(10, 3000)], 0, 0, [new(5, 3000)]);
	}

	/// <summary>
	/// Checks that the input order and duplicate ranges do not matter.
	/// </summary>
	[TestMethod]
	public void Combine_UnsortedAndDuplicate_SortsAndMerges()
	{
		AssertCombine([new(10, 12), new(0, 2), new(10, 12)], 100, 0, [new(0, 2), new(10, 12)]);
	}

	/// <summary>
	/// Checks that a gap is filled if it is not larger than the allowed waste.
	/// </summary>
	[TestMethod]
	public void Combine_GapWithinAllowedWaste_Merges()
	{
		AssertCombine([new(0, 4), new(7, 9)], 100, 2, [new(0, 9)]);
	}

	/// <summary>
	/// Checks that a gap is kept if it is larger than the allowed waste.
	/// </summary>
	[TestMethod]
	public void Combine_GapBeyondAllowedWaste_KeepsSeparate()
	{
		AssertCombine([new(0, 4), new(7, 9)], 100, 1, [new(0, 4), new(7, 9)]);
	}

	/// <summary>
	/// Checks that a range longer than the maximum length is split into chunks.
	/// </summary>
	[TestMethod]
	public void Combine_LongRange_Splits()
	{
		AssertCombine([new(0, 249)], 100, 0, [new(0, 99), new(100, 199), new(200, 249)]);
	}

	/// <summary>
	/// Checks that adjacent ranges are merged first and then split at the maximum length.
	/// </summary>
	[TestMethod]
	public void Combine_AdjacentBeyondMaxLength_MergesAndSplits()
	{
		AssertCombine([new(0, 79), new(80, 159)], 100, 0, [new(0, 99), new(100, 159)]);
	}

	/// <summary>
	/// Checks that a gap is not filled if the merged range would need an extra request anyway.
	/// </summary>
	[TestMethod]
	public void Combine_GapCrossingMaxLength_KeepsSeparate()
	{
		AssertCombine([new(0, 99), new(102, 110)], 100, 5, [new(0, 99), new(102, 110)]);
	}

	/// <summary>
	/// Checks that ranges at the end of the address space are handled.
	/// </summary>
	[TestMethod]
	public void Combine_EndOfAddressSpace_Merges()
	{
		AssertCombine([new(65530, 65532), new(65533, 65535)], 100, 0, [new(65530, 65535)]);
	}

	#endregion Combine

	#region Protocol limits

	/// <summary>
	/// Checks the read limits from the Modbus specification.
	/// </summary>
	[TestMethod]
	[DataRow(ModbusObjectType.Coil, 2000)]
	[DataRow(ModbusObjectType.DiscreteInput, 2000)]
	[DataRow(ModbusObjectType.HoldingRegister, 125)]
	[DataRow(ModbusObjectType.InputRegister, 125)]
	public void GetMaxReadLength_SpecValues(ModbusObjectType objectType, int expected)
	{
		Assert.AreEqual(expected, ModbusRange.GetMaxReadLength(objectType));
	}

	/// <summary>
	/// Checks the write limits from the Modbus specification.
	/// </summary>
	[TestMethod]
	[DataRow(ModbusObjectType.Coil, 1968)]
	[DataRow(ModbusObjectType.HoldingRegister, 123)]
	public void GetMaxWriteLength_SpecValues(ModbusObjectType objectType, int expected)
	{
		Assert.AreEqual(expected, ModbusRange.GetMaxWriteLength(objectType));
	}

	/// <summary>
	/// Checks that read-only object types have no write limit.
	/// </summary>
	[TestMethod]
	[DataRow(ModbusObjectType.DiscreteInput)]
	[DataRow(ModbusObjectType.InputRegister)]
	[DataRow(ModbusObjectType.None)]
	public void GetMaxWriteLength_ReadOnlyType_Throws(ModbusObjectType objectType)
	{
		Assert.ThrowsExactly<ArgumentException>(() => ModbusRange.GetMaxWriteLength(objectType));
	}

	#endregion Protocol limits

	#region Helper methods

	/// <summary>
	/// Combines the <paramref name="ranges"/> and compares the result with the
	/// <paramref name="expected"/> ranges.
	/// </summary>
	private static void AssertCombine(
		ModbusRange[] ranges,
		int maxLength,
		int allowedWaste,
		ModbusRange[] expected)
	{
		var actual = ModbusRange.Combine(ranges, maxLength, allowedWaste);
		CollectionAssert.AreEqual(expected, actual.ToArray());
	}

	#endregion Helper methods
}
