using System.Net;
using System.Net.Sockets;
using System.Text;
using Miller.Machine.Links;
using Xunit;

namespace Miller.Tests.Machine;

// T-140: the serial library lists ports and reports a port it cannot open with a message instead of
// a crash; the TCP link carries bytes both ways, returns 0 on a read timeout and reports a closed
// or unreachable controller as MachineLinkException. No serial controller is attached here, so the
// serial read and write paths are exercised only by the shared controller tests over TCP and fakes.
public sealed class MachineLinkTests
{
    private const string MissingPort = "MILLER_NO_SUCH_PORT";

    [Fact]
    public void SerialList_ReturnsDistinctNames()
    {
        var ports = SerialLink.List();
        Assert.Equal(ports.Count, ports.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ports, p => Assert.False(string.IsNullOrWhiteSpace(p)));
    }

    [Fact]
    public void SerialOpen_MissingPort_ThrowsWithTheName()
    {
        var ex = Assert.Throws<MachineLinkException>(() => SerialLink.Open(MissingPort, SerialLink.DefaultBaudRate));
        Assert.Contains(MissingPort, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SerialOpen_RejectsBadArguments()
    {
        Assert.Throws<ArgumentException>(() => SerialLink.Open(" ", SerialLink.DefaultBaudRate));
        Assert.Throws<ArgumentOutOfRangeException>(() => SerialLink.Open(MissingPort, 250000));
        Assert.Throws<ArgumentOutOfRangeException>(() => SerialLink.Open(MissingPort, 0));
        Assert.Contains(SerialLink.DefaultBaudRate, SerialLink.BaudRates);
    }

    [Fact]
    public async Task TcpLink_CarriesBytesBothWays_AndTimesOutWithZero()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();
        using var link = TcpLink.Open(IPAddress.Loopback.ToString(), port);
        using var server = await accept;
        var stream = server.GetStream();
        Assert.Equal($"127.0.0.1:{port}", link.Name);

        var buffer = new byte[64];
        Assert.Equal(0, link.Read(buffer, 20));

        await stream.WriteAsync(Encoding.ASCII.GetBytes("Grbl 1.1h ['$' for help]\r\n"), TestContext.Current.CancellationToken);
        var received = new StringBuilder();
        while (!received.ToString().EndsWith('\n'))
        {
            var count = link.Read(buffer, 1000);
            received.Append(Encoding.ASCII.GetString(buffer, 0, count));
        }

        Assert.Equal("Grbl 1.1h ['$' for help]\r\n", received.ToString());

        link.Write(new byte[] { (byte)'?', 0x85 });
        var echo = new byte[2];
        var read = 0;
        while (read < 2)
        {
            read += await stream.ReadAsync(echo.AsMemory(read), TestContext.Current.CancellationToken);
        }

        Assert.Equal(new byte[] { (byte)'?', 0x85 }, echo);

        server.Close();
        Assert.Throws<MachineLinkException>(() => ReadUntilFailure(link));
    }

    private static void ReadUntilFailure(IMachineLink link)
    {
        var buffer = new byte[64];
        while (true)
        {
            link.Read(buffer, 1000);
        }
    }

    [Fact]
    public void TcpOpen_NobodyListening_ThrowsWithTheAddress()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var ex = Assert.Throws<MachineLinkException>(() => TcpLink.Open(IPAddress.Loopback.ToString(), port));
        Assert.Contains($"127.0.0.1:{port}", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => TcpLink.Open("localhost", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TcpLink.Open("localhost", TcpLink.MaxPort + 1));
    }
}
