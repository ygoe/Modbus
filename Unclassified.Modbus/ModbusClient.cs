using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Unclassified.Modbus.Util;

namespace Unclassified.Modbus;

// NOTE:
// Comments marked with the VIOLATION tag describe work-around behaviour for non-conforming Modbus
// devices. They explain code that deliberately deviates from the Modbus specification to still
// allow communicating with those devices off the standard protocol where possible through auto-
// detection with the available data.

/// <summary>
/// Implements a client that communicates with a server using the Modbus protocol.
/// </summary>
public class ModbusClient : IDisposable
{
	#region Private data

	private readonly IModbusConnectionFactory connectionFactory;
	private readonly ILogger? logger;
	private readonly SemaphoreSlim connectionSemaphore = new(1, 1);
	private readonly Timer connectionIdleTimer;
	private IModbusConnection? connection;

	#endregion Private data

	#region Constructors

	/// <summary>
	/// Initializes a new instance of the <see cref="ModbusClient"/> class.
	/// </summary>
	/// <param name="connectionFactory">The factory to create new connections.</param>
	/// <param name="logger">The logger instance.</param>
	public ModbusClient(IModbusConnectionFactory connectionFactory, ILogger? logger)
	{
		this.connectionFactory = connectionFactory;
		this.logger = logger;
		connectionIdleTimer = new(OnConnectionIdleTimer);
	}

	#endregion Constructors

	#region Properties

	/// <summary>
	/// Gets or sets the maximum time to wait for a server response on the current connection.
	/// Default is 2 seconds. Set to <see cref="Timeout.InfiniteTimeSpan"/> to wait infinitely for
	/// connections and responses. The timeout begins after the semaphore has been entered.
	/// </summary>
	public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(2);

	/// <summary>
	/// Gets or sets the time to wait after an exception response before retrying the request.
	/// Default is 500 ms.
	/// </summary>
	public TimeSpan ExceptionRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

	/// <summary>
	/// Gets or sets the time to wait after a "server busy" response before retrying the request.
	/// Default is 1 second.
	/// </summary>
	public TimeSpan BusyRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

	/// <summary>
	/// Gets or sets the number of retries for a request after a temporary failure. Default is 4,
	/// resulting in total 5 efforts to send a request.
	/// </summary>
	public int RetryCount { get; init; } = 4;

	/// <summary>
	/// Gets or sets the idle time after that the connection is closed. Default is 7 seconds.
	/// Set to <see cref="Timeout.InfiniteTimeSpan"/> to disable idle closing the connection.
	/// Set to <see cref="TimeSpan.Zero"/> to close the connection immediately after each request.
	/// </summary>
	public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(7);

	/// <summary>
	/// Gets or sets the maximum length of a single request range. If unset, only the protocol
	/// limits apply.
	/// </summary>
	public int MaxRequestLength { get; init; }

	/// <summary>
	/// Gets or sets the maximum allowed objects to add to merge request ranges. Default is 0.
	/// </summary>
	public int AllowedRequestWaste { get; init; }

	/// <summary>
	/// Gets a value indicating whether the client will always write single Modbus objects to the
	/// server even if multiple should be written. This state is determined automatically by try and
	/// error.
	/// </summary>
	public bool AlwaysWriteSingle { get; private set; }

	/// <summary>
	/// Gets a value indicating whether the client will always write multiple Modbus objects to the
	/// server even if only a single should be written. This state is determined automatically by
	/// try and error.
	/// </summary>
	public bool AlwaysWriteMultiple { get; private set; }

	/// <summary>
	/// Gets the <see cref="IModbusConnectionFactory"/> instance that is used to connect to a Modbus
	/// server. A description of the connection data can be accessed through its
	/// <see cref="object.ToString()"/> method.
	/// </summary>
	public IModbusConnectionFactory ConnectionFactory => connectionFactory;

	#endregion Properties

	#region Public client methods

