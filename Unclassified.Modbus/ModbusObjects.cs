namespace Unclassified.Modbus;

#region Modbus object interfaces

/// <summary>
/// An object in the Modbus data model.
/// </summary>
public interface IModbusObject
{
	/// <summary>
	/// Gets the data object type.
	/// </summary>
	ModbusObjectType Type { get; }

	/// <summary>
	/// Gets the data object address.
	/// </summary>
	ushort Address { get; }
}

/// <summary>
/// A Modbus object that holds a single bit (0 or 1).
/// </summary>
public interface IModbusStateObject : IModbusObject
{
	/// <summary>
	/// Gets the bit state of the data object.
	/// </summary>
	bool State { get; }
}

/// <summary>
/// A Modbus object that holds a word value (16 bit).
/// </summary>
public interface IModbusValueObject : IModbusObject
{
	/// <summary>
	/// Gets the word value of the data object.
	/// </summary>
	ushort Value { get; }
}

#endregion Modbus object interfaces

#region Modbus object classes

/// <summary>
/// Represents a Modbus discrete input object that holds a single bit (0 or 1) and can only be read.
/// </summary>
public readonly struct ModbusDiscreteInput : IModbusStateObject
{
	/// <summary>
	/// Gets the Modbus object type. Always returns <see cref="ModbusObjectType.DiscreteInput"/>.
	/// </summary>
	public ModbusObjectType Type => ModbusObjectType.DiscreteInput;

	/// <inheritdoc/>
	public ushort Address { get; init; }

	/// <inheritdoc/>
	public bool State { get; init; }
}

/// <summary>
/// Represents a Modbus coil object that holds a single bit (0 or 1) and can be read and written.
/// </summary>
public readonly struct ModbusCoil : IModbusStateObject
{
	/// <summary>
	/// Gets the Modbus object type. Always returns <see cref="ModbusObjectType.Coil"/>.
	/// </summary>
	public ModbusObjectType Type => ModbusObjectType.Coil;

	/// <inheritdoc/>
	public ushort Address { get; init; }

	/// <inheritdoc/>
	public bool State { get; init; }
}

/// <summary>
/// Represents a Modbus input register object that holds a word value (16 bit) and can only be read.
/// </summary>
public readonly struct ModbusInputRegister : IModbusValueObject
{
	/// <summary>
	/// Gets the Modbus object type. Always returns <see cref="ModbusObjectType.InputRegister"/>.
	/// </summary>
	public ModbusObjectType Type => ModbusObjectType.InputRegister;

	/// <inheritdoc/>
	public ushort Address { get; init; }

	/// <inheritdoc/>
	public ushort Value { get; init; }
}

/// <summary>
/// Represents a Modbus holding register object that holds a word value (16 bit) and can be read and
/// written.
/// </summary>
public readonly struct ModbusHoldingRegister : IModbusValueObject
{
	/// <summary>
	/// Gets the Modbus object type. Always returns <see cref="ModbusObjectType.HoldingRegister"/>.
	/// </summary>
	public ModbusObjectType Type => ModbusObjectType.HoldingRegister;

	/// <inheritdoc/>
	public ushort Address { get; init; }

	/// <inheritdoc/>
	public ushort Value { get; init; }
}

#endregion Modbus object classes

#region Enums

/// <summary>
/// Defines object types in the Modbus data model.
/// </summary>
public enum ModbusObjectType
{
	/// <summary>
	/// Unspecified.
	/// </summary>
	None,
	/// <summary>
	/// A discrete input that holds a single bit (0 or 1) and can only be read.
	/// </summary>
	DiscreteInput,
	/// <summary>
	/// A coil that holds a single bit (0 or 1) and can be read and written.
	/// </summary>
	Coil,
	/// <summary>
	/// An input register that holds a word value (16 bit) and can only be read.
	/// </summary>
	InputRegister,
	/// <summary>
	/// A holding register that holds a word value (16 bit) and can be read and written.
	/// </summary>
	HoldingRegister
}

/// <summary>
/// Defines objects in the device identification address space.
/// </summary>
public enum ModbusDeviceIdentificationObject : byte
{
	/// <summary>
	/// The vendor name.
	/// </summary>
	VendorName = 0,
	/// <summary>
	/// The product code.
	/// </summary>
	ProductCode = 1,
	/// <summary>
	/// The major and minor revision.
	/// </summary>
	MajorMinorRevision = 2,
	/// <summary>
	/// The vendor URL.
	/// </summary>
	VendorUrl = 3,
	/// <summary>
	/// The product name.
	/// </summary>
	ProductName = 4,
	/// <summary>
	/// The model name.
	/// </summary>
	ModelName = 5,
	/// <summary>
	/// The user application name.
	/// </summary>
	UserApplicationName = 6,
	/// <summary>
	/// The first value of product-specific extended objects.
	/// </summary>
	FirstPrivateObject = 0x80
}

/// <summary>
/// Defines function codes to send in the Modbus protocol.
/// </summary>
internal enum FunctionCode : byte
{
	None = 0,
	ReadCoils = 1,
	ReadDiscreteInputs = 2,
	ReadHoldingRegisters = 3,
	ReadInputRegisters = 4,
	WriteCoil = 5,
	WriteHoldingRegister = 6,
	WriteCoils = 15,
	WriteHoldingRegisters = 16,
	ReadDeviceIdentification = 43
}

#endregion Enums
