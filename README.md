# Unclassified.Modbus

[![NuGet](https://img.shields.io/nuget/v/Unclassified.Modbus.svg)](https://www.nuget.org/packages/Unclassified.Modbus)

A Modbus library for .NET. It provides a client for Modbus TCP and Modbus RTU over serial lines, and a Modbus TCP server. The client takes care of protocol limits, retries and many quirks of real devices, so that applications can simply read and write typed values.

## Features

- Client for Modbus TCP and Modbus RTU (serial line, with RS-485 hardware support on Linux)
- Read coils, discrete inputs, holding registers and input registers
- Write coils and holding registers, with single or multiple objects per request
- Read the device identification (vendor name, product code, revision etc.)
- Typed access to 16, 32 and 64-bit integers, single and double precision floating point values, and 8-bit/Unicode strings
- Address ranges of any size and number, automatically split and combined into requests within the protocol limits
- Automatic retries after timeouts, busy devices and connection errors
- Works with many devices that deviate from the Modbus specification
- Modbus TCP server that passes read and write requests to your own handler class, also usable as a hosted service
- Asynchronous API with cancellation support and logging through `Microsoft.Extensions.Logging`

## Installation

```
dotnet add package Unclassified.Modbus
```

The library targets .NET 9 and .NET 10.

## Quick start

Read and write values over Modbus TCP:

```csharp
using Unclassified.Modbus;

var factory = new ModbusTcpConnectionFactory("192.168.1.10", 502, ignoreTransactionIdMismatch: false);
using var client = new ModbusClient(factory, logger: null);

// Read two float values from the holding registers 100-103 of device 1
var objects = await client.Read(ModbusObjectType.HoldingRegister, 1, ModbusRange.ForSingle(100, 2));
float value1 = objects.GetSingle(100);
float value2 = objects.GetSingle(102);

// Write a 32-bit integer to the holding registers 200-201 of device 1
var data = new ModbusCollection(ModbusObjectType.HoldingRegister);
data.SetInt32(200, 123456);
await client.Write(1, data);

// Read the device identification
var identification = await client.ReadDeviceIdentification(1);
string vendor = identification[ModbusDeviceIdentificationObject.VendorName];
```

For Modbus RTU, only the connection factory is different:

```csharp
using System.IO.Ports;

var factory = new ModbusSerialConnectionFactory("/dev/ttyUSB0", 9600, Parity.None);
```

The repository contains two console apps that use the library. Their source code also serves as a usage example for the library.

- `ModbusClientDemo`: an interactive client to read and write values of a device. Start it without arguments to see its command line options, e.g. `-tcp <host>[:<port>]` or `-serial <port>[@<baudrate>[<parity>[<stopbits>]]]`. After starting, it lists its commands with examples, like `h100-103:f32` to read two float values from holding registers.
- `ModbusServerDemo`: a Modbus TCP server that keeps all objects in memory; start it with an invalid argument like `-help` to see its options.

## Concepts

### Object types

| Type              | Size   | Access       |
|-------------------|--------|--------------|
| `DiscreteInput`   | 1 bit  | read         |
| `Coil`            | 1 bit  | read, write  |
| `InputRegister`   | 16 bit | read         |
| `HoldingRegister` | 16 bit | read, write  |

### Addresses and device IDs

Addresses are the **0-based protocol addresses from 0 to 65535** that are sent in the requests. Device documentation often uses the register numbers from the traditional notation instead, like 40001 for the first holding register; these must be converted (40001 is the holding register address 0).

The device ID is the unit identifier in Modbus TCP or the slave address in Modbus RTU. It is passed to each client method.

### Ranges

A `ModbusRange` covers the addresses from `StartAddress` to `EndAddress`, both inclusive. The methods `ForInt32`, `ForSingle`, `ForString8` etc. create a range for a number of values of a data type.

### Collections and data types

A `ModbusCollection` contains objects of a single type by address. It is returned by `Read` and passed to `Write`. Its methods like `GetUInt16`, `GetInt32`, `GetDouble`, `GetString8` and the corresponding `Set` methods convert between typed values and the objects at consecutive addresses. Bit objects are accessed with `GetState` and `SetState`.

Values are stored in big-endian order: a 32 or 64-bit value starts with its most significant register at the lowest address. `String8` stores two single-byte characters per register (ASCII by default), `String16` one UTF-16 character per register. Devices that use another word order can be read with `GetUInt16` and converted by the application.

## Client behaviour

- **Requests:** `Read` accepts any number of ranges. They are combined and split into requests within the protocol limits of 125 registers or 2000 bits. `MaxRequestLength` sets a lower limit for devices that need it, `AllowedRequestWaste` allows reading unrequested addresses between ranges to save requests. `Write` sends objects at consecutive addresses in a single request where possible.
- **Connection:** The client connects on the first request and closes the connection after `IdleTimeout` without requests (default 7 seconds). It sends one request at a time; concurrent calls wait for each other.
- **Retries:** After timeouts, connection errors and responses that don't match the request, a request is repeated up to `RetryCount` times (default 4). If the device reports that it is busy, the request is repeated after `BusyRetryDelay`. Other exception responses of the device are not repeated.
- **Timeout:** `ResponseTimeout` (default 2 seconds) applies to each attempt of a request, including connecting.
- **Monitoring:** Pass an `ILogger` to the constructor to log the communication. The events `SendingRequest` and `ResponseReceived` provide the decoded request and response data, and `GetLoad` returns how busy the connection was.

All settings are init-only properties of `ModbusClient`.

## Error handling

A failed request throws a `ModbusException`. Its `Code` property tells the reason: values from 1 to 255 are exception codes returned by the device, like `IllegalDataAddress`; values above 255 are errors detected by the client, like `CrcMismatch` or `IncompleteResponse`.

Besides that, a `TimeoutException` indicates that the device did not respond in time, and an `OperationCanceledException` that the provided `CancellationToken` was cancelled. Connection failures throw the exceptions of the network or serial port classes, like `SocketException` or `IOException`.

## Serial connections

Modbus RTU uses 8 data bits and, by default, 19200 baud with even parity. As defined by the Modbus specification, 2 stop bits are used if parity is set to `None`, and 1 stop bit otherwise; a constructor parameter overrides this. On Linux, the serial driver is switched to RS-485 mode while the port is open.

## Server

`ModbusTcpListener` accepts Modbus TCP connections and passes each request to a `ModbusRequestHandler`. Derive from that class and override `ProvideReadData` and `ProcessWriteData` to provide and accept the objects for any device ID. Throw a `ModbusException` with a code like `IllegalDataAddress` to send an exception response. Set the listener's `Port` (502 is the standard port; 0 selects any free port) and call `StartAsync` and `StopAsync`, or register it as a hosted service. The server supports the function codes to read and write coils and registers.

## Non-conforming devices

Many devices deviate from the Modbus specification. The client handles these cases automatically:

- Responses with fewer objects than requested: the remaining objects are requested separately.
- Responses with more data than requested: the extra data is ignored.
- Devices that only support either the single or the multiple write function codes: the client detects this on the first write and keeps using the supported functions (see `AlwaysWriteSingle` and `AlwaysWriteMultiple`).
- Responses with a different device ID or Modbus TCP transaction ID: these can be accepted with `IgnoreDeviceIdMismatch` and the `ignoreTransactionIdMismatch` parameter of `ModbusTcpConnectionFactory`.
- Late Modbus TCP responses to earlier requests that have timed out: responses received before a request is sent, and responses with another transaction ID, are discarded while waiting for the matching response. Transaction IDs continue across connections, so that a late response on a new connection is detected as well.
- Read device identification: devices that require a specific start object per category, or that send the exception code at a different position.

## Not supported

- Modbus ASCII, Modbus RTU over TCP, Modbus UDP and Modbus TCP Security (TLS)
- Other function codes, like diagnostics, file records, FIFO queues or combined read/write
- Server over serial lines
- Read device identification in the server
- Configurable byte and word order

## License

MIT license. Copyright Yves Goergen.
