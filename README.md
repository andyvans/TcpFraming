# TCP Framing Example with Pipelines

A small .NET 10 sample showing **why TCP applications need a framing protocol**, and how to
implement one using `System.IO.Pipelines`.

## Client
```pwsh
.\TcpFraming.Client.exe
Connecting to 127.0.0.1:8087
Type messages and press Enter to send (Ctrl+C to exit):
hello world! 🌍
Sent message (18 bytes)
Received response (32 bytes) in 15.4205ms : Echoing back: hello world! 🌍
```

## Server
```pwsh
.\TcpFraming.Server.exe
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

Every message is preceded by a two-byte, big-endian (network byte order) length header:

```
+--------+--------+----------------------------+
| len hi | len lo |  payload (len bytes)       |
+--------+--------+----------------------------+
   0        1        2 .. 2+len-1
```

- The length covers the **payload only**, not the header.
- Maximum payload is 65,535 bytes, the largest value a `ushort` can express.
- A length of `0` is legal and acts as a **keep-alive** — a header with no payload.

Big-endian is used because it is the conventional byte order for network protocols, so the
format stays interoperable regardless of the sending machine's native endianness.

In this example UTF-8 is used for text and emoji support, but the framing protocol is binary-safe and can carry any payload.

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
`BinaryPrimitives.WriteUInt16BigEndian`, then copies the payload in after it.

### Reading — `FramingProtocol.TryGetMessage`

Takes the received bytes as a `ref ReadOnlySequence<byte>` and returns `true` only when a
**complete** message is available. The `ref` matters: on success the method re-slices the
caller's buffer past the message it consumed, so repeated calls walk through every message
in the buffer:

```csharp
while (FramingProtocol.TryGetMessage(ref buffer, out var message))
{
	// handles the coalesced case: drains all complete messages
}
```

The method returns `false` — leaving `buffer` untouched — when fewer than two bytes are
available (header incomplete) or when the payload has not fully arrived yet. That is the
fragmented case: the caller loops, reads more from the socket, and tries again.

### The read loop — `System.IO.Pipelines`

Both server and client wrap the `NetworkStream` in a `PipeReader`. Pipelines handles the
buffer management that framing otherwise forces you to write by hand: growing the buffer
when a message spans reads, and reusing memory once bytes are consumed.

Once a message is read, then the reader is advanced past the message:

```csharp
reader.AdvanceTo(buffer.Start, buffer.End);
```
