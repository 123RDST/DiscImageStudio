using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscImageStudio.Core;

/// <summary>
/// Persists an <see cref="AngularResidualTable"/> as its knot list. The table normalizes itself
/// on construction (sorted, anchored, tangents derived), so only the measured points are stored.
/// </summary>
/// <remarks>
/// Registered where whole fit results are saved — a calibration session and a disc preset both
/// carry the table — so a saved session keeps the correction it measured instead of dropping it.
/// </remarks>
public sealed class AngularResidualTableJsonConverter : JsonConverter<AngularResidualTable>
{
    // A null token must still be accepted: older session files have no such property, and a
    // non-nullable record member must never be handed a null by the deserializer.
    public override bool HandleNull => true;

    public override AngularResidualTable Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return AngularResidualTable.Empty;
        List<AngularResidualPoint>? points = JsonSerializer.Deserialize<List<AngularResidualPoint>>(ref reader, options);
        if (points is null || points.Count == 0) return AngularResidualTable.Empty;
        try
        {
            return AngularResidualTable.Create(points);
        }
        catch (ArgumentException error)
        {
            throw new JsonException($"角度残差表无效：{error.Message}", error);
        }
    }

    public override void Write(Utf8JsonWriter writer, AngularResidualTable value, JsonSerializerOptions options)
    {
        if (value is null || value.IsEmpty)
        {
            writer.WriteStartArray();
            writer.WriteEndArray();
            return;
        }
        JsonSerializer.Serialize(writer, value.Points, options);
    }
}
