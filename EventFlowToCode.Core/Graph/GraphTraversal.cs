namespace EventFlowToCode.Core.Graph;

public static class GraphTraversal
{
    internal static IEnumerable<FlowNode> EnumerateStructuredNodes(IEnumerable<FlowNode> roots)
    {
        HashSet<FlowNode> visited = [];
        Stack<FlowNode> pending = new(roots);
        while (pending.TryPop(out var node))
        {
            if (!visited.Add(node)) continue;
            yield return node;
            foreach (var child in node.OutEdges) pending.Push(child);
            if (node is GroupNode group) pending.Push(group.Root);
        }
    }

    public static List<FlowNode> FindPostorder(FlowNode root, Func<FlowNode, bool>? predicate = null, bool reverse = false) =>
        PostorderTraversal.Find(root, node => reverse ? node.InEdges : node.OutEdges, node => node.Name, predicate);

    public static List<FlowNode> FindReversePostorder(FlowNode root, Func<FlowNode, bool>? predicate = null, bool reverse = false)
    {
        var postorder = FindPostorder(root, predicate, reverse);
        postorder.Reverse();
        return postorder;
    }
}
