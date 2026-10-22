using EventFlowToCode.Core.Decompile;

namespace EventFlowToCode.Core.Graph;

public sealed class IfElseNode(string name, IEnumerable<IfElseNode.Rule> rules, FlowNode defaultNode) : FlowNode(name)
{
    public sealed record Rule(Predicate Predicate, FlowNode Node);

    public List<Rule> Rules { get; } = [.. rules];
    public FlowNode Default { get; set; } = defaultNode;

    public override void RerouteOutEdge(FlowNode oldDestination, FlowNode newDestination)
    {
        base.RerouteOutEdge(oldDestination, newDestination);

        for (var index = 0; index < Rules.Count; index++)
        {
            if (ReferenceEquals(Rules[index].Node, oldDestination))
            {
                Rules[index] = new IfElseNode.Rule(Rules[index].Predicate, newDestination);
            }
        }

        if (ReferenceEquals(Default, oldDestination))
        {
            Default = newDestination;
        }
    }
}

public sealed class WhileNode(string name, Predicate loopCondition, FlowNode loopBody, FlowNode loopExit) : FlowNode(name)
{
    public Predicate LoopCondition { get; } = loopCondition;
    public FlowNode LoopBody { get; set; } = loopBody;
    public FlowNode LoopExit { get; set; } = loopExit;
}

public sealed class DoWhileNode(string name, Predicate loopCondition, FlowNode loopBody, FlowNode loopExit) : FlowNode(name)
{
    public Predicate LoopCondition { get; } = loopCondition;
    public FlowNode LoopBody { get; set; } = loopBody;
    public FlowNode LoopExit { get; set; } = loopExit;
}