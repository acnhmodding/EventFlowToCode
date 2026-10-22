using EventFlowToCode.Core.Model;

namespace EventFlowToCode.Core.Compile;

internal static class CompileArgumentBinder
{
    public static CompileValue Bind(CompileValue value, FlowType expected,
        IReadOnlyDictionary<string, FlowType> variables, bool allowImplicitActor, string context)
    {
        if (value.Value is FlowValue.Argument argument)
        {
            if (variables.TryGetValue(argument.Name, out var declared))
            {
                CheckType(declared, expected, context);
                // Unknown signatures stay unknown. Another flow may reach this
                // instruction with the same argument name but a different type.
                return value with { Type = expected, IsBoundVariable = true };
            }
            if (!value.IsBoundVariable && expected.Name.StartsWith("enum[", StringComparison.Ordinal))
            {
                var keys = expected.Name[5..^1].Split(',').Select(key => key.Trim()).ToArray();
                var index = Array.IndexOf(keys, argument.Name);
                if (index >= 0) return new CompileValue(FlowType.Int, index);
            }
            if (!value.IsBoundVariable && (expected == FlowType.Actor || expected == FlowType.Any && allowImplicitActor))
            {
                return new CompileValue(FlowType.Actor, new FlowValue.ActorMarker(argument.Name, string.Empty));
            }
            throw new InvalidDataException($"Variable {argument.Name} not defined in {context}.");
        }
        CheckType(value.Type, expected, context);
        return value;
    }

    internal static void CheckType(FlowType actual, FlowType expected, string context)
    {
        var integerCompatible = actual == FlowType.Int
            && (expected.Name.StartsWith("int", StringComparison.Ordinal) || expected.Name.StartsWith("enum[", StringComparison.Ordinal));
        if (actual != FlowType.Any && expected != FlowType.Any && actual != expected && !integerCompatible)
            throw new InvalidDataException($"{context}: expected {expected}, got {actual}.");
        
    }
}
