namespace Miller.Core.Native;

// The worker threads of the parallel generation stages (T-160): the processor count, or the limit set
// here (0 for none). Every stage gives the same result with any count; tests compare one worker with all.
public static class NativeWorkers
{
    public static int Count => CoreNative.mn_workers();

    public static void Limit(int limit) => CoreNative.mn_set_worker_limit(limit);
}
