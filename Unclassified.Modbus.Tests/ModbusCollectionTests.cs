using System.Text;

namespace Unclassified.Modbus.Tests;

/// <summary>
/// Tests the <see cref="ModbusCollection"/> class.
/// </summary>
[TestClass]
public class ModbusCollectionTests
{
	#region Construction and object management

	/// <summary>
	/// Checks that the constructor takes the object type from the specified objects.
	/// </summary>
	[TestMethod]
	public void Constructor_WithObjects_TakesObjectType()
	{
		var collection = new ModbusCollection([new ModbusCoil { Address = 3, State = true }]);

		Assert.AreEqual(ModbusObjectType.Coil, collection.ObjectType);
		Assert.IsTrue(collection.GetState(3));
	}

	/// <summary>
	/// Checks that the constructor rejects an empty set of objects.
	/// </summary>
	[TestMethod]
	public void Constructor_NoObjects_Throws()
	{
		IModbusObject[] objects = [];
		Assert.ThrowsExactly<ArgumentException>(() => new ModbusCollection(objects));
	}

	/// <summary>
	/// Checks that the constructor rejects objects of different types.
	/// </summary>
	[TestMethod]
	public void Constructor_MixedTypes_Throws()
	{
		IModbusObject[] objects =
		[
			new ModbusCoil { Address = 0 },
			new ModbusDiscreteInput { Address = 1 }
		];
		Assert.ThrowsExactly<ArgumentException>(() => new ModbusCollection(objects));
	}

	/// <summary>
	/// Checks that adding an object of another type is rejected.
	/// </summary>
	[TestMethod]
	public void Add_OtherType_Throws()
	{
		var collection = new ModbusCollection(ModbusObjectType.Coil);

		var discreteInput = new ModbusDiscreteInput();
		Assert.ThrowsExactly<InvalidOperationException>(() => collection.Add(discreteInput));
	}

	/// <summary>
	/// Checks that adding an object replaces an existing object at the same address.
	/// </summary>
	[TestMethod]
	public void Add_SameAddress_Replaces()
	{
		var collection = new ModbusCollection(ModbusObjectType.HoldingRegister);
		collection.Add(new ModbusHoldingRegister { Address = 5, Value = 1 });
		collection.Add(new ModbusHoldingRegister { Address = 5, Value = 2 });

		Assert.AreEqual(1, collection.Count);
		Assert.AreEqual((ushort)2, collection.GetUInt16(5));
	}

	/// <summary>
	/// Checks that bit values cannot be set in a register collection.
	/// </summary>
	[TestMethod]
	public void SetState_InRegisterCollection_Throws()
	{
		var collection = new ModbusCollection(ModbusObjectType.HoldingRegister);

		Assert.ThrowsExactly<InvalidOperationException>(() => collection.SetState(0, true));
	}

	/// <summary>
	/// Checks that word values cannot be set in a coil collection.
	/// </summary>
	[TestMethod]
	public void SetUInt16_InCoilCollection_Throws()
	{
		var collection = new ModbusCollection(ModbusObjectType.Coil);

		Assert.ThrowsExactly<InvalidOperationException>(() => collection.SetUInt16(0, 1));
	}

	/// <summary>
	/// Checks that the ranges of the contained objects are combined.
	/// </summary>
	[TestMethod]
	public void GetRanges_CombinesAddresses()
	{
		var collection = new ModbusCollection(ModbusObjectType.Coil);
		foreach (int address in new[] { 5, 0, 1, 2 })
		{
			collection.SetState(address, true);
		}

		var ranges = collection.GetRanges(10, 0);

		ModbusRange[] expected = [new(0, 2), new(5)];
		CollectionAssert.AreEqual(expected, ranges.ToArray());
	}

