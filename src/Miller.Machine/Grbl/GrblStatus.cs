using System.Globalization;

namespace Miller.Machine.Grbl;

public readonly record struct Axes(double X, double Y, double Z)
{
    public static readonly Axes Zero = new(0, 0, 0);

    public static Axes operator +(Axes a, Axes b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Axes operator -(Axes a, Axes b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
}

// One status report (`<Idle|MPos:1.000,2.000,3.000|FS:0,0|WCO:0.000,0.000,0.000>`). Grbl sends either
// MPos or WPos ($10) and the work offset and the overrides only every 10 to 30 reports, so both are
// carried over from the previous report and the missing position is derived: WPos = MPos - WCO.
// Only the first three axes are kept; values are in the controller's units ($13).
public sealed record GrblStatus(
    string State,
    int SubState,
    Axes MachinePosition,
    Axes WorkPosition,
    Axes WorkOffset,
    double Feed,
    double Spindle,
    int FeedOverride,
    int RapidOverride,
    int SpindleOverride,
    int PlannerBlocksFree,
    int ReceiveBytesFree,
    int LineNumber,
    string Pins,
    string Accessories)
{
    public const string UnknownState = "Unknown";
    public const string Idle = "Idle";
    public const string Run = "Run";
    public const string Hold = "Hold";
    public const string Jog = "Jog";
    public const string Alarm = "Alarm";
    public const string Door = "Door";
    public const string Check = "Check";
    public const string Home = "Home";
    public const string Sleep = "Sleep";
    public const int NoValue = -1;
    public const int FullOverride = 100;

    // Hold:0 means the machine has come to a stop; Hold:1 means it is still decelerating.
    public const int HoldComplete = 0;

    public static readonly GrblStatus Unknown = new(UnknownState, NoValue, Axes.Zero, Axes.Zero, Axes.Zero, 0, 0,
        FullOverride, FullOverride, FullOverride, NoValue, NoValue, NoValue, string.Empty, string.Empty);

    public bool IsReport(string state) => string.Equals(State, state, StringComparison.Ordinal);

    // Stopped with nothing left to move: idle, a finished hold, an alarm or check mode.
    public bool IsAtRest => IsReport(Idle) || IsReport(Alarm) || IsReport(Check) || (IsReport(Hold) && SubState == HoldComplete);

    public static bool IsStatusReport(string line) => line.Length >= 2 && line[0] == '<' && line[^1] == '>';

    public static GrblStatus Parse(string report, GrblStatus previous)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(previous);
        if (!IsStatusReport(report))
        {
            throw new FormatException($"'{report}' is not a status report.");
        }

        var fields = report[1..^1].Split('|');
        var stateField = fields[0];
        var colon = stateField.IndexOf(':');
        var state = colon < 0 ? stateField : stateField[..colon];
        var sub = colon < 0 ? NoValue : ParseInt(stateField[(colon + 1)..]);
        Axes? machine = null;
        Axes? work = null;
        var offset = previous.WorkOffset;
        double feed = 0, spindle = 0;
        int feedOverride = previous.FeedOverride, rapidOverride = previous.RapidOverride, spindleOverride = previous.SpindleOverride;
        int blocks = NoValue, bytes = NoValue, line = NoValue;
        var pins = string.Empty;
        var accessories = string.Empty;
        for (var k = 1; k < fields.Length; k++)
        {
            var field = fields[k];
            var split = field.IndexOf(':');
            if (split < 0)
            {
                continue;
            }

            var value = field[(split + 1)..];
            switch (field[..split])
            {
                case "MPos":
                    machine = ParseAxes(value);
                    break;
                case "WPos":
                    work = ParseAxes(value);
                    break;
                case "WCO":
                    offset = ParseAxes(value);
                    break;
                case "FS":
                    var fs = Numbers(value);
                    feed = fs[0];
                    spindle = fs.Length > 1 ? fs[1] : 0;
                    break;
                case "F":
                    feed = Numbers(value)[0];
                    break;
                case "Ov":
                    var ov = Numbers(value);
                    if (ov.Length >= 3)
                    {
                        (feedOverride, rapidOverride, spindleOverride) = ((int)ov[0], (int)ov[1], (int)ov[2]);
                    }

                    break;
                case "Bf":
                    var bf = Numbers(value);
                    blocks = (int)bf[0];
                    bytes = bf.Length > 1 ? (int)bf[1] : NoValue;
                    break;
                case "Ln":
                    line = ParseInt(value);
                    break;
                case "Pn":
                    pins = value;
                    break;
                case "A":
                    accessories = value;
                    break;
            }
        }

        var machinePosition = machine ?? (work ?? previous.WorkPosition) + offset;
        var workPosition = work ?? machinePosition - offset;
        return new GrblStatus(state, sub, machinePosition, workPosition, offset, feed, spindle, feedOverride, rapidOverride, spindleOverride,
            blocks, bytes, line, pins, accessories);
    }

    private static Axes ParseAxes(string value)
    {
        var numbers = Numbers(value);
        return numbers.Length >= 3 ? new Axes(numbers[0], numbers[1], numbers[2]) : throw new FormatException($"'{value}' does not hold three axes.");
    }

    private static double[] Numbers(string value)
        => value.Split(',').Select(v => double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();

    private static int ParseInt(string value) => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
}
