using System.Text.Json;

namespace VideoDownloader.Infrastructure.Json;

/// <summary>
/// .NET 10 JsonElement.TryGetInt*/TryGetDouble throw on JsonValueKind.Null —
/// always gate on Number before reading.
/// </summary>
internal static class JsonNumber
{
    public static bool TryInt32(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }

    public static bool TryInt64(JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }

    public static bool TryDouble(JsonElement element, out double value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value);
    }

    public static bool TryInt32Prop(JsonElement parent, string name, out int value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var el) && TryInt32(el, out value);
    }

    public static bool TryInt64Prop(JsonElement parent, string name, out long value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var el) && TryInt64(el, out value);
    }

    public static bool TryDoubleProp(JsonElement parent, string name, out double value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var el) && TryDouble(el, out value);
    }
}
