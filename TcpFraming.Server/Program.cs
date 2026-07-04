using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TcpFraming.Core;

namespace TcpFraming.Server;

class Program
{
    static async Task Main(string[] args)
    {
        var listenSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        listenSocket.Bind(new IPEndPoint(IPAddress.Loopback, 8087));

        Console.WriteLine("Listening on port 8087");

        listenSocket.Listen(120);

        while (true)
        {
            var socket = await listenSocket.AcceptAsync();
            _ = ProcessMessagesAsync(socket);
        }
    }

    private static async Task ProcessMessagesAsync(Socket socket)
    {
        Console.WriteLine($"[{socket.RemoteEndPoint}]: connected");

        // Create a PipeReader over the network stream
        var stream = new NetworkStream(socket);
        var reader = PipeReader.Create(stream);

        while (true)
        {
            var result = await reader.ReadAsync();
            var buffer = result.Buffer;

            while (PacketProtocol.TryGetMessage(ref buffer, out var message))
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

        // Mark the PipeReader as complete.
        await reader.CompleteAsync();

        Console.WriteLine($"[{socket.RemoteEndPoint}]: disconnected");
    }

    private static async Task ProcessMessageAsync(byte[] message, Stream stream)
    {
        Console.WriteLine($"Received message ({message.Length} bytes): {Encoding.UTF8.GetString(message)}");

        // Echo the message back to the client
        var response = PacketProtocol.WrapMessageForHost(message);
        await stream.WriteAsync(response);
        await stream.FlushAsync();
    }
}