	/// <summary>
	/// Reads Modbus objects from the server. The specified address ranges will be split or combined
	/// into one or multiple Modbus requests as necessary. If the device returns less objects than
	/// requested, the remaining will be requested separately until complete.
	/// </summary>
	/// <param name="objectType">The type of objects to read.</param>
	/// <param name="deviceId">The remote device ID.</param>
	/// <param name="range">The address range to read.</param>
	/// <param name="cancellationToken">A cancellation token used to propagate notification that
	///   this operation should be canceled.</param>
	/// <returns>A collection of Modbus objects returned by the server.</returns>
	/// <exception cref="ModbusException">When the Modbus operation failed because of a negative
	///   response or protocol violation. The reason will be indicated in the exception's
	///   <see cref="ModbusException.Code"/> property.</exception>
	public Task<ModbusCollection> Read(ModbusObjectType objectType, int deviceId, ModbusRange range, CancellationToken cancellationToken = default) =>
		Read(objectType, deviceId, new[] { range }, cancellationToken);

	/// <summary>
	/// Reads Modbus objects from the server. The specified address ranges will be split or combined
	/// into one or multiple Modbus requests as necessary. If the device returns less objects than
	/// requested, the remaining will be requested separately until complete.
	/// </summary>
	/// <param name="objectType">The type of objects to read.</param>
	/// <param name="deviceId">The remote device ID.</param>
	/// <param name="ranges">The address ranges to read.</param>
	/// <param name="cancellationToken">A cancellation token used to propagate notification that
	///   this operation should be canceled.</param>
	/// <returns>A collection of Modbus objects returned by the server.</returns>
	/// <exception cref="ModbusException">When the Modbus operation failed because of a negative
	///   response or protocol violation. The reason will be indicated in the exception's
	///   <see cref="ModbusException.Code"/> property.</exception>
	public async Task<ModbusCollection> Read(ModbusObjectType objectType, int deviceId, IEnumerable<ModbusRange> ranges, CancellationToken cancellationToken = default)
	{
		if (IsDisposed)
			throw new ObjectDisposedException(nameof(ModbusClient));

		int limitLength = ModbusRange.GetMaxLength(objectType);
		int maxRequestLength = MaxRequestLength;
		if (maxRequestLength <= 0 || maxRequestLength > limitLength)
			maxRequestLength = limitLength;

		var combinedRanges = ModbusRange.Combine(ranges, maxRequestLength, AllowedRequestWaste);
		var results = new ModbusCollection(objectType);
		foreach (var range in combinedRanges)
		{
			var currentRange = range;
			int retryCount = RetryCount;
			while (true)
			{
				var requestBody = MakeReadRequest((byte)deviceId, objectType, currentRange.StartAddress, currentRange.Length);
				var responseBody = await SendRequest(requestBody, cancellationToken).NoSync();
				ModbusCollection objects;
				try
				{
					objects = DecodeReadResponse(responseBody, objectType, currentRange.StartAddress, currentRange.Length);
				}
				catch (ModbusException ex) when (ex.Code == ModbusError.ServerDeviceBusy)
				{
					if (retryCount-- <= 0)
						throw;
					// Repeat that later
					logger?.LogDebug("Server is busy, retrying later...");
					await Task.Delay(RandomizeTimeSpan(BusyRetryDelay), cancellationToken).NoSync();
					continue;
				}
				results.AddRange(objects);
				if (objects.Count >= currentRange.Length)
					break;
				// VIOLATION: Repeat for remaining objects not included in the response
				if (logger?.IsEnabled(LogLevel.Debug) == true)
					logger?.LogDebug("Server has returned less objects ({Count}) than requested ({Length}), repeating for remaining...", objects.Count, currentRange.Length);
				currentRange = currentRange.Subrange(objects.Count);
				retryCount = RetryCount;
			}
		}
		return results;
	}

