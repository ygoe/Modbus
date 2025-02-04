namespace ModbusClientDemo.Util;

public static class AnsiEscape
{
	// Source: https://stackoverflow.com/questions/4842424/list-of-ansi-color-escape-sequences

	public static string Reset => "\x1b[0m";

	public static string Bold => "\x1b[1m";
	public static string Dim => "\x1b[2m";
	public static string Italic => "\x1b[3m";
	public static string Underline => "\x1b[4m";
	public static string BlinkSlow => "\x1b[5m";
	public static string BlinkFast => "\x1b[6m";
	public static string Inverse => "\x1b[7m";
	public static string Hide => "\x1b[8m";
	public static string StrikeThrough => "\x1b[9m";
	public static string FontDefault => "\x1b[10m";
	public static string Font1 => "\x1b[11m";
	public static string Font2 => "\x1b[12m";
	public static string Font3 => "\x1b[13m";
	public static string Font4 => "\x1b[14m";
	public static string Font5 => "\x1b[15m";
	public static string Font6 => "\x1b[16m";
	public static string Font7 => "\x1b[17m";
	public static string Font8 => "\x1b[18m";
	public static string Font9 => "\x1b[19m";
	public static string NoBoldOrDim => "\x1b[22m";
	public static string NoItalic => "\x1b[23m";
	public static string NoUnderline => "\x1b[24m";
	public static string NoBlink => "\x1b[25m";
	public static string NoInverse => "\x1b[27m";
	public static string NoHide => "\x1b[28m";
	public static string NoStrikeThrough => "\x1b[29m";
	public static string ForegroundBlack => "\x1b[30m";
	public static string ForegroundRed => "\x1b[31m";
	public static string ForegroundGreen => "\x1b[32m";
	public static string ForegroundYellow => "\x1b[33m";
	public static string ForegroundBlue => "\x1b[34m";
	public static string ForegroundMagenta => "\x1b[35m";
	public static string ForegroundCyan => "\x1b[36m";
	public static string ForegroundWhite => "\x1b[37m";
	public static string ForegroundDefault => "\x1b[39m";
	public static string BackgroundBlack => "\x1b[40m";
	public static string BackgroundRed => "\x1b[41m";
	public static string BackgroundGreen => "\x1b[42m";
	public static string BackgroundYellow => "\x1b[43m";
	public static string BackgroundBlue => "\x1b[44m";
	public static string BackgroundMagenta => "\x1b[45m";
	public static string BackgroundCyan => "\x1b[46m";
	public static string BackgroundWhite => "\x1b[47m";
	public static string BackgroundDefault => "\x1b[49m";
	public static string Frame => "\x1b[51m";
	public static string Overline => "\x1b[53m";
	public static string NoFrame => "\x1b[54m";
	public static string NoOverline => "\x1b[55m";
	public static string ForegroundBrightBlack => "\x1b[90m";
	public static string ForegroundBrightRed => "\x1b[91m";
	public static string ForegroundBrightGreen => "\x1b[92m";
	public static string ForegroundBrightYellow => "\x1b[93m";
	public static string ForegroundBrightBlue => "\x1b[94m";
	public static string ForegroundBrightMagenta => "\x1b[95m";
	public static string ForegroundBrightCyan => "\x1b[96m";
	public static string ForegroundBrightWhite => "\x1b[97m";
	public static string ForegroundBrightDefault => "\x1b[99m";
	public static string BackgroundBrightBlack => "\x1b[100m";
	public static string BackgroundBrightRed => "\x1b[101m";
	public static string BackgroundBrightGreen => "\x1b[102m";
	public static string BackgroundBrightYellow => "\x1b[103m";
	public static string BackgroundBrightBlue => "\x1b[104m";
	public static string BackgroundBrightMagenta => "\x1b[105m";
	public static string BackgroundBrightCyan => "\x1b[106m";
	public static string BackgroundBrightWhite => "\x1b[107m";
	public static string BackgroundBrightDefault => "\x1b[109m";

	public static string Foreground(byte r, byte g, byte b) => $"\x1b[38;2;{r};{g};{b}m";
	public static string Background(byte r, byte g, byte b) => $"\x1b[48;2;{r};{g};{b}m";
}
