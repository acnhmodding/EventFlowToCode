using System.Text.Json;

namespace EventFlowToCode.Core.Metadata;

public static class HintsLoader
{
    public static Dictionary<string, string> Load(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return [];

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
    }
}