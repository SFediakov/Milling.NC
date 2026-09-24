using System.Globalization;

namespace Miller.Machine.Grbl;

public enum MachineAxis
{
    X,
    Y,
    Z,
}

// The command lines of the machine panel, in millimetres whatever the controller's default unit is.
// Jogging uses $J=, which Grbl keeps apart from the program state and cancels with 0x85; zero uses
// G10 L20 P0, which sets the active work coordinate system so that the current position reads as
// the given value and survives a reset.
public static class GrblCommands
{
    public const string Home = "$H";
    public const string Unlock = "$X";
    public const string GoToXyZero = "G90 G0 X0 Y0";
    public const double MaxJogDistance = 1000;
    public const double MaxFeed = 100_000;
    public const double MaxProbeTravel = 200;
    private const string NumberFormat = "0.###";

    public static string Jog(MachineAxis axis, double distance, double feed)
    {
        if (!double.IsFinite(distance) || distance == 0 || Math.Abs(distance) > MaxJogDistance)
        {
            throw new ArgumentOutOfRangeException(nameof(distance), $"A jog step must be non-zero and at most {MaxJogDistance} mm, got {distance}.");
        }

        CheckFeed(feed);
        return $"$J=G91 G21 {axis}{Number(distance)} F{Number(feed)}";
    }

    public static string Zero(bool x, bool y, bool z)
    {
        if (!x && !y && !z)
        {
            throw new ArgumentException("Zero needs at least one axis.");
        }

        return "G10 L20 P0" + (x ? " X0" : string.Empty) + (y ? " Y0" : string.Empty) + (z ? " Z0" : string.Empty);
    }

    // Touch plate on the work surface: probe down, set Z so the contact point reads as the plate
    // thickness, lift clear. A missed contact ends in alarm 5 and the lines after it are not sent.
    public static string[] ProbeZ(double plateThickness, double maxTravel, double feed, double retract)
    {
        if (!double.IsFinite(plateThickness) || plateThickness < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(plateThickness), $"The plate thickness must be zero or more, got {plateThickness}.");
        }

        if (!double.IsFinite(maxTravel) || maxTravel <= 0 || maxTravel > MaxProbeTravel)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTravel), $"The probe travel must be above 0 and at most {MaxProbeTravel} mm, got {maxTravel}.");
        }

        if (!double.IsFinite(retract) || retract <= 0 || retract > MaxProbeTravel)
        {
            throw new ArgumentOutOfRangeException(nameof(retract), $"The retract must be above 0 and at most {MaxProbeTravel} mm, got {retract}.");
        }

        CheckFeed(feed);
        return new[]
        {
            $"G21 G91 G38.2 Z-{Number(maxTravel)} F{Number(feed)}",
            $"G10 L20 P0 Z{Number(plateThickness)}",
            $"G0 Z{Number(retract)}",
            "G90",
        };
    }

    // The rectangle around the program in XY at the current height, rapid moves, back to the start
    // corner, so the operator sees where the program will cut.
    public static string[] Outline(GrblBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        return new[]
        {
            $"G21 G90 G0 X{Number(bounds.MinX)} Y{Number(bounds.MinY)}",
            $"G0 X{Number(bounds.MaxX)} Y{Number(bounds.MinY)}",
            $"G0 X{Number(bounds.MaxX)} Y{Number(bounds.MaxY)}",
            $"G0 X{Number(bounds.MinX)} Y{Number(bounds.MaxY)}",
            $"G0 X{Number(bounds.MinX)} Y{Number(bounds.MinY)}",
        };
    }

    private static void CheckFeed(double feed)
    {
        if (!double.IsFinite(feed) || feed <= 0 || feed > MaxFeed)
        {
            throw new ArgumentOutOfRangeException(nameof(feed), $"The feed must be above 0 and at most {MaxFeed} mm/min, got {feed}.");
        }
    }

    private static string Number(double value) => value.ToString(NumberFormat, CultureInfo.InvariantCulture);
}
