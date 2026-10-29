using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace McDownload.Core.Meta;

/// <summary>
/// Tolerant <see cref="DateTimeOffset"/> converter. Mojang emits strict ISO-8601, but Fabric
/// and Quilt emit offsets without a colon (<c>2026-10-26T19:07:24+0000</c>) and some old
/// third-party version files contain empty strings; all of those must not break parsing.
/// </summary>
public sealed class LenientDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
            {
                var text = reader.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                if (DateTimeOffset.TryParse(
                        text,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                {
                    return parsed;
                }

                // Last resort for "yyyyMMddHHmmss" style values.
                if (DateTime.TryParseExact(
                        text,
                        ["yyyyMMddHHmmss", "yyyy-MM-dd HH:mm:ss"],
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var fallback))
                {
                    return new DateTimeOffset(fallback, TimeSpan.Zero);
                }

                return null;
            }

            case JsonTokenType.Number when reader.TryGetInt64(out var unix):
                return DateTimeOffset.FromUnixTimeSeconds(unix);
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value.Value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
        }
    }
}
