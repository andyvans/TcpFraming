using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace TcpFraming.Core;

/// <summary>
///     Manages TCP/IP message framing. Messages contain a binary two-byte message header which indicates the length of the
///     TCP/IP data that follows, followed by the TCP message itself. Messages are in network byte order and need to be translated to/from
///     network byte order for processing.
/// </summary>
public static class FramingProtocol
{
    private const int FramingSize = 2;
    private const int MaxMessageSize = ushort.MaxValue;

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

    /// <summary>
    ///     Wraps a TCP message into a packet containing a message header. The message header is two bytes and contains a data length
    ///     value. This is in network byte order and needs to be translated to/from network byte order for processing.
    /// </summary>
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
}