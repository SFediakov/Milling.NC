using System.Runtime.InteropServices;

namespace Miller.Machine.Native;

// The serial port library of the machine connection (src/Miller.Machine.Native/include/miller_serial.h).
// The error text is thread-local, so it is read right after the failing call on the same thread.
internal static unsafe class SerialNative
{
    private const string Library = "miller_serial";

    public const int Ok = 0;
    public const int ErrorArgument = -1;

    [DllImport(Library)]
    public static extern IntPtr ms_last_error();

    [DllImport(Library)]
    public static extern int ms_list(byte* buffer, int capacity);

    [DllImport(Library)]
    public static extern int ms_open(byte* name, int baud, IntPtr* port);

    [DllImport(Library)]
    public static extern int ms_read(IntPtr port, byte* buffer, int capacity, int timeoutMs);

    [DllImport(Library)]
    public static extern int ms_write(IntPtr port, byte* data, int length);

    [DllImport(Library)]
    public static extern void ms_close(IntPtr port);

    public static string LastError() => Marshal.PtrToStringUTF8(ms_last_error()) ?? string.Empty;
}
