using System.Numerics;

namespace Miller.Core.Simulation;

public enum SimulationEventKind
{
    HeadCollision,
    RapidIntoMaterial,
}

// A problem found during simulation, located by the segment and the tool tip position.
public sealed record SimulationEvent(SimulationEventKind Kind, int SegmentIndex, Vector3 Position, string Message);
