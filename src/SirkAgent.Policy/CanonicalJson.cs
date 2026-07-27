using System.Buffers;
using System.Text;
using System.Text.Json;

namespace SirkAgent.Policy;

public static class CanonicalJson
{
    public static byte[] SerializePayloadWithoutSignature(PolicyEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope));
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });

        WriteObjectWithoutSignature(document.RootElement, writer);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteObjectWithoutSignature(JsonElement root, Utf8JsonWriter writer)
    {
        writer.WriteStartObject();

        foreach (var property in root.EnumerateObject()
                     .Where(p => !string.Equals(p.Name, "signature", StringComparison.Ordinal))
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            writer.WritePropertyName(property.Name);
            WriteElement(property.Value, writer);
        }

        writer.WriteEndObject();
    }

    private static void WriteElement(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(property.Value, writer);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(item, writer);
                }
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
