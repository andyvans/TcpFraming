# TCP Framing Example with Pipelines

A small .NET 10 sample showing **why TCP applications need a framing protocol**, and how to
implement one using `System.IO.Pipelines`.  Read more at [https://www.codify.nz/tcp-framing-with-pipelines/](https://www.codify.nz/tcp-framing-with-pipelines/).

## Background

In modern software development, developers typically rely on HTTP, REST, or gRPC for
communication. Back in the '80s and '90s, many mainframe-style financial systems used TCP
with framed payloads to communicate with other financial systems and devices — mainframes,
HSMs (Hardware Security Modules), and ISO8583 message processors.

Those systems are still processing transactions today, so software that needs to talk to
them will likely need TCP framing. Even on core replacement projects, the first step is
often to gain more access to the legacy system so functionality can be redeveloped
alongside it, which supports a gradual replacement rather than a big-bang cutover.

Alternatively, if you wanted to build a web server or even do light-weight communication to
or from an ESP32, then TCP sockets may be the way to go.

## Client
```pwsh
.\TcpFraming.Client.exe 127.0.0.1 8087
Connecting to 127.0.0.1:8087
Type messages and press Enter to send (Ctrl+C to exit):
hello world! 🌍
Sent message (18 bytes)
Received response (32 bytes) in 15.4205ms : Echoing back: hello world! 🌍
```

## Server
```pwsh
.\TcpFraming.Server.exe 127.0.0.1 8087
Listening on 127.0.0.1:8087
[[::ffff:127.0.0.1]:65247]: connected
Received message (18 bytes): hello world! 🌍
```

## Why framing is needed

TCP is a **byte stream**, not a message protocol. The network guarantees that bytes arrive
in order and without corruption — but it guarantees *nothing* about where one application
message ends and the next begins.

This means a single `Send` on one side does **not** map to a single `Receive` on the other:

- **Fragmentation** — one message can be split across several reads. You send 500 bytes,
  the receiver gets 200 then 300.
- **Coalescing** — several messages can arrive in a single read. You send three messages,
  the receiver gets all of them in one buffer.

Both happen for reasons outside your control: MSS/MTU limits, Nagle's algorithm, kernel
socket buffer sizes, and router-level fragmentation. Code that assumes "one read == one
message" appears to work on localhost with small payloads, then fails in production under
load or across a real network.

The fix is a **framing protocol**: an agreed-upon convention that lets the receiver find
message boundaries in the stream. Common approaches are:

| Approach | Example | Trade-off |
| --- | --- | --- |
| Length prefix | `[len][payload]` | Simple, binary-safe, fixed overhead |
| Delimiter | newline-terminated | Human-readable, but payload must be escaped |
| Fixed size | every message is N bytes | Trivial, but wasteful and inflexible |

This sample uses a **length prefix**, the most common choice for binary protocols.

## The wire format

This sample uses a two-byte big-endian length prefix which seems be a standard approach. This has the following rules:

- The length describes the payload only. The 2 byte header is not included
- The maximum payload is 65,535 bytes (16 bit unsigned short).
- A length of zero is valid and can be used as a keep-alive.
- Big-endian keeps wire format consistent across host architectures.

```
+--------+--------+----------------------------+
| len hi | len lo |  payload (len bytes)       |
+--------+--------+----------------------------+
   0        1        2 .. 2+len-1
```

In this example UTF-8 is used on the console for text and emoji support, but the framing protocol is binary-safe and can carry any payload.

## System.IO.Pipelines

[System.IO.Pipelines](https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines) is a library designed to make high-performance I/O in .NET easier. It came about as part of the .NET Kestrel web server development. The problem it solves is described [here](https://devblogs.microsoft.com/dotnet/system-io-pipelines-high-performance-io-in-net/) quite nicely. When switching existing TCP streaming code to use Pipelines you quickly realise how much tidier the code becomes.


## Projects

| Project | Description |
| --- | --- |
| `TcpFraming.Core` | `FramingProtocol` — the encode/decode logic, shared by both sides |
| `TcpFraming.Server` | Echo server listening on `127.0.0.1:8087` |
| `TcpFraming.Client` | Console client that sends typed lines and prints the echo |

## Running

Start the server in one terminal:

```pwsh
dotnet run --project TcpFraming.Server
```

Then the client in another:

```pwsh
dotnet run --project TcpFraming.Client
```

Type a message and press Enter. The client frames it, sends it, and waits for the server's
echo. Press Enter on an empty line to exit.

### Address and port arguments

Both programs accept an optional IP address and port, defaulting to `127.0.0.1 8087`:

```
TcpFraming.Server [ipAddress] [port]
TcpFraming.Client [ipAddress] [port]
```

To run on a different port:

```pwsh
dotnet run --project TcpFraming.Server -- 127.0.0.1 9000
dotnet run --project TcpFraming.Client -- 127.0.0.1 9000
```

By default the server binds to the **loopback** address, so it only accepts connections from
the same machine. To accept connections from other machines, bind to `0.0.0.0` and point the
client at the server's actual address:

```pwsh
# on the server machine
dotnet run --project TcpFraming.Server -- 0.0.0.0 8087

# on another machine
dotnet run --project TcpFraming.Client -- 192.168.1.50 8087
```

Running across a real network is also the easiest way to see *why* framing matters — over
loopback with short messages, each write tends to arrive as a single read, which can mask a
missing framing protocol entirely.

Only literal IP addresses are accepted, not hostnames such as `localhost`.

## How the code works

### Writing — `FramingProtocol.WrapMessageForHost`

Allocates a buffer two bytes larger than the payload, writes the length with
`BinaryPrimitives.WriteUInt16BigEndian`, then copies the payload in after it:

```csharp
public static byte[] WrapMessageForHost(byte[] message)
{
    if (message.Length > MaxMessageSize)
        throw new ProtocolViolationException($"Message length is greater than {MaxMessageSize} : {message.Length}");

    var requestLength = (ushort)message.Length;

    var buffer = new byte[requestLength + FramingSize];
    // Write the length header in network byte order (big-endian)
    BinaryPrimitives.WriteUInt16BigEndian(buffer, requestLength);
    Buffer.BlockCopy(message, 0, buffer, FramingSize, requestLength);

    return buffer;
}
```

### Reading — `FramingProtocol.TryGetMessage`

Takes the received bytes as a `ref ReadOnlySequence<byte>` and returns `true` only when a
**complete** message is available. The `ref` matters: on success the method re-slices the
caller's buffer past the message it consumed, so repeated calls walk through every message
in the buffer:

```csharp
public static bool TryGetMessage(ref ReadOnlySequence<byte> buffer, [NotNullWhen(true)] out byte[]? message)
{
	message = null;

	var bufferSize = buffer.Length;

	// Check we have received the frame size
	if (bufferSize < FramingSize) return false;

	// Read the two-byte header in network byte order (big-endian), without allocating
	Span<byte> header = stackalloc byte[FramingSize];
	buffer.Slice(0, FramingSize).CopyTo(header);
	int length = BinaryPrimitives.ReadUInt16BigEndian(header);

	// Sanity check for very large packets, to prevent denial-of-service attacks
	if (length > MaxMessageSize) throw new ProtocolViolationException($"Message length {length} is larger than maximum message size {MaxMessageSize}");

	// Zero-length packets are allowed as keep-alives
	if (length == 0)
	{
		// Move the buffer past the frame size
		buffer = buffer.Slice(FramingSize);
		return false;
	}

	// Do we have a complete message?
	if (bufferSize < FramingSize + length) return false;

	// We have the complete message
	message = buffer.Slice(FramingSize, length).ToArray();

	// Move the buffer past the frame size and message
	buffer = buffer.Slice(FramingSize + length);

	return true;
}
```

The method returns `false` when fewer than two bytes are available (header incomplete) or
when the payload has not fully arrived yet. That is the fragmented case: the caller loops,
reads more from the socket, and tries again.

It also sanity-checks the declared length against the maximum message size to guard against
denial-of-service attacks, and skips zero-length keep-alive frames by slicing past the
header.

### The read loop — `System.IO.Pipelines`

Both server and client wrap the `NetworkStream` in a `PipeReader`. Pipelines handles the
buffer management that framing otherwise forces you to write by hand: growing the buffer
when a message spans reads, and reusing memory once bytes are consumed.

The server drains every complete message from each read before asking for more, which
handles the coalesced case:

```csharp
using var stream = new NetworkStream(socket, ownsSocket: true);
var reader = PipeReader.Create(stream);

while (true)
{
	var result = await reader.ReadAsync();
	var buffer = result.Buffer;

	while (FramingProtocol.TryGetMessage(ref buffer, out var message))
	{
		// Process the message and echo it back to the client.
		await ProcessMessageAsync(message, stream);
	}

	// Tell the PipeReader how much of the buffer has been consumed.
	reader.AdvanceTo(buffer.Start, buffer.End);

	// Stop reading if there's no more data coming.
	if (result.IsCompleted)
	{
		break;
	}
}
```

The client uses the same pattern, but only needs a single response per request:

```csharp
private static async Task<byte[]?> ReadResponseAsync(PipeReader reader)
{
	while (true)
	{
		var result = await reader.ReadAsync();
		var buffer = result.Buffer;

		if (FramingProtocol.TryGetMessage(ref buffer, out var message))
		{
			reader.AdvanceTo(buffer.Start, buffer.End);
			return message;
		}

		reader.AdvanceTo(buffer.Start, buffer.End);

		if (result.IsCompleted)
		{
			return null;
		}
	}
}
```

The two `AdvanceTo` arguments mean different things and are the most commonly misunderstood
part of the API:

- **`consumed`** — bytes the pipe may discard. Data before this point is gone for good.
- **`examined`** — bytes already inspected. This tells the pipe that another `ReadAsync`
  should not return until *more* data than this has arrived.

## Closing point

TCP socket communication is typically not the first tool to reach for. However it works very
well when dealing with legacy software or equipment, and can be extremely fast because there
is minimal application-layer overhead.
