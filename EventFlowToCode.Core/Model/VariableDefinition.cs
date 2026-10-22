namespace EventFlowToCode.Core.Model;

public sealed record VariableDefinition(string Name, FlowType Type, object? InitialValue);
