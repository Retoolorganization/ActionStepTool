using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ActionstepGet;

internal static class ResponseDisplay
{
    public static string Combine(IReadOnlyList<ActionstepClient.GetResponse> pages)
    {
        if (pages.Count == 0) return "";
        if (pages.Count == 1) return pages[0].Body;
        var array = new JsonArray();
        foreach (var page in pages)
        {
            try { array.Add(JsonNode.Parse(page.Body)); }
            catch (JsonException) { array.Add(JsonValue.Create(page.Body)); }
        }
        return new JsonObject { ["pages"] = array }.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true });
    }

    public static string ToYaml(string json)
    {
        using var document = JsonDocument.Parse(json);
        var stream = new YamlStream(new YamlDocument(ToNode(document.RootElement)));
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static YamlNode ToNode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new YamlMappingNode();
                foreach (var field in element.EnumerateObject())
                    map.Add(new YamlScalarNode(field.Name), ToNode(field.Value));
                return map;
            case JsonValueKind.Array:
                var list = new YamlSequenceNode();
                foreach (var item in element.EnumerateArray()) list.Add(ToNode(item));
                return list;
            case JsonValueKind.String:
                // Explicit quotes preserve IDs, dates, booleans, and empty strings as strings.
                return new YamlScalarNode(element.GetString()) { Style = ScalarStyle.DoubleQuoted };
            default:
                return new YamlScalarNode(element.GetRawText()) { Style = ScalarStyle.Plain };
        }
    }

    public static bool TryReadNextPage(string body, string resource, out int? nextPage)
    {
        nextPage = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
                !meta.TryGetProperty("paging", out var paging) || paging.ValueKind != JsonValueKind.Object ||
                !paging.TryGetProperty(resource, out var details) || details.ValueKind != JsonValueKind.Object ||
                !details.TryGetProperty("nextPage", out var next)) return false;
            if (next.ValueKind == JsonValueKind.Null) return true;
            var value = next.ValueKind == JsonValueKind.String ? next.GetString() : next.GetRawText();
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
                throw new InvalidOperationException("The API returned an unrecognized nextPage value.");
            nextPage = number;
            return true;
        }
        catch (JsonException) { return false; }
    }

    public static Uri WithPage(Uri uri, int page)
    {
        var parts = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Uri.UnescapeDataString(p.Split('=', 2)[0]) != "page")
            .ToList();
        parts.Add("page=" + page.ToString(CultureInfo.InvariantCulture));
        return new UriBuilder(uri) { Query = string.Join("&", parts) }.Uri;
    }
}
