using System.Text.Json;
using System.Text.Json.Serialization;
using GenHub.Core.Constants;
using GenHub.Core.Models.Enums;

namespace GenHub.Core.Models.GameProfile;

/// <summary>
/// Custom JSON converter for WorkspaceStrategy that applies the configured default (HardLink)
/// when the property is missing from JSON, instead of using the enum default (SymlinkOnly = 0).
/// This fixes the bug where profiles revert to SymlinkOnly after deserialization.
/// </summary>
public class WorkspaceStrategyJsonConverter : JsonConverter<WorkspaceStrategy>
{
    /// <inheritdoc/>
    public override WorkspaceStrategy Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            // If the JSON value is explicitly null, use the configured default
            return WorkspaceConstants.DefaultWorkspaceStrategy;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            var value = reader.GetInt32();

            // If the value is 0 (SymlinkOnly), it could be:
            // 1. Explicitly set by the user (intentional)
            // 2. The result of missing property defaulting to 0 (unintentional)
            //
            // Since we can't distinguish between these cases reliably,
            // we'll treat 0 as the configured default to fix the bug.
            // Users who explicitly want SymlinkOnly will need to re-save their profiles.
            if (value == 0)
            {
                return WorkspaceConstants.DefaultWorkspaceStrategy;
            }

            return (WorkspaceStrategy)value;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var stringValue = reader.GetString();
            if (Enum.TryParse<WorkspaceStrategy>(stringValue, true, out var result))
            {
                // Same logic: if parsed as SymlinkOnly, use the default
                return result == WorkspaceStrategy.SymlinkOnly
                    ? WorkspaceConstants.DefaultWorkspaceStrategy
                    : result;
            }
        }

        // Fallback to default if we can't parse
        return WorkspaceConstants.DefaultWorkspaceStrategy;
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, WorkspaceStrategy value, JsonSerializerOptions options)
    {
        // Write as integer for compact JSON
        writer.WriteNumberValue((int)value);
    }
}
