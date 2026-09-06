using System.Net;

namespace TcpFraming.Core;

/// <summary>
///     Parses an optional IP address and port from command-line arguments, shared by the client and server.
/// </summary>
public static class EndPointParser
{
    public const int DefaultPort = 8087;

    /// <summary>
    ///     Parses <c>[ipAddress] [port]</c> from <paramref name="args" />, falling back to the loopback
    ///     address and <see cref="DefaultPort" /> when an argument is omitted.
    /// </summary>
    /// <returns><c>true</c> if the arguments were valid; otherwise <c>false</c>, with the reason in <paramref name="error" />.</returns>
    public static bool TryParse(string[] args, out IPEndPoint endPoint, out string? error)
    {
        endPoint = new IPEndPoint(IPAddress.Loopback, DefaultPort);
        error = null;

        if (args.Length > 2)
        {
            error = $"Expected at most 2 arguments but received {args.Length}";
            return false;
        }

        var address = IPAddress.Loopback;
        if (args.Length > 0 && !IPAddress.TryParse(args[0], out address!))
        {
            error = $"Invalid IP address: {args[0]}";
            return false;
        }

        var port = DefaultPort;
        if (args.Length > 1 && (!int.TryParse(args[1], out port) || port is < 1 or > 65535))
        {
            error = $"Invalid port: {args[1]}";
            return false;
        }

        endPoint = new IPEndPoint(address, port);
        return true;
    }
}
