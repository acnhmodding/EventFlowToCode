using EventFlowToCode.Core.Model;

namespace EventFlowToCode.Core.Compile;

internal abstract class CompileNode(string name)
{
    private readonly List<CompileNode> _outEdges = [];

    public string Name { get; set; } = name;
    public IReadOnlyList<CompileNode> OutEdges => _outEdges;

    public virtual void AddOutEdge(CompileNode destination)
    {
        if (_outEdges.Contains(destination))
        {
            return;
        }

        _outEdges.Add(destination);
    }

    public virtual void RemoveOutEdge(CompileNode destination) => _outEdges.Remove(destination);

    public virtual void RerouteOutEdge(CompileNode oldDestination, CompileNode newDestination)
    {
        var index = _outEdges.IndexOf(oldDestination);
        if (index < 0)
        {
            return;
        }

        if (_outEdges.Contains(newDestination))
        {
            _outEdges.RemoveAt(index);
            return;
        }

        _outEdges[index] = newDestination;
    }
}

internal sealed class CompileRootNode(string name, bool local = false, bool entrypoint = false,
                                      bool emitEntryPoint = true, IEnumerable<VariableDefinition>? variables = null) : CompileNode(name)
{
    public bool Local { get; set; } = local;
    public bool WasLocal { get; } = local;
    public bool Entrypoint { get; } = entrypoint;
    public bool EmitEntryPoint { get; } = emitEntryPoint;
    public List<VariableDefinition> Variables { get; } = [.. variables ?? []];
}

internal sealed class CompileActionNode(string name, ActionSignature action, IReadOnlyDictionary<string, CompileValue> parameters) : CompileNode(name)
{
    public ActionSignature Action { get; } = action;
    public Dictionary<string, CompileValue> Parameters { get; } = new Dictionary<string, CompileValue>(parameters);
}

internal sealed class CompileSwitchNode(string name, QuerySignature query, IReadOnlyDictionary<string, CompileValue> parameters) : CompileNode(name)
{
    public QuerySignature Query { get; } = query;
    public bool PreserveTerminalCases { get; init; }
    public Dictionary<string, CompileValue> Parameters { get; } = new Dictionary<string, CompileValue>(parameters);
    public Dictionary<CompileNode, List<int>> Cases { get; } = [];
    public List<int> RemovedTerminalCases { get; } = [];
    public CompileConnectorNode Connector { get; } = new CompileConnectorNode($"{name}Connector");

    public void AddCase(CompileNode node, int value)
    {
        if (!Cases.TryGetValue(node, out var values))
        {
            values = [];
            Cases[node] = values;
        }

        values.Add(value);
    }

    public override void RemoveOutEdge(CompileNode destination)
    {
        base.RemoveOutEdge(destination);
        Cases.Remove(destination);
    }

    public override void RerouteOutEdge(CompileNode oldDestination, CompileNode newDestination)
    {
        base.RerouteOutEdge(oldDestination, newDestination);
        if (!Cases.Remove(oldDestination, out var values))
        {
            return;
        }
        if (!Cases.TryGetValue(newDestination, out var existing))
        {
            existing = [];
            Cases[newDestination] = existing;
        }
        existing.AddRange(values);
    }
}

internal sealed class CompileForkNode(string name, CompileJoinNode joinNode, IEnumerable<CompileNode> forks) : CompileNode(name)
{
    public CompileJoinNode JoinNode { get; } = joinNode;
    public List<CompileNode> Forks { get; } = [.. forks];

    public override void RemoveOutEdge(CompileNode destination)
    {
        base.RemoveOutEdge(destination);
        Forks.RemoveAll(branch => ReferenceEquals(branch, destination));
    }

    public override void RerouteOutEdge(CompileNode oldDestination, CompileNode newDestination)
    {
        base.RerouteOutEdge(oldDestination, newDestination);
        // Branch slots describe invocations, so equal targets must retain their
        // multiplicity and order even though graph adjacency contains each node once.
        for (var index = 0; index < Forks.Count; index++)
        {
            if (ReferenceEquals(Forks[index], oldDestination)) Forks[index] = newDestination;
        }
    }
}

internal sealed class CompileJoinNode(string name) : CompileNode(name)
{
}

internal sealed class CompileSubflowNode(string name, string flowchartName, string calledRootName,
    IReadOnlyDictionary<string, CompileValue> parameters) : CompileNode(name)
{
    public string FlowchartName { get; } = flowchartName;
    public string CalledRootName { get; set; } = calledRootName;
    public Dictionary<string, CompileValue> Parameters { get; } = new Dictionary<string, CompileValue>(parameters);
}

internal sealed class CompileConnectorNode(string name) : CompileNode(name)
{
}

internal sealed class CompileTerminalNode : CompileNode
{
    private CompileTerminalNode() : base("TerminalNode")
    {
    }

    public static CompileTerminalNode Instance { get; } = new();

    public override void AddOutEdge(CompileNode destination)
    {
    }

    public override void RemoveOutEdge(CompileNode destination)
    {
    }

    public override void RerouteOutEdge(CompileNode oldDestination, CompileNode newDestination)
    {
    }
}

internal sealed record CompileDocument(IReadOnlyList<CompileRootNode> Roots, IReadOnlyList<CompileActor> Actors);

internal sealed class CompileActor(ActorName name)
{
    public ActorName Name { get; } = name;
    public Dictionary<string, ActionSignature> Actions { get; } = [];
    public Dictionary<string, QuerySignature> Queries { get; } = [];
    public HashSet<string> UsedActions { get; } = [];
    public HashSet<string> UsedQueries { get; } = [];
    public bool Declared { get; set; }
    public string? EntryPointName { get; set; }
}