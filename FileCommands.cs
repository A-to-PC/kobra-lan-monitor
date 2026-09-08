using System.Text.Json;

namespace KobraLanMonitor;

public record PrinterFile(string FileName, string Path, long? SizeBytes, DateTimeOffset? ModifiedAt, bool IsDirectory);

public static class FileListParser
{
    private static readonly string[] GcodeExtensions = [".gcode", ".gco", ".gc", ".3mf"];

    /// <summary>
    /// The printer's exact list-response shape isn't documented anywhere we found, so this walks the
    /// response looking for anything that looks like a file record or filename rather than assuming one
    /// fixed layout -- same defensive approach KobraCache uses against the same undocumented responses.
    /// </summary>
    public static List<PrinterFile> Parse(JsonElement root)
    {
        var results = new List<PrinterFile>();
        Walk(root, results);
        return results;
    }

    private static void Walk(JsonElement element, List<PrinterFile> results)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Walk(item, results);
                break;

            case JsonValueKind.String:
                var s = element.GetString();
                if (LooksLikeFileName(s))
                    results.Add(new PrinterFile(s!, "/", null, null, false));
                break;

            case JsonValueKind.Object:
                if (TryParseFileRecord(element, out var file))
                {
                    results.Add(file);
                    return;
                }
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        Walk(prop.Value, results);
                }
                break;
        }
    }

    private static bool TryParseFileRecord(JsonElement item, out PrinterFile file)
    {
        var name = GetString(item, "filename") ?? GetString(item, "fileName") ?? GetString(item, "file_name")
            ?? GetString(item, "name");

        if (string.IsNullOrWhiteSpace(name))
        {
            file = null!;
            return false;
        }

        var isDir = GetBool(item, "is_dir") == true || GetBool(item, "isDir") == true;
        var path = GetString(item, "path") ?? GetString(item, "filepath") ?? "/";
        var size = GetLong(item, "filesize") ?? GetLong(item, "file_size") ?? GetLong(item, "size");
        var modified = GetDate(item, "timestamp") ?? GetDate(item, "update_time") ?? GetDate(item, "time");

        file = new PrinterFile(name!, path, size, modified, isDir);
        return true;
    }

    private static bool LooksLikeFileName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && GcodeExtensions.Any(ext => value.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    private static string? GetString(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static long? GetLong(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var n2)) return n2;
        return null;
    }

    private static bool? GetBool(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }

    private static DateTimeOffset? GetDate(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var unix))
        {
            var parsed = unix > 9_999_999_999 ? DateTimeOffset.FromUnixTimeMilliseconds(unix) : DateTimeOffset.FromUnixTimeSeconds(unix);
            // The printer sometimes writes a small counter instead of a real timestamp for files
            // tied to a completed/attempted print task (seen as low as 89263, i.e. ~1970) -- anything
            // before this project existed is obviously not a real file date, so don't show it.
            return parsed.Year < 2025 ? null : parsed;
        }
        if (v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var dt))
            return dt;
        return null;
    }
}
