using System.Diagnostics;
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

	private readonly Stopwatch loadStopwatch = Stopwatch.StartNew();
	private long loadStartTimeUs;
	private long loadBusyTimeUs;

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
	/// Gets or sets a value indicating whether responses from a different device ID than requested
	/// are accepted. Default is false, so such responses are rejected with
	/// <see cref="ModbusError.DeviceIdMismatch"/>.
	/// </summary>
	public bool IgnoreDeviceIdMismatch { get; init; }

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

	#region Events

	/// <summary>
	/// Raised when a request is being sent.
	/// </summary>
	public event EventHandler<RequestEventArgs>? SendingRequest;

	/// <summary>
	/// Raised when a response to a request has been received.
	/// </summary>
	public event EventHandler<ResponseEventArgs>? ResponseReceived;

	/// <summary>
	/// Defines data for the <see cref="SendingRequest"/> event.
	/// </summary>
	public class RequestEventArgs : EventArgs
	{
		/// <summary>The device ID.</summary>
		public byte? DeviceId { get; set; }
		/// <summary>The function code.</summary>
		public string? FunctionCode { get; set; }
		/// <summary>The start address, in registers or coils.</summary>
		public ushort? StartAddress { get; set; }
		/// <summary>The number of requested objects (registers or coils).</summary>
		public ushort? Count { get; set; }
		/// <summary>The data value to write.</summary>
		public ushort? Data { get; set; }
		/// <summary>The data values to write.</summary>
		public ushort[]? DataArray { get; set; }
		/// <summary>The category (for Read Device Identification).</summary>
		public int? Category { get; set; }
		/// <summary>The start object ID (for Read Device Identification).</summary>
		public string? StartObjectId { get; set; }
	}

	/// <summary>
	/// Defines data for the <see cref="ResponseReceived"/> event.
	/// </summary>
	public class ResponseEventArgs : EventArgs
	{
		/// <summary>The device ID.</summary>
		public byte? DeviceId { get; set; }
		/// <summary>The function code.</summary>
		public string? FunctionCode { get; set; }
		/// <summary>The error code, if indicated.</summary>
		public string? ErrorCode { get; set; }
		/// <summary>The start address, in registers or coils.</summary>
		public ushort? StartAddress { get; set; }
		/// <summary>The number of confirmed objects (registers or coils).</summary>
		public ushort? Count { get; set; }
		/// <summary>The read or confirmed data value.</summary>
		public ushort? Data { get; set; }
		/// <summary>The read data values.</summary>
		public ushort[]? DataArray { get; set; }
		/// <summary>The read ID objects (for Read Device Identification).</summary>
		public Dictionary<string, string>? Objects { get; set; }
	}

	#endregion Events

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
	public Task<ModbusCollection> Read(
		ModbusObjectType objectType,
		int deviceId,
		ModbusRange range,
		CancellationToken cancellationToken = default) =>
		Read(objectType, deviceId, [range], cancellationToken);

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
	public async Task<ModbusCollection> Read(
		ModbusObjectType objectType,
		int deviceId,
		IEnumerable<ModbusRange> ranges,
		CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(IsDisposed, this);

		int limitLength = ModbusRange.GetMaxReadLength(objectType);
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
				var requestEventArgs = SendingRequest != null ? new RequestEventArgs() : null;
				var requestBody = MakeReadRequest(
					(byte)deviceId,
					objectType,
					currentRange.StartAddress,
					currentRange.Length,
					requestEventArgs);
				ModbusCollection objects;
				try
				{
					objects = await SendRequest(
						requestBody,
						requestEventArgs,
						(responseBody, responseEventArgs) => DecodeReadResponse(
							responseBody,
							objectType,
							currentRange.StartAddress,
							currentRange.Length,
							responseEventArgs),
						cancellationToken).NoSync();
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
					logger?.LogDebug(
						"Server has returned less objects ({Count}) than requested ({Length}), repeating for remaining...",
						objects.Count,
						currentRange.Length);
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
		ObjectDisposedException.ThrowIf(IsDisposed, this);

		int limitLength = ModbusRange.GetMaxWriteLength(objects.ObjectType);
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
				var requestEventArgs = SendingRequest != null ? new RequestEventArgs() : null;
				bool writeSingle = (AlwaysWriteSingle || range.Length == 1) && !AlwaysWriteMultiple;
				var requestBody = writeSingle ?
					MakeSingleWriteRequest(
						(byte)deviceId, currentRange.StartAddress, objects, requestEventArgs) :
					MakeMultipleWriteRequest(
						(byte)deviceId,
						currentRange.StartAddress,
						currentRange.Length,
						objects,
						requestEventArgs);
				int? writtenCount;
				try
				{
					writtenCount = await SendRequest<int?>(
						requestBody,
						requestEventArgs,
						(responseBody, responseEventArgs) =>
						{
							if (writeSingle)
							{
								DecodeSingleWriteResponse(
									responseBody,
									currentRange.StartAddress,
									objects,
									responseEventArgs);
								return 1;
							}
							return DecodeMultipleWriteResponse(
								responseBody,
								currentRange.StartAddress,
								currentRange.Length,
								responseEventArgs);
						},
						cancellationToken,
						writeSingle: writeSingle).NoSync();
				}
				catch (ModbusException ex) when (
					ex.Code == ModbusError.IllegalFunction &&
					!writeSingle &&
					!AlwaysWriteSingle &&
					!AlwaysWriteMultiple)
				{
					logger?.LogDebug("Server does not support writing multiple objects, switching to single write mode.");
					AlwaysWriteSingle = true;
					retryCount = RetryCount;
					continue;
				}
				catch (ModbusException ex) when (
					ex.Code == ModbusError.IllegalFunction &&
					writeSingle &&
					!AlwaysWriteSingle &&
					!AlwaysWriteMultiple)
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
				if (writtenCount == null)
				{
					// The write mode was switched, repeat the request in the new mode
					retryCount = RetryCount;
					continue;
				}
				if (writtenCount >= currentRange.Length)
					break;
				// VIOLATION: Repeat for remaining objects not included in the response
				currentRange = currentRange.Subrange(writtenCount.Value);
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
	public async Task<IDictionary<ModbusDeviceIdentificationObject, string>> ReadDeviceIdentification(
		int deviceId,
		CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(IsDisposed, this);

		var results = new Dictionary<ModbusDeviceIdentificationObject, string>();
		byte maxCategory = 1;
		for (byte category = 1; category <= maxCategory; category++)
		{
			int retryCount = RetryCount;
			var nextObjectId = (ModbusDeviceIdentificationObject)0;
			while (true)
			{
				var requestEventArgs = SendingRequest != null ? new RequestEventArgs() : null;
				var requestBody = MakeReadIdRequest(
					(byte)deviceId, category, nextObjectId, requestEventArgs);
				ReadIdStatus response;
				try
				{
					response = await SendRequest(
						requestBody,
						requestEventArgs,
						(responseBody, responseEventArgs) =>
						{
							DecodeReadIdResponse(
								responseBody,
								results,
								responseEventArgs,
								out byte conformityLevel,
								out bool moreFollows,
								out var newNextObjectId);
							return new ReadIdStatus(conformityLevel, moreFollows, newNextObjectId);
						},
						cancellationToken).NoSync();
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
				catch (ModbusException ex) when (
					ex.Code == ModbusError.IllegalDataAddress &&
					(category == 2 || category == 3) &&
					nextObjectId == 0)
				{
					// The other categories might need to start reading at their specific first object, try that.
					// VIOLATION: This should not be necessary by the Modbus spec but helps with the Solvimus M-Bus gateway
					if (category == 2)
						nextObjectId = ModbusDeviceIdentificationObject.VendorUrl;
					if (category == 3)
						nextObjectId = ModbusDeviceIdentificationObject.FirstPrivateObject;
					retryCount = RetryCount;
					continue;
				}
				byte newMaxCategory = (byte)(response.ConformityLevel & 0x7f);
				if (newMaxCategory != maxCategory)
				{
					if (logger?.IsEnabled(LogLevel.Trace) == true)
						logger?.LogTrace(
							"Device indicated support of data category {NewMaxCategory}, was {MaxCategory}.",
							newMaxCategory,
							maxCategory);
					maxCategory = newMaxCategory;
				}
				if (!response.MoreFollows)
					break;
				if (response.NextObjectId <= nextObjectId)
				{
					// Not incrementing the next read ID would return in an endless loop, stop here
					throw new ModbusException(
						ModbusError.ReadDeviceIdentificationLoop,
						"The read device identification function suggests reading the data in an endless loop. It is unclear whether the data can be read completely.");
				}
				nextObjectId = response.NextObjectId;
				// Repeat for remaining objects not included in the response (this is expected and
				// described in the Modbus specification)
				retryCount = RetryCount;
			}
		}
		return results;
	}

	/// <summary>
	/// Gets the connection load statistics since the last call of the method.
	/// </summary>
	/// <returns>A value between 0 and 1 that indicates how much of the available time the
	///   connection was busy.</returns>
	/// <remarks>
	/// Load is measured across connections. Times with no open connection are considered as zero
	/// load.
	/// </remarks>
	public double GetLoad()
	{
		connectionSemaphore.Wait();
		try
		{
			if (connection?.IsOpen == true)
			{
				loadBusyTimeUs += connection.GetBusyTimeUs();
			}
		}
		finally
		{
			connectionSemaphore.Release();
		}

		long nowUs = (long)loadStopwatch.Elapsed.TotalMicroseconds;
		long elapsedTimeUs = nowUs - loadStartTimeUs;
		loadStartTimeUs = nowUs;

		long busyTimeUs = loadBusyTimeUs;
		loadBusyTimeUs = 0;

		return elapsedTimeUs > 0 ? (double)busyTimeUs / elapsedTimeUs : 0;
	}

	#endregion Public client methods

	#region Read request/response coding

	private ReadOnlyMemory<byte> MakeReadRequest(
		byte deviceId,
		ModbusObjectType objectType,
		ushort startAddress,
		int count,
		RequestEventArgs? eventArgs)
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

		if (eventArgs != null)
		{
			eventArgs.DeviceId = deviceId;
			eventArgs.FunctionCode = ((FunctionCode)bytes[1]).ToString();
			eventArgs.StartAddress = startAddress;
			eventArgs.Count = (ushort)count;
		}
		return bytes;
	}

	private ModbusCollection DecodeReadResponse(
		ReadOnlyMemory<byte> responseBody,
		ModbusObjectType objectType,
		ushort startAddress,
		int count,
		ResponseEventArgs? eventArgs)
	{
		if (responseBody.Length < 3)
			throw new ModbusException(
				ModbusError.IncompleteResponse,
				$"Response is too short: {responseBody.Length} bytes starting at device ID");
		byte functionCode = responseBody.Span[1];
		if (eventArgs != null)
			eventArgs.FunctionCode = ((FunctionCode)functionCode).ToString();
		if ((functionCode & 0x80) != 0)
		{
			var errorCode = (ModbusError)responseBody.Span[2];
			if (eventArgs != null)
				eventArgs.ErrorCode = errorCode.ToString();
			throw new ModbusException(errorCode);
		}
		byte dataLength = responseBody.Span[2];
		if (responseBody.Length < 3 + dataLength)
			throw new ModbusException(
				ModbusError.IncompleteResponse,
				$"Response data ends unexpectedly after {responseBody.Length - 3} instead of {dataLength} bytes.");

		var objects = new ModbusCollection(objectType);
		if (objectType == ModbusObjectType.Coil || objectType == ModbusObjectType.DiscreteInput)
		{
			// VIOLATION: Ignore data bytes beyond the requested objects
			int usedLength = Math.Min(dataLength, (count + 7) / 8);
			LogIgnoredResponseData(dataLength, usedLength);
			if (eventArgs != null)
				eventArgs.DataArray = new ushort[Math.Min(dataLength * 8, count)];
			for (int index = 0; index < usedLength; index++)
			{
				byte value = responseBody.Span[3 + index];
				for (int bit = 0; bit < 8 && index * 8 + bit < count; bit++)
				{
					bool state = (value & (1 << bit)) != 0;
					objects.SetState(startAddress + index * 8 + bit, state);
					if (eventArgs != null)
						eventArgs.DataArray![index * 8 + bit] = state ? (ushort)1 : (ushort)0;
				}
			}
		}
		else if (objectType == ModbusObjectType.HoldingRegister || objectType == ModbusObjectType.InputRegister)
		{
			// VIOLATION: Ignore registers beyond the requested objects and an incomplete register
			int registerCount = Math.Min(dataLength / 2, count);
			LogIgnoredResponseData(dataLength, registerCount * 2);
			if (eventArgs != null)
				eventArgs.DataArray = new ushort[registerCount];
			for (int addressOffset = 0; addressOffset < registerCount; addressOffset++)
			{
				ushort value = (ushort)(responseBody.Span[3 + addressOffset * 2] << 8 | responseBody.Span[3 + addressOffset * 2 + 1]);
				objects.SetUInt16(startAddress + addressOffset, value);
				if (logger?.IsEnabled(LogLevel.Trace) == true)
					logger?.LogTrace(
						"Register {Address}: {Value} ({HexValue})",
						startAddress + addressOffset,
						value,
						$"0x{value:x4}");
				if (eventArgs != null)
					eventArgs.DataArray![addressOffset] = value;
			}
		}
		else
		{
			throw new ArgumentException("Invalid Modbus object type: " + objectType);
		}
		return objects;
	}

	/// <summary>
	/// Logs that a read response contains data bytes that were not decoded.
	/// </summary>
	/// <param name="dataLength">The number of data bytes in the response.</param>
	/// <param name="usedLength">The number of decoded data bytes.</param>
	private void LogIgnoredResponseData(int dataLength, int usedLength)
	{
		if (dataLength > usedLength && logger?.IsEnabled(LogLevel.Debug) == true)
			logger?.LogDebug(
				"Response contains {DataLength} data bytes, ignoring {IgnoredLength} bytes that were not requested or are incomplete",
				dataLength,
				dataLength - usedLength);
	}

	#endregion Read request/response coding

	#region Write request/response coding

	private ReadOnlyMemory<byte> MakeSingleWriteRequest(
		byte deviceId,
		ushort startAddress,
		ModbusCollection objects,
		RequestEventArgs? eventArgs)
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
		ushort value = 0;
		if (objects.ObjectType == ModbusObjectType.Coil)
		{
			bool state = objects.GetState(startAddress);
			value = state ? (ushort)0xff00 : (ushort)0;
			bytes[4] = (byte)(state ? 0xff : 0);
			bytes[5] = 0;
		}
		else if (objects.ObjectType == ModbusObjectType.HoldingRegister)
		{
			value = objects.GetUInt16(startAddress);
			bytes[4] = (byte)(value >> 8);
			bytes[5] = (byte)(value & 0xff);
		}

		if (eventArgs != null)
		{
			eventArgs.DeviceId = deviceId;
			eventArgs.FunctionCode = ((FunctionCode)bytes[1]).ToString();
			eventArgs.StartAddress = startAddress;
			eventArgs.Data = value;
		}
		return bytes;
	}

	private void DecodeSingleWriteResponse(
		ReadOnlyMemory<byte> responseBody,
		ushort startAddress,
		ModbusCollection objects,
		ResponseEventArgs? eventArgs)
	{
		if (responseBody.Length < 3)
			throw new ModbusException(
				ModbusError.IncompleteResponse,
				$"Response is too short: {responseBody.Length} bytes starting at device ID");
		byte functionCode = responseBody.Span[1];
		if (eventArgs != null)
			eventArgs.FunctionCode = ((FunctionCode)functionCode).ToString();
		if ((functionCode & 0x80) != 0)
		{
			var errorCode = (ModbusError)responseBody.Span[2];
			if (eventArgs != null)
				eventArgs.ErrorCode = errorCode.ToString();
			throw new ModbusException(errorCode);
		}
		if (responseBody.Length < 6)
			throw new ModbusException(
				ModbusError.IncompleteResponse,
				$"Response is too short: {responseBody.Length} bytes starting at device ID");
		if (responseBody.Length > 6 && logger?.IsEnabled(LogLevel.Trace) == true)
			logger?.LogTrace("Response is too long: {Length} bytes, expected 6", responseBody.Length);
		ushort rcvdStartAddress = (ushort)(responseBody.Span[2] << 8 | responseBody.Span[3]);
		if (eventArgs != null)
			eventArgs.StartAddress = rcvdStartAddress;
		if (rcvdStartAddress != startAddress)
			throw new ModbusException(
				ModbusError.AddressMismatch,
				$"Response has different start address confirmed ({rcvdStartAddress}) than requested ({startAddress}).");

		if (objects.ObjectType == ModbusObjectType.Coil)
		{
			bool state = objects.GetState(startAddress);
			ushort rcvdValue = (ushort)(responseBody.Span[4] << 8 | responseBody.Span[5]);
			if (eventArgs != null)
				eventArgs.Data = rcvdValue;
			if ((rcvdValue != 0) != state)
				throw new ModbusException(
					ModbusError.WriteMismatch,
					$"Response has different state confirmed ({rcvdValue != 0}) than requested ({state}).");
		}
		else if (objects.ObjectType == ModbusObjectType.HoldingRegister)
		{
			ushort value = objects.GetUInt16(startAddress);
			ushort rcvdValue = (ushort)(responseBody.Span[4] << 8 | responseBody.Span[5]);
			if (eventArgs != null)
				eventArgs.Data = rcvdValue;
			if (rcvdValue != value)
				throw new ModbusException(
					ModbusError.WriteMismatch,
					$"Response has different value confirmed ({rcvdValue}) than requested ({value}).");
		}
		else
		{
			throw new ArgumentException("Invalid Modbus object type: " + objects.ObjectType);
		}
	}

	private ReadOnlyMemory<byte> MakeMultipleWriteRequest(
		byte deviceId,
		ushort startAddress,
		int count,
		ModbusCollection objects,
		RequestEventArgs? eventArgs)
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
			// State bits are written in address order from least to most significant bit, one byte
			// after the other
			if (eventArgs != null)
				eventArgs.DataArray = new ushort[count];
			for (int i = 0; i < count; i += 8)
			{
				byte value = 0;
				for (int j = 0; j < 8 && i + j < count; j++)
				{
					bool state = objects.GetState(startAddress + i + j);
					if (state)
						value |= (byte)(1 << j);
					if (eventArgs != null)
						eventArgs.DataArray![i + j] = state ? (ushort)1 : (ushort)0;
				}
				bytes[index++] = value;
			}
		}
		else if (objects.ObjectType == ModbusObjectType.HoldingRegister)
		{
			// Values are written in 2 bytes each
			if (eventArgs != null)
				eventArgs.DataArray = new ushort[count];
			for (int i = 0; i < count; i++)
			{
				ushort value = objects.GetUInt16(startAddress + i);
				bytes[index++] = (byte)(value >> 8);
				bytes[index++] = (byte)(value & 0xff);
				if (eventArgs != null)
					eventArgs.DataArray![i] = value;
			}
		}

		if (eventArgs != null)
		{
			eventArgs.DeviceId = deviceId;
			eventArgs.FunctionCode = ((FunctionCode)bytes[1]).ToString();
			eventArgs.StartAddress = startAddress;
			eventArgs.Count = (ushort)count;
		}
		return bytes;
	}

	private int DecodeMultipleWriteResponse(
		ReadOnlyMemory<byte> responseBody,
		ushort startAddress,
		int count,
		ResponseEventArgs? eventArgs)
	{
		if (responseBody.Length < 3)
			throw new ModbusException(
				ModbusError.IncompleteResponse,
				$"Response is too short: {responseBody.Length} bytes starting at device ID");
		byte functionCode = responseBody.Span[1];
		if (eventArgs != null)
			eventArgs.FunctionCode = ((FunctionCode)functionCode).ToString();
		if ((functionCode & 0x80) != 0)
		{
			var errorCode = (ModbusError)responseBody.Span[2];
			if (eventArgs != null)
				eventArgs.ErrorCode = errorCode.ToString();
			throw new ModbusException(errorCode);
		}
		if (responseBody.Length < 6)
			throw new ModbusException(
				ModbusError.IncompleteResponse,
				$"Response is too short: {responseBody.Length} bytes starting at device ID");
		if (responseBody.Length > 6 && logger?.IsEnabled(LogLevel.Trace) == true)
			logger?.LogTrace("Response is too long: {Length} bytes, expected 6", responseBody.Length);
		ushort rcvdStartAddress = (ushort)(responseBody.Span[2] << 8 | responseBody.Span[3]);
		if (eventArgs != null)
			eventArgs.StartAddress = rcvdStartAddress;
		if (rcvdStartAddress != startAddress)
			throw new ModbusException(
				ModbusError.AddressMismatch,
				$"Response has different start address confirmed ({rcvdStartAddress}) than requested ({startAddress}).");
		ushort rcvdCount = (ushort)(responseBody.Span[4] << 8 | responseBody.Span[5]);
		if (eventArgs != null)
			eventArgs.Count = rcvdCount;
		if (rcvdCount == 0)
			throw new ModbusException(
				ModbusError.WriteMismatch,
				$"Response confirms zero written registers (requested {count}).");
		return rcvdCount;
	}

	#endregion Write request/response coding

	#region Read device identification coding

	private ReadOnlyMemory<byte> MakeReadIdRequest(
		byte deviceId,
		byte category,
		ModbusDeviceIdentificationObject startObjectId,
		RequestEventArgs? eventArgs)
	{
		byte[] bytes = new byte[5];
		bytes[0] = deviceId;
		bytes[1] = (byte)FunctionCode.ReadDeviceIdentification;
		bytes[2] = 14;
		bytes[3] = category;
		bytes[4] = (byte)startObjectId;

		if (eventArgs != null)
		{
			eventArgs.DeviceId = deviceId;
			eventArgs.FunctionCode = ((FunctionCode)bytes[1]).ToString();
			eventArgs.Category = category;
			eventArgs.StartObjectId = startObjectId.ToString() + " (" + (int)startObjectId + ")";
		}
		return bytes;
	}

	private void DecodeReadIdResponse(
		ReadOnlyMemory<byte> responseBody,
		Dictionary<ModbusDeviceIdentificationObject, string> values,
		ResponseEventArgs? eventArgs,
		out byte conformityLevel,
		out bool moreFollows,
		out ModbusDeviceIdentificationObject nextObjectId)
	{
		if (responseBody.Length < 3)
			throw new ModbusException(
				ModbusError.IncompleteResponse,
				$"Response is too short: {responseBody.Length} bytes starting at device ID");
		byte functionCode = responseBody.Span[1];
		if (eventArgs != null)
			eventArgs.FunctionCode = ((FunctionCode)functionCode).ToString();
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
			if (eventArgs != null)
				eventArgs.ErrorCode = errorCode.ToString();
			throw new ModbusException(errorCode);
		}
		if (responseBody.Length < 8)
			throw new ModbusException(
				ModbusError.IncompleteResponse,
				$"Response is too short: {responseBody.Length} bytes starting at device ID");
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
				throw new ModbusException(
					ModbusError.IncompleteResponse,
					$"Response data ends unexpectedly after {responseBody.Length} bytes in the object #{i + 1}.");
			var objectId = (ModbusDeviceIdentificationObject)responseBody.Span[index++];
			byte length = responseBody.Span[index++];
			if (responseBody.Length < index + length)
				throw new ModbusException(
					ModbusError.IncompleteResponse,
					$"Response data ends unexpectedly after {responseBody.Length} instead of {index + length} bytes in the object #{i + 1}.");
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
			if (eventArgs != null)
			{
				eventArgs.Objects ??= [];
				string key = objectId.ToString() + " (" + (int)objectId + ")";
				eventArgs.Objects[key] = value;
			}
		}
	}

	/// <summary>
	/// The status information of a read device identification response.
	/// </summary>
	/// <param name="ConformityLevel">The conformity level of the device.</param>
	/// <param name="MoreFollows">Indicates whether more objects can be requested.</param>
	/// <param name="NextObjectId">The ID of the object to request next if more follow.</param>
	private readonly record struct ReadIdStatus(
		byte ConformityLevel,
		bool MoreFollows,
		ModbusDeviceIdentificationObject NextObjectId);

	#endregion Read device identification coding

	#region Internal request methods

	/// <summary>
	/// Sends a request to the server and decodes the response, repeating the request after errors.
	/// </summary>
	/// <typeparam name="T">The type of the decoded response.</typeparam>
	/// <param name="requestBody">The request body to send.</param>
	/// <param name="requestEventArgs">The data for the <see cref="SendingRequest"/> event, or null
	///   if the event is not raised.</param>
	/// <param name="decodeResponse">Decodes the response body and fills the data for the
	///   <see cref="ResponseReceived"/> event if it is not null. It is only called for a response
	///   from the requested device and for the requested function.</param>
	/// <param name="cancellationToken">A cancellation token used to propagate notification that
	///   this operation should be canceled.</param>
	/// <param name="writeSingle">true for a request that writes a single object, false for a
	///   request that writes multiple objects, null for other requests.</param>
	/// <returns>The decoded response, or the default value if the write request should be repeated
	///   in the other write mode.</returns>
	/// <remarks>
	/// Connection errors and errors that suggest that the response does not belong to the request
	/// (see <see cref="IsOutOfSyncError"/>) are retried. Other <see cref="ModbusException"/>s, for
	/// exception responses or a different written value, are thrown without retrying, so that the
	/// caller can handle them. The <see cref="ResponseReceived"/> event is raised for each received
	/// response.
	/// </remarks>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068:CancellationToken parameters must come last",
		Justification = "writeSingle is less commonly used")]
	private async Task<T> SendRequest<T>(
		ReadOnlyMemory<byte> requestBody,
		RequestEventArgs? requestEventArgs,
		Func<ReadOnlyMemory<byte>, ResponseEventArgs?, T> decodeResponse,
		CancellationToken cancellationToken = default,
		bool? writeSingle = null)
	{
		await connectionSemaphore.WaitAsync(cancellationToken).NoSync();
		try
		{
			int retryCount = RetryCount;
			while (true)
			{
				var responseEventArgs = ResponseReceived != null ? new ResponseEventArgs() : null;
				try
				{
					logger?.LogDebug("Sending request...");
					using var linkedCts = TaskHelper.CreateTimeoutCancellationTokenSource(
						cancellationToken, ResponseTimeout);
					ReadOnlyMemory<byte> responseBody;
					try
					{
						var connection = await GetConnection(linkedCts.Token).NoSync();
						if (requestEventArgs != null)
							SendingRequest?.Invoke(this, requestEventArgs);
						if (responseEventArgs != null && requestEventArgs != null)
							responseEventArgs.DeviceId = requestEventArgs.DeviceId;
						responseBody = await connection.SendRequest(requestBody, linkedCts.Token).NoSync();
					}
					catch (OperationCanceledException ex)
					{
						throw TaskHelper.GetOperationCanceledOrTimeoutException(ex, cancellationToken);
					}
					logger?.LogTrace("Response ready");
					try
					{
						CheckResponseMatchesRequest(
							requestBody.Span, responseBody.Span, responseEventArgs);
						return decodeResponse(responseBody, responseEventArgs);
					}
					finally
					{
						if (responseEventArgs != null)
							ResponseReceived?.Invoke(this, responseEventArgs);
					}
				}
				catch (ModbusException ex) when (!IsOutOfSyncError(ex.Code))
				{
					// The response belongs to the request, the caller handles the reported error
					throw;
				}
				catch (Exception ex)
				{
					if (retryCount-- <= 0 || ex is OperationCanceledException)
					{
						if (ex is ModbusException modbusEx && IsOutOfSyncError(modbusEx.Code))
						{
							// This error suggests that the connection is out of sync somehow.
							// Close it and start fresh, maybe that helps to recover.
							logger?.LogWarning(
								"Connection seems out of sync (error code: {ErrorCode}), closing connection to recover.",
								modbusEx.Code);
							connection?.Close();
						}
						// Finally fail the request, we've done all we can
						throw;
					}
					if (ex is TimeoutException)
					{
						if (writeSingle == true && !AlwaysWriteSingle && !AlwaysWriteMultiple)
						{
							logger?.LogDebug("Server does not respond to writing single objects, switching to multiple write mode.");
							AlwaysWriteMultiple = true;
							// VIOLATION: Indicate retry with new request
							return default!;
						}
						if (writeSingle == false && !AlwaysWriteSingle && !AlwaysWriteMultiple)
						{
							logger?.LogDebug("Server does not respond to writing multiple objects, switching to single write mode.");
							AlwaysWriteSingle = true;
							// VIOLATION: Indicate retry with new request
							return default!;
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
			else if (connection != null)
			{
				// Close connection immediately
				loadBusyTimeUs += connection.GetBusyTimeUs();
				connection.Close();
				connection = null;
			}
			connectionSemaphore.Release();
		}
	}

	/// <summary>
	/// Returns a value indicating whether an error suggests that the received response does not
	/// belong to the request, so that the connection may be out of sync.
	/// </summary>
	/// <param name="code">The error code to check.</param>
	/// <returns>true for errors to retry and finally close the connection; otherwise,
	///   false.</returns>
	private static bool IsOutOfSyncError(ModbusError code) => code is
		ModbusError.CrcMismatch or
		ModbusError.AddressMismatch or
		ModbusError.IncompleteResponse or
		ModbusError.TransactionIdMismatch or
		ModbusError.DeviceIdMismatch or
		ModbusError.FunctionCodeMismatch;

	/// <summary>
	/// Checks that a response is from the requested device and for the requested function.
	/// </summary>
	/// <remarks>
	/// The function code of an exception response additionally has the exception bit set.
	/// Responses that are too short to contain the device ID and function code are left to the
	/// decoding methods, which report them as incomplete.
	/// </remarks>
	/// <exception cref="ModbusException">With <see cref="ModbusError.DeviceIdMismatch"/> or
	///   <see cref="ModbusError.FunctionCodeMismatch"/>.</exception>
	private void CheckResponseMatchesRequest(
		ReadOnlySpan<byte> requestBody,
		ReadOnlySpan<byte> responseBody,
		ResponseEventArgs? eventArgs)
	{
		if (responseBody.Length < 2)
			return;

		if (eventArgs != null)
			eventArgs.DeviceId = responseBody[0];
		if (responseBody[0] != requestBody[0])
		{
			if (IgnoreDeviceIdMismatch)
			{
				if (logger?.IsEnabled(LogLevel.Debug) == true)
					logger?.LogDebug(
						"Response device ID {RcvdDeviceId} does not match request device ID {DeviceId}.",
						responseBody[0],
						requestBody[0]);
			}
			else
			{
				throw new ModbusException(
					ModbusError.DeviceIdMismatch,
					$"Response device ID ({responseBody[0]}) does not match request ({requestBody[0]}).");
			}
		}
		if ((responseBody[1] & 0x7f) != requestBody[1])
			throw new ModbusException(
				ModbusError.FunctionCodeMismatch,
				$"Response function code ({responseBody[1] & 0x7f}) does not match request ({requestBody[1]}).");
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
				loadBusyTimeUs += connection.GetBusyTimeUs();
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
