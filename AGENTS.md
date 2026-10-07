# AGENTS.md

This file provides guidance to AI agents when working with code in this repository.

## What this is

A Modbus library for .NET (NuGet package `Unclassified.Modbus`) implementing a client for Modbus TCP and Modbus RTU over serial lines, and a Modbus TCP server.

## Build

```
dotnet build Modbus.sln
dotnet test Unclassified.Modbus.Tests
```

The build also creates the NuGet package; the version is derived from git by `Unclassified.NetRevisionTask`. The build is currently free of warnings; keep it that way.

The tests use MSTest and reach the library's internal classes through `InternalsVisibleTo`.

## Architectural big picture

The solution contains the library `Unclassified.Modbus`, its tests in `Unclassified.Modbus.Tests`, and the console apps `ModbusClientDemo` and `ModbusServerDemo`, which show how to use it. `ModbusServerDemo` compiles `ModbusClientDemo/Util/ConsoleLogger.cs` as a linked file.

- **Message bodies:** Inside the library, a "request body" or "response body" is the device ID (unit ID) followed by the PDU (function code and data). Only the transport classes add and remove the framing (MBAP header for TCP, CRC for RTU).
- **Client:** `ModbusClient` is the public API (`Read`, `Write`, `ReadDeviceIdentification`). It encodes and decodes the PDUs, splits and merges address ranges within the protocol limits (`ModbusRange.Combine`), retries after busy responses, timeouts and other errors, and detects by trial whether a device only accepts the single or the multiple write function codes (`AlwaysWriteSingle`, `AlwaysWriteMultiple`). It holds one connection at a time behind a semaphore, so requests never overlap, and closes it after `IdleTimeout`.
- **Client transports:** Connections are created through `IModbusConnectionFactory`. The public `ModbusTcpConnectionFactory` and `ModbusSerialConnectionFactory` create the internal `ModbusTcpConnection` and `ModbusSerialConnection` (RTU). On Linux, the serial connection switches the driver to RS-485 mode (`Util/RS485.Linux.cs`).
- **Server:** Only Modbus TCP. `ModbusTcpListener` (also an `IHostedService`) accepts connections, reads MBAP frames through a `ByteBuffer` and passes each request body to a `ModbusRequestHandler`. Applications derive from that handler and override `ProvideReadData` and `ProcessWriteData`. `HandleRequest` returns the response length; 0 sends no response, -1 closes the connection.
- **Data model:** `ModbusCollection` holds `IModbusObject`s of a single `ModbusObjectType` by address, with typed accessors that span multiple registers (`GetInt32`, `SetSingle`, `GetString8` etc.). `ModbusRange` is an address range with an inclusive `EndAddress`.
- **Errors:** `ModbusException.Code` is a `ModbusError`. Values 1 to 255 are protocol exception codes that go on the wire; values above 255 are client-side errors (CRC, mismatches, incomplete responses) that never do.
- **Non-conforming devices:** Comments tagged `VIOLATION` (see the note at the top of `ModbusClient.cs`) mark code that deliberately deviates from the Modbus specification to work with real devices. Keep the tag on such code and do not "fix" it towards the specification.

## C# coding guidelines

- Tabs for indentation in C# (match existing files).
- File-scoped namespaces.
- `Nullable` and `ImplicitUsings` are enabled in every project file (there is no `Directory.Build.props`); set them in new projects as well.
- Use XML doc comments with a `<summary>` element for the most important part and an optional `<remarks>` for details or the why. `<param>` and `<returns>` may be omitted if obvious from the names; but must be complete if used. An `async Task` method without a true return value needs no `<returns>`. See comment policy. Also use XML doc for private members.
- Braces for single-line bodies of conditions are optional. If any branch in an `if` statement needs or has braces around the body, add them to all other branches as well. Always add braces around the body of other types (loops, locks etc.).
- Use the explicit language type keyword instead of `var` for simple types.
- Group members in the existing `#region` blocks (`Private data`, `Constructors`, `Properties`, ...) and put new members into the matching one. `#endregion` repeats the region name.
- In the library, await with `.NoSync()` from `Util/TaskHelper.cs` instead of a plain `await` or `ConfigureAwait(false)`; CA2007 is enabled as a warning there.
- `CheckForOverflowUnderflow` is enabled in the library and `ModbusClientDemo`, so narrowing casts throw on overflow. Mask the value before casting (`(byte)(value & 0xff)`) or use `unchecked` where truncation or reinterpretation is intended.