	/// <summary>
	/// Writes Modbus objects to the server. The specified objects will be processed in one or
	/// multiple Modbus requests as necessary. If the device confirms less objects than requested,
	/// the remaining will be written separately until complete. If the device rejects the function
	/// codes to write multiple objects, each is written individually.
	/// </summary>
	/// <param name="deviceId">The remote device ID.</param>
	/// <param name="objects">The objects to write.</param>
	/// <param name="cancellationToken">A cancellation token used to propagate notification that
	///   this operation should be canceled.</param>
	/// <returns>The task object representing the asynchronous operation.</returns>
	/// <exception cref="ModbusException">When the Modbus operation failed because of a negative
	///   response or protocol violation. The reason will be indicated in the exception's
	///   <see cref="ModbusException.Code"/> property.</exception>
	public async Task Write(int deviceId, ModbusCollection objects, CancellationToken cancellationToken = default)
	{
		if (IsDisposed)
			throw new ObjectDisposedException(nameof(ModbusClient));

		int limitLength = ModbusRange.GetMaxLength(objects.ObjectType);
		int maxRequestLength = MaxRequestLength;
		if (maxRequestLength <= 0 || maxRequestLength > limitLength)
			maxRequestLength = limitLength;

		var ranges = objects.GetRanges(maxRequestLength, 0);
		foreach (var range in ranges)
		{
			var currentRange = range;
			int retryCount = RetryCount;
			while (true)
			{
				bool writeSingle = (AlwaysWriteSingle || range.Length == 1) && !AlwaysWriteMultiple;
				var requestBody = writeSingle ?
					MakeSingleWriteRequest((byte)deviceId, range.StartAddress, objects) :
					MakeMultipleWriteRequest((byte)deviceId, range.StartAddress, range.Length, objects);
				var responseBody = await SendRequest(requestBody, cancellationToken, writeSingle: writeSingle).NoSync();
				if (responseBody.IsEmpty)
				{
					retryCount = RetryCount;
					continue;
				}
				int writtenCount = 1;
				try
				{
					if (writeSingle)
						DecodeSingleWriteResponse(responseBody, currentRange.StartAddress, objects);
					else
						writtenCount = DecodeMultipleWriteResponse(responseBody, currentRange.StartAddress, range.Length);
				}
				catch (ModbusException ex) when (ex.Code == ModbusError.IllegalFunction && !writeSingle && !AlwaysWriteSingle && !AlwaysWriteMultiple)
				{
					logger?.LogDebug("Server does not support writing multiple objects, switching to single write mode.");
					AlwaysWriteSingle = true;
					retryCount = RetryCount;
					continue;
				}
				catch (ModbusException ex) when (ex.Code == ModbusError.IllegalFunction && writeSingle && !AlwaysWriteSingle && !AlwaysWriteMultiple)
				{
					logger?.LogDebug("Server does not support writing single objects, switching to multiple write mode.");
					AlwaysWriteMultiple = true;
					retryCount = RetryCount;
					continue;
				}
				catch (ModbusException ex) when (ex.Code == ModbusError.ServerDeviceBusy)
				{
					if (retryCount-- <= 0)
						throw;
					// Repeat that later
					logger?.LogDebug("Server is busy, retrying later...");
					await Task.Delay(RandomizeTimeSpan(BusyRetryDelay), cancellationToken).NoSync();
					continue;
				}
				if (writtenCount >= currentRange.Length)
					break;
				// VIOLATION: Repeat for remaining objects not included in the response
				currentRange = currentRange.Subrange(writtenCount);
				retryCount = RetryCount;
			}
		}
	}

