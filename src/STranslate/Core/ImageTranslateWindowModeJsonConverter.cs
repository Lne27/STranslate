using System.Text.Json;
using System.Text.Json.Serialization;

namespace STranslate.Core;

/// <summary>旧预览版的 pinned 设置迁移到生成贴图所需的 Compact 入口。</summary>
internal sealed class ImageTranslateWindowModeJsonConverter : JsonConverter<ImageTranslateWindowMode>
{
    private static readonly JsonConverter<ImageTranslateWindowMode> EnumConverter =
        (JsonConverter<ImageTranslateWindowMode>)new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
            .CreateConverter(typeof(ImageTranslateWindowMode), new JsonSerializerOptions());

    public override ImageTranslateWindowMode Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String &&
            string.Equals(reader.GetString(), "pinned", StringComparison.OrdinalIgnoreCase) ||
            reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value) && value == 2)
            return ImageTranslateWindowMode.Compact;
        return EnumConverter.Read(ref reader, type, options);
    }

    public override void Write(Utf8JsonWriter writer, ImageTranslateWindowMode value, JsonSerializerOptions options) =>
        EnumConverter.Write(writer, value, options);
}
