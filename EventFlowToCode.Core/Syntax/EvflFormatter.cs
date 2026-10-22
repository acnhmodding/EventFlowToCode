using EventFlowToCode.Core.Model;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EventFlowToCode.Core.Syntax;

public static partial class EvflFormatter
{
    private const int IndentSpaces = 4;

    public static string Indent(int level) => new(' ', IndentSpaces * level);

    public static string Identifier(string name) =>
        RawIdentifierRegex().IsMatch(name) ? name : QuoteIdentifier(name);

    public static string QuoteIdentifier(string value) =>
        $"`{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal)}`";

    internal static string FormatAction(ActionSignature action, IReadOnlyDictionary<string, object?> parameters, IReadOnlySet<string>? variableNames)
    {
        var useConversion = action.Conversion is not null && !HasExtraParameters(action.Parameters, parameters);
        var conversion = (useConversion ? action.Conversion : null)
            ?? $"<.name>({string.Join(", ", action.Parameters.Select(parameter => $"<{parameter.Name}>"))})";

        return FormatCall(
            action.ActorName,
            action.Name,
            action.Parameters,
            parameters,
            conversion,
            appendExtraParameters: !useConversion,
            variableNames: variableNames);
    }

    public static string FormatQuery(QuerySignature query, IReadOnlyDictionary<string, object?> parameters, bool negated) =>
        FormatQuery(query, parameters, negated, null);

    internal static string FormatQuery(QuerySignature query, IReadOnlyDictionary<string, object?> parameters, bool negated, IReadOnlySet<string>? variableNames)
    {
        var conversionObject = negated ? query.NegatedConversion : query.Conversion;
        var useConversion = !HasExtraParameters(query.Parameters, parameters);
        var conversion = useConversion
            ? ResolveQueryConversion(query, parameters, negated, conversionObject)
            : DefaultQueryConversion(query, negated);

        return FormatCall(
            query.ActorName,
            query.Name,
            query.Parameters,
            parameters,
            conversion,
            appendExtraParameters: !useConversion || query.Conversion is null,
            variableNames: variableNames);
    }

    // Shorthand has no place for fields it does not describe. An ordinary call
    // retains them when the game binary is newer than the function catalog.
    private static bool HasExtraParameters(IReadOnlyList<FunctionParameter> signature, IReadOnlyDictionary<string, object?> parameters) =>
        parameters.Keys.Any(name => !signature.Any(parameter => parameter.Name == name));

    public static string FormatType(FlowType type, object? value, bool forceBoolToInt = false) =>
        FormatType(type, value, forceBoolToInt, null);

    private static string FormatType(FlowType type, object? value, bool forceBoolToInt, IReadOnlySet<string>? variableNames)
    {
        if (value is FlowValue.Argument argument)
            return FormatArgument(argument.Name);

        if (type.Name.StartsWith("int", StringComparison.Ordinal))
            return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

        if (type.Name.StartsWith("enum", StringComparison.Ordinal))
        {
            var index = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            var values = type.Name[5..^1].Split(',');
            if (index < 0 || index >= values.Length) return index.ToString(CultureInfo.InvariantCulture);
            var key = values[index].Trim();
            return variableNames?.Contains(key) == true ? ReprString(key) : QuoteIdentifier(key);
        }

        if (type == FlowType.Float)
            return FormatPythonFloat(Convert.ToSingle(value, CultureInfo.InvariantCulture));

        if (type == FlowType.String)
            return ReprString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);

        if (type == FlowType.Bool || type == FlowType.Any && value is bool)
        {
            var boolValue = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            return forceBoolToInt ? (boolValue ? "1" : "0") : (boolValue ? "true" : "false");
        }

        if (type == FlowType.Actor || value is FlowValue.ActorMarker)
        {
            var actor = (FlowValue.ActorMarker)value!;
            return Identifier(actor.Name) + (string.IsNullOrEmpty(actor.SecondaryName) ? string.Empty : $"@{Identifier(actor.SecondaryName)}");
        }

        if (type == FlowType.Any)
            return FormatAny(value);

