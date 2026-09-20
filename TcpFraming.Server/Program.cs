using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TcpFraming.Core;

namespace TcpFraming.Server;

class Program
{
    private const string DefaultAddress = "127.0.0.1";

    private static readonly object _consoleLock = new();

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (!EndPointParser.TryParse(args, out var endPoint, out var error))
        {
            Console.WriteLine(error);
            Console.WriteLine($"Usage: TcpFraming.Server [ipAddress] [port]  (defaults: {DefaultAddress} {EndPointParser.DefaultPort})");
            Console.WriteLine("Use 0.0.0.0 to accept connections from other machines.");
            return;
        }

        var listenSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        listenSocket.Bind(endPoint);

        Console.WriteLine($"Listening on {endPoint}");

        listenSocket.Listen(120);

        while (true)
        {
            var socket = await listenSocket.AcceptAsync();
            // Fire-and-forget: each client is processed concurrently.
            // ProcessMessagesAsync never throws, so the task never faults.
            _ = ProcessMessagesAsync(socket);
        }
    }

    private static async Task ProcessMessagesAsync(Socket socket)
    {
        // Capture the endpoint up front: it is unavailable once the socket is disposed.
        var remoteEndPoint = socket.RemoteEndPoint;
        SafeWriteLine($"[{remoteEndPoint}]: connected");

        using var stream = new NetworkStream(socket, ownsSocket: true);
        var reader = PipeReader.Create(stream);

        try
        {
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

            SafeWriteLine($"[{remoteEndPoint}]: disconnected");
        }
        catch (Exception ex)
        {
            SafeWriteLine($"[{remoteEndPoint}]: Error - {ex.Message}");
        }
        finally
        {
            // Always signal completion, even if the loop threw.
            await reader.CompleteAsync();
        }
    }

    private static async Task ProcessMessageAsync(byte[] message, Stream stream)
    {
        var decodedMessage = Encoding.UTF8.GetString(message);
        SafeWriteLine($"Received message ({message.Length} bytes): {decodedMessage}");

        // Echo demonstrates bidirectional communication and confirms message receipt
        var responseMessage = $"Echoing back: {decodedMessage}";

        // Echo the message back to the client
        var response = FramingProtocol.WrapMessageForHost(Encoding.UTF8.GetBytes(responseMessage));
        await stream.WriteAsync(response);
    }

    // Serialises output from the concurrent per-client tasks so lines don't interleave.
    // Startup code runs before any client is accepted and writes to Console directly.
    private static void SafeWriteLine(string message)
    {
        lock (_consoleLock)
        {
            Console.WriteLine(message);
        }
    }
}