## General coding guidelines

- Code symbols (classes, functions, variables etc.), log messages (`ILogger`) and exception messages in English only.
- When adding new methods/functions, sections or similar to a set where existing entries are sorted in alphabetical order, also insert new elements in alphabetical order. When renaming elements, move them to maintain the sort order.
- Never use EN-DASH (U+2013), EM-DASH (U+2014) or MINUS (U+2212) in code comments; find other ways to phrase it, e.g. using colon, semicolon or a new sentence and use a regular hyphen for maths or exceptional cases.
- Keep lines within 100 chars, counting a tab as 4 columns. Go past that only where a break would hurt readability, and rarely past 120. Many existing method signatures, log and exception messages are longer; don't rewrap lines you are not otherwise changing.
- Files use CRLF line endings, and `.gitattributes` disables git's line ending normalization, so write new and edited files with CRLF.

## Comment policy

- Comments in English only.
- Wrap comments at 100 chars, with tab indentation of 4 columns, if the comment is multiple lines long. Single-line comments up to 120 chars are acceptable inside a body. Exceeding that makes side-by-side comparisons harder to read.
- Preserve existing comments verbatim unless your own change makes them incorrect.
- Don't compare with a previous version of the code, only mention what is there now.
- When mentioning a code symbol (e.g. an enum flag like "Complete" in PascalCase) that happens to be an adjective or other existing word, preserve the symbol's casing for differentiation.
- Magic values:
	- A literal a reader cannot name gets its name, not a sentence: `15   // WriteCoils`. If the value appears in several places, consider giving it (and related values) a named constant instead of a comment.
	- A literal needs a reason only when a reader cannot tell from nearby code whether it is right: a margin or timing threshold, a value that must match something outside this file, or an exception carved out of a range.
	- A size or count whose only job is to fit its content needs neither; if it is wrong, the result shows it.
	- When the non-obvious part is the approach rather than the number, that is a rejected alternative, not a magic value.
- When you find a comment that already disagrees with the code, treat it as a bug to report, not a wording problem to fix silently. Possibly the code is wrong.
- When you find commented-out code that would need to be updated with your changes, report it.
- Do not invent a rationale to fill a comment; if the reason is unknown, state only what is known.

### Comments inside a body

- Only comment code whose purpose isn't obvious from naming.
- One short line per comment; no full sentences, no trailing periods.
- A comment that carries a reason rather than a label may run longer and be written as sentences, with normal punctuation.
- Never restate what the code does ("// increment counter").
- Don't add comments to code that had none, unless the logic is non-obvious.
- A comment explaining an absence has nothing to attach to, so it has to be phrased as a statement about what is there.

### Function and class header comments

- Open with one short line, phrased so that someone scanning search results can tell whether it is relevant to them:
	- third person without a subject for anything that acts (`Writes the value to the file.`) or defines (`Defines the supported protocols.`)
	- a plain noun phrase for anything that only holds a value (`The unique ID of the dashboard.`)
- Use one of the two forms throughout a type; do not mix `Gets or sets the ...` and `The ...` for properties in the same class. In an existing type, keep the form it already uses (most types here use `Gets or sets the ...`).
- Add more only where a reader needs it in order not to break the code, and cannot get it from the code itself or its immediate surroundings (examples below). Having nothing to add is the normal case.
	- an ordering constraint
	- an invariant
	- coupling to something in another file
	- a protocol detail
	- an approach that was tried and does not work
	- why something a reader would expect to find is deliberately absent
- Prefer explaining why over what. The test is whether a reader could recover the sentence at a glance from the body itself; if they could, cut it.
- When adding or changing a public, protected or exported member, give it a comment that at least states what it does.