        throw new InvalidOperationException($"Bad EVFL type: {type.Name}");
    }

    public static FlowType InferType(object? value) =>
        value switch
        {
            bool => FlowType.Bool,
            float or double => FlowType.Float,
            string => FlowType.String,
            int or short or long or byte => FlowType.Int,
            FlowValue.ActorMarker => FlowType.Actor,
            _ => FlowType.Any,
        };

    public static string FormatPythonRepr(object? value) => FormatAny(value);

    internal static string FormatActorName(ActorName actorName) =>
        Identifier(actorName.Name)
        + (string.IsNullOrEmpty(actorName.SecondaryName) ? string.Empty : $"@{Identifier(actorName.SecondaryName)}")
        + (string.IsNullOrEmpty(actorName.ArgumentName) ? string.Empty : $"({Identifier(actorName.ArgumentName)})");

    private static string ApplyActorPlaceholders(string conversion, string formattedActorName, ActorName actorName, string functionName) =>
        conversion
            .Replace("<.name>", $"{formattedActorName}.{Identifier(functionName)}", StringComparison.Ordinal)
            .Replace("<.actor>", formattedActorName, StringComparison.Ordinal)
            .Replace("<.actor_name>", Identifier(actorName.Name), StringComparison.Ordinal)
            .Replace("<.actor_secondary>", string.IsNullOrEmpty(actorName.SecondaryName) ? string.Empty : $"@{Identifier(actorName.SecondaryName)}", StringComparison.Ordinal);

    private static string FormatCall(ActorName actorName, string functionName,
        IReadOnlyList<FunctionParameter> signatureParameters, IReadOnlyDictionary<string, object?> parameters,
        string conversion, bool appendExtraParameters, IReadOnlySet<string>? variableNames)
    {
        var actorText = FormatActorName(actorName);
        conversion = ApplyActorPlaceholders(conversion, actorText, actorName, functionName);

        foreach (var parameter in signatureParameters)
        {
            var rawValue = parameters[parameter.Name];
            var rawText = Convert.ToString(rawValue, CultureInfo.InvariantCulture) ?? string.Empty;
            var value = IsEntryVariableKeyPassthrough(parameter.Name, rawValue)
                ? rawText
                : FormatType(parameter.Type, rawValue, forceBoolToInt: false, variableNames);

            conversion = conversion.Replace($"<<{parameter.Name}>>", Identifier(rawText), StringComparison.Ordinal);
            conversion = conversion.Replace($"<{parameter.Name}>", value, StringComparison.Ordinal);
        }

        return appendExtraParameters
            ? AppendExtraParameters(conversion, signatureParameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal), parameters)
            : conversion;
    }

    private static string DefaultQueryConversion(QuerySignature query, bool negated)
    {
        var conversion = $"<.name>({string.Join(", ", query.Parameters.Select(parameter => $"<{parameter.Name}>"))})";
        return negated ? $"not {conversion}" : conversion;
    }

    private static string ResolveQueryConversion(QuerySignature query, IReadOnlyDictionary<string, object?> parameters,
        bool negated, object? conversionObject)
    {
        // Complementary table entries may describe the same condition using a
        // different stored query mode. Negate the original expression so an edit
        // and recompile retain that mode as well as the branch behavior.
        if (negated && query.Conversion is ConversionTable positiveTable)
        {
            var index = Convert.ToInt32(parameters[positiveTable.Key], CultureInfo.InvariantCulture);
            return $"not ({positiveTable.Values[index]})";
        }

        if (conversionObject is string conversion)
            return conversion;

        if (conversionObject is ConversionTable table)
        {
            var index = Convert.ToInt32(parameters[table.Key], CultureInfo.InvariantCulture);
            return table.Values[index];
        }

        if (negated && query.Conversion is string positiveConversion)
            return $"not {positiveConversion}";

        return DefaultQueryConversion(query, negated);
    }

    private static string AppendExtraParameters(string conversion, HashSet<string> specifiedParameters, IReadOnlyDictionary<string, object?> parameters)
    {
        var extraParameters = parameters
            .Where(pair => !specifiedParameters.Contains(pair.Key))
            .Select(pair => $"{Identifier(pair.Key)}={FormatType(InferType(pair.Value), pair.Value)}")
            .Order(StringComparer.Ordinal)
            .ToList();

        if (extraParameters.Count == 0)
            return conversion;

        var separator = specifiedParameters.Count > 0 ? ", " : string.Empty;
        return conversion[..^1] + separator + string.Join(", ", extraParameters) + ")";
    }

    private static string FormatAny(object? value) =>
        value switch
        {
            null => "None",
            bool boolValue => boolValue ? "True" : "False",
            string stringValue => ReprString(stringValue),
            float floatValue => FormatPythonFloat(floatValue),
            double doubleValue => FormatPythonDouble(doubleValue),
            FlowValue.Argument argument => FormatArgument(argument.Name),
            FlowValue.ActorMarker actor => Identifier(actor.Name) + (string.IsNullOrEmpty(actor.SecondaryName) ? string.Empty : $"@{Identifier(actor.SecondaryName)}"),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };

    private static string ReprString(string value)
    {
        StringBuilder builder = new("'");
        foreach (var ch in value)
        {
            builder.Append(ch switch
            {
                '\\' => "\\\\",
                '\'' => "\\'",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => ch,
            });
        }

        builder.Append('\'');
        return builder.ToString();
    }

    private static string FormatArgument(string name) => name is "true" or "false" or "not" or "and" or "or" or "in" ? QuoteIdentifier(name) : Identifier(name);

    private static string FormatPythonFloat(float value) => FormatPythonDouble(value);

    private static string FormatPythonDouble(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) || text.Contains('e', StringComparison.Ordinal)
            ? text
            : $"{text}.0";
    }

    private static bool IsEntryVariableKeyPassthrough(string parameterName, object? rawValue)
    {
        var value = Convert.ToString(rawValue, CultureInfo.InvariantCulture) ?? string.Empty;
        return parameterName == $"EntryVariableKeyInt_{value}"
            || parameterName == $"EntryVariableKeyBool_{value}"
            || parameterName == $"EntryVariableKeyFloat_{value}";
    }

    [GeneratedRegex(@"^[A-Za-z](?:[A-Za-z0-9_\-]*[A-Za-z0-9]|[A-Za-z0-9]*)$")]
    private static partial Regex RawIdentifierRegex();
}