using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace TcpFraming.Core;

/// <summary>
///     Manages ISO8583 TCP/IP message framing. Messages contain a binary two-byte message header which indicates the length of the
///     TCP/IP data that follows, followed by the ISO8583 message itself. Messages are in network byte order and need to be translated to/from
///     network byte order for processing.
/// </summary>
public static class PacketProtocol
{
    private const int FramingSize = 2;
    private const int MaxMessageSize = 8192;

    public static bool TryGetMessage(ref ReadOnlySequence<byte> buffer, [NotNullWhen(true)] out byte[]? message)
    {
        message = null;

        var bufferSize = buffer.Length;

        // Check we have received the frame size
        if (bufferSize < FramingSize) return false;

        var networkLength = buffer.Slice(0, FramingSize).ToArray();
        int length = BitConverter.ToUInt16(networkLength.Reverse().ToArray(), 0);

        // Sanity check for very large packets, to prevent denial-of-service attacks
        if (length > MaxMessageSize) throw new ProtocolViolationException($"Message length {length} is larger than maximum message size {MaxMessageSize}");

        // Zero-length packets are allowed as keep-alives
        if (length == 0)
        {
            // Move the buffer past the frame size
            buffer = buffer.Slice(0, FramingSize);
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
    ///     Wraps an ISO8583 message into a packet containing a message header. The message header is two bytes and contains a data length
    ///     value. This is in network byte order and needs to be translated to/from network byte order for processing.
    /// </summary>
    public static byte[] WrapMessageForHost(byte[] message)
    {
        if (message.Length > ushort.MaxValue)
            throw new ProtocolViolationException($"Message length is greater than {ushort.MaxValue} : {message.Length}");

        // Assume message is not greater than 65536 bytes
        var requestLength = (ushort)message.Length;

        var buffer = new byte[requestLength + FramingSize];
        Buffer.BlockCopy(BitConverter.GetBytes(requestLength).Reverse().ToArray(), 0, buffer, 0, FramingSize);
        Buffer.BlockCopy(message, 0, buffer, FramingSize, requestLength);

        return buffer;
    }
}