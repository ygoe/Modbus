using System.Runtime.InteropServices;

namespace Unclassified.Modbus.Util;

internal class RS485 : IDisposable
{
	private readonly string portName;
	private readonly RS485Flags serialDriverFlags;
	private bool isDriverModified;

	public RS485(string portName)
	{
		this.portName = portName;

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			var rs485 = GetDriverState();
			serialDriverFlags = rs485.Flags;
			rs485.Flags |= RS485Flags.Enabled;
			rs485.Flags &= ~RS485Flags.RxDuringTx;
			SetDriverState(rs485);
			isDriverModified = true;
		}
	}

	public void Dispose()
	{
		if (isDriverModified)
		{
			var rs485 = GetDriverState();
			rs485.Flags = serialDriverFlags;
			SetDriverState(rs485);
			isDriverModified = false;
		}
	}

	private SerialRS485 GetDriverState()
	{
		var rs485 = new SerialRS485();
		using var handle = UnsafeNativeMethods.Open(portName, UnsafeNativeMethods.O_RDWR | UnsafeNativeMethods.O_NOCTTY);
		if (UnsafeNativeMethods.IoCtl(handle, UnsafeNativeMethods.TIOCGRS485, ref rs485) == -1)
			throw new UnixIOException();
		return rs485;
	}

	private void SetDriverState(SerialRS485 rs485)
	{
		using var handle = UnsafeNativeMethods.Open(portName, UnsafeNativeMethods.O_RDWR | UnsafeNativeMethods.O_NOCTTY);
		if (UnsafeNativeMethods.IoCtl(handle, UnsafeNativeMethods.TIOCSRS485, ref rs485) == -1)
			throw new UnixIOException();
	}

	/// <summary>
	/// Represents the structure of the driver settings for RS-485.
	/// </summary>
	[StructLayout(LayoutKind.Sequential, Size = 32)]
	internal struct SerialRS485
	{
		/// <summary>
		/// The flags to change the driver state.
		/// </summary>
		public RS485Flags Flags;

		/// <summary>
		/// The delay in milliseconds before send.
		/// </summary>
		public uint RtsDelayBeforeSend;

		/// <summary>
		/// The delay in milliseconds after send.
		/// </summary>
		public uint RtsDelayAfterSend;
	}

	/// <summary>
	/// The flags for the driver state.
	/// </summary>
	[Flags]
	internal enum RS485Flags : uint
	{
		/// <summary>
		/// RS-485 is enabled.
		/// </summary>
		Enabled = 1,
		/// <summary>
		/// RS-485 uses RTS on send.
		/// </summary>
		RtsOnSend = 2,
		/// <summary>
		/// RS-485 uses RTS after send.
		/// </summary>
		RtsAfterSend = 4,
		/// <summary>
		/// Receive during send (duplex).
		/// </summary>
		RxDuringTx = 16
	}

	// Source: https://stackoverflow.com/a/10388107

	/// <summary>
	/// Implements a safe handle for unix systems.
	/// </summary>
	internal sealed class SafeUnixHandle : SafeHandle
	{
		/// <summary>
		/// Initializes a new instance of the <see cref="SafeUnixHandle"/> class.
		/// </summary>
		private SafeUnixHandle()
			: base(new IntPtr(-1), true)
		{
		}

		/// <inheritdoc/>
		public override bool IsInvalid => handle == new IntPtr(-1);

		/// <inheritdoc/>
		protected override bool ReleaseHandle()
		{
			return UnsafeNativeMethods.Close(handle) != -1;
		}
	}

	/// <summary>
	/// Definitions of the unsafe system methods.
	/// </summary>
	internal static class UnsafeNativeMethods
	{
		/// <summary>
		/// A flag for <see cref="Open"/>.
		/// </summary>
		internal const int O_RDWR = 2;
		/// <summary>
		/// A flag for <see cref="Open"/>.
		/// </summary>
		internal const int O_NOCTTY = 256;
		/// <summary>
		/// A flag for <see cref="IoCtl"/>.
		/// </summary>
		internal const uint TIOCGRS485 = 0x542e;
		/// <summary>
		/// A flag for <see cref="IoCtl"/>.
		/// </summary>
		internal const uint TIOCSRS485 = 0x542f;

		/// <summary>
		/// Opens a handle to a defined path (serial port).
		/// </summary>
		/// <param name="path">The path to open the handle.</param>
		/// <param name="flag">The flags for the handle.</param>
		/// <returns></returns>
		[DllImport("libc", EntryPoint = "open", SetLastError = true)]
		internal static extern SafeUnixHandle Open(string path, uint flag);

		/// <summary>
		/// Performs an ioctl request to the open handle.
		/// </summary>
		/// <param name="handle">The handle.</param>
		/// <param name="request">The request.</param>
		/// <param name="serialRs485">The data structure to read / write.</param>
		/// <returns></returns>
		[DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
		internal static extern int IoCtl(SafeUnixHandle handle, uint request, ref SerialRS485 serialRs485);

		/// <summary>
		/// Closes an open handle.
		/// </summary>
		/// <param name="handle">The handle.</param>
		/// <returns></returns>
		[DllImport("libc", EntryPoint = "close", SetLastError = true)]
		internal static extern int Close(IntPtr handle);

		/// <summary>
		/// Converts the given error number (errno) into a readable string.
		/// </summary>
		/// <param name="errno">The error number (errno).</param>
		/// <returns></returns>
		[DllImport("libc", EntryPoint = "strerror", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
		internal static extern IntPtr StrError(int errno);
	}

	/// <summary>
	/// Represents a unix specific IO exception.
	/// </summary>
	[Serializable]
	public class UnixIOException : ExternalException
	{
		/// <summary>
		/// Initializes a new instance of a <see cref="UnixIOException"/> class.
		/// </summary>
		public UnixIOException()
			: this(Marshal.GetLastWin32Error())
		{
		}

		/// <summary>
		/// Initializes a new instance of a <see cref="UnixIOException"/> class.
		/// </summary>
		/// <param name="error">The error number.</param>
		public UnixIOException(int error)
			: this(error, GetErrorMessage(error))
		{
		}

		/// <summary>
		/// Initializes a new instance of a <see cref="UnixIOException"/> class.
		/// </summary>
		/// <param name="message">The error message.</param>
		public UnixIOException(string message)
			: this(Marshal.GetLastWin32Error(), message)
		{
		}

		/// <summary>
		/// Initializes a new instance of a <see cref="UnixIOException"/> class.
		/// </summary>
		/// <param name="error">The error number.</param>
		/// <param name="message">The error message.</param>
		public UnixIOException(int error, string message)
			: base(message)
		{
			NativeErrorCode = error;
		}

		/// <summary>
		/// Initializes a new instance of a <see cref="UnixIOException"/> class.
		/// </summary>
		/// <param name="message">The error message.</param>
		/// <param name="innerException">An inner exception.</param>
		public UnixIOException(string message, Exception innerException)
			: base(message, innerException)
		{
		}

		/// <summary>
		/// Gets the native error code set by the unix system.
		/// </summary>
		public int NativeErrorCode { get; }

		private static string GetErrorMessage(int errno)
		{
			try
			{
				nint ptr = UnsafeNativeMethods.StrError(errno);
				return Marshal.PtrToStringAnsi(ptr) ?? "";
			}
			catch
			{
				return $"Unknown error (0x{errno:x})";
			}
		}
	}
}
