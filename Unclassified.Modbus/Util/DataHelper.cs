using System.Text;

namespace Unclassified.Modbus.Util;

internal static class DataHelper
{
	public static string ToHexString(this Span<byte> bytes) => ToHexString((ReadOnlySpan<byte>)bytes);

	public static string ToHexString(this ReadOnlySpan<byte> bytes)
	{
		var stringBuilder = new StringBuilder();
		foreach (byte b in bytes)
		{
			if (stringBuilder.Length > 0)
				stringBuilder.Append(' ');
			stringBuilder.AppendFormat("{0:x2}", b);
		}
		return stringBuilder.ToString();
	}
}
