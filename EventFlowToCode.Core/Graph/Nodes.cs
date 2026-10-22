using EventFlowToCode.Core.Model;

namespace EventFlowToCode.Core.Graph;

public sealed class RootNode(string name, IEnumerable<VariableDefinition>? variableDefinitions = null, bool local = false, bool inline = false, bool entryPointAlias = false, string? entryPointLabel = null) : FlowNode(name)
{
    public List<VariableDefinition> VariableDefinitions { get; } = [.. variableDefinitions ?? []];
    public bool Local { get; set; } = local;
    public bool Inline { get; set; } = inline;
    public bool EntryPointAlias { get; } = entryPointAlias;
    public string EntryPointLabel { get; } = entryPointLabel ?? name;

    public override void AddInEdge(FlowNode source)
    {
    }
}

public sealed class ActionNode(string name, ActionSignature action, IDictionary<string, object?> parameters) : FlowNode(name)
{
    public ActionSignature Action { get; set; } = action;
    public Dictionary<string, object?> Parameters { get; } = new Dictionary<string, object?>(parameters);
}

public sealed class SwitchNode(string name, QuerySignature query, IDictionary<string, object?> parameters) : FlowNode(name)
{
    public QuerySignature Query { get; set; } = query;
    public Dictionary<string, object?> Parameters { get; } = new Dictionary<string, object?>(parameters);
    public Dictionary<string, List<object?>> Cases { get; } = [];
    internal List<object?> ExplicitTerminalCases { get; } = [];
    public FlowNode? TerminalNode { get; set; }

    public void AddCase(string nodeName, object? value)
    {
        if (!Cases.TryGetValue(nodeName, out var values))
        {
            values = [];
            Cases[nodeName] = values;
        }

        values.Add(value);
    }

    public void RegisterTerminalNode(FlowNode terminalNode)
    {
        if (Cases.Values.Sum(values => values.Count) == Query.ReturnType.NumValues)
        {
            TerminalNode = null;
            return;
        }

        AddOutEdge(terminalNode);
        terminalNode.AddInEdge(this);
        TerminalNode = terminalNode;
    }

    public override void RemoveOutEdge(FlowNode destination)
    {
        base.RemoveOutEdge(destination);
        Cases.Remove(destination.Name);
        if (ReferenceEquals(TerminalNode, destination))
        {
            TerminalNode = null;
        }
    }

    public override void RerouteOutEdge(FlowNode oldDestination, FlowNode newDestination)
    {
        base.RerouteOutEdge(oldDestination, newDestination);
        if (Cases.Remove(oldDestination.Name, out var values))
        {
            if (Cases.TryGetValue(newDestination.Name, out var existing)) existing.AddRange(values);
            else Cases[newDestination.Name] = values;
        }
        if (ReferenceEquals(TerminalNode, oldDestination))
        {
            TerminalNode = newDestination;
        }
    }
}

public sealed class ForkNode(string name, JoinNode joinNode, IEnumerable<string>? forks = null) : FlowNode(name)
{
    public JoinNode JoinNode { get; set; } = joinNode;
    public List<string> Forks { get; } = [.. forks ?? []];

    public override void RemoveOutEdge(FlowNode destination)
    {
        base.RemoveOutEdge(destination);
        Forks.RemoveAll(name => name == destination.Name);
    }

    public override void RerouteOutEdge(FlowNode oldDestination, FlowNode newDestination)
    {
        base.RerouteOutEdge(oldDestination, newDestination);
        for (var index = 0; index < Forks.Count; index++)
        {
            if (Forks[index] == oldDestination.Name) Forks[index] = newDestination.Name;
        }
    }
}

public sealed class JoinNode(string name) : FlowNode(name)
{
}

public sealed class SubflowNode(string name, string flowchartName, string calledRootName, IDictionary<string, object?>? parameters = null) : FlowNode(name)
{
    public string FlowchartName { get; set; } = flowchartName;
    internal bool IsSyntheticJump { get; init; }
    public string CalledRootName { get; set; } = calledRootName;
    public Dictionary<string, object?> Parameters { get; } = new Dictionary<string, object?>(parameters ?? new Dictionary<string, object?>());
}

public class TerminalNode(string name) : FlowNode(name)
{
    public override void AddOutEdge(FlowNode destination)
    {
    }
}

public sealed class DeadendTerminalNode(string name) : TerminalNode(name)
{
}

public sealed class NoopNode(string name) : FlowNode(name)
{
}

public sealed class EntryPointNode(string name, string? entryLabel = null, bool exported = true) : FlowNode(name)
{
    public string EntryLabel { get; } = entryLabel ?? name;
    public bool Exported { get; } = exported;
}

public sealed class GroupNode : FlowNode
{
    public GroupNode(FlowNode root) : base($"grp_{root.Name}")
    {
        Root = root;
        PassNode = new NoopNode($"grp-end_{root.Name}");
        Nodes = EnumerateGroup();
        Root.GroupNode = this;
    }

    public FlowNode Root { get; }
    public NoopNode PassNode { get; }
    public List<FlowNode> Nodes { get; private set; }

    public void RecalculateGroup() => Nodes = EnumerateGroup();

    private List<FlowNode> EnumerateGroup()
    {
        HashSet<FlowNode> visited = [Root];
        Stack<FlowNode> stack = new([Root]);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var child in node.OutEdges)
            {
                if (visited.Add(child))
                {
                    stack.Push(child);
                }
            }
        }

        return [.. visited];
    }
}