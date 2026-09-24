namespace Miller.Machine;

// Times of the controller thread in milliseconds. The read timeout bounds how late a realtime byte
// leaves while the thread waits for input; Grbl recommends status queries at no more than 5 Hz; an
// Arduino restarts when its port opens and prints the welcome line after about 1 to 2 s.
public sealed record MachineTiming(
    int ReadTimeoutMs = 10,
    int PollMs = 200,
    int BannerWaitMs = 2500,
    int IdentifyTimeoutMs = 5000,
    int WatchdogMs = 3000,
    int HoldTimeoutMs = 5000)
{
    public static readonly MachineTiming Default = new();
}
