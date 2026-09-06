# TCP Framing Example with Pipelines

A small .NET 10 sample showing **why TCP applications need a framing protocol**, and how to
implement one using `System.IO.Pipelines`.

## Client
```pwsh
.\TcpFraming.Client.exe
Connecting to port 8087
Type messages and press Enter to send (Ctrl+C to exit):
hello world! 🌍
Sent message (18 bytes)
Received response (32 bytes) in 15.4205ms : Echoing back: hello world! 🌍
```

## Server
```pwsh
.\TcpFraming.Server.exe
Listening on port 8087
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

UTF-8 is used for text and emoji support, but the framing protocol is binary-safe and can carry any payload.

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

## How the code works

### Writing — `FramingProtocol.WrapMessageForHost`

Allocates a buffer two bytes larger than the payload, writes the length with
`BinaryPrimitives.WriteUInt16BigEndian`, then copies the payload in after it.

`BinaryPrimitives` is used rather than `BitConverter` because `BitConverter` uses the
*host's* byte order, which would produce a different wire format on a big-endian machine.

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

Note that the maximum-size check happens **before** the completeness check. A malicious peer
could otherwise advertise a huge length and force the receiver to buffer indefinitely, so the
length is validated as soon as it is known.

### The read loop — `System.IO.Pipelines`

Both server and client wrap the `NetworkStream` in a `PipeReader`. Pipelines handles the
buffer management that framing otherwise forces you to write by hand: growing the buffer
when a message spans reads, and reusing memory once bytes are consumed.

The key call is:

```csharp
reader.AdvanceTo(buffer.Start, buffer.End);
```

The two arguments mean different things and are the most commonly misunderstood part of the
API:

- **`consumed`** — bytes the pipe may discard. Data before this point is gone for good.
- **`examined`** — bytes already inspected. This tells the pipe that another `ReadAsync`
  should not return until *more* data than this has arrived.

Passing `buffer.End` as `examined` is what prevents a busy loop on a partial message: without
it, `ReadAsync` would return immediately with the same incomplete data forever.

## Things deliberately left simple

This is teaching code, not a production library:

- `TryGetMessage` calls `.ToArray()`, allocating a `byte[]` per message. A real
  implementation would hand back the `ReadOnlySequence` slice and avoid the copy.
- The server's per-client task is fire-and-forget, with no shutdown coordination or
  `CancellationToken`.
- There is no backpressure on the write side, no reconnect logic, and no TLS.