	/// <summary>
	/// Checks that an object is only contained if both its address and its value match.
	/// </summary>
	[TestMethod]
	public void Contains_ComparesAddressAndValue()
	{
		var collection = new ModbusCollection(ModbusObjectType.HoldingRegister);
		collection.SetUInt16(5, 1);

		Assert.IsTrue(collection.Contains(new ModbusHoldingRegister { Address = 5, Value = 1 }));
		Assert.IsFalse(collection.Contains(new ModbusHoldingRegister { Address = 5, Value = 2 }));
		Assert.IsFalse(collection.Contains(new ModbusHoldingRegister { Address = 6, Value = 1 }));
	}

	/// <summary>
	/// Checks that an object is only removed if both its address and its value match.
	/// </summary>
	[TestMethod]
	public void Remove_ComparesAddressAndValue()
	{
		var collection = new ModbusCollection(ModbusObjectType.HoldingRegister);
		collection.SetUInt16(5, 1);

		Assert.IsFalse(collection.Remove(new ModbusHoldingRegister { Address = 5, Value = 2 }));
		Assert.IsTrue(collection.Remove(new ModbusHoldingRegister { Address = 5, Value = 1 }));
		Assert.AreEqual(0, collection.Count);
	}

	/// <summary>
	/// Checks that reading an address without an object of the requested kind is rejected.
	/// </summary>
	[TestMethod]
	public void Get_MissingObject_Throws()
	{
		var collection = new ModbusCollection(ModbusObjectType.HoldingRegister);
		collection.SetUInt16(5, 1);

		Assert.ThrowsExactly<InvalidOperationException>(() => collection.GetUInt16(6));
		Assert.ThrowsExactly<InvalidOperationException>(() => collection.GetState(5));
	}

	/// <summary>
	/// Checks that objects can be set and read for the whole address space.
	/// </summary>
	[TestMethod]
	public void WholeAddressSpace_RoundTrip()
	{
		var collection = new ModbusCollection(ModbusObjectType.HoldingRegister);
		for (int address = 0; address <= 0xffff; address++)
		{
			collection.SetUInt16(address, (ushort)address);
		}

		Assert.AreEqual(0x10000, collection.Count);
		for (int address = 0; address <= 0xffff; address++)
		{
			Assert.AreEqual((ushort)address, collection.GetUInt16(address));
		}
	}

	#endregion Construction and object management

	#region Typed values

	/// <summary>
	/// Checks that a bit value can be read back.
	/// </summary>
	[TestMethod]
	public void State_RoundTrip()
	{
		var collection = new ModbusCollection(ModbusObjectType.DiscreteInput);
		collection.SetState(10, true);
		collection.SetState(11, false);

		Assert.IsTrue(collection.GetState(10));
		Assert.IsFalse(collection.GetState(11));
	}

	/// <summary>
	/// Checks that 16-bit values can be read back, also as the other signedness.
	/// </summary>
	[TestMethod]
	public void Int16_RoundTrip()
	{
		var collection = new ModbusCollection(ModbusObjectType.InputRegister);
		collection.SetInt16(0, -2);

		Assert.AreEqual((short)-2, collection.GetInt16(0));
		Assert.AreEqual((ushort)0xfffe, collection.GetUInt16(0));
	}

	/// <summary>
	/// Checks that 32-bit values are stored with the high word first.
	/// </summary>
	[TestMethod]
	public void UInt32_HighWordFirst()
	{
		var collection = new ModbusCollection(ModbusObjectType.HoldingRegister);
		collection.SetUInt32(100, 0x12345678);

		Assert.AreEqual((ushort)0x1234, collection.GetUInt16(100));
		Assert.AreEqual((ushort)0x5678, collection.GetUInt16(101));
		Assert.AreEqual(0x12345678u, collection.GetUInt32(100));
	}

	/// <summary>
	/// Checks that signed 32-bit values can be read back.
	/// </summary>
	[TestMethod]
	public void Int32_RoundTrip()
	{
		var collection = new ModbusCollection(ModbusObjectType.HoldingRegister);
		collection.SetInt32(0, -123456789);

		Assert.AreEqual(-123456789, collection.GetInt32(0));
	}

