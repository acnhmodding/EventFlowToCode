using EventFlowToCode.Core.Graph;
using EventFlowToCode.Core.Model;

namespace EventFlowToCode.Core.Compile;

internal static class LocalFlowInliner
{
    public static List<CompileRootNode> Process(IReadOnlyList<CompileRootNode> roots)
    {
        var localRoots = roots
            .Where(root => root.Local)
            .GroupBy(root => root.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        if (localRoots.Count == 0)
        {
            return [.. roots];
        }

        var calls = roots.SelectMany(EvflParser.FindPostorder)
            .OfType<CompileSubflowNode>().Distinct().ToArray();
        var preserve = RecursiveFlowsRequiringCalls(localRoots);
        foreach (var call in calls)
        {
            if (call.FlowchartName.Length == 0
                && localRoots.TryGetValue(call.CalledRootName, out var target)
                && !CanInlineArguments(call, target))
            {
                preserve.Add(target.Name);
            }
        }
        foreach (var name in preserve)
        {
            localRoots[name].Local = false;
            localRoots.Remove(name);
        }

        var callers = calls
            .Where(call => call.FlowchartName.Length == 0 && localRoots.ContainsKey(call.CalledRootName))
            .GroupBy(call => call.CalledRootName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        Queue<string> pendingContinuations = new(callers.Keys);
        HashSet<string> linked = [];
        while (pendingContinuations.TryDequeue(out var name))
        {
            if (linked.Contains(name)) continue;
            var continuations = Continuations(callers[name]);
            if (continuations.Count != 1) continue;
            var continuation = continuations.Single();
            if (continuation is CompileTerminalNode) continue;
            linked.Add(name);
            // Linking an outer helper gives its terminal inner calls a continuation.
            // Revisit those targets before deciding whether they need real calls.
            foreach (var call in LinkOpenExits(localRoots[name], continuation))
            {
                if (call.FlowchartName.Length == 0 && callers.ContainsKey(call.CalledRootName))
                    pendingContinuations.Enqueue(call.CalledRootName);
            }
        }
        foreach ((var name, var rootCalls) in callers)
        {
            if (Continuations(rootCalls).Count > 1)
            {
                localRoots[name].Local = false;
                localRoots.Remove(name);
            }
        }

        Dictionary<CompileSubflowNode, CompileNode> reroutes = [];
        foreach (var subflow in calls)
        {
            if (string.IsNullOrEmpty(subflow.FlowchartName)
                && localRoots.TryGetValue(subflow.CalledRootName, out var targetRoot)
                && CanInlineArguments(subflow, targetRoot))
            {
                reroutes[subflow] = targetRoot.OutEdges.FirstOrDefault() ?? subflow.OutEdges.FirstOrDefault() ?? CompileTerminalNode.Instance;
            }
        }

        foreach (var subflow in reroutes.Keys.ToArray())
        {
            var target = reroutes[subflow];
            HashSet<CompileSubflowNode> chain = [subflow];
            while (target is CompileSubflowNode next && reroutes.TryGetValue(next, out var resolved))
            {
                if (!chain.Add(next))
                {
                    throw new InvalidDataException("Cyclic local-flow alias cannot be inlined.");
                }
                target = resolved;
            }
            foreach (var alias in chain)
            {
                reroutes[alias] = target;
            }
        }

        foreach (var root in roots)
        {
            Stack<CompileNode> pending = new([root]);
            HashSet<CompileNode> visited = [];
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                foreach (var child in node.OutEdges.ToList())
                {
                    if (child is CompileSubflowNode subflow && reroutes.TryGetValue(subflow, out var target))
                    {
                        node.RerouteOutEdge(subflow, target);
                    }
                }
                foreach (var child in node.OutEdges)
                {
                    if (visited.Add(child))
                    {
                        pending.Push(child);
                    }
                }
            }
        }

        return [.. roots.Where(root => !root.Local)];
    }

    private static bool CanInlineArguments(CompileSubflowNode call, CompileRootNode target) =>
        call.Parameters.All(pair => pair.Value.Value is FlowValue.Argument argument && argument.Name == pair.Key)
        && target.Variables.All(variable => variable.InitialValue is null
            && (target.Entrypoint || call.Parameters.ContainsKey(variable.Name)));

    private static HashSet<CompileNode> Continuations(IEnumerable<CompileSubflowNode> calls) =>
        [.. calls.Select(call => call.OutEdges.SingleOrDefault() ?? CompileTerminalNode.Instance)];

    private static HashSet<string> RecursiveFlowsRequiringCalls(IReadOnlyDictionary<string, CompileRootNode> roots)
    {
        var calls = roots.ToDictionary(pair => pair.Key,
            pair => EvflParser.FindPostorder(pair.Value).OfType<CompileSubflowNode>()
                .Where(call => call.FlowchartName.Length == 0 && roots.ContainsKey(call.CalledRootName))
                .ToArray(), StringComparer.Ordinal);
        var edges = calls.ToDictionary(pair => pair.Key,
            pair => pair.Value.Select(call => call.CalledRootName).Distinct(StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
        var reverse = roots.Keys.ToDictionary(name => name, _ => new List<string>(), StringComparer.Ordinal);
        foreach ((var source, var targets) in edges)
        {
            foreach (var target in targets)
            {
                reverse[target].Add(source);
            }
        }

        // Kosaraju's two passes find recursive components without using the call stack.
        HashSet<string> visited = [];
        List<string> finishOrder = [];
        foreach (var root in roots.Keys)
        {
            Stack<(string Name, bool Finish)> pending = new([(root, false)]);
            while (pending.TryPop(out var item))
            {
                if (item.Finish)
                {
                    finishOrder.Add(item.Name);
                }
                else if (visited.Add(item.Name))
                {
                    pending.Push((item.Name, true));
                    foreach (var child in edges[item.Name].Reverse())
                    {
                        pending.Push((child, false));
                    }
                }
            }
        }
        visited.Clear();
        HashSet<string> recursive = [];
        foreach (var root in finishOrder.AsEnumerable().Reverse())
        {
            if (!visited.Add(root))
            {
                continue;
            }
            List<string> component = [];
            Stack<string> pending = new([root]);
            while (pending.TryPop(out var name))
            {
                component.Add(name);
                foreach (var parent in reverse[name])
                {
                    if (visited.Add(parent))
                    {
                        pending.Push(parent);
                    }
                }
            }
            if (component.Count > 1 || edges[root].Contains(root, StringComparer.Ordinal))
            {
                var members = component.ToHashSet(StringComparer.Ordinal);
                // A returning recursive call needs a stack frame even when every caller
                // resumes at the same instruction. Only tail recursion may become a loop.
                var returnsToWork = component.SelectMany(name => calls[name])
                    .Any(call => members.Contains(call.CalledRootName)
                        && call.OutEdges.Any(next => next is not CompileTerminalNode));
                // A cycle made entirely of entry aliases has no event to resolve to.
                var aliasesOnly = component.All(name =>
                    roots[name].OutEdges.FirstOrDefault() is CompileSubflowNode call
                    && call.FlowchartName.Length == 0 && members.Contains(call.CalledRootName));
                if (returnsToWork || aliasesOnly) recursive.UnionWith(component);
            }
        }
        return recursive;
    }

    private static List<CompileSubflowNode> LinkOpenExits(CompileRootNode root, CompileNode continuation)
    {
        List<CompileSubflowNode> linkedCalls = [];
        // The helper may already jump into the caller's continuation. Its exits
        // belong to the caller and must not be linked back to its own beginning.
        foreach (var node in PostorderTraversal.Find<CompileNode, CompileNode>(root,
            node => node is CompileForkNode fork ? [fork.JoinNode] : node.OutEdges, node => node,
            node => !ReferenceEquals(node, continuation)))
        {
            if (node is CompileSwitchNode switchNode && !ReferenceEquals(continuation, CompileTerminalNode.Instance))
            {
                switchNode.RerouteOutEdge(CompileTerminalNode.Instance, continuation);
                if (switchNode.RemovedTerminalCases.Count > 0)
                {
                    switchNode.AddOutEdge(continuation);
                    foreach (var value in switchNode.RemovedTerminalCases) switchNode.AddCase(continuation, value);
                    switchNode.RemovedTerminalCases.Clear();
                }
            }
            if (ReferenceEquals(node, root)
                || ReferenceEquals(node, continuation)
                || node.OutEdges.Count > 0
                || node is CompileTerminalNode)
            {
                continue;
            }

            node.AddOutEdge(continuation);
            if (node is CompileSubflowNode call) linkedCalls.Add(call);
        }
        return linkedCalls;
    }

}
