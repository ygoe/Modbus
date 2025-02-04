using System.Collections;
using System.Text;
using static System.BitConverter;

namespace Unclassified.Modbus;

/// <summary>
/// Contains Modbus data objects of a single type.
/// </summary>
public class ModbusCollection : ICollection<IModbusObject>
{
	#region Private data

	private readonly List<IModbusObject> objects = [];

	#endregion Private data

	#region Constructors

	/// <summary>
	/// Initializes a new instance of the <see cref="ModbusCollection"/> class.
	/// </summary>
	/// <param name="objectType">The type of data objects that the collection can contain.</param>
	public ModbusCollection(ModbusObjectType objectType)
	{
		ObjectType = objectType;
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="ModbusCollection"/> class with the
	/// specified objects.
	/// </summary>
	/// <param name="objects">The data objects to add to the collection. All objects must have the
	///   same <see cref="IModbusObject.Type"/>.</param>
	public ModbusCollection(IEnumerable<IModbusObject> objects)
	{
		this.objects.AddRange(objects);
		if (this.objects.Count == 0)
			throw new ArgumentException("At least one Modbus object must be specified for the collection. Use another constructor to create an empty collection.");
		ObjectType = this.objects[0].Type;
		if (!this.objects.All(e => e.Type == ObjectType))
			throw new ArgumentException("Inconsistent Modbus object types specified.");
	}

	#endregion Constructors

	#region Properties

	/// <summary>
	/// Gets the type of objects that the collection can contain.
	/// </summary>
	public ModbusObjectType ObjectType { get; }

	/// <summary>
	/// Gets the number of objects contained in the <see cref="ModbusCollection"/>.
	/// </summary>
	public int Count => objects.Count;

	#endregion Properties

	#region Object ranges

	/// <summary>
	/// Returns the combined ranges of all objects in the collection.
	/// </summary>
	/// <param name="maxLength">The maximum length of a single range.</param>
	/// <param name="allowedWaste">The maximum allowed objects to add to merge ranges.</param>
	/// <returns></returns>
	public IList<ModbusRange> GetRanges(int maxLength, int allowedWaste)
	{
		var objectRanges = objects.Select(o => new ModbusRange(o.Address));
		return ModbusRange.Combine(objectRanges, maxLength, allowedWaste);
	}

	#endregion Object ranges

	#region Data retrieval

	/// <summary>
	/// Gets the bit value of the object at the specified <paramref name="address"/>.
	/// </summary>
	/// <param name="address">The address of the object to retrieve.</param>
	/// <returns>The bit value of the object.</returns>
	public bool GetState(int address) =>
		objects.OfType<IModbusStateObject>().First(e => e.Address == address).State;

	/// <summary>
	/// Gets the unsigned 16-bit value of the object at the specified <paramref name="address"/>.
	/// </summary>
	/// <param name="address">The address of the object to retrieve.</param>
	/// <returns>The value of the object.</returns>
	public ushort GetUInt16(int address) =>
		objects.OfType<IModbusValueObject>().First(e => e.Address == address).Value;

	/// <summary>
	/// Gets the signed 16-bit value of the object at the specified <paramref name="address"/>.
	/// </summary>
	/// <param name="address">The address of the object to retrieve.</param>
	/// <returns>The value of the object.</returns>
	public short GetInt16(int address) => unchecked((short)GetUInt16(address));

	/// <summary>
	/// Gets the unsigned 32-bit value of the 2 objects starting at the specified
	/// <paramref name="address"/>.
	/// </summary>
	/// <param name="address">The address of the first object to retrieve.</param>
	/// <returns>The value of the objects.</returns>
	public uint GetUInt32(int address) =>
		(uint)GetUInt16(address + 0) << 16 |
		(uint)GetUInt16(address + 1);

	/// <summary>
	/// Gets the signed 32-bit value of the 2 objects starting at the specified
	/// <paramref name="address"/>.
	/// </summary>
	/// <param name="address">The address of the first object to retrieve.</param>
	/// <returns>The value of the objects.</returns>
	public int GetInt32(int address) => unchecked((int)GetUInt32(address));

	/// <summary>
	/// Gets the unsigned 64-bit value of the 4 objects starting at the specified
	/// <paramref name="address"/>.
	/// </summary>
	/// <param name="address">The address of the first object to retrieve.</param>
	/// <returns>The value of the objects.</returns>
	public ulong GetUInt64(int address) =>
		(ulong)GetUInt16(address + 0) << 48 |
		(ulong)GetUInt16(address + 1) << 32 |
		(ulong)GetUInt16(address + 2) << 16 |
		(ulong)GetUInt16(address + 3);

	/// <summary>
	/// Gets the signed 64-bit value of the 4 objects starting at the specified
	/// <paramref name="address"/>.
	/// </summary>
	/// <param name="address">The address of the first object to retrieve.</param>
	/// <returns>The value of the objects.</returns>
	public long GetInt64(int address) => unchecked((long)GetUInt64(address));

	/// <summary>
	/// Gets the single precision floating point value of the 2 objects starting at the specified
	/// <paramref name="address"/>.
	/// </summary>
	/// <param name="address">The address of the first object to retrieve.</param>
	/// <returns>The value of the objects.</returns>
	public float GetSingle(int address) => UInt32BitsToSingle(GetUInt32(address));

	/// <summary>
	/// Gets the double precision floating point value of the 4 objects starting at the specified
	/// <paramref name="address"/>.
	/// </summary>
	/// <param name="address">The address of the first object to retrieve.</param>
	/// <returns>The value of the objects.</returns>
	public double GetDouble(int address) => UInt64BitsToDouble(GetUInt64(address));

	/// <summary>
	/// Gets the string value of the objects starting at the specified <paramref name="address"/>.
	/// For each 2 characters 1 word object is read.
	/// </summary>
	/// <param name="address">The address of the first object to retrieve.</param>
	/// <param name="length">The length of the string in characters.</param>
	/// <param name="encoding">The single-byte text encoding, defaults to 7-bit ASCII.</param>
	/// <returns>The string value of the objects.</returns>
	public string GetString8(int address, int length, Encoding? encoding = null)
	{
		encoding ??= Encoding.ASCII;
		CheckSingleByteEncoding(encoding);
		var sb = new StringBuilder();
		while (length > 0)
		{
			ushort word1 = GetUInt16(address);
			sb.Append(encoding.GetChars([(byte)(word1 >> 8)])[0]);
			if (length > 1)
				sb.Append(encoding.GetChars([(byte)(word1 & 0xff)])[0]);
			length -= 2;
			address++;
		}
		return sb.ToString();
	}

	/// <summary>
	/// Gets the string value of the objects starting at the specified <paramref name="address"/>.
	/// For each character a word object is read.
	/// </summary>
	/// <param name="address">The address of the first object to retrieve.</param>
	/// <param name="length">The length of the string in characters.</param>
	/// <returns>The string value of the objects.</returns>
	public string GetString16(int address, int length)
	{
		var sb = new StringBuilder();
		while (length > 0)
		{
			ushort word1 = GetUInt16(address);
			sb.Append((char)word1);
			length--;
			address++;
		}
		return sb.ToString();
	}

	#endregion Data retrieval

	#region Data manipulation

	/// <summary>
	/// Sets the bit value of the object at the specified <paramref name="address"/>. Missing
	/// objects are added to the collection.
	/// </summary>
	/// <param name="address">The address of the object to set.</param>
	/// <param name="state">The new bit value.</param>
	public void SetState(int address, bool state)
	{
		CheckStateObjectType();
		objects.RemoveAll(e => e.Address == address);
		objects.Add(CreateStateObject(address, state));
	}

	/// <summary>
	/// Sets the unsigned 16-bit value of the object at the specified <paramref name="address"/>.
	/// Missing objects are added to the collection.
	/// </summary>
	/// <param name="address">The address of the object to set.</param>
	/// <param name="value">The new value.</param>
	public void SetUInt16(int address, ushort value)
	{
		CheckValueObjectType();
		objects.RemoveAll(e => e.Address == address);
		objects.Add(CreateValueObject(address, value));
	}

	/// <summary>
	/// Sets the signed 16-bit value of the object at the specified <paramref name="address"/>.
	/// Missing objects are added to the collection.
	/// </summary>
	/// <param name="address">The address of the object to set.</param>
	/// <param name="value">The new value.</param>
	public void SetInt16(int address, short value) => SetUInt16(address, unchecked((ushort)value));

	/// <summary>
	/// Sets the unsigned 32-bit value of the 2 objects starting at the specified
	/// <paramref name="address"/>. Missing objects are added to the collection.
	/// </summary>
	/// <param name="address">The address of the object to set.</param>
	/// <param name="value">The new value.</param>
	public void SetUInt32(int address, uint value)
	{
		CheckValueObjectType();
		objects.RemoveAll(e => e.Address == address + 0);
		objects.RemoveAll(e => e.Address == address + 1);
		objects.Add(CreateValueObject(address + 0, (ushort)(value >> 16)));
		objects.Add(CreateValueObject(address + 1, (ushort)(value & 0xffff)));
	}

	/// <summary>
	/// Sets the signed 32-bit value of the 2 objects starting at the specified
	/// <paramref name="address"/>. Missing objects are added to the collection.
	/// </summary>
	/// <param name="address">The address of the object to set.</param>
	/// <param name="value">The new value.</param>
	public void SetInt32(int address, int value) => SetUInt32(address, unchecked((uint)value));

	/// <summary>
	/// Sets the unsigned 64-bit value of the 4 objects starting at the specified
	/// <paramref name="address"/>. Missing objects are added to the collection.
	/// </summary>
	/// <param name="address">The address of the object to set.</param>
	/// <param name="value">The new value.</param>
	public void SetUInt64(int address, ulong value)
	{
		CheckValueObjectType();
		objects.RemoveAll(e => e.Address == address + 0);
		objects.RemoveAll(e => e.Address == address + 1);
		objects.RemoveAll(e => e.Address == address + 2);
		objects.RemoveAll(e => e.Address == address + 3);
		objects.Add(CreateValueObject(address + 0, (ushort)(value >> 48)));
		objects.Add(CreateValueObject(address + 1, unchecked((ushort)(value >> 32))));
		objects.Add(CreateValueObject(address + 2, unchecked((ushort)(value >> 16))));
		objects.Add(CreateValueObject(address + 3, (ushort)(value & 0xffff)));
	}

	/// <summary>
	/// Sets the signed 64-bit value of the 4 objects starting at the specified
	/// <paramref name="address"/>. Missing objects are added to the collection.
	/// </summary>
	/// <param name="address">The address of the object to set.</param>
	/// <param name="value">The new value.</param>
	public void SetInt64(int address, long value) => SetUInt64(address, unchecked((ulong)value));

	/// <summary>
	/// Sets the single precision floating point value of the 2 objects starting at the specified
	/// <paramref name="address"/>. Missing objects are added to the collection.
	/// </summary>
	/// <param name="address">The address of the object to set.</param>
	/// <param name="value">The new value.</param>
	public void SetSingle(int address, float value) => SetUInt32(address, SingleToUInt32Bits(value));

	/// <summary>
	/// Sets the double precision floating point value of the 4 objects starting at the specified
	/// <paramref name="address"/>. Missing objects are added to the collection.
	/// </summary>
	/// <param name="address">The address of the object to set.</param>
	/// <param name="value">The new value.</param>
	public void SetDouble(int address, double value) => SetUInt64(address, DoubleToUInt64Bits(value));

	/// <summary>
	/// Sets the string value of the objects starting at the specified <paramref name="address"/>.
	/// For each 2 characters 1 word object is written. Missing objects are added to the collection.
	/// </summary>
	/// <param name="address">The address of the first object to set.</param>
	/// <param name="value">The new string.</param>
	/// <param name="encoding">The single-byte text encoding, defaults to 7-bit ASCII.</param>
	public void SetString8(int address, string value, Encoding? encoding = null)
	{
		CheckValueObjectType();
		encoding ??= Encoding.ASCII;
		CheckSingleByteEncoding(encoding);
		for (int addressOffset = 0; addressOffset < (value.Length + 1) / 2; addressOffset++)
		{
			objects.RemoveAll(e => e.Address == address + addressOffset);
			ushort word = (ushort)(encoding.GetBytes([value[addressOffset * 2]])[0] << 8);
			if (addressOffset * 2 + 1 < value.Length)
				word |= encoding.GetBytes([value[addressOffset * 2]])[0];
			objects.Add(CreateValueObject(address + addressOffset, word));
		}
	}

	/// <summary>
	/// Sets the string value of the objects starting at the specified <paramref name="address"/>.
	/// For each character a word object is written. Missing objects are added to the collection.
	/// </summary>
	/// <param name="address">The address of the first object to set.</param>
	/// <param name="value">The new string.</param>
	public void SetString16(int address, string value)
	{
		CheckValueObjectType();
		for (int addressOffset = 0; addressOffset < value.Length; addressOffset++)
		{
			objects.RemoveAll(e => e.Address == address + addressOffset);
			objects.Add(CreateValueObject(address + addressOffset, value[addressOffset]));
		}
	}

	#endregion Data manipulation

	#region Private helper methods

	private void CheckStateObjectType()
	{
		if (ObjectType != ModbusObjectType.DiscreteInput && ObjectType != ModbusObjectType.Coil)
			throw new InvalidOperationException("The Modbus collection cannot contain bit values.");
	}

	private void CheckValueObjectType()
	{
		if (ObjectType != ModbusObjectType.InputRegister && ObjectType != ModbusObjectType.HoldingRegister)
			throw new InvalidOperationException("The Modbus collection cannot contain word values.");
	}

	private IModbusStateObject CreateStateObject(int address, bool state) => ObjectType switch
	{
		ModbusObjectType.DiscreteInput => new ModbusDiscreteInput() { Address = (ushort)address, State = state },
		ModbusObjectType.Coil => new ModbusCoil() { Address = (ushort)address, State = state },
		_ => throw new NotSupportedException("Invalid Modbus object type."),
	};

	private IModbusValueObject CreateValueObject(int address, ushort value) => ObjectType switch
	{
		ModbusObjectType.InputRegister => new ModbusInputRegister() { Address = (ushort)address, Value = value },
		ModbusObjectType.HoldingRegister => new ModbusHoldingRegister() { Address = (ushort)address, Value = value },
		_ => throw new NotSupportedException("Invalid Modbus object type."),
	};

	private static void CheckSingleByteEncoding(Encoding encoding)
	{
		if (!encoding.IsSingleByte)
			throw new ArgumentException("Only single-byte text encodings are allowed. Use the 16-bit string methods for Unicode strings.");
	}

	#endregion Private helper methods

	#region ICollection implementation

	bool ICollection<IModbusObject>.IsReadOnly => false;

	/// <inheritdoc/>
	public void Add(IModbusObject item)
	{
		if (item.Type != ObjectType)
			throw new InvalidOperationException($"The new item object type ({item.Type}) does not match the collection ({ObjectType}).");
		objects.RemoveAll(o => o.Address == item.Address);
		objects.Add(item);
	}

	/// <inheritdoc/>
	public void AddRange(IEnumerable<IModbusObject> items)
	{
		foreach (var item in items)
		{
			Add(item);
		}
	}

	/// <inheritdoc/>
	public void Clear()
	{
		objects.Clear();
	}

	/// <inheritdoc/>
	public bool Contains(IModbusObject item)
	{
		return objects.Contains(item);
	}

	/// <inheritdoc/>
	public void CopyTo(IModbusObject[] array, int arrayIndex)
	{
		throw new NotImplementedException();
	}

	/// <inheritdoc/>
	public bool Remove(IModbusObject item)
	{
		return objects.Remove(item);
	}

	/// <inheritdoc/>
	public IEnumerator<IModbusObject> GetEnumerator()
	{
		return objects.GetEnumerator();
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		return objects.GetEnumerator();
	}

	#endregion ICollection implementation
}
