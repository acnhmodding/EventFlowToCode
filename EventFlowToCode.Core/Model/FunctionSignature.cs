namespace EventFlowToCode.Core.Model;

public sealed record FunctionParameter(string Name, FlowType Type);

public sealed record ActionSignature(
    ActorName ActorName,
    string Name,
    IReadOnlyList<FunctionParameter> Parameters,
    bool Varargs,
    string? Conversion);

public sealed record QuerySignature(
    ActorName ActorName,
    string Name,
    IReadOnlyList<FunctionParameter> Parameters,
    bool Varargs,
    FlowType ReturnType,
    bool Inverted,
    object? Conversion,
    object? NegatedConversion);

public sealed record ActorName(string Name, string SecondaryName = "", string ArgumentName = "");

public sealed record ConversionTable(string Key, IReadOnlyList<string> Values);

public sealed class FunctionCatalog
{
    public Dictionary<string, ActionPrototype> Actions { get; } = [];
    public Dictionary<string, QueryPrototype> Queries { get; } = [];

    public ActionSignature ResolveAction(ActorName actorName, string name) =>
        Actions.TryGetValue(name, out var prototype)
            ? new ActionSignature(actorName, name, prototype.Parameters, prototype.Varargs, prototype.Conversion)
            : new ActionSignature(actorName, name, [], true, null);

    public QuerySignature ResolveQuery(ActorName actorName, string name, FlowType? fallbackReturnType = null) =>
        Queries.TryGetValue(name, out var prototype)
            ? new QuerySignature(actorName, name, prototype.Parameters, prototype.Varargs, prototype.ReturnType, prototype.Inverted, prototype.Conversion, prototype.NegatedConversion)
            : new QuerySignature(actorName, name, [], true, fallbackReturnType ?? FlowType.Int, false, null, null);
}

public sealed record ActionPrototype(
    IReadOnlyList<FunctionParameter> Parameters,
    bool Varargs,
    string? Conversion,
    string? ParseRule = null);

public sealed record QueryPrototype(
    IReadOnlyList<FunctionParameter> Parameters,
    bool Varargs,
    FlowType ReturnType,
    bool Inverted,
    object? Conversion,
    object? NegatedConversion,
    string? ParseType = null,
    string? ParseRule = null);
