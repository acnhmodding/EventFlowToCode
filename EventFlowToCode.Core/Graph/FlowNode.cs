namespace EventFlowToCode.Core.Graph;

public abstract class FlowNode(string name)
{
    private readonly List<FlowNode> _inEdges = [];
    private readonly List<FlowNode> _outEdges = [];

    public string Name { get; set; } = name;
    public IReadOnlyList<FlowNode> InEdges => _inEdges;
    public IReadOnlyList<FlowNode> OutEdges => _outEdges;
    public GroupNode? GroupNode { get; set; }

    public virtual void AddInEdge(FlowNode source)
    {
        if (!_inEdges.Contains(source))
            _inEdges.Add(source);
    }

    public virtual void AddOutEdge(FlowNode destination)
    {
        if (!_outEdges.Contains(destination))
            _outEdges.Add(destination);
    }

    public virtual void RemoveInEdge(FlowNode source) => _inEdges.Remove(source);

    public virtual void RemoveOutEdge(FlowNode destination) => _outEdges.Remove(destination);

    internal void AddAnalysisInEdge(FlowNode source)
    {
        if (!_inEdges.Contains(source))
            _inEdges.Add(source);
    }

    internal void RemoveAnalysisInEdge(FlowNode source) => _inEdges.Remove(source);

    internal void AddAnalysisOutEdge(FlowNode destination)
    {
        if (!_outEdges.Contains(destination))
            _outEdges.Add(destination);
    }

    internal void RemoveAnalysisOutEdge(FlowNode destination) => _outEdges.Remove(destination);

    public void ClearInEdges() => _inEdges.Clear();

    public virtual void RerouteInEdge(FlowNode oldSource, FlowNode newSource)
    {
        var index = _inEdges.IndexOf(oldSource);
        if (index < 0)
            return;

        if (_inEdges.Contains(newSource))
            _inEdges.RemoveAt(index);
        else
            _inEdges[index] = newSource;
    }

    public virtual void RerouteOutEdge(FlowNode oldDestination, FlowNode newDestination)
    {
        var index = _outEdges.IndexOf(oldDestination);
        if (index < 0) return;

        if (_outEdges.Contains(newDestination))
            _outEdges.RemoveAt(index);
        else
            _outEdges[index] = newDestination; 
    }

    public override string ToString() =>
        $"{GetType().Name}[name={Name}, in_edges=[{string.Join(", ", InEdges.Select(n => n.Name))}], out_edges=[{string.Join(", ", OutEdges.Select(n => n.Name))}]]";
}