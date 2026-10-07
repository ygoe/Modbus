using System.Text;
using Unclassified.Modbus.Util;

namespace Unclassified.Modbus.Tests.Util;

/// <summary>
/// Tests the <see cref="Crc16"/> class.
/// </summary>
[TestClass]
public class Crc16Tests
{
	/// <summary>
	/// Checks the standard check value of the CRC-16/MODBUS algorithm.
	/// </summary>
	[TestMethod]
	public void ComputeChecksum_CheckValue()
	{
		byte[] bytes = Encoding.ASCII.GetBytes("123456789");

		Assert.AreEqual((ushort)0x4b37, Crc16.ComputeChecksum(bytes));
	}

	/// <summary>
	/// Checks the CRC of RTU request frames as they are found in Modbus documentation.
	/// </summary>
	/// <remarks>
	/// The CRC is sent with the low byte first, so the frame 01 03 00 00 00 0A C5 CD has the CRC
	/// 0xCDC5.
	/// </remarks>
	[TestMethod]
	public void ComputeChecksum_RequestFrames()
	{
		byte[] readHoldingRegisters = [0x01, 0x03, 0x00, 0x00, 0x00, 0x0a];
		byte[] readInputRegister = [0x01, 0x04, 0x00, 0x00, 0x00, 0x01];

		Assert.AreEqual((ushort)0xcdc5, Crc16.ComputeChecksum(readHoldingRegisters));
		Assert.AreEqual((ushort)0xca31, Crc16.ComputeChecksum(readInputRegister));
	}

	/// <summary>
	/// Checks that updating the checksum byte by byte gives the same result.
	/// </summary>
	[TestMethod]
	public void UpdateChecksum_MatchesComputeChecksum()
	{
		byte[] bytes = [0x01, 0x03, 0x00, 0x00, 0x00, 0x0a];
		ushort crc = 0xffff;
		foreach (byte b in bytes)
		{
			crc = Crc16.UpdateChecksum(crc, b);
		}

		Assert.AreEqual(Crc16.ComputeChecksum(bytes), crc);
	}
}
