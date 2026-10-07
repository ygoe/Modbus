namespace Unclassified.Modbus.Tests;

/// <summary>
/// Tests the RTU frame detection of the <see cref="ModbusSerialConnection"/> class.
/// </summary>
[TestClass]
public class ModbusSerialConnectionTests
{
	#region Private data

	/// <summary>
	/// A complete response body to a read device identification request with 3 objects.
	/// </summary>
	private static readonly byte[] deviceIdentificationResponse =
	[
		1, 43, 14, 1, 0x01, 0x00, 0x00,
		3,   // Number of objects
		0x00, 3, (byte)'A', (byte)'B', (byte)'C',
		0x01, 2, (byte)'P', (byte)'1',
		0x02, 3, (byte)'1', (byte)'.', (byte)'0'
	];

	#endregion Private data

	#region GetResponseBodyLength

	/// <summary>
	/// Checks that the length is unknown before the function code and the next byte are
	/// received.
	/// </summary>
	[TestMethod]
	public void GetResponseBodyLength_TooShort_Unknown()
	{
		Assert.AreEqual(-1, ModbusSerialConnection.GetResponseBodyLength([]));
		Assert.AreEqual(-1, ModbusSerialConnection.GetResponseBodyLength([1, 3]));
	}

	/// <summary>
	/// Checks that the length of a read response is taken from its byte count.
	/// </summary>
	[TestMethod]
	[DataRow((byte)1)]
	[DataRow((byte)2)]
	[DataRow((byte)3)]
	[DataRow((byte)4)]
	public void GetResponseBodyLength_Read_ByteCount(byte functionCode)
	{
		Assert.AreEqual(3 + 4, ModbusSerialConnection.GetResponseBodyLength([1, functionCode, 4]));
	}

	/// <summary>
	/// Checks the fixed length of write responses.
	/// </summary>
	[TestMethod]
	[DataRow((byte)5)]
	[DataRow((byte)6)]
	[DataRow((byte)15)]
	[DataRow((byte)16)]
	public void GetResponseBodyLength_Write_Fixed(byte functionCode)
	{
		Assert.AreEqual(6, ModbusSerialConnection.GetResponseBodyLength([1, functionCode, 0]));
	}

	/// <summary>
	/// Checks the fixed length of exception responses.
	/// </summary>
	[TestMethod]
	public void GetResponseBodyLength_Exception_Fixed()
	{
		Assert.AreEqual(3, ModbusSerialConnection.GetResponseBodyLength([1, 0x83, 2]));
	}

	/// <summary>
	/// Checks that responses to unsupported functions are rejected.
	/// </summary>
	[TestMethod]
	public void GetResponseBodyLength_UnsupportedFunction_Throws()
	{
		Assert.ThrowsExactly<NotImplementedException>(
			() => ModbusSerialConnection.GetResponseBodyLength([1, 7, 0]));
	}

	/// <summary>
	/// Checks that the length of a device identification response includes all objects.
	/// </summary>
	[TestMethod]
	public void GetResponseBodyLength_DeviceIdentification_Complete()
	{
		int length = ModbusSerialConnection.GetResponseBodyLength(deviceIdentificationResponse);

		Assert.AreEqual(deviceIdentificationResponse.Length, length);
	}

	/// <summary>
	/// Checks that the length of a device identification response is unknown until the
	/// header and each object's length are received.
	/// </summary>
	[TestMethod]
	[DataRow(7)]
	[DataRow(8)]
	[DataRow(9)]
	[DataRow(13)]
	[DataRow(14)]
	public void GetResponseBodyLength_DeviceIdentification_Incomplete(int receivedLength)
	{
		int length = ModbusSerialConnection.GetResponseBodyLength(
			deviceIdentificationResponse.AsSpan(0, receivedLength));

		Assert.AreEqual(-1, length);
	}

	#endregion GetResponseBodyLength
}
