using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TcpFraming.Core;

namespace TcpFraming.Client;

class Program
{
    static async Task Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        var clientSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);

        Console.WriteLine("Connecting to port 8087");

        clientSocket.Connect(new IPEndPoint(IPAddress.Loopback, 8087));
        var stream = new NetworkStream(clientSocket);
        var reader = PipeReader.Create(stream);

        Console.WriteLine("Type messages and press Enter to send (Ctrl+C to exit):");
            
        while (true)
        {
            var input = Console.ReadLine();
            if (string.IsNullOrEmpty(input))
                break;

            var messageBytes = Encoding.UTF8.GetBytes(input);
            var packet = PacketProtocol.WrapMessageForHost(messageBytes);

            var stopwatch = Stopwatch.StartNew();
            await stream.WriteAsync(packet, 0, packet.Length);
            Console.WriteLine($"Sent message ({messageBytes.Length} bytes)");

            // Read the response from the server
            var response = await ReadResponseAsync(reader);
            stopwatch.Stop();
            if (response != null)
            {
                Console.WriteLine($"Received response ({response.Length} bytes) in {stopwatch.ElapsedTicks/10000f}ms : {Encoding.UTF8.GetString(response)}");
            }
        }
    }

    private static async Task<byte[]?> ReadResponseAsync(PipeReader reader)
    {
        while (true)
        {
            var result = await reader.ReadAsync();
            var buffer = result.Buffer;

            if (PacketProtocol.TryGetMessage(ref buffer, out var message))
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
}