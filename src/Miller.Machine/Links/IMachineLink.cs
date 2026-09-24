namespace Miller.Machine.Links;

// A byte stream to a controller. One I/O thread reads and writes it; a failed link throws
// MachineLinkException and stays failed.
public interface IMachineLink : IDisposable
{
    string Name { get; }

    // Bytes read into the buffer, 0 when nothing arrived within the timeout.
    int Read(Span<byte> buffer, int timeoutMs);

    void Write(ReadOnlySpan<byte> data);
}