	/// <summary>
	/// Checks that 64-bit values are stored with the high word first.
	/// </summary>
	[TestMethod]
	public void UInt64_HighWordFirst()
	{
		var collection = new ModbusCollection(ModbusObjectType.HoldingRegister);
		collection.SetUInt64(0, 0x0123456789abcdef);

		Assert.AreEqual((ushort)0x0123, collection.GetUInt16(0));
		Assert.AreEqual((ushort)0x4567, collection.GetUInt16(1));
		Assert.AreEqual((ushort)0x89ab, collection.GetUInt16(2));
		Assert.AreEqual((ushort)0xcdef, collection.GetUInt16(3));
		Assert.AreEqual(0x0123456789abcdefUL, collection.GetUInt64(0));
	}

	/// <summary>
	/// Checks that signed 64-bit values can be read back.
	/// </summary>
	[TestMethod]
	public void Int64_RoundTrip()
	{
		var collection = new ModbusCollection(ModbusObjectType.HoldingRegister);
		collection.SetInt64(0, -1234567890123456789);

		Assert.AreEqual(-1234567890123456789, collection.GetInt64(0));
	}

	/// <summary>
	/// Checks that single precision values are stored as IEEE 754 bits with the high word first.
	/// </summary>
	[TestMethod]
	public void Single_RoundTrip()
	{
		var collection = new ModbusCollection(ModbusObjectType.HoldingRegister);
		collection.SetSingle(0, 1.5f);

		Assert.AreEqual((ushort)0x3fc0, collection.GetUInt16(0));
		Assert.AreEqual((ushort)0x0000, collection.GetUInt16(1));
		Assert.AreEqual(1.5f, collection.GetSingle(0));
	}

	/// <summary>
	/// Checks that double precision values can be read back.
	/// </summary>
	[TestMethod]
	public void Double_RoundTrip()
	{
		var collection = new ModbusCollection(ModbusObjectType.HoldingRegister);
		collection.SetDouble(0, -1234.5678);

		Assert.AreEqual(-1234.5678, collection.GetDouble(0));
	}

	/// <summary>
	/// Checks that 8-bit strings store 2 characters per register, the first in the high byte.
	/// </summary>
	[TestMethod]
	public void String8_TwoCharactersPerRegister()
	{
		var collection = new ModbusCollection(ModbusObjectType.HoldingRegister);
		collection.SetString8(0, "ABCD");

		Assert.AreEqual((ushort)0x4142, collection.GetUInt16(0));
		Assert.AreEqual((ushort)0x4344, collection.GetUInt16(1));
		Assert.AreEqual("ABCD", collection.GetString8(0, 4));
	}

	/// <summary>
	/// Checks that an 8-bit string of odd length leaves the last low byte zero.
	/// </summary>
	[TestMethod]
	public void String8_OddLength()
	{
		var collection = new ModbusCollection(ModbusObjectType.HoldingRegister);
		collection.SetString8(0, "ABC");

		Assert.AreEqual(2, collection.Count);
		Assert.AreEqual((ushort)0x4300, collection.GetUInt16(1));
		Assert.AreEqual("ABC", collection.GetString8(0, 3));
	}

	/// <summary>
	/// Checks that 8-bit strings are only supported with single-byte encodings.
	/// </summary>
	[TestMethod]
	public void String8_MultiByteEncoding_Throws()
	{
		var collection = new ModbusCollection(ModbusObjectType.HoldingRegister);

		Assert.ThrowsExactly<ArgumentException>(
			() => collection.SetString8(0, "AB", Encoding.UTF8));
	}

	/// <summary>
	/// Checks that 16-bit strings store 1 character per register.
	/// </summary>
	[TestMethod]
	public void String16_OneCharacterPerRegister()
	{
		var collection = new ModbusCollection(ModbusObjectType.HoldingRegister);
		string text = new([(char)0x00c4, (char)0x20ac]);
		collection.SetString16(0, text);

		Assert.AreEqual((ushort)0x00c4, collection.GetUInt16(0));
		Assert.AreEqual((ushort)0x20ac, collection.GetUInt16(1));
		Assert.AreEqual(text, collection.GetString16(0, 2));
	}

	#endregion Typed values
}
