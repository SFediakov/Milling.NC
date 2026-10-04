namespace Miller.Core.Simulation;

// The status byte the native generation keeps per cell (miller_native.h MN_CELL_*): Model is the
// part that shouldn't be removed, ShouldRemove is stock the head hit that the next pass cuts to
// its closing first, Collision marks a cell the kept pass's check entered and could not resolve,
// Forbidden a tool position raised until its head clears, Bridge a cut-through cell of a holding
// bridge that keeps the bridge height above the floor (separation scope).
[Flags]
public enum CellStatus : byte
{
    None = 0,
    Model = 1,
    ShouldRemove = 2,
    Collision = 4,
    Forbidden = 8,
    Bridge = 16,
}
