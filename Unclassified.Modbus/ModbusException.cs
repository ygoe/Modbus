namespace Unclassified.Modbus;

/// <summary>
/// Represents a Modbus error that was reported by the server or that occurred in the client.
/// </summary>
[Serializable]
public class ModbusException : Exception
{
	/// <summary>
	/// Gets the Modbus exception code.
	/// </summary>
	public ModbusError Code { get; protected set; }

	/// <summary>
	/// Initializes a new instance of the <see cref="ModbusException"/> class with a specified
	/// exception code.
	/// </summary>
	/// <param name="code">The Modbus exception code.</param>
	public ModbusException(ModbusError code)
		: base(GetMessage(code))
	{
		Code = code;
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="ModbusException"/> class with a specified
	/// exception code and error message.
	/// </summary>
	/// <param name="code">The Modbus exception code.</param>
	/// <param name="message">The message that describes the error.</param>
	public ModbusException(ModbusError code, string? message)
		: base(message ?? GetMessage(code))
	{
		Code = code;
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="ModbusException"/> class with a specified
	/// exception code, error message and a reference to the inner exception that is the cause of
	/// this exception.
	/// </summary>
	/// <param name="code">The Modbus exception code.</param>
	/// <param name="message">The message that describes the error.</param>
	/// <param name="inner">The exception that is the cause of the current exception, or a null
	///   reference if no inner exception is specified.</param>
	public ModbusException(ModbusError code, string? message, Exception? inner)
		: base(message ?? GetMessage(code), inner)
	{
		Code = code;
	}

	/// <summary>
	/// Returns a description of the specified Modbus error code.
	/// </summary>
	/// <param name="code">The Modbus error code.</param>
	/// <returns>The description string.</returns>
	private static string GetMessage(ModbusError code) => code switch
	{
		ModbusError.None => "No error.",

		// Modbus exception codes
		ModbusError.IllegalFunction => "The function code received in the query is not recognized or allowed by the server. (Illegal function)",
		ModbusError.IllegalDataAddress => "The data address of some or all the required objects are not allowed or do not exist in the server. (Illegal data address)",
		ModbusError.IllegalDataValue => "The value is not accepted by the server. (Illegal data value)",
		ModbusError.ServerDeviceFailure => "An unrecoverable error occurred while the server was attempting to perform the requested action. (Server device failure)",
		ModbusError.Acknowledge => "The server has accepted the request and is processing it, but a long duration of time is required. (Acknowledge)",
		ModbusError.ServerDeviceBusy => "The server is engaged in processing a long-duration command. The client should retry later. (Server device busy)",
		ModbusError.NegativeAcknowledge => "The server cannot perform the programming functions. The client should request diagnostic or error information from the server. (Negative acknowledge)",
		ModbusError.MemoryParityError => "The server detected a parity error in its memory. The client can retry the request, but service may be required on the server device. (Memory parity error)",

		// Internal client errors
		ModbusError.CrcMismatch=> "The CRC on the received response is invalid.",
		ModbusError.ReadDeviceIdentificationLoop => "The read device identification function suggests reading the data in an endless loop.",
		ModbusError.IncompleteResponse => "The received response is too short or ends prematurely.",
		ModbusError.AddressMismatch => "The received response indicates a different start address than requested.",
		ModbusError.WriteMismatch => "The received response indicates a different written value than requested.",

		_ => $"An unknown Modbus exception occurred (code {(int)code}, 0x{(int)code:x2})."
	};
}

/// <summary>
/// Defines exception codes in response to a Modbus request.
/// </summary>
public enum ModbusError
{
	/// <summary>
	/// No error.
	/// </summary>
	None = 0,

	#region Modbus exception codes

	/// <summary>
	/// The function code received in the query is not recognized or allowed by the server.
	/// </summary>
	IllegalFunction = 1,

	/// <summary>
	/// The data address of some or all the required objects are not allowed or do not exist in the
	/// server.
	/// </summary>
	IllegalDataAddress = 2,

	/// <summary>
	/// The value is not accepted by the server.
	/// </summary>
	IllegalDataValue = 3,

	/// <summary>
	/// An unrecoverable error occurred while the server was attempting to perform the requested
	/// action.
	/// </summary>
	ServerDeviceFailure = 4,

	/// <summary>
	/// The server has accepted the request and is processing it, but a long duration of time is
	/// required. This response is returned to prevent a timeout error from occurring in the client.
	/// The client can next issue a Poll Program Complete message to determine whether processing is
	/// completed.
	/// </summary>
	Acknowledge = 5,

	/// <summary>
	/// The server is engaged in processing a long-duration command. The client should retry later.
	/// </summary>
	ServerDeviceBusy = 6,

	/// <summary>
	/// The server cannot perform the programming functions. The client should request diagnostic or
	/// error information from the server.
	/// </summary>
	NegativeAcknowledge = 7,

	/// <summary>
	/// The server detected a parity error in its memory. The client can retry the request, but
	/// service may be required on the server device.
	/// </summary>
	MemoryParityError = 8,

	/// <summary>
	/// The greatest value defined by the Modbus specification.
	/// </summary>
	MaxSpecValue = MemoryParityError,

	#endregion Modbus exception codes

	#region Internal client errors (above 255)

	/// <summary>
	/// The CRC on the received response is invalid.
	/// </summary>
	CrcMismatch = 256,

	/// <summary>
	/// The read device identification function suggests reading the data in an endless loop. It is
	/// unclear whether the data can be read completely.
	/// </summary>
	ReadDeviceIdentificationLoop = 257,

	/// <summary>
	/// The received response is too short or ends prematurely.
	/// </summary>
	IncompleteResponse = 258,

	/// <summary>
	/// The received response indicates a different start address than requested. The response is
	/// probably about something else than what the request asked for.
	/// </summary>
	AddressMismatch = 259,

	/// <summary>
	/// The received response indicates a different written value than requested. The write
	/// operation may have failed or was corrected to a different value.
	/// </summary>
	WriteMismatch = 260,

	#endregion Internal client errors (above 255)
}
