using System.Text;

namespace Miller.Core.Native;

// The OpenCL GPU of the collision mode Backwards_GPU (T-175): asked once per process (the native library
// opens the device and builds its kernels on the first call and keeps the outcome).
public static class GpuDevice
{
    private static readonly Lazy<(bool Available, string Text)> Probe = new(Open);

    public static bool Available => Probe.Value.Available;

    // The device name when there is one, otherwise why there is none.
    public static string Description => Probe.Value.Text;

    private static unsafe (bool, string) Open()
    {
        var name = new byte[256];
        int status;
        fixed (byte* n = name)
        {
            status = CoreNative.mn_gpu_device(n, name.Length);
        }

        if (status != 0)
        {
            return (false, CoreNative.LastError);
        }

        var length = Array.IndexOf(name, (byte)0);
        return (true, Encoding.ASCII.GetString(name, 0, length < 0 ? name.Length : length).Trim());
    }
}