	/// <summary>
	/// Reads the device identification from a Modbus server.
	/// </summary>
	/// <param name="deviceId">The remote device ID.</param>
	/// <param name="cancellationToken">A cancellation token used to propagate notification that
	///   this operation should be canceled.</param>
	/// <returns>A dictionary containing the device identification objects and their string values.</returns>
	public async Task<IDictionary<ModbusDeviceIdentificationObject, string>> ReadDeviceIdentification(int deviceId, CancellationToken cancellationToken = default)
	{
		if (IsDisposed)
			throw new ObjectDisposedException(nameof(ModbusClient));

		var results = new Dictionary<ModbusDeviceIdentificationObject, string>();
		byte maxCategory = 1;
		for (byte category = 1; category <= maxCategory; category++)
		{
			int retryCount = RetryCount;
			var nextObjectId = (ModbusDeviceIdentificationObject)0;
			while (true)
			{
				var requestBody = MakeReadIdRequest((byte)deviceId, category, nextObjectId);
				var responseBody = await SendRequest(requestBody, cancellationToken).NoSync();
				try
				{
					DecodeReadIdResponse(responseBody, results, out byte conformityLevel, out bool moreFollows, out var newNextObjectId);
					byte newMaxCategory = (byte)(conformityLevel & 0x7f);
					if (newMaxCategory != maxCategory)
					{
						if (logger?.IsEnabled(LogLevel.Trace) == true)
							logger?.LogTrace("Device indicated support of data category {NewMaxCategory}, was {MaxCategory}.", newMaxCategory, maxCategory);
						maxCategory = newMaxCategory;
					}
					if (!moreFollows)
						break;
					if (newNextObjectId <= nextObjectId)
					{
						// Not incrementing the next read ID would return in an endless loop, stop here
						throw new ModbusException(
							ModbusError.ReadDeviceIdentificationLoop,
							"The read device identification function suggests reading the data in an endless loop. It is unclear whether the data can be read completely.");
					}
					nextObjectId = newNextObjectId;
				}
				catch (ModbusException ex) when (ex.Code == ModbusError.ServerDeviceBusy)
				{
					if (retryCount-- <= 0)
						throw;
					// Repeat that later
					logger?.LogDebug("Server is busy, retrying later...");
					await Task.Delay(RandomizeTimeSpan(BusyRetryDelay), cancellationToken).NoSync();
					continue;
				}
				catch (ModbusException ex) when (ex.Code == ModbusError.IllegalDataAddress && (category == 2 || category == 3) && nextObjectId == 0)
				{
					// The other categories might need to start reading at their specific first object, try that.
					// VIOLATION: This should not be necessary by the Modbus spec but helps with the Solvimus M-Bus gateway
					if (category == 2)
						nextObjectId = ModbusDeviceIdentificationObject.VendorUrl;
					if (category == 3)
						nextObjectId = ModbusDeviceIdentificationObject.FirstPrivateObject;
				}
				// Repeat for remaining objects not included in the response (this is expected and
				// described in the Modbus specification)
				retryCount = RetryCount;
			}
		}
		return results;
	}

	#endregion Public client methods

	#region Read request/response coding

	private static ReadOnlyMemory<byte> MakeReadRequest(byte deviceId, ModbusObjectType objectType, ushort startAddress, ushort count)
	{
		byte[] bytes = new byte[6];
		bytes[0] = deviceId;
		bytes[1] = objectType switch
		{
			ModbusObjectType.Coil => (byte)FunctionCode.ReadCoils,
			ModbusObjectType.DiscreteInput => (byte)FunctionCode.ReadDiscreteInputs,
			ModbusObjectType.HoldingRegister => (byte)FunctionCode.ReadHoldingRegisters,
			ModbusObjectType.InputRegister => (byte)FunctionCode.ReadInputRegisters,
			_ => throw new ArgumentException("Invalid Modbus object type: " + objectType)
		};
		bytes[2] = (byte)(startAddress >> 8);
		bytes[3] = (byte)(startAddress & 0xff);
		bytes[4] = (byte)(count >> 8);
		bytes[5] = (byte)(count & 0xff);
		return bytes;
	}

