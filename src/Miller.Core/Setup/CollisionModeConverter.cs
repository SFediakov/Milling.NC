using System.Text.Json;
using System.Text.Json.Serialization;

namespace Miller.Core.Setup;

// The collision mode by name. Recursion and OneRun, removed in T-214, read as Backwards: the CPU copy of
// the mode left, which needs no GPU. Any other name must be a mode of the enum.
public sealed class CollisionModeConverter : JsonConverter<CollisionMode>
{
    public static readonly IReadOnlyList<string> RemovedModes = new[] { "Recursion", "OneRun" };

    public override CollisionMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"The collision mode must be a name, got {reader.TokenType}.");
        }

        var name = reader.GetString() ?? string.Empty;
        if (RemovedModes.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return CollisionMode.Backwards;
        }

        if (!int.TryParse(name, out _) && Enum.TryParse<CollisionMode>(name, true, out var mode) && Enum.IsDefined(mode))
        {
            return mode;
        }

        throw new JsonException($"Unknown collision mode '{name}'. Known modes: {string.Join(", ", Enum.GetNames<CollisionMode>())}.");
    }

    public override void Write(Utf8JsonWriter writer, CollisionMode value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}
