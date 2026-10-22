namespace EventFlowToCode.Core.Model;

public abstract record FlowValue
{
    public sealed record Argument(string Name) : FlowValue;
    public sealed record ActorMarker(string Name, string SecondaryName) : FlowValue;
}