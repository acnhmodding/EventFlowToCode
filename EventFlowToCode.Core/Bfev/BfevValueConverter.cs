using BfevLibrary.Core;
using EventFlowToCode.Core.Model;

namespace EventFlowToCode.Core.Bfev;

public static class BfevValueConverter
{
    public static List<VariableDefinition> FromVariableDefinitions(IEnumerable<KeyValuePair<string, VariableDef>>? variables)
    {
        if (variables is null)
        {
            return [];
        }

        return [.. variables.Select(variable =>
        {
            (var type, var value) = FromVariableDefinition(variable.Value);
            return new VariableDefinition(variable.Key, type, value);
        })];
    }

    public static Dictionary<string, object?> FromContainer(Container? container)
    {
        if (container is null) return [];

        return container.ToDictionary(pair => pair.Key, pair => FromContainerItem(pair.Value));
    }

    public static RadixTree<VariableDef> ToVariableDefinitions(IEnumerable<VariableDefinition> variables) =>
        new(variables
            .Where(variable => variable.InitialValue is not null)
            .Select(variable => new KeyValuePair<string, VariableDef>(variable.Name, ToVariableDefinition(variable))));

    public static Container ToContainer(IReadOnlyDictionary<string, object?> values) =>
        new(values.Select(pair => new KeyValuePair<string, ContainerItem>(pair.Key, ToContainerItem(pair.Value))));

    private static (FlowType Type, object? Value) FromVariableDefinition(VariableDef variable)
    {
        if (variable.Int is not null)
        {
            return (FlowType.Int, variable.Int.Value);
        }
        if (variable.Bool is not null)
        {
            return (FlowType.Bool, variable.Bool.Value);
        }
        if (variable.Float is not null)
        {
            return (FlowType.Float, variable.Float.Value);
        }

        throw new NotSupportedException("Variable arrays cannot be represented in readable EVFL.");
    }

    private static object? FromContainerItem(ContainerItem item)
    {
        if (item.Argument is not null)
        {
            return new FlowValue.Argument(item.Argument);
        }
        if (item.Int is not null)
        {
            return item.Int.Value;
        }
        if (item.Bool is not null)
        {
            return item.Bool.Value;
        }
        if (item.Float is not null)
        {
            return item.Float.Value;
        }
        if (item.String is not null)
        {
            return item.String;
        }
        if (item.ActorIdentifier is not null)
        {
            return new FlowValue.ActorMarker(item.ActorIdentifier.Item1, item.ActorIdentifier.Item2);
        }
        if (item.IntArray is not null)
        {
            return item.IntArray;
        }
        if (item.BoolArray is not null)
        {
            return item.BoolArray;
        }
        if (item.FloatArray is not null)
        {
            return item.FloatArray;
        }
        if (item.StringArray is not null)
        {
            return item.StringArray;
        }
        if (item.Items is not null)
        {
            return item.Items.ToDictionary(pair => pair.Key, pair => FromContainerItem(pair.Value));
        }

        throw new NotSupportedException("This parameter type cannot be represented in readable EVFL.");
    }

    private static VariableDef ToVariableDefinition(VariableDefinition variable)
    {
        VariableDef result = new();
        if (variable.InitialValue is null)
        {
            throw new ArgumentException("BFEV variable definitions require an initial value.", nameof(variable));
        }

        if (variable.Type == FlowType.Bool)
        {
            result.Bool = Convert.ToBoolean(variable.InitialValue, System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (variable.Type == FlowType.Float)
        {
            result.Float = Convert.ToSingle(variable.InitialValue, System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            result.Int = Convert.ToInt32(variable.InitialValue, System.Globalization.CultureInfo.InvariantCulture);
        }

        return result;
    }

    private static ContainerItem ToContainerItem(object? value)
    {
        ContainerItem item = new();
        switch (value)
        {
            case FlowValue.Argument argument:
                item.Argument = argument.Name;
                break;
            case FlowValue.ActorMarker actor:
                item.ActorIdentifier = new Tuple<string, string>(actor.Name, actor.SecondaryName);
                break;
            case IReadOnlyDictionary<string, object?> nested:
                item.Items = ToContainer(nested);
                break;
            case int[] values:
                item.IntArray = values;
                break;
            case bool[] values:
                item.BoolArray = values;
                break;
            case float[] values:
                item.FloatArray = values;
                break;
            case string[] values:
                item.StringArray = values;
                break;
            case bool boolValue:
                item.Bool = boolValue;
                break;
            case float floatValue:
                item.Float = floatValue;
                break;
            case double doubleValue:
                item.Float = Convert.ToSingle(doubleValue, System.Globalization.CultureInfo.InvariantCulture);
                break;
            case string stringValue:
                item.String = stringValue;
                break;
            case null:
                throw new ArgumentException("BFEV container items cannot encode null values.", nameof(value));
            default:
                item.Int = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
                break;
        }

        return item;
    }
}
