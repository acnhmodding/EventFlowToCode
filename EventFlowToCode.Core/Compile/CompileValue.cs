using EventFlowToCode.Core.Model;

namespace EventFlowToCode.Core.Compile;

internal sealed record CompileValue(FlowType Type, object Value)
{
    public bool IsBoundVariable { get; init; }
}