	private ModbusCollection DecodeReadResponse(ReadOnlyMemory<byte> responseBody, ModbusObjectType objectType, ushort startAddress, ushort count)
	{
		if (responseBody.Length < 3)
			throw new ModbusException(ModbusError.IncompleteResponse, $"Response is too short: {responseBody.Length} bytes starting at device ID");
		byte functionCode = responseBody.Span[1];
		if ((functionCode & 0x80) != 0)
		{
			var errorCode = (ModbusError)responseBody.Span[2];
			throw new ModbusException(errorCode);
		}
		byte dataLength = responseBody.Span[2];
		if (responseBody.Length < 3 + dataLength)
			throw new ModbusException(ModbusError.IncompleteResponse, $"Response data ends unexpectedly after {responseBody.Length - 3} instead of {dataLength} bytes.");

		var objects = new ModbusCollection(objectType);
		if (objectType == ModbusObjectType.Coil || objectType == ModbusObjectType.DiscreteInput)
		{
			for (int index = 0; index < dataLength; index++)
			{
				byte value = responseBody.Span[3 + index];
				for (int bit = 0; bit < 8 && index * 8 + bit < count; bit++)
				{
					bool state = (value & (1 << bit)) != 0;
					objects.SetState(startAddress + index * 8 + bit, state);
				}
			}
		}
		else if (objectType == ModbusObjectType.HoldingRegister || objectType == ModbusObjectType.InputRegister)
		{
			for (int addressOffset = 0; addressOffset < dataLength / 2; addressOffset++)
			{
				ushort value = (ushort)(responseBody.Span[3 + addressOffset * 2] << 8 | responseBody.Span[3 + addressOffset * 2 + 1]);
				objects.SetUInt16(startAddress + addressOffset, value);
				if (logger?.IsEnabled(LogLevel.Trace) == true)
					logger?.LogTrace("Register {Address}: {Value} ({HexValue})", startAddress + addressOffset, value, $"0x{value:x4}");
			}
		}
		else
		{
			throw new ArgumentException("Invalid Modbus object type: " + objectType);
		}
		return objects;
	}

	#endregion Read request/response coding

	#region Write request/response coding

	private static ReadOnlyMemory<byte> MakeSingleWriteRequest(byte deviceId, ushort startAddress, ModbusCollection objects)
	{
		byte[] bytes = new byte[6];
		bytes[0] = deviceId;
		bytes[1] = objects.ObjectType switch
		{
			ModbusObjectType.Coil => (byte)FunctionCode.WriteCoil,
			ModbusObjectType.HoldingRegister => (byte)FunctionCode.WriteHoldingRegister,
			_ => throw new ArgumentException("Invalid Modbus object type: " + objects.ObjectType)
		};
		bytes[2] = (byte)(startAddress >> 8);
		bytes[3] = (byte)(startAddress & 0xff);
		if (objects.ObjectType == ModbusObjectType.Coil)
		{
			bool state = objects.GetState(startAddress);
			bytes[4] = (byte)(state ? 0xff : 0);
			bytes[5] = 0;
		}
		else if (objects.ObjectType == ModbusObjectType.HoldingRegister)
		{
			ushort value = objects.GetUInt16(startAddress);
			bytes[4] = (byte)(value >> 8);
			bytes[5] = (byte)(value & 0xff);
		}
		return bytes;
	}

	private void DecodeSingleWriteResponse(ReadOnlyMemory<byte> responseBody, ushort startAddress, ModbusCollection objects)
	{
		if (responseBody.Length < 3)
			throw new ModbusException(ModbusError.IncompleteResponse, $"Response is too short: {responseBody.Length} bytes starting at device ID");
		byte functionCode = responseBody.Span[1];
		if ((functionCode & 0x80) != 0)
		{
			var errorCode = (ModbusError)responseBody.Span[2];
			throw new ModbusException(errorCode);
		}
		if (responseBody.Length < 6)
			throw new ModbusException(ModbusError.IncompleteResponse, $"Response is too short: {responseBody.Length} bytes starting at device ID");
		if (responseBody.Length > 6 && logger?.IsEnabled(LogLevel.Trace) == true)
			logger?.LogTrace("Response is too long: {Length} bytes, expected 6", responseBody.Length);
		ushort rcvdStartAddress = (ushort)(responseBody.Span[2] << 8 | responseBody.Span[3]);
		if (rcvdStartAddress != startAddress)
			throw new ModbusException(ModbusError.AddressMismatch, $"Response has different start address confirmed ({rcvdStartAddress}) than requested ({startAddress}).");

		if (objects.ObjectType == ModbusObjectType.Coil)
		{
			bool state = objects.GetState(startAddress);
			ushort rcvdValue = (ushort)(responseBody.Span[4] << 8 | responseBody.Span[5]);
			if ((rcvdValue != 0) != state)
				throw new ModbusException(ModbusError.WriteMismatch, $"Response has different state confirmed ({rcvdValue != 0}) than requested ({state}).");
		}
		else if (objects.ObjectType == ModbusObjectType.HoldingRegister)
		{
			ushort value = objects.GetUInt16(startAddress);
			ushort rcvdValue = (ushort)(responseBody.Span[4] << 8 | responseBody.Span[5]);
			if (rcvdValue != value)
				throw new ModbusException(ModbusError.WriteMismatch, $"Response has different value confirmed ({rcvdValue}) than requested ({value}).");
		}
		else
		{
			throw new ArgumentException("Invalid Modbus object type: " + objects.ObjectType);
		}
	}

