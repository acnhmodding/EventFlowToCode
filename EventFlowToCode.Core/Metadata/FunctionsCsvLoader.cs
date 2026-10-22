using CsvHelper;
using CsvHelper.Configuration;
using EventFlowToCode.Core.Model;
using System.Globalization;

namespace EventFlowToCode.Core.Metadata;

public static class FunctionsCsvLoader
{
    public static FunctionCatalog Load(string path)
    {
        if (!File.Exists(path))
            return new FunctionCatalog();
        
        using var reader = File.OpenText(path);
        return Load(reader);
    }

    /// <summary>Loads function metadata without taking ownership of the reader.</summary>
    public static FunctionCatalog Load(TextReader reader)
    {
        FunctionCatalog catalog = new();
        using CsvReader csv = new(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            IgnoreBlankLines = true,
            MissingFieldFound = null,
        }, leaveOpen: true);

        if (!csv.Read() || !csv.ReadHeader() || csv.HeaderRecord is null)
            return catalog;

        var headers = csv.HeaderRecord.ToHashSet(StringComparer.Ordinal);

        string[] requiredHeaders = ["Type", "Name", "Parameters", "Return", "ConversionKey", "Conversion", "NegatedConversion"];
        foreach (var header in requiredHeaders)
            if (!headers.Contains(header))
                throw new InvalidDataException($"functions.csv is missing required header '{header}'.");

        while (csv.Read())
        {
            var type = Get(csv, "Type");
            var name = Get(csv, "Name");

            if (string.IsNullOrWhiteSpace(name)) continue;

            var parameterParts = Get(csv, "Parameters")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            var varargs = parameterParts.Count > 0 && parameterParts[^1] == "...";
            if (varargs) parameterParts.RemoveAt(parameterParts.Count - 1);

            var parameters = parameterParts.Select(ParseParameter).ToList();
            var conversion = ParseConversion(
                Get(csv, "ConversionKey"),
                Get(csv, "Conversion"));
            var negatedConversion = ParseConversion(
                Get(csv, "ConversionKey"),
                Get(csv, "NegatedConversion"));
            var parseType = Get(csv, "ParseType");
            var parseRule = Get(csv, "ParseRule");

            if (type == "Action")
            {
                catalog.Actions.TryAdd(name, new ActionPrototype(parameters, varargs, conversion as string, EmptyToNull(parseRule)));
            }
            else if (type == "Query")
            {
                var returnType = Get(csv, "Return");
                var inverted = returnType == "inverted_bool";
                if (inverted) returnType = "bool";
                
                catalog.Queries.TryAdd(name, new QueryPrototype(
                    parameters,
                    varargs,
                    string.IsNullOrEmpty(returnType) ? FlowType.Any : new FlowType(returnType),
                    inverted,
                    conversion,
                    negatedConversion,
                    EmptyToNull(parseType),
                    EmptyToNull(parseRule)));
            }
            else throw new InvalidDataException($"Unknown function type '{type}' in functions.csv.");
        }

        return catalog;
    }

    private static FunctionParameter ParseParameter(string value)
    {
        var parts = value.Split(':', 2, StringSplitOptions.TrimEntries);

        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            throw new InvalidDataException($"Bad parameter declaration '{value}'.");

        var typeName = parts[1].StartsWith("Enum", StringComparison.Ordinal) ? "str" : parts[1];
        return new FunctionParameter(parts[0], new FlowType(typeName));
    }

    private static object? ParseConversion(string key, string value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        if (string.IsNullOrEmpty(key))
            return value;

        return new ConversionTable(key, [.. value.Split('\n').Select(line => line.Trim())]);
    }

    private static string Get(CsvReader csv, string name) =>
        csv.TryGetField(name, out string? value) ? value ?? string.Empty : string.Empty;

    private static string? EmptyToNull(string value) => string.IsNullOrEmpty(value) ? null : value;
}