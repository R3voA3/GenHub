using System.Text.Json;
using System.Text.Json.Serialization;
using GenHub.Core.Constants;
using GenHub.Core.Models.Enums;

namespace GenHub.Core.Models.GameProfile;

/// <summary>
/// Custom JSON converter for WorkspaceStrategy.
/// Defaulting is applied by services, not in this converter.
/// This converter ensures that the value is correctly deserialized from JSON.
/// </summary>
public class WorkspaceStrategyJsonConverter : JsonConverter<WorkspaceStrategy?>
{
    /// <inheritdoc/>
    public override WorkspaceStrategy? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            return (WorkspaceStrategy)reader.GetInt32();
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var stringValue = reader.GetString();
            if (Enum.TryParse<WorkspaceStrategy>(stringValue, true, out var result))
            {
                return result;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, WorkspaceStrategy? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
        }
        else
        {
            // Write as string for better readability and to ensure 0 (SymlinkOnly) is explicit
            writer.WriteStringValue(value.Value.ToString());
        }
    }
}
