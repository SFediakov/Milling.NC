using System.Numerics;
using System.Text.Json.Serialization;

namespace Miller.Core.Setup;

// One STL on the table: after the axis orientation the model is turned about Z around its own
// center by RotationZ (degrees) and moved by Offset (mm). Machine zero is applied afterwards to
// every model together, so offsets are relative between the models and the stock.
public sealed class ModelPlacement
{
    public string StlPath { get; set; } = string.Empty;

    // Shown in the model list; the file name when empty.
    public string Name { get; set; } = string.Empty;

    public Vector3 Offset { get; set; } = Vector3.Zero;

    public float RotationZ { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrEmpty(Name) ? Path.GetFileName(StlPath) : Name;
}
