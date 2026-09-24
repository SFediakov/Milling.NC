using System.Net.Sockets;

namespace Miller.Machine.Links;

// A raw TCP stream to a network controller (FluidNC, grblHAL over Ethernet or Wi-Fi, a serial
// bridge), port 23 by default. Nagle is off so a one-byte realtime command leaves at once.
public sealed class TcpLink : IMachineLink
{
    public const int DefaultPort = 23;
    public const int MaxPort = 65535;
    public const int ConnectTimeoutMs = 3000;
    private const int MicrosecondsPerMillisecond = 1000;

    private readonly Socket _socket;
    private bool _disposed;

    private TcpLink(string name, Socket socket)
    {
        Name = name;
        _socket = socket;
    }

    public string Name { get; }

    public static TcpLink Open(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, MaxPort);
        var name = $"{host}:{port}";
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var timeout = new CancellationTokenSource(ConnectTimeoutMs);
            socket.ConnectAsync(host, port, timeout.Token).AsTask().GetAwaiter().GetResult();
            return new TcpLink(name, socket);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            socket.Dispose();
            var reason = ex is OperationCanceledException ? $"no answer within {ConnectTimeoutMs} ms" : ex.Message;
            throw new MachineLinkException($"{name} cannot be reached: {reason}.", ex);
        }
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            if (!_socket.Poll(Math.Max(0, timeoutMs) * MicrosecondsPerMillisecond, SelectMode.SelectRead))
            {
                return 0;
            }

            var count = _socket.Receive(buffer);
            return count > 0 ? count : throw new MachineLinkException($"{Name}: the controller closed the connection.");
        }
        catch (SocketException ex)
        {
            throw new MachineLinkException($"{Name}: {ex.Message}", ex);
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            while (!data.IsEmpty)
            {
                data = data[_socket.Send(data)..];
            }
        }
        catch (SocketException ex)
        {
            throw new MachineLinkException($"{Name}: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _socket.Dispose();
        }
    }
}