	private static ReadOnlyMemory<byte> MakeMultipleWriteRequest(byte deviceId, ushort startAddress, ushort count, ModbusCollection objects)
	{
		byte dataLength = objects.ObjectType switch
		{
			// Each coil takes 1 bit (8 per byte)
			ModbusObjectType.Coil => (byte)((count + 7) / 8),
			// Each register takes 2 bytes
			ModbusObjectType.HoldingRegister => (byte)(count * 2),
			_ => throw new ArgumentException("Invalid Modbus object type: " + objects.ObjectType)
		};

		byte[] bytes = new byte[7 + dataLength];
		bytes[0] = deviceId;
		bytes[1] = objects.ObjectType switch
		{
			ModbusObjectType.Coil => (byte)FunctionCode.WriteCoils,
			ModbusObjectType.HoldingRegister => (byte)FunctionCode.WriteHoldingRegisters,
			_ => 0   // exception thrown above
		};
		bytes[2] = (byte)(startAddress >> 8);
		bytes[3] = (byte)(startAddress & 0xff);
		bytes[4] = (byte)(count >> 8);
		bytes[5] = (byte)(count & 0xff);
		bytes[6] = dataLength;

		int index = 7;
		if (objects.ObjectType == ModbusObjectType.Coil)
		{
			// State bits are written in address order from least to most significant bit, one byte after the other
			for (int i = 0; i < count; i += 8)
			{
				byte value = 0;
				for (int j = 0; j < 8 && i + j < count; j++)
				{
					bool state = objects.GetState(startAddress + i + j);
					if (state)
						value |= (byte)(1 << j);
				}
				bytes[index++] = value;
			}
		}
		else if (objects.ObjectType == ModbusObjectType.HoldingRegister)
		{
			// Values are written in 2 bytes each
			for (int i = 0; i < count; i++)
			{
				ushort value = objects.GetUInt16(startAddress + i);
				bytes[index++] = (byte)(value >> 8);
				bytes[index++] = (byte)(value & 0xff);
			}
		}
		return bytes;
	}

	private int DecodeMultipleWriteResponse(ReadOnlyMemory<byte> responseBody, ushort startAddress, ushort count)
	{
		if (responseBody.Length < 3)
			throw new ModbusException(ModbusError.IncompleteResponse, $"Response is too short: {responseBody.Length} bytes starting at device ID");
		byte functionCode = responseBody.Span[1];
		if ((functionCode & 0x80) != 0)
		{
			var errorCode = (ModbusError)responseBody.Span[2];
			throw new ModbusException(errorCode);
		}
		if (responseBody.Length < 6)
			throw new ModbusException(ModbusError.IncompleteResponse, $"Response is too short: {responseBody.Length} bytes starting at device ID");
		if (responseBody.Length > 6 && logger?.IsEnabled(LogLevel.Trace) == true)
			logger?.LogTrace("Response is too long: {Length} bytes, expected 6", responseBody.Length);
		ushort rcvdStartAddress = (ushort)(responseBody.Span[2] << 8 | responseBody.Span[3]);
		if (rcvdStartAddress != startAddress)
			throw new ModbusException(ModbusError.AddressMismatch, $"Response has different start address confirmed ({rcvdStartAddress}) than requested ({startAddress}).");
		ushort rcvdCount = (ushort)(responseBody.Span[4] << 8 | responseBody.Span[5]);
		if (rcvdCount == 0)
			throw new ModbusException(ModbusError.WriteMismatch, $"Response confirms zero written registers (requested {count}).");
		return rcvdCount;
	}

