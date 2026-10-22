using BfevLibrary;
using BfevLibrary.Core;
using System.Text.Json;

namespace EventFlowToCode.Core.Bfev;

internal static class ExplicitFlowText
{
    private const string Prefix = "evfl-graph-json\n";
    private const int SchemaVersion = 1;

    public static string Serialize(BfevFile file)
    {
        using var document = JsonDocument.Parse(file.ToJson());
        object source = new
        {
            SchemaVersion,
            Bfev = document.RootElement.Clone(),
            EntryPointOrder = file.Flowcharts.ToDictionary(
                flowchart => flowchart.Key,
                flowchart => flowchart.Value.EntryPoints.Select(pair => pair.Key).ToArray()),
            VariableOrder = file.Flowcharts.ToDictionary(
                flowchart => flowchart.Key,
                flowchart => flowchart.Value.EntryPoints
                    .ToDictionary(
                        entryPoint => entryPoint.Key,
                        entryPoint => entryPoint.Value.Variables is null
                            ? []
                            : entryPoint.Value.Variables
                                .Select(variable => variable.Key)
                                .ToList(),
                        StringComparer.Ordinal),
                StringComparer.Ordinal),
        };
        return Prefix + JsonSerializer.Serialize(source, new JsonSerializerOptions { WriteIndented = true });
    }

    public static bool TryDeserialize(string source, out BfevFile file)
    {
        var prefixLength = source.StartsWith(Prefix, StringComparison.Ordinal) ? Prefix.Length
            : source.StartsWith("evfl-graph-json\r\n", StringComparison.Ordinal) ? Prefix.Length + 1 : 0;
        if (prefixLength == 0)
        {
            file = null!;
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(source[prefixLength..]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("SchemaVersion", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema) || schema != SchemaVersion)
            {
                throw new InvalidDataException($"Unsupported evfl-graph-json schema version.");
            }

            if (!root.TryGetProperty("Bfev", out var bfev)
                || bfev.ValueKind != JsonValueKind.Object
                || !bfev.TryGetProperty("Flowcharts", out var flowcharts) || flowcharts.ValueKind != JsonValueKind.Object
                || !bfev.TryGetProperty("Timelines", out var timelines) || timelines.ValueKind != JsonValueKind.Object
                || !bfev.TryGetProperty("FileName", out var fileName) || fileName.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("VariableOrder", out var variableOrder)
                || variableOrder.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("evfl-graph-json is missing required graph data.");
            }

            file = BfevFile.FromJson(bfev.GetRawText());
            BfevGraphValidator.RequireSingleFlowchart(file);
            if (root.TryGetProperty("EntryPointOrder", out var entryPointOrder))
            {
                foreach (var property in entryPointOrder.EnumerateObject())
                {
                    if (!file.Flowcharts.TryGetValue(property.Name, out var flowchart))
                        throw new InvalidDataException($"Unknown flowchart in entrypoint order: '{property.Name}'.");
                    var names = OrderedNames(property.Value, flowchart.EntryPoints.Keys);
                    flowchart.EntryPoints = new(names.Select(name =>
                        new KeyValuePair<string, EntryPoint>(name, flowchart.EntryPoints[name])));
                }
            }
            RestoreVariableOrder(file, variableOrder);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NullReferenceException)
        {
            throw new InvalidDataException("Invalid evfl-graph-json graph data.", ex);
        }
    }

    private static string[] OrderedNames(JsonElement element, IEnumerable<string> keys)
    {
        var names = element.EnumerateArray().Select(value => value.GetString()
            ?? throw new InvalidDataException("Order names cannot be null.")).ToArray();
        HashSet<string> known = new(keys, StringComparer.Ordinal);
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(name => !known.Contains(name)))
            throw new InvalidDataException("Order metadata contains duplicate or unknown names.");
        return names.Concat(keys.Where(name => !names.Contains(name, StringComparer.Ordinal))).ToArray();
    }

    private static void RestoreVariableOrder(BfevFile file, JsonElement variableOrder)
    {
        foreach (var flowchartProperty in variableOrder.EnumerateObject())
        {
            if (!file.Flowcharts.TryGetValue(flowchartProperty.Name, out var flowchart))
            {
                throw new InvalidDataException($"Unknown flowchart in variable order: '{flowchartProperty.Name}'.");
            }

            foreach (var entryPointProperty in flowchartProperty.Value.EnumerateObject())
            {
                if (!flowchart.EntryPoints.TryGetValue(entryPointProperty.Name, out var entryPoint)
                    || entryPointProperty.Value.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException($"Invalid variable order for '{entryPointProperty.Name}'.");
                }

                var orderedNames = OrderedNames(entryPointProperty.Value, entryPoint.Variables?.Keys ?? Enumerable.Empty<string>());
                if (entryPoint.Variables is null) continue;
                entryPoint.Variables = new(
                    orderedNames
                        .Select(name => new KeyValuePair<string, VariableDef>(name, entryPoint.Variables[name])));
            }
        }
    }
}
