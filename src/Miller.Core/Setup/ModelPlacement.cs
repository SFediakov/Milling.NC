using System.Numerics;
using System.Text.Json.Serialization;

namespace Miller.Core.Setup;

// One STL on the table: after the axis orientation the model is scaled per axis by Scale and turned
// about Z by RotationZ (degrees), both around its own oriented center, and moved by Offset (mm).
// Machine zero is applied afterwards to every model together, so offsets are relative between the
// models and the stock.
public sealed class ModelPlacement
{
    public string StlPath { get; set; } = string.Empty;

    // Shown in the model list; the file name when empty.
    public string Name { get; set; } = string.Empty;

    public Vector3 Offset { get; set; } = Vector3.Zero;

    public float RotationZ { get; set; }

    // Factors along the model's oriented axes, applied before the turn about Z.
    public Vector3 Scale { get; set; } = Vector3.One;

    // Editing rule only: one factor for all three axes. The transform always reads Scale.
    public bool LinkedScale { get; set; } = true;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrEmpty(Name) ? Path.GetFileName(StlPath) : Name;
}