	#endregion Write request/response coding

	#region Read device identification coding

	private static ReadOnlyMemory<byte> MakeReadIdRequest(byte deviceId, byte category, ModbusDeviceIdentificationObject startObjectId)
	{
		byte[] bytes = new byte[5];
		bytes[0] = deviceId;
		bytes[1] = (byte)FunctionCode.ReadDeviceIdentification;
		bytes[2] = 14;
		bytes[3] = category;
		bytes[4] = (byte)startObjectId;
		return bytes;
	}

	private void DecodeReadIdResponse(
		ReadOnlyMemory<byte> responseBody,
		Dictionary<ModbusDeviceIdentificationObject, string> values,
		out byte conformityLevel,
		out bool moreFollows,
		out ModbusDeviceIdentificationObject nextObjectId)
	{
		if (responseBody.Length < 3)
			throw new ModbusException(ModbusError.IncompleteResponse, $"Response is too short: {responseBody.Length} bytes starting at device ID");
		byte functionCode = responseBody.Span[1];
		if ((functionCode & 0x80) != 0)
		{
			var errorCode = (ModbusError)responseBody.Span[2];
			if (errorCode > ModbusError.MaxSpecValue &&
				responseBody.Length >= 4 &&
				(ModbusError)responseBody.Span[3] <= ModbusError.MaxSpecValue)
			{
				// VIOLATION: Index 2 is correct by the Modbus spec but the Solvimus M-Bus gateway
				// also echoes the number "14" (which happens not to be a valid error code) and sets
				// the error code at index 3! (TCP transport includes this extra data, Serial would
				// not read it, but that's irrelevant here.)
				errorCode = (ModbusError)responseBody.Span[3];
			}
			throw new ModbusException(errorCode);
		}
		if (responseBody.Length < 8)
			throw new ModbusException(ModbusError.IncompleteResponse, $"Response is too short: {responseBody.Length} bytes starting at device ID");
		conformityLevel = responseBody.Span[4];
		moreFollows = responseBody.Span[5] != 0;
		nextObjectId = (ModbusDeviceIdentificationObject)responseBody.Span[6];
		// Number of objects in the response seems unclear, the specification description and its
		// examples don't match. (The Modbus specification is of poor quality in general, at least
		// that section.)
		//byte count = responseBody[7];
		// Better ignore this value and continue reading objects as long as more data is available.

		int index = 8;
		for (int i = 0; index < responseBody.Length; i++)
		{
			if (responseBody.Length < index + 2)
				throw new ModbusException(ModbusError.IncompleteResponse, $"Response data ends unexpectedly after {responseBody.Length} bytes in the object #{i + 1}.");
			var objectId = (ModbusDeviceIdentificationObject)responseBody.Span[index++];
			byte length = responseBody.Span[index++];
			if (responseBody.Length < index + length)
				throw new ModbusException(ModbusError.IncompleteResponse, $"Response data ends unexpectedly after {responseBody.Length} instead of {index + length} bytes in the object #{i + 1}.");
			string value = length > 0 ?
				Encoding.ASCII.GetString(responseBody.Span[index..(index + length)]) :
				"";
			index += length;
			values[objectId] = value;
			if (logger?.IsEnabled(LogLevel.Trace) == true)
			{
				// Replace control characters by \x00 to \x1f notation for logging
				string dumpValue = Regex.Replace(value, @"[\x00-\x1f]", m => $"\\x{m.Groups[0].Value[0]:x2}");
				logger?.LogTrace("Device identification object {ObjectId}: \"{Data}\"", objectId, dumpValue);
			}
		}
	}

	#endregion Read device identification coding

