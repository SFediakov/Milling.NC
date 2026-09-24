using System.Text;
using Miller.Machine.Native;

namespace Miller.Machine.Links;

// A serial port through the native library: 8N1, no flow control, DTR and RTS on. The rates are
// the standard ones every system accepts; GRBL uses 115200.
public sealed unsafe class SerialLink : IMachineLink
{
    public const int DefaultBaudRate = 115200;
    private const int ListCapacity = 4096;

    public static readonly IReadOnlyList<int> BaudRates = new[] { 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };

    private IntPtr _port;

    private SerialLink(string name, IntPtr port)
    {
        Name = name;
        _port = port;
    }

    public string Name { get; }

    // The ports present now, in natural order (COM3 before COM10).
    public static IReadOnlyList<string> List()
    {
        for (var capacity = ListCapacity; ; capacity *= 2)
        {
            var buffer = new byte[capacity];
            int length;
            fixed (byte* pointer = buffer)
            {
                length = SerialNative.ms_list(pointer, capacity);
            }

            if (length < 0)
            {
                throw new MachineLinkException(SerialNative.LastError());
            }

            if (length < capacity)
            {
                return Encoding.UTF8.GetString(buffer, 0, length)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.Ordinal)
                    .Order(PortNameComparer.Instance)
                    .ToList();
            }
        }
    }

    public static SerialLink Open(string name, int baudRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!BaudRates.Contains(baudRate))
        {
            throw new ArgumentOutOfRangeException(nameof(baudRate), $"{baudRate} baud is not one of {string.Join(", ", BaudRates)}.");
        }

        var bytes = Encoding.UTF8.GetBytes(name + '\0');
        IntPtr port;
        int status;
        fixed (byte* pointer = bytes)
        {
            status = SerialNative.ms_open(pointer, baudRate, &port);
        }

        if (status != SerialNative.Ok)
        {
            var message = SerialNative.LastError();
            throw status == SerialNative.ErrorArgument ? new ArgumentException(message, nameof(name)) : new MachineLinkException(message);
        }

        return new SerialLink(name, port);
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_port == IntPtr.Zero, this);
        int count;
        fixed (byte* pointer = buffer)
        {
            count = SerialNative.ms_read(_port, pointer, buffer.Length, timeoutMs);
        }

        return count >= 0 ? count : throw new MachineLinkException($"{Name}: {SerialNative.LastError()}");
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_port == IntPtr.Zero, this);
        int count;
        fixed (byte* pointer = data)
        {
            count = SerialNative.ms_write(_port, pointer, data.Length);
        }

        if (count < 0)
        {
            throw new MachineLinkException($"{Name}: {SerialNative.LastError()}");
        }
    }

    public void Dispose()
    {
        if (_port != IntPtr.Zero)
        {
            SerialNative.ms_close(_port);
            _port = IntPtr.Zero;
        }
    }

    // Letters first, then the trailing number by value.
    private sealed class PortNameComparer : IComparer<string>
    {
        public static readonly PortNameComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var (xStem, xNumber) = Split(x ?? string.Empty);
            var (yStem, yNumber) = Split(y ?? string.Empty);
            var stem = string.CompareOrdinal(xStem, yStem);
            return stem != 0 ? stem : xNumber.CompareTo(yNumber);
        }

        private static (string Stem, long Number) Split(string name)
        {
            var end = name.Length;
            while (end > 0 && char.IsAsciiDigit(name[end - 1]) && name.Length - end < 9)
            {
                end--;
            }

            return end == name.Length ? (name, -1) : (name[..end], long.Parse(name[end..], System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
