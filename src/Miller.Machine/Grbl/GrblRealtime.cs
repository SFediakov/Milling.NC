namespace Miller.Machine.Grbl;

// Grbl v1.1 realtime commands: single bytes the controller acts on the moment it receives them,
// outside the line buffer and without an ok. They are never part of a streamed line.
public static class GrblRealtime
{
    public const byte StatusQuery = (byte)'?';
    public const byte CycleStart = (byte)'~';
    public const byte FeedHold = (byte)'!';
    public const byte SoftReset = 0x18;
    public const byte SafetyDoor = 0x84;
    public const byte JogCancel = 0x85;
    public const byte FeedReset = 0x90;
    public const byte FeedPlus10 = 0x91;
    public const byte FeedMinus10 = 0x92;
    public const byte FeedPlus1 = 0x93;
    public const byte FeedMinus1 = 0x94;
    public const byte RapidFull = 0x95;
    public const byte RapidHalf = 0x96;
    public const byte RapidQuarter = 0x97;
    public const byte SpindleReset = 0x99;
    public const byte SpindlePlus10 = 0x9A;
    public const byte SpindleMinus10 = 0x9B;
    public const byte SpindlePlus1 = 0x9C;
    public const byte SpindleMinus1 = 0x9D;
    public const byte SpindleStopToggle = 0x9E;
    public const byte FloodToggle = 0xA0;
    public const byte MistToggle = 0xA1;

    private static readonly HashSet<byte> All = new()
    {
        StatusQuery, CycleStart, FeedHold, SoftReset, SafetyDoor, JogCancel,
        FeedReset, FeedPlus10, FeedMinus10, FeedPlus1, FeedMinus1,
        RapidFull, RapidHalf, RapidQuarter,
        SpindleReset, SpindlePlus10, SpindleMinus10, SpindlePlus1, SpindleMinus1, SpindleStopToggle,
        FloodToggle, MistToggle,
    };

    public static bool IsRealtime(byte value) => All.Contains(value);

    // Characters that Grbl takes as a realtime command wherever they appear in the input.
    public static bool IsRealtimeCharacter(char c) => c is '?' or '~' or '!' || c >= 0x80 || c == (char)SoftReset;
}