	#region Internal request methods

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068:CancellationToken parameters must come last", Justification = "writeSingle is less commonly used")]
	private async Task<ReadOnlyMemory<byte>> SendRequest(ReadOnlyMemory<byte> requestBody, CancellationToken cancellationToken = default, bool? writeSingle = null)
	{
		await connectionSemaphore.WaitAsync(cancellationToken).NoSync();
		try
		{
			int retryCount = RetryCount;
			while (true)
			{
				try
				{
					logger?.LogDebug("Sending request...");
					using var linkedCts = TaskHelper.CreateTimeoutCancellationTokenSource(cancellationToken, ResponseTimeout);
					ReadOnlyMemory<byte> responseBody;
					try
					{
						var connection = await GetConnection(linkedCts.Token).NoSync();
						responseBody = await connection.SendRequest(requestBody, linkedCts.Token).NoSync();
					}
					catch (OperationCanceledException ex)
					{
						throw TaskHelper.GetOperationCanceledOrTimeoutException(ex, cancellationToken);
					}
					logger?.LogTrace("Response ready");
					return responseBody;
				}
				catch (Exception ex)
				{
					if (retryCount-- <= 0 || ex is OperationCanceledException)
						throw;
					if (ex is TimeoutException)
					{
						if (writeSingle == true && !AlwaysWriteSingle && !AlwaysWriteMultiple)
						{
							logger?.LogDebug("Server does not respond to writing single objects, switching to multiple write mode.");
							AlwaysWriteMultiple = true;
							// VIOLATION: Indicate retry with new request
							return default;
						}
						if (writeSingle == false && !AlwaysWriteSingle && !AlwaysWriteMultiple)
						{
							logger?.LogDebug("Server does not respond to writing multiple objects, switching to single write mode.");
							AlwaysWriteSingle = true;
							// VIOLATION: Indicate retry with new request
							return default;
						}
						logger?.LogWarning("Retrying after timeout");
					}
					else
					{
						logger?.LogWarning("Retrying after Exception: {Message}", ex.Message);
						await Task.Delay(RandomizeTimeSpan(ExceptionRetryDelay), cancellationToken).NoSync();
					}
				}
			}
		}
		finally
		{
			if (IdleTimeout.TotalMilliseconds > 0)
			{
				connectionIdleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
			}
			else
			{
				// Close connection immediately
				connection!.Close();
				connection = null;
			}
			connectionSemaphore.Release();
		}
	}

	// must be called within semaphore
	private async Task<IModbusConnection> GetConnection(CancellationToken cancellationToken = default)
	{
		if (connection?.IsOpen != true)
		{
			connection = await connectionFactory.GetConnection(logger, cancellationToken).NoSync();
		}
		return connection;
	}

	private void OnConnectionIdleTimer(object? state)
	{
		connectionSemaphore.Wait();
		try
		{
			if (connection?.IsOpen == true)
			{
				connection.Close();
				connection = null;
			}
		}
		finally
		{
			connectionSemaphore.Release();
		}
	}

	/// <summary>
	/// Applies a small extra time to a regular time span to avoid multiple concurrent clients to
	/// always hit the target at the same regular retry interval.
	/// </summary>
	/// <param name="originalTimeSpan">The original <see cref="TimeSpan"/> to randomize.</param>
	/// <returns>
	/// The <paramref name="originalTimeSpan"/>, incremented by a short random time if it is not
	/// <see cref="TimeSpan.Zero"/> or <see cref="Timeout.Infinite"/> which are passed through
	/// unchanged.
	/// </returns>
	private static TimeSpan RandomizeTimeSpan(TimeSpan originalTimeSpan)
	{
		if (originalTimeSpan > TimeSpan.Zero && originalTimeSpan != Timeout.InfiniteTimeSpan)
		{
			return originalTimeSpan + TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * 50);
		}
		return originalTimeSpan;
	}

	#endregion Internal request methods

	#region IDisposable implementation

	/// <summary>
	/// Gets a value indicating whether this instance is disposed.
	/// </summary>
	public bool IsDisposed { get; private set; }

	/// <summary>
	/// Disposes this <see cref="ModbusClient"/> instance and closes the connection.
	/// </summary>
	public void Dispose()
	{
		if (!IsDisposed)
		{
			connectionSemaphore.Dispose();
			connection?.Close();
			connection = null;
			connectionIdleTimer.Dispose();
			IsDisposed = true;
			GC.SuppressFinalize(this);
		}
	}

	#endregion IDisposable implementation
}
