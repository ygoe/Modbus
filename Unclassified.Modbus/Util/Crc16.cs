namespace Unclassified.Modbus.Util;

internal static class Crc16
{
	private const ushort Initial = 0xffff;
	private const ushort Polynomial = 0xa001;
	private static readonly ushort[] table = new ushort[256];

	static Crc16()
	{
		ushort value;
		ushort temp;
		for (ushort i = 0; i < table.Length; ++i)
		{
			value = 0;
			temp = i;
			for (byte j = 0; j < 8; ++j)
			{
				if (((value ^ temp) & 0x0001) != 0)
					value = (ushort)(value >> 1 ^ Polynomial);
				else
					value >>= 1;
				temp >>= 1;
			}
			table[i] = value;
		}
	}

	public static ushort ComputeChecksum(ReadOnlySpan<byte> bytes)
	{
		ushort crc = Initial;
		for (int i = 0; i < bytes.Length; i++)
		{
			byte index = unchecked((byte)(crc ^ bytes[i]));
			crc = (ushort)(crc >> 8 ^ table[index]);
		}
		return crc;
	}

	public static ushort UpdateChecksum(ushort crc, byte data)
	{
		byte index = unchecked((byte)(crc ^ data));
		return (ushort)(crc >> 8 ^ table[index]);
	}
}
