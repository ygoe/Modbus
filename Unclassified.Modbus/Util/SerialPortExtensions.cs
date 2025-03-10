using System.IO.Ports;
using System.Runtime.InteropServices;

namespace Unclassified.Modbus.Util;

// Problem description:
// - https://sparxeng.com/blog/software/must-use-net-system-io-ports-serialport
// - https://github.com/dotnet/runtime/projects/40 (lots of open issues)

// .NET implementation:
// - https://github.com/dotnet/runtime/blob/main/src/libraries/System.IO.Ports/src/System/IO/Ports/SerialStream.Windows.cs

// Resources when reimplementing serial ports using Win32 API:
// - https://learn.microsoft.com/en-us/windows/win32/devio/configuring-a-communications-resource
// - http://web.archive.org/web/20150822192836/http://www.codeproject.com:80/Articles/28827/Serial-Port-Communication-and-Implementation-of-th
// - https://metacpan.org/pod/Win32::SerialPort
// - https://stackoverflow.com/questions/15752272/serial-communication-with-minimal-delay

// Solution inspired by: https://stackoverflow.com/a/54610437

/// <summary>
/// This class provides extension methods to read from and write to a serial port with async and
/// cancellation support. This is not provided by .NET's <see cref="SerialPort"/> class in a
/// reliable and consistent way. These extension methods use workarounds and try to provide a
/// consistent solution that solves all problems.
/// </summary>
internal static class SerialPortExtensions
{
	/// <summary>
	/// Opens the serial port and discards the OS buffers that may have been received before this
	/// application was using the port.
	/// </summary>
	/// <param name="serialPort">The serial port to open.</param>
	public static void OpenClean(this SerialPort serialPort)
	{
		serialPort.Open();
		// Start with a clean stream, discard anything that was received before the port was opened
		// that might have been buffered by the OS
		serialPort.DiscardInBuffer();
		serialPort.DiscardOutBuffer();
	}

	/// <summary>
	/// Asynchronously reads a sequence of bytes from the serial port.
	/// </summary>
	/// <param name="serialPort">The serial port to read from.</param>
	/// <param name="buffer">The region of memory to write the data into.</param>
	/// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
	/// <returns>A task that represents the asynchronous read operation. Its result contains the
	///   total number of bytes read into the buffer. The result value can be less than the number
	///   of bytes allocated in the buffer if that many bytes are not currently available, or it can
	///   be 0 (zero) if the end of the stream has been reached.</returns>
	/// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was
	///   triggered.</exception>
	/// <exception cref="TimeoutException">No bytes were read in the
	///   <see cref="SerialPort.ReadTimeout"/> configured for the serial port instance.</exception>
	/// <remarks>
	/// This methods returns when the number of bytes, specified with the <paramref name="buffer"/>
	/// parameter, have been read. It throws an exception before that when the timeout has elapsed
	/// or the <paramref name="cancellationToken"/> was triggered.
	/// </remarks>
	public static async Task<int> ReadAsync(this SerialPort serialPort, Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		// SerialPort.ReadTimeout seems to be ignored, use a custom implementation instead
		using (var cts = new CancellationTokenSource(serialPort.ReadTimeout))
		using (cancellationToken.Register(() => cts.Cancel()))
		{
			var ctr = default(CancellationTokenRegistration);
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				// On Windows, SerialPort.ReadAsync ignores the CancellationToken.
				// This lets it wake up and return cleanly.
				ctr = cts.Token.Register(() => serialPort.DiscardInBuffer());
			}

			try
			{
				return await serialPort.BaseStream.ReadAsync(buffer, cts.Token).NoSync();
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				cancellationToken.ThrowIfCancellationRequested();
				return 0;   // Never reached
			}
			catch (OperationCanceledException) when (cts.IsCancellationRequested)
			{
				// cts was cancelled but not the parameter: it's the timeout
				throw new TimeoutException("No bytes to read within the ReadTimeout.");
			}
			catch (IOException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
			{
				throw new TimeoutException("No bytes to read within the ReadTimeout.");
			}
			finally
			{
				ctr.Dispose();
			}
		}
	}

	/// <summary>
	/// Asynchronously writes a sequence of bytes to the serial port.
	/// </summary>
	/// <param name="serialPort">The serial port to write to.</param>
	/// <param name="buffer">The region of memory to write data from.</param>
	/// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
	/// <returns>A task that represents the asynchronous write operation.</returns>
	/// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was
	///   triggered.</exception>
	/// <exception cref="TimeoutException">The write operation did not complete in the
	///   <see cref="SerialPort.WriteTimeout"/> configured for the serial port instance.</exception>
	/// <remarks>
	/// <para>
	/// Linux: Even when this method throws one of the described exceptions, the data will still be
	/// sent out completely. The Close method will block until the data has been sent. There are no
	/// workarounds known to actually discard the send buffer.
	/// </para>
	/// <para>
	/// Windows: The serial port can be closed immediately after a timeout or cancellation.
	/// Sometimes, even though sending timed out, no exception will be thrown. It is unknown what
	/// was actually sent in that case.
	/// </para>
	/// </remarks>
	public static async Task WriteAsync(this SerialPort serialPort, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
	{
		// SerialPort.WriteTimeout seems to be ignored, use a custom implementation instead
		using (var cts = new CancellationTokenSource(serialPort.WriteTimeout))
		using (cancellationToken.Register(() => cts.Cancel()))
		{
			var ctr = default(CancellationTokenRegistration);
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				// On Windows, SerialPort.WriteAsync ignores the CancellationToken.
				// This lets it wake up and return cleanly.
				ctr = cts.Token.Register(() => serialPort.DiscardOutBuffer());
			}

			try
			{
				await serialPort.BaseStream.WriteAsync(buffer, cts.Token).NoSync();
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				cancellationToken.ThrowIfCancellationRequested();
			}
			catch (OperationCanceledException) when (cts.IsCancellationRequested)
			{
				throw new TimeoutException("Write operation not completed within the WriteTimeout.");
			}
			catch (IOException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
			{
				throw new TimeoutException("Write operation not completed within the WriteTimeout.");
			}
			finally
			{
				ctr.Dispose();
			}
		}
	}

	/// <summary>
	/// Returns a concise description of the serial port connection parameters: baud rate, data
	/// bits, parity and stop bits. Example: 9600,8N1
	/// </summary>
	/// <param name="serialPort">The serial port to describe.</param>
	/// <returns>The description of the parameters.</returns>
	public static string GetParamDescription(this SerialPort serialPort)
	{
		string stopStr = serialPort.StopBits == StopBits.OnePointFive ?
			"1.5" :
			serialPort.StopBits.ToString("D");
		return $"{serialPort.BaudRate},{serialPort.DataBits}{serialPort.Parity.ToString()[0]}{stopStr}";
	}
}
