using EventFlowToCode.Core.Graph;
using EventFlowToCode.Core.Model;
using EventFlowToCode.Core.Syntax;

namespace EventFlowToCode.Core.Decompile;

public sealed class ControlFlowGraph(string name)
{
    public string Name { get; } = name;
    public List<RootNode> Roots { get; } = [];
    public Dictionary<ActorName, ActorDefinition> Actors { get; } = [];
    public Dictionary<string, FlowNode> Nodes { get; } = [];

    public void AddActor(ActorDefinition actor) => Actors.TryAdd(actor.Name, actor);

    public void AddNode(FlowNode node)
    {
        Nodes[node.Name] = node;
        if (node is RootNode root && !Roots.Contains(root))
        {
            Roots.Add(root);
        }
    }

    public void AddEdge(string source, string destination)
    {
        var sourceNode = Nodes[source];
        var destinationNode = Nodes[destination];
        sourceNode.AddOutEdge(destinationNode);
        destinationNode.AddInEdge(sourceNode);
    }

    public void Prepare()
    {
        SeparateOverlappingFlows();
        AddTerminalNodes();
        SplitLoops();
    }

    private List<List<RootNode>> AssignComponents()
    {
        HashSet<RootNode> visited = [];
        List<List<RootNode>> components = [];
        foreach (var root in Roots)
        {
            if (visited.Contains(root))
            {
                continue;
            }

            var component = GetConnectedRoots(root);
            components.Add(component);
            visited.UnionWith(component);
        }

        return components;
    }

    private static List<RootNode> GetConnectedRoots(RootNode root)
    {
        HashSet<FlowNode> visited = [root];
        Stack<FlowNode> stack = new([root]);
        List<RootNode> component = [];

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is RootNode componentRoot)
                component.Add(componentRoot);

            foreach (var next in node.InEdges.Concat(node.OutEdges))
                if (visited.Add(next))
                    stack.Push(next);
        }

        return component;
    }

    public void Restructure(RestructureOptions? options = null)
    {
        options ??= new RestructureOptions();

        if (options.SwitchToIf)
        {
            ConvertSwitchToIf();
        }

        if (options.CollapseAndOr)
        {
            CollapseAndOr();
        }

        if (options.CollapseIf)
        {
            CollapseIf();
        }

        if (options.CollapseCase)
        {
            CollapseCases();
        }

        RunSecondaryPasses(options);

        if (options.RemoveTrailingReturn)
        {
            RemoveTrailingReturn();
        }
    }

    private void RunSecondaryPasses(RestructureOptions options)
    {
        HashSet<FlowNode>? previousNodes = null;
        var iteration = 0;

        while (iteration < options.SecondaryMaxIterations)
        {
            var currentNodes = Nodes.Values.ToHashSet();
            if (previousNodes is not null && previousNodes.SetEquals(currentNodes))
            {
                break;
            }

            previousNodes = currentNodes;

            if (options.ExtractReusedBlocks)
            {
                ExtractReusedBlocks(options.ExtractSingleStatement);
            }

            if (options.RemoveRedundantEntryPoints)
            {
                RemoveRedundantEntryPoints();
            }

            if (options.CollapseSubflowOnly)
            {
                CollapseSubflowOnlyRoot();
            }

            if (options.SimplifyIfElseOrder)
            {
                SimplifyIfElseOrder();
            }

            iteration++;
        }

    }

    public void FinalPass()
    {
        AddImplicitVariableDefinitions();
    }

    public static List<FlowNode> FindPostorder(FlowNode root, Func<FlowNode, bool>? predicate = null, bool reverse = false)
        => GraphTraversal.FindPostorder(root, predicate, reverse);

    public static List<FlowNode> FindReversePostorder(FlowNode root, Func<FlowNode, bool>? predicate = null, bool reverse = false)
        => GraphTraversal.FindReversePostorder(root, predicate, reverse);

    private void AddTerminalNodes()
    {
        foreach (var root in Roots)
        {
            TerminalNode terminalNode = new($"tm_{root.Name}");

            foreach (var node in FindPostorder(root))
            {
                if (node is SwitchNode switchNode)
                {
                    if (switchNode.ExplicitTerminalCases.Count > 0)
                    {
                        foreach (var value in switchNode.ExplicitTerminalCases) switchNode.AddCase(terminalNode.Name, value);
                        switchNode.ExplicitTerminalCases.Clear();
                        switchNode.AddOutEdge(terminalNode);
                        terminalNode.AddInEdge(switchNode);
                    }
                    switchNode.RegisterTerminalNode(terminalNode);
                }
                else if (node.OutEdges.Count == 0 && node is not TerminalNode)
                {
                    node.AddOutEdge(terminalNode);
                    terminalNode.AddInEdge(node);
                }
            }

            if (terminalNode.InEdges.Count > 0)
            {
                Nodes[terminalNode.Name] = terminalNode;
            }
        }
    }

    private void SeparateOverlappingFlows()
    {
        var components = AssignComponents();
        for (var componentIndex = 0; componentIndex < components.Count; componentIndex++)
        {
            var component = components[componentIndex];
            if (component.Count <= 1)
            {
                continue;
            }

            var mainEntry = component[0];
            var mainEntryReachableCount = FindPostorder(mainEntry).Count;
            foreach (var root in component)
            {
                var reachableCount = FindPostorder(root).Count;
                if (reachableCount > mainEntryReachableCount)
                {
                    mainEntry = root;
                    mainEntryReachableCount = reachableCount;
                }
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var root in component.ToList())
                {
                    if (ReferenceEquals(root, mainEntry))
                    {
                        continue;
                    }

                    if (IsCut(root.OutEdges))
                    {
                        changed = true;
                        var newRoot = DetachRoot(root);
                        components.Add(GetConnectedRoots(newRoot));
                        component = GetConnectedRoots(mainEntry);
                        break;
                    }

                    ConvertRootToEntryPoints(root);
                }
            }
        }
    }

    private RootNode DetachRoot(RootNode root) => DetachNodeAsSub(root, root.OutEdges[0], inline: false);

    private void ConvertRootToEntryPoints(RootNode root)
    {
        (var exclusive, var connections) = GetExclusiveSubgraph(root);
        var simpleConnections = connections
            .Where(node => node is not GroupNode
                && node.OutEdges.Count <= 1
                && node.OutEdges.All(outEdge => outEdge.GetType() == typeof(TerminalNode)))
            .ToList();
        var labels = connections
            .Where(node => !simpleConnections.Contains(node))
            .Select(node => ConvertNodeToEntryPoint(node, node.Name))
            .ToHashSet();

        var outEdges = exclusive.SelectMany(node => node.OutEdges.OfType<EntryPointNode>().Where(labels.Contains).ToList().Select(label => (node, label)));
        foreach (var (node, label) in outEdges)
            DetachNodesWithCall(node, label, label.EntryLabel, []);
        
    }

    private static (HashSet<FlowNode> Exclusive, HashSet<FlowNode> Connections) GetExclusiveSubgraph(RootNode root)
    {
        HashSet<FlowNode> reachable = [.. FindPostorder(root)];
        var otherRoots = GetConnectedRoots(root).Where(candidate => !ReferenceEquals(candidate, root)).ToList();
        HashSet<FlowNode> otherReachable = [];

        foreach (var otherRoot in otherRoots)
            otherReachable.UnionWith(FindPostorder(otherRoot));

        var exclusive = reachable.Except(otherReachable).ToHashSet();
        HashSet<FlowNode> connections = [];
        foreach (var node in exclusive)
        {
            connections.UnionWith(node.OutEdges.Where(outEdge => !exclusive.Contains(outEdge)));
        }

        return (exclusive, connections);
    }

    private EntryPointNode ConvertNodeToEntryPoint(FlowNode node, string name)
    {
        if (node is EntryPointNode entryPoint)
            return entryPoint;

        EntryPointNode entryPointNode = new($"{node.Name}-entrypoint", name);
        entryPointNode.AddOutEdge(node);
        foreach (var caller in node.InEdges.ToList())
        {
            entryPointNode.AddInEdge(caller);
            caller.RerouteOutEdge(node, entryPointNode);
        }

        node.ClearInEdges();
        node.AddInEdge(entryPointNode);
        Nodes[entryPointNode.Name] = entryPointNode;
        return entryPointNode;
    }

    private static RootNode FindRoot(FlowNode node)
    {
        Stack<FlowNode> stack = new([node]);
        HashSet<FlowNode> visited = [];

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is RootNode root)
            {
                return root;
            }

            if (current.GroupNode is not null && visited.Add(current.GroupNode))
            {
                stack.Push(current.GroupNode);
            }
            else
            {
                foreach (var parent in current.InEdges.Where(parent => !ReferenceEquals(parent, current)))
                {
                    if (visited.Add(parent))
                    {
                        stack.Push(parent);
                    }
                }
            }
        }

        throw new InvalidOperationException($"Root not found for node '{node.Name}'.");
    }

    private string UniqueNodeName(string baseName)
    {
        if (!Nodes.ContainsKey(baseName))
        {
            return baseName;
        }

        var suffix = 1;
        while (Nodes.ContainsKey($"{baseName}_{suffix}"))
        {
            suffix++;
        }

        return $"{baseName}_{suffix}";
    }

    private void ConvertSwitchToIf()
    {
        foreach (var root in Roots)
        {
            foreach (var node in FindPostorder(root).ToList())
            {
                if (node is not SwitchNode switchNode)
                {
                    continue;
                }

                if (switchNode.OutEdges.Count != 2)
                {
                    continue;
                }

                FlowNode inner;
                FlowNode defaultNode;

                if (switchNode.Cases.Count == 1)
                {
                    if (switchNode.TerminalNode is null)
                    {
                        continue;
                    }

                    inner = switchNode.OutEdges[0];
                    defaultNode = switchNode.TerminalNode;
                }
                else
                {
                    inner = switchNode.OutEdges[0];
                    defaultNode = switchNode.OutEdges[1];
                }

                if (!switchNode.Cases.TryGetValue(inner.Name, out var values))
                {
                    continue;
                }

                IfElseNode ifElseNode = new(
                    switchNode.Name,
                    [new IfElseNode.Rule(QueryPredicate.Create(switchNode.Query, switchNode.Parameters, values), inner)],
                    defaultNode);

                foreach (var caller in switchNode.InEdges.ToList())
                {
                    ifElseNode.AddInEdge(caller);
                    RerouteStructuredOutEdge(caller, switchNode, ifElseNode);
                }

                foreach (var exitNode in switchNode.OutEdges.ToList())
                {
                    ifElseNode.AddOutEdge(exitNode);
                    exitNode.RerouteInEdge(switchNode, ifElseNode);
                }

                Nodes.Remove(switchNode.Name);
                Nodes[ifElseNode.Name] = ifElseNode;
            }
        }
    }

    private void CollapseAndOr()
    {
        foreach (var root in Roots)
        {
            var changed = true;
            while (changed)
            {
                changed = false;

                foreach (var candidate in FindPostorder(root))
                {
                    if (candidate is not IfElseNode node)
                    {
                        continue;
                    }

                    if (node.Rules.Count != 1)
                    {
                        continue;
                    }

                    foreach (var child in node.OutEdges.OfType<IfElseNode>().Reverse())
                    {
                        if (child.Rules.Count != 1 || child.InEdges.Count != 1)
                        {
                            continue;
                        }

                        var otherChildren = node.OutEdges.Where(edge => !ReferenceEquals(edge, child)).ToList();
                        if (otherChildren.Count != 1)
                        {
                            continue;
                        }

                        var otherChild = otherChildren[0];
                        if (!child.OutEdges.Contains(otherChild))
                        {
                            continue;
                        }

                        var trueBranches = child.OutEdges.Where(edge => !ReferenceEquals(edge, otherChild)).ToList();
                        if (trueBranches.Count != 1)
                        {
                            continue;
                        }

                        var trueBranch = trueBranches[0];
                        var falseBranch = otherChild;

                        var nodePredicate = node.Rules[0].Predicate;
                        if (ReferenceEquals(node.Default, child))
                        {
                            nodePredicate = nodePredicate.Invert();
                        }

                        var childPredicate = child.Rules[0].Predicate;
                        if (ReferenceEquals(child.Default, trueBranch))
                        {
                            childPredicate = childPredicate.Invert();
                        }

                        var predicate = nodePredicate.And(childPredicate);
                        IfElseNode newNode = new(
                            node.Name,
                            [new IfElseNode.Rule(predicate, trueBranch)],
                            falseBranch);

                        MergeCoupledNodes(node, child, newNode);
                        changed = true;
                        break;
                    }

                }
            }
        }
    }

    private void CollapseIf()
    {
        var roots = Roots.SelectMany(root => FindPostorder(root).Select(candidate => (root, candidate)));
        foreach (var (root, candidate) in roots)
        {
            if (candidate is not IfElseNode node || node.Rules.Count != 1)
                continue;
  
            var childOk = node.Rules[0].Node is IfElseNode { InEdges.Count: 1 };
            var defaultOk = node.Default is IfElseNode { InEdges.Count: 1 };

            Predicate predicate;
            FlowNode valueBranch;
            IfElseNode elseBranch;

            if (defaultOk && !childOk)
            {
                predicate = node.Rules[0].Predicate;
                valueBranch = node.Rules[0].Node;
                elseBranch = (IfElseNode)node.Default;
            }
            else if (childOk && !defaultOk)
            {
                predicate = node.Rules[0].Predicate.Invert();
                valueBranch = node.Default;
                elseBranch = (IfElseNode)node.Rules[0].Node;
            }
            else if (childOk && defaultOk)
            {
                var childScore = FindPostorder(node.Rules[0].Node).Count;
                var defaultScore = FindPostorder(node.Default).Count;
                if (childScore <= defaultScore)
                {
                    predicate = node.Rules[0].Predicate;
                    valueBranch = node.Rules[0].Node;
                    elseBranch = (IfElseNode)node.Default;
                }
                else
                {
                    predicate = node.Rules[0].Predicate.Invert();
                    valueBranch = node.Default;
                    elseBranch = (IfElseNode)node.Rules[0].Node;
                }
            }
            else
            {
                continue;
            }

            if (PathExists(elseBranch, valueBranch))
            {
                node.Rules[0] = new IfElseNode.Rule(predicate.Invert(), elseBranch);
                node.Default = valueBranch;
                continue;
            }

            var dominators = FindDominatorTree(root);
            var reverseDominators = FindDominatorTree(node, reverse: true);
            var currentEnd = FindBlockEnd(node, dominators, reverseDominators);
            var elseEnd = FindBlockEnd(elseBranch, dominators, reverseDominators);
            if (!ReferenceEquals(currentEnd, elseEnd))
            {
                continue;
            }

            IfElseNode ifElseNode = new(node.Name, [new IfElseNode.Rule(predicate, valueBranch), .. elseBranch.Rules], elseBranch.Default);

            MergeCoupledNodes(node, elseBranch, ifElseNode);
        }
    }

    private void SimplifyIfElseOrder()
    {
        foreach (var node in Nodes.Values.ToList())
        {
            SimplifyIfElseOrder(node);
        }
    }

    private static void SimplifyIfElseOrder(FlowNode node)
    {
        if (node is GroupNode groupNode)
        {
            foreach (var innerNode in groupNode.Nodes)
            {
                SimplifyIfElseOrder(innerNode);
            }

            return;
        }

        if (node is not IfElseNode ifElseNode || ifElseNode.Rules.Count == 0)
        {
            return;
        }

        var penultimatePredicate = ifElseNode.Rules[^1].Predicate;
        if (penultimatePredicate is NotPredicate or QueryPredicate { Negated: true })
        {
            var oldRule = ifElseNode.Rules[^1];
            ifElseNode.Rules[^1] = new IfElseNode.Rule(oldRule.Predicate.Invert(), ifElseNode.Default);
            ifElseNode.Default = oldRule.Node;
        }

        if (ifElseNode.Rules[^1].Node is NoopNode && ifElseNode.Default is not NoopNode)
        {
            var oldRule = ifElseNode.Rules[^1];
            ifElseNode.Rules[^1] = new IfElseNode.Rule(oldRule.Predicate.Invert(), ifElseNode.Default);
            ifElseNode.Default = oldRule.Node;
        }
    }

    private void MergeCoupledNodes(FlowNode parentNode, FlowNode childNode, FlowNode newNode)
    {
        foreach (var caller in parentNode.InEdges.ToList())
        {
            newNode.AddInEdge(caller);
            caller.RerouteOutEdge(parentNode, newNode);
        }

        foreach (var caller in childNode.InEdges.ToList())
        {
            if (!ReferenceEquals(caller, parentNode))
            {
                newNode.AddInEdge(caller);
                caller.RerouteOutEdge(childNode, newNode);
            }
        }

        foreach (var parentOut in parentNode.OutEdges.ToList())
        {
            if (!ReferenceEquals(parentOut, childNode))
            {
                newNode.AddOutEdge(parentOut);
                parentOut.RerouteInEdge(parentNode, newNode);
            }
        }

        foreach (var exitNode in childNode.OutEdges.ToList())
        {
            newNode.AddOutEdge(exitNode);
            exitNode.RerouteInEdge(childNode, newNode);
        }

        Nodes.Remove(parentNode.Name);
        Nodes.Remove(childNode.Name);
        Nodes[newNode.Name] = newNode;
    }

    private static bool PathExists(FlowNode source, FlowNode destination) => FindPostorder(source).Contains(destination);

    private void CollapseCases()
    {
        foreach (var root in Roots.ToList())
        {
            Dictionary<FlowNode, FlowNode>? dominators = null;
            Dictionary<FlowNode, FlowNode>? reverseDominators = null;

            foreach (var node in FindPostorder(root))
            {
                if (node.OutEdges.Distinct().Count() < 2)
                    continue;

                dominators ??= FindDominatorTree(root);
                reverseDominators ??= FindDominatorTree(root, reverse: true);
                if (TryCollapseBlock(node, dominators, reverseDominators))
                {
                    dominators = null;
                    reverseDominators = null;
                }
            }
        }
    }

    private static List<FlowNode> FindTerminals(FlowNode node) => [.. FindPostorder(node).Where(candidate => candidate.OutEdges.Count == 0)];

    private Dictionary<FlowNode, FlowNode> FindDominatorTree(FlowNode root, bool reverse = false)
    {
        var backEdges = AddWhileBackEdges();
        var roots = reverse ? FindTerminals(root) : [root];
        NoopNode dummyRoot = new(string.Empty);

        foreach (var candidate in roots)
        {
            if (!reverse)
            {
                dummyRoot.AddOutEdge(candidate);
                candidate.AddAnalysisInEdge(dummyRoot);
            }
            else
            {
                dummyRoot.AddInEdge(candidate);
                candidate.AddAnalysisOutEdge(dummyRoot);
            }
        }

        Dictionary<FlowNode, FlowNode> dominators = new() { [dummyRoot] = dummyRoot };
        var reversePostorder = FindReversePostorder(dummyRoot, reverse: reverse);
        var reversePostorderIndex = reversePostorder
            .Select((node, index) => (node, index))
            .ToDictionary(pair => pair.node, pair => pair.index);

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var block in reversePostorder.Skip(1))
            {
                FlowNode? newImmediateDominator = null;
                IEnumerable<FlowNode> predecessors = reverse ? block.OutEdges : block.InEdges;
                foreach (var predecessor in predecessors)
                {
                    if (!dominators.ContainsKey(predecessor))
                    {
                        continue;
                    }

                    if (newImmediateDominator is null)
                    {
                        newImmediateDominator = predecessor;
                        continue;
                    }

                    var finger1 = predecessor;
                    var finger2 = newImmediateDominator;
                    while (!ReferenceEquals(finger1, finger2))
                    {
                        while (reversePostorderIndex[finger1] > reversePostorderIndex[finger2])
                        {
                            finger1 = dominators[finger1];
                        }
                        while (reversePostorderIndex[finger1] < reversePostorderIndex[finger2])
                        {
                            finger2 = dominators[finger2];
                        }
                    }

                    newImmediateDominator = finger1;
                }

                if (newImmediateDominator is null)
                {
                    continue;
                }

                if (!dominators.TryGetValue(block, out var current) || !ReferenceEquals(current, newImmediateDominator))
                {
                    dominators[block] = newImmediateDominator;
                    changed = true;
                }
            }
        }

        foreach (var candidate in roots)
        {
            if (!reverse)
            {
                candidate.RemoveAnalysisInEdge(dummyRoot);
                dummyRoot.RemoveOutEdge(candidate);
            }
            else
            {
                candidate.RemoveAnalysisOutEdge(dummyRoot);
                dummyRoot.RemoveInEdge(candidate);
            }

            dominators[candidate] = candidate;
        }

        foreach (var key in dominators.Keys.ToList())
        {
            if (ReferenceEquals(dominators[key], dummyRoot))
            {
                dominators.Remove(key);
            }
        }

        RemoveWhileBackEdges(backEdges);

        return dominators;
    }

    private List<(FlowNode From, FlowNode To)> AddWhileBackEdges()
    {
        List<(FlowNode From, FlowNode To)> backEdges = [];
        foreach (var node in Nodes.Values)
        {
            var loopBody = node switch
            {
                WhileNode whileNode => whileNode.LoopBody,
                DoWhileNode doWhileNode => doWhileNode.LoopBody,
                _ => null,
            };

            if (loopBody is null)
            {
                continue;
            }

            foreach (var terminal in FindTerminals(loopBody))
            {
                terminal.AddOutEdge(node);
                node.AddInEdge(terminal);
                backEdges.Add((terminal, node));
            }
        }

        return backEdges;
    }

    private static void RemoveWhileBackEdges(IEnumerable<(FlowNode From, FlowNode To)> edges)
    {
        foreach ((var from, var to) in edges)
        {
            from.RemoveOutEdge(to);
            to.RemoveInEdge(from);
        }
    }

    private FlowNode? FindBlockEnd(FlowNode node, Dictionary<FlowNode, FlowNode> dominators, IReadOnlyDictionary<FlowNode, FlowNode> reverseDominators)
    {
        if (!reverseDominators.ContainsKey(node))
        {
            return null;
        }

        if (!node.OutEdges.All(child =>
            dominators.ContainsKey(child) && ReferenceEquals(dominators[child], node)
            || child is TerminalNode))
        {
            return null;
        }

        var end = reverseDominators[node];
        if (!dominators.ContainsKey(end))
        {
            return null;
        }

        if (end is TerminalNode)
        {
            var innerDominators = FindDominatorTree(node);
            if (!innerDominators.TryGetValue(end, out var idom) || !ReferenceEquals(idom, node))
            {
                return null;
            }
        }
        else if (!ReferenceEquals(dominators[end], node))
        {
            return null;
        }

        if (!node.OutEdges.All(child => PathExists(child, end)))
        {
            return null;
        }

        return end;
    }

    private bool TryCollapseBlock(FlowNode root, Dictionary<FlowNode, FlowNode> dominators, IReadOnlyDictionary<FlowNode, FlowNode> reverseDominators)
    {
        var end = FindBlockEnd(root, dominators, reverseDominators);

        if (end is null || root.OutEdges.SequenceEqual([end]))
            return false;
        
        // An opaque group must have a single entry. Otherwise later analysis loses
        // the other paths into its shared instructions and can steal their continuation.
        var body = FindPostorder(root, node => !ReferenceEquals(node, end) && node is not TerminalNode).ToHashSet();
        if (body.Any(node => !ReferenceEquals(node, root) && node.InEdges.Any(caller => !body.Contains(caller))))
            return false;

        if (root.OutEdges.Contains(end))
            DetachNodesWithNoop(root, end);

        GroupNode group = new(root);
        foreach (var inNode in root.InEdges.ToList())
        {
            group.AddInEdge(inNode);
            RerouteStructuredOutEdge(inNode, root, group);
        }
        root.ClearInEdges();

        FlowNode innerTerminal = group.PassNode;
        Stack<FlowNode> stack = new([root]);
        HashSet<FlowNode> deleted = [];

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (ReferenceEquals(node, end) || node is TerminalNode)
            {
                continue;
            }

            if (node.OutEdges.Contains(end))
            {
                DeleteOutEdgeForBlockCollapse(node, end);
            }

            if (end.InEdges.Contains(node))
            {
                end.RemoveInEdge(node);
            }

            if (node is EntryPointNode entryPointNode)
            {
                for (var index = 0; index < entryPointNode.InEdges.Count; index++)
                {
                    var caller = entryPointNode.InEdges[index];
                    if (!dominators.ContainsKey(caller))
                    {
                        // Match Python's live list iteration: removing a caller skips the shifted next edge.
                        DetachNodesWithCall(caller, entryPointNode, entryPointNode.EntryLabel, []);
                    }
                }
            }

            if (Nodes.Remove(node.Name))
            {
                deleted.Add(node);
            }

            if (node is SwitchNode switchNode)
            {
                switchNode.RegisterTerminalNode(innerTerminal);
                if (switchNode.TerminalNode is not null)
                {
                    Nodes[innerTerminal.Name] = innerTerminal;
                }
            }

            foreach (var next in node.OutEdges)
                if (dominators.TryGetValue(next, out var idom) && deleted.Contains(idom))
                    stack.Push(next);
        }

        group.AddOutEdge(end);
        end.AddInEdge(group);
        Nodes[group.Name] = group;

        return true;
    }

    private void DetachNodesWithNoop(FlowNode source, FlowNode destination)
    {
        NoopNode noopNode = new(UniqueNodeName($"noop_{source.Name}-{destination.Name}"));
        Nodes[noopNode.Name] = noopNode;

        RerouteStructuredOutEdge(source, destination, noopNode);
        destination.RemoveInEdge(source);
        noopNode.AddInEdge(source);
    }

    private void DeleteOutEdgeForBlockCollapse(FlowNode source, FlowNode destination)
    {
        if (source is SwitchNode or IfElseNode or WhileNode or DoWhileNode)
        {
            NoopNode noopNode = new(UniqueNodeName($"pass_{source.Name}"));
            Nodes[noopNode.Name] = noopNode;
            RerouteStructuredOutEdge(source, destination, noopNode);
            destination.RemoveInEdge(source);
            noopNode.AddInEdge(source);
            return;
        }

        source.RemoveOutEdge(destination);
        destination.RemoveInEdge(source);
    }

    private static void RerouteStructuredOutEdge(FlowNode source, FlowNode oldDestination, FlowNode newDestination)
    {
        source.RerouteOutEdge(oldDestination, newDestination);

        switch (source)
        {
            case IfElseNode ifElseNode:
                for (var index = 0; index < ifElseNode.Rules.Count; index++)
                {
                    if (ReferenceEquals(ifElseNode.Rules[index].Node, oldDestination))
                    {
                        ifElseNode.Rules[index] = new IfElseNode.Rule(ifElseNode.Rules[index].Predicate, newDestination);
                    }
                }

                if (ReferenceEquals(ifElseNode.Default, oldDestination))
                {
                    ifElseNode.Default = newDestination;
                }
                break;
            case WhileNode whileNode:
                if (ReferenceEquals(whileNode.LoopBody, oldDestination))
                {
                    whileNode.LoopBody = newDestination;
                }
                if (ReferenceEquals(whileNode.LoopExit, oldDestination))
                {
                    whileNode.LoopExit = newDestination;
                }
                break;
            case DoWhileNode doWhileNode:
                if (ReferenceEquals(doWhileNode.LoopBody, oldDestination))
                {
                    doWhileNode.LoopBody = newDestination;
                }
                if (ReferenceEquals(doWhileNode.LoopExit, oldDestination))
                {
                    doWhileNode.LoopExit = newDestination;
                }
                break;
        }
    }

    private static bool IsCut(IReadOnlyList<FlowNode> nodes)
    {
        HashSet<FlowNode> nodeSet = [.. nodes];
        HashSet<FlowNode> reachable = [];
        foreach (var node in nodeSet)
        {
            reachable.UnionWith(FindPostorder(node));
        }

        foreach (var node in reachable)
        {
            // An empty group-end marker, like a terminal, has no instruction to
            // absorb from another path. Shared returns do not prevent extraction.
            if (node.InEdges.Any(inEdge => !reachable.Contains(inEdge)) && !nodeSet.Contains(node)
                && node is not TerminalNode and not NoopNode { OutEdges.Count: 0 })
            {
                return false;
            }
        }

        return true;
    }

    private void SplitLoops()
    {
        foreach (var root in Roots)
        {
            while (SplitLoopsHelper(root, [], [], []))
            {
            }
        }
    }

    private bool SplitLoopsHelper(
        FlowNode node,
        HashSet<FlowNode> active,
        HashSet<FlowNode> visited,
        Dictionary<FlowNode, FlowNode> replacementNodes)
    {
        active.Add(node);

        foreach (var next in node.OutEdges.ToList())
        {
            if (!visited.Contains(next) && !active.Contains(next))
            {
                if (SplitLoopsHelper(next, active, visited, replacementNodes))
                {
                    return true;
                }
            }
            else if (active.Contains(next) && !visited.Contains(next))
            {
                return SplitLoopBackEdge(node, next, active, replacementNodes);
            }
        }

        active.Remove(node);
        visited.Add(node);
        if (replacementNodes.TryGetValue(node, out var replacement))
        {
            active.Remove(replacement);
            visited.Add(replacement);
        }

        return false;
    }

    private bool SplitLoopBackEdge(
        FlowNode node,
        FlowNode next,
        HashSet<FlowNode> active,
        Dictionary<FlowNode, FlowNode> replacementNodes)
    {
        if (TrySplitWhileLoop(node, next, active, replacementNodes))
        {
            return true;
        }

        if (TrySplitWhileTrueLoop(node, next, active, replacementNodes))
        {
            return true;
        }

        if (TrySplitDoWhileLoop(node, next, active, replacementNodes))
        {
            return true;
        }

        var entryPoint = ConvertNodeToEntryPoint(next, next.Name);
        DetachNodesWithCall(node, entryPoint, entryPoint.EntryLabel, []);
        if (!ReferenceEquals(next, entryPoint))
        {
            active.Add(entryPoint);
            replacementNodes[next] = entryPoint;
        }

        return true;
    }

    private bool TrySplitWhileLoop(
        FlowNode node,
        FlowNode next,
        HashSet<FlowNode> active,
        Dictionary<FlowNode, FlowNode> replacementNodes)
    {
        if (node.OutEdges.Count != 1 || next is not SwitchNode switchNode || next.OutEdges.Count != 2)
        {
            return false;
        }

        var body = next.OutEdges[0];
        var exit = next.OutEdges[1];
        if (PathExists(exit, body))
        {
            (body, exit) = (exit, body);
        }

        if (!PathExists(body, exit) || PathExists(exit, body) || !IsContainedSubgraph(body, next))
        {
            return false;
        }

        var loopCondition = GetLoopCondition(switchNode, body, exit);
        node.RemoveOutEdge(next);
        next.RemoveInEdge(node);
        var whileNode = InjectWhile(next, loopCondition, body, exit);
        active.Add(whileNode);
        replacementNodes[next] = whileNode;
        return true;
    }

    private bool TrySplitWhileTrueLoop(
        FlowNode node,
        FlowNode next,
        HashSet<FlowNode> active,
        Dictionary<FlowNode, FlowNode> replacementNodes)
    {
        if (node.OutEdges.Count != 1 || next is not SwitchNode || next.OutEdges.Count != 1)
        {
            return false;
        }

        var body = next.OutEdges[0];
        if (!IsContainedSubgraph(body, next))
        {
            return false;
        }

        node.RemoveOutEdge(next);
        DeadendTerminalNode terminal = new(UniqueNodeName($"infloop_{next.Name}"));
        Nodes[terminal.Name] = terminal;
        next.AddOutEdge(terminal);
        terminal.AddInEdge(next);

        var whileNode = InjectWhile(next, new ConstPredicate(true), body, terminal);
        active.Add(whileNode);
        replacementNodes[next] = whileNode;
        return true;
    }

    private bool TrySplitDoWhileLoop(
        FlowNode node,
        FlowNode next,
        HashSet<FlowNode> active,
        Dictionary<FlowNode, FlowNode> replacementNodes)
    {
        if (node is not SwitchNode switchNode || node.OutEdges.Count != 2)
        {
            return false;
        }

        var body = node.OutEdges[0];
        var exit = node.OutEdges[1];
        if (ReferenceEquals(exit, next))
        {
            (body, exit) = (exit, body);
        }

        if (PathExists(exit, body) || !IsContainedSubgraph(body, node))
        {
            return false;
        }

        foreach (var inEdge in node.InEdges.ToList())
        {
            inEdge.RemoveOutEdge(node);
        }

        foreach (var outEdge in node.OutEdges.ToList())
        {
            outEdge.RemoveInEdge(node);
        }

        Nodes.Remove(node.Name);
        var loopCondition = GetLoopCondition(switchNode, body, exit);
        DoWhileNode doWhileNode = new(UniqueNodeName($"dw_{next.Name}"), loopCondition, next, exit);

        foreach (var inEdge in body.InEdges.ToList())
        {
            RerouteStructuredOutEdge(inEdge, body, doWhileNode);
            doWhileNode.AddInEdge(inEdge);
            body.RemoveInEdge(inEdge);
        }

        doWhileNode.AddOutEdge(body);
        body.AddInEdge(doWhileNode);
        doWhileNode.AddOutEdge(exit);
        exit.AddInEdge(doWhileNode);
        Nodes[doWhileNode.Name] = doWhileNode;
        active.Add(doWhileNode);
        replacementNodes[next] = doWhileNode;
        return true;
    }

    private static Predicate GetLoopCondition(SwitchNode switchNode, FlowNode body, FlowNode exit)
    {
        if (switchNode.Cases.TryGetValue(body.Name, out var bodyValues))
            return QueryPredicate.Create(switchNode.Query, switchNode.Parameters, bodyValues);

        if (switchNode.Cases.TryGetValue(exit.Name, out var exitValues) && switchNode.Cases.Count == 1)
            return QueryPredicate.Create(switchNode.Query, switchNode.Parameters, exitValues).Invert();
        
        throw new InvalidOperationException($"Cannot determine loop condition for '{switchNode.Name}'.");
    }

    private WhileNode InjectWhile(FlowNode loopHead, Predicate loopCondition, FlowNode loopBody, FlowNode loopExit)
    {
        WhileNode whileNode = new(loopHead.Name, loopCondition, loopBody, loopExit);

        foreach (var inEdge in loopHead.InEdges.ToList())
        {
            RerouteStructuredOutEdge(inEdge, loopHead, whileNode);
            whileNode.AddInEdge(inEdge);
        }

        foreach (var outEdge in loopHead.OutEdges.ToList())
        {
            outEdge.RerouteInEdge(loopHead, whileNode);
            whileNode.AddOutEdge(outEdge);
        }

        Nodes[loopHead.Name] = whileNode;
        return whileNode;
    }

    private bool IsContainedSubgraph(FlowNode source, FlowNode destination)
    {
        NoopNode pointer = new("temp");
        pointer.AddOutEdge(source);
        source.AddInEdge(pointer);

        var dominators = FindDominatorTree(pointer);
        var result = true;
        foreach (var node in FindPostorder(destination, candidate => !ReferenceEquals(candidate, source)))
        {
            if (!Dominates(destination, node, dominators))
            {
                result = false;
                break;
            }
        }

        source.RemoveInEdge(pointer);
        return result;
    }

    private static bool Dominates(FlowNode dominator, FlowNode dominatee, Dictionary<FlowNode, FlowNode> tree)
    {
        if (ReferenceEquals(dominator, dominatee))
            return true;

        var node = dominatee;
        while (tree.TryGetValue(node, out var parent) && !ReferenceEquals(node, parent))
        {
            if (ReferenceEquals(parent, dominator))
                return true;

            node = parent;
        }

        return false;
    }

    private void ExtractReusedBlocks(bool extractSingleStatement, List<FlowNode>? candidateNodes = null)
    {
        candidateNodes ??= [.. Nodes.Values];

        foreach (var node in candidateNodes.ToList())
        {
            // Empty control-flow markers have no instruction identity to share.
            // Extracting one can also create a helper with no editable body.
            if (node is TerminalNode or NoopNode or EntryPointNode { OutEdges.Count: 0 }) continue;
            if (node.InEdges.Distinct().Count() >= 2
                && IsCut([node])
                && (extractSingleStatement
                    || node.OutEdges.Count > 0
                    || node is not ActionNode and not TerminalNode and not NoopNode and not EntryPointNode and not SubflowNode))
            {
                DetachNodeAsSub(FindRoot(node), node);
            }
            else if (node is GroupNode groupNode)
            {
                var innerNodes = groupNode.Nodes.ToList();
                innerNodes.Remove(groupNode.Root);
                ExtractReusedBlocks(extractSingleStatement, innerNodes);
                groupNode.RecalculateGroup();
            }
        }
    }

    private RootNode DetachNodeAsSub(RootNode root, FlowNode entryPoint, string? rootName = null, bool inline = true)
    {
        var name = entryPoint switch
        {
            EntryPointNode entryPointNode => entryPointNode.EntryLabel,
            _ => entryPoint.Name,
        };
        RootNode newRoot = new(
            UniqueRootName(rootName ?? $"Sub_{name}"),
            root.VariableDefinitions.Select(variable => variable with { InitialValue = null }),
            local: true,
            inline: inline);
        newRoot.AddOutEdge(entryPoint);

        Nodes[newRoot.Name] = newRoot;
        Roots.Add(newRoot);

        foreach (var caller in entryPoint.InEdges.ToList())
        {
            DetachNodesWithCall(caller, entryPoint, newRoot.Name, newRoot.VariableDefinitions, directJump: !inline);
        }

        entryPoint.ClearInEdges();
        entryPoint.AddInEdge(newRoot);

        foreach (var node in FindPostorder(newRoot))
        {
            Nodes.TryAdd(node.Name, node);
        }

        return newRoot;
    }

    private void DetachNodesWithCall(FlowNode source, FlowNode destination, string entryPoint, IReadOnlyList<VariableDefinition> variableDefinitions, bool directJump = true)
    {
        var parameters = variableDefinitions.ToDictionary(
            variable => variable.Name,
            variable => (object?)new FlowValue.Argument(variable.Name));
        SubflowNode callNode = new(UniqueNodeName($"ext_{source.Name}-{destination.Name}"), string.Empty, entryPoint, parameters: parameters)
        {
            IsSyntheticJump = directJump,
        };
        Nodes[callNode.Name] = callNode;

        RerouteStructuredOutEdge(source, destination, callNode);
        destination.RemoveInEdge(source);
        callNode.AddInEdge(source);
    }

    private string UniqueRootName(string baseName)
    {
        if (!Roots.Any(root => root.Name == baseName) && !Nodes.ContainsKey(baseName))
        {
            return baseName;
        }

        var suffix = 1;
        while (Roots.Any(root => root.Name == $"{baseName}_{suffix}") || Nodes.ContainsKey($"{baseName}_{suffix}"))
        {
            suffix++;
        }

        return $"{baseName}_{suffix}";
    }

    private void RemoveRedundantEntryPoints()
    {
        Dictionary<(string FlowchartName, string RootName), (string FlowchartName, string RootName, IReadOnlyList<VariableDefinition> Variables)> remappedEntryPoints = [];
        List<RootNode> newRoots = [];

        foreach (var root in Roots.ToList())
        {
            var newRoot = root;
            foreach (var node in FindPostorder(root).ToList())
            {
                if (node is not RootNode and not EntryPointNode)
                {
                    continue;
                }

                if (node.OutEdges.Count != 1 || node.OutEdges[0] is not EntryPointNode entryPoint)
                {
                    continue;
                }

                FlowNode newParent;
                if (node is RootNode oldRoot)
                {
                    newRoot = new RootNode(oldRoot.Name, oldRoot.VariableDefinitions, oldRoot.Local, oldRoot.Inline);
                    newParent = newRoot;
                    remappedEntryPoints[(string.Empty, entryPoint.EntryLabel)] = (string.Empty, oldRoot.Name, oldRoot.VariableDefinitions);
                }
                else
                {
                    var oldEntryPoint = (EntryPointNode)node;
                    newParent = new EntryPointNode(oldEntryPoint.Name, oldEntryPoint.EntryLabel, oldEntryPoint.Exported);
                    remappedEntryPoints[(string.Empty, entryPoint.EntryLabel)] = (string.Empty, oldEntryPoint.EntryLabel, []);
                }

                MergeCoupledNodes(node, entryPoint, newParent);
            }

            newRoots.Add(newRoot);
        }

        if (remappedEntryPoints.Count == 0)
        {
            return;
        }

        Roots.Clear();
        Roots.AddRange(newRoots);

        foreach (var root in Roots)
        {
            RemapSubflowCalls(root, remappedEntryPoints, []);
        }
    }

    private void CollapseSubflowOnlyRoot()
    {
        Dictionary<string, (string FlowchartName, string RootName)> remappedRoots = [];
        // A helper reached by run must remain local so the compiler can retain or
        // inline each call according to its continuation. Renaming it to an exported
        // flow changes those generated helper calls into real exported calls.
        var calledHelpers = GraphTraversal.EnumerateStructuredNodes(Roots)
            .OfType<SubflowNode>().Where(call => call.FlowchartName.Length == 0 && !call.IsSyntheticJump)
            .Select(call => call.CalledRootName).ToHashSet(StringComparer.Ordinal);
        foreach (var root in Roots.ToList())
        {
            if (root.OutEdges.Count != 1 || root.OutEdges[0] is not SubflowNode subflow)
            {
                continue;
            }

            if (subflow.OutEdges.Count > 0 && subflow.OutEdges[0] is not TerminalNode)
            {
                continue;
            }

            if (root.Local)
            {
                // A called one-instruction helper represents that shared call
                // event. Replacing every caller with its target duplicates it.
                if (calledHelpers.Contains(root.Name)) continue;
                remappedRoots.TryAdd(root.Name, (subflow.FlowchartName, subflow.CalledRootName));
            }
            else if (string.IsNullOrEmpty(subflow.FlowchartName)
                && !remappedRoots.ContainsKey(subflow.CalledRootName)
                && !calledHelpers.Contains(subflow.CalledRootName)
                && Roots.FirstOrDefault(candidate => candidate.Name == subflow.CalledRootName) is { Local: true } calledRoot)
            {
                var oldRootName = root.Name;
                var oldCalledRootName = calledRoot.Name;
                var oldRootLocal = root.Local;

                calledRoot.Name = oldRootName;
                root.Name = oldCalledRootName;
                calledRoot.Local = oldRootLocal;
                root.Local = true;

                var rootVariables = root.VariableDefinitions.ToList();
                root.VariableDefinitions.Clear();
                root.VariableDefinitions.AddRange(calledRoot.VariableDefinitions);
                calledRoot.VariableDefinitions.Clear();
                calledRoot.VariableDefinitions.AddRange(rootVariables);

                Nodes.Remove(oldRootName);
                Nodes.Remove(oldCalledRootName);
                Nodes[root.Name] = root;
                Nodes[calledRoot.Name] = calledRoot;
                remappedRoots[root.Name] = (string.Empty, calledRoot.Name);
            }
        }

        if (remappedRoots.Count == 0)
        {
            return;
        }

        Roots.RemoveAll(root => remappedRoots.ContainsKey(root.Name));
        foreach (var rootName in remappedRoots.Keys)
        {
            Nodes.Remove(rootName);
        }

        Dictionary<(string FlowchartName, string RootName), (string FlowchartName, string RootName, IReadOnlyList<VariableDefinition> Variables)> remap = remappedRoots.ToDictionary(
            pair => (string.Empty, pair.Key),
            pair => (pair.Value.FlowchartName, pair.Value.RootName, (IReadOnlyList<VariableDefinition>)[]));
        foreach (var root in Roots)
        {
            RemapSubflowCalls(root, remap, []);
        }
    }

    private static void RemapSubflowCalls(
        FlowNode node,
        IReadOnlyDictionary<(string FlowchartName, string RootName), (string FlowchartName, string RootName, IReadOnlyList<VariableDefinition> Variables)> remappedRoots,
        HashSet<FlowNode> visited)
    {
        if (!visited.Add(node))
        {
            return;
        }

        if (node is SubflowNode subflow)
        {
            (string FlowchartName, string RootName) target = (subflow.FlowchartName, subflow.CalledRootName);
            while (remappedRoots.TryGetValue(target, out var replacement))
            {
                subflow.FlowchartName = replacement.FlowchartName;
                subflow.CalledRootName = replacement.RootName;
                if (replacement.Variables.Count > 0)
                {
                    subflow.Parameters.Clear();
                    foreach (var variable in replacement.Variables)
                    {
                        subflow.Parameters[variable.Name] = new FlowValue.Argument(variable.Name);
                    }
                }

                target = (subflow.FlowchartName, subflow.CalledRootName);
            }
        }

        if (node is IfElseNode ifElse)
        {
            foreach (var rule in ifElse.Rules)
            {
                RemapSubflowCalls(rule.Node, remappedRoots, visited);
            }
            RemapSubflowCalls(ifElse.Default, remappedRoots, visited);
        }
        else if (node is WhileNode whileNode)
        {
            RemapSubflowCalls(whileNode.LoopBody, remappedRoots, visited);
            RemapSubflowCalls(whileNode.LoopExit, remappedRoots, visited);
        }
        else if (node is DoWhileNode doWhileNode)
        {
            RemapSubflowCalls(doWhileNode.LoopBody, remappedRoots, visited);
            RemapSubflowCalls(doWhileNode.LoopExit, remappedRoots, visited);
        }
        else if (node is GroupNode groupNode)
        {
            RemapSubflowCalls(groupNode.Root, remappedRoots, visited);
        }

        foreach (var child in node.OutEdges)
        {
            RemapSubflowCalls(child, remappedRoots, visited);
        }
    }

    private void RemoveTrailingReturn()
    {
        foreach (var root in Roots)
        {
            RemoveTrailingReturnFromTail(root);
        }
    }

    private static void RemoveTrailingReturnFromTail(FlowNode start)
    {
        var previousNode = start;
        var currentNode = start;

        while (currentNode.OutEdges.Count > 0)
        {
            if (currentNode is WhileNode whileNode)
            {
                previousNode = currentNode;
                currentNode = whileNode.LoopExit;
                continue;
            }

            if (currentNode is DoWhileNode doWhileNode)
            {
                previousNode = currentNode;
                currentNode = doWhileNode.LoopExit;
                continue;
            }

            if (currentNode.OutEdges.Count != 1)
            {
                break;
            }

            previousNode = currentNode;
            currentNode = currentNode.OutEdges[0];
        }

        if (currentNode is TerminalNode
            && previousNode is not RootNode)
        {
            previousNode.RemoveOutEdge(currentNode);
        }
    }
    private void AddImplicitVariableDefinitions()
    {
        var knownRootTypes = Roots.ToDictionary(
            root => root.Name,
            root => root.VariableDefinitions.ToDictionary(variable => variable.Name, variable => variable.Type, StringComparer.Ordinal),
            StringComparer.Ordinal);

        var initialValueNames = Roots.ToDictionary(
            root => root.Name,
            root => root.VariableDefinitions.Where(variable => variable.InitialValue is not null).Select(variable => variable.Name).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        // An inline jump label inherits its enclosing flow's argument scope.
        foreach (var root in Roots)
        {
            Queue<FlowNode> pending = new([root]);
            HashSet<FlowNode> visited = [root];
            while (pending.TryDequeue(out var start))
            {
                foreach (var node in FindPostorder(start))
                {
                    if (node is GroupNode group && visited.Add(group.Root)) pending.Enqueue(group.Root);
                    if (node is EntryPointNode label) knownRootTypes.TryAdd(label.EntryLabel, knownRootTypes[root.Name]);
                }
            }
        }

        var placeholderType = FlowType.Placeholder;
        var changed = true;

        while (changed)
        {
            changed = false;

            foreach (var root in Roots)
            {
                var rootTypes = knownRootTypes[root.Name];
                Queue<FlowNode> queue = new([root]);
                HashSet<FlowNode> queued = [root];

                while (queue.Count > 0)
                {
                    foreach (var node in FindReversePostorder(queue.Dequeue()))
                    {
                        if (node is GroupNode groupNode && queued.Add(groupNode.Root))
                        {
                            queue.Enqueue(groupNode.Root);
                            continue;
                        }

                        foreach ((var argumentName, var type) in GetImplicitArgumentUses(node, knownRootTypes, placeholderType))
                        {
                            var result = UpdateVariableDefinition(root, argumentName, rootTypes.GetValueOrDefault(argumentName, placeholderType), type);
                            if (result is not null || !rootTypes.ContainsKey(argumentName))
                            {
                                rootTypes[argumentName] = result ?? placeholderType;
                                changed = true;
                            }
                        }

                        if (node is SubflowNode subflowNode && string.IsNullOrEmpty(subflowNode.FlowchartName) && knownRootTypes.TryGetValue(subflowNode.CalledRootName, out var expectedTypes))
                        {
                            var calledRoot = Roots.FirstOrDefault(candidate => candidate.Name == subflowNode.CalledRootName);
                            if (calledRoot is null)
                            {
                                continue;
                            }

                            foreach ((var parameterName, var value) in subflowNode.Parameters)
                            {
                                var type = placeholderType;
                                if (value is FlowValue.Argument argument && rootTypes.TryGetValue(argument.Name, out var argumentType))
                                {
                                    type = argumentType;
                                }
                                else if (value is not FlowValue.Argument)
                                {
                                    type = EvflFormatter.InferType(value);
                                }

                                var result = UpdateVariableDefinition(calledRoot, parameterName, expectedTypes.GetValueOrDefault(parameterName, placeholderType), type);
                                if (result is not null || !expectedTypes.ContainsKey(parameterName))
                                {
                                    expectedTypes[parameterName] = result ?? placeholderType;
                                    changed = true;
                                }
                            }

                            foreach ((var parameterName, var parameterType) in expectedTypes.ToList())
                            {
                                // Preserve omitted parameter items in existing binary calls. Generated
                                // local helpers still need their source bindings.
                                if (!calledRoot.Local && !subflowNode.IsSyntheticJump) continue;
                                if (initialValueNames[subflowNode.CalledRootName].Contains(parameterName))
                                {
                                    continue;
                                }

                                if (!subflowNode.Parameters.ContainsKey(parameterName))
                                {
                                    subflowNode.Parameters[parameterName] = new FlowValue.Argument(parameterName);
                                    changed = true;
                                }
                            }
                        }
                    }
                }
            }
        }

        foreach (var root in Roots)
            foreach ((var name, var type) in knownRootTypes[root.Name])
                if (type == placeholderType)
                    UpdateVariableDefinition(root, name, placeholderType, FlowType.Any);

    }

    private static IEnumerable<(string ArgumentName, FlowType Type)> GetImplicitArgumentUses(FlowNode node, Dictionary<string, Dictionary<string, FlowType>> knownRootTypes, FlowType placeholderType)
    {
        switch (node)
        {
            case ActionNode actionNode:
                if (!string.IsNullOrEmpty(actionNode.Action.ActorName.ArgumentName))
                {
                    yield return (actionNode.Action.ActorName.ArgumentName, FlowType.Actor);
                }

                foreach ((var name, var value) in actionNode.Parameters)
                {
                    if (value is FlowValue.Argument argument)
                    {
                        var type = GetParameterType(actionNode.Action.Parameters, name, placeholderType);
                        yield return (argument.Name, type);
                    }
                }
                break;

            case SwitchNode switchNode:
                if (!string.IsNullOrEmpty(switchNode.Query.ActorName.ArgumentName))
                {
                    yield return (switchNode.Query.ActorName.ArgumentName, FlowType.Actor);
                }

                foreach ((var name, var value) in switchNode.Parameters)
                {
                    if (value is FlowValue.Argument argument)
                    {
                        var type = GetParameterType(switchNode.Query.Parameters, name, placeholderType);
                        yield return (argument.Name, type);
                    }
                }
                break;

            case SubflowNode subflowNode:
                if (subflowNode.IsSyntheticJump
                    && subflowNode.FlowchartName.Length == 0
                    && knownRootTypes.TryGetValue(subflowNode.CalledRootName, out var jumpScope))
                {
                    foreach (var variable in jumpScope.ToArray()) yield return (variable.Key, variable.Value);
                }

                foreach ((var name, var value) in subflowNode.Parameters)
                {
                    if (value is not FlowValue.Argument argument)
                        continue;

                    var type = placeholderType;
                    if (string.IsNullOrEmpty(subflowNode.FlowchartName)
                        && knownRootTypes.TryGetValue(subflowNode.CalledRootName, out var signature)
                        && signature.TryGetValue(name, out var parameterType))
                    {
                        type = parameterType;
                    }

                    yield return (argument.Name, type);
                }
                break;

            case IfElseNode ifElseNode:
                foreach ((var query, var parameters) in ifElseNode.Rules.SelectMany(rule => rule.Predicate.GetQueries()))
                {
                    foreach ((var name, var value) in parameters)
                    {
                        if (value is FlowValue.Argument argument)
                        {
                            var type = GetParameterType(query.Parameters, name, placeholderType);
                            yield return (argument.Name, type);
                        }
                    }
                }
                break;

            case WhileNode whileNode:
                foreach ((var query, var parameters) in whileNode.LoopCondition.GetQueries())
                {
                    foreach ((var name, var value) in parameters)
                    {
                        if (value is FlowValue.Argument argument)
                        {
                            var type = GetParameterType(query.Parameters, name, placeholderType);
                            yield return (argument.Name, type);
                        }
                    }
                }
                break;

            case DoWhileNode doWhileNode:
                foreach ((var query, var parameters) in doWhileNode.LoopCondition.GetQueries())
                {
                    foreach ((var name, var value) in parameters)
                    {
                        if (value is FlowValue.Argument argument)
                        {
                            var type = GetParameterType(query.Parameters, name, placeholderType);
                            yield return (argument.Name, type);
                        }
                    }
                }
                break;
        }
    }

    private static FlowType GetParameterType(IEnumerable<FunctionParameter> parameters, string name, FlowType fallback) =>
        parameters.FirstOrDefault(parameter => parameter.Name == name)?.Type ?? fallback;

    private static FlowType? UpdateVariableDefinition(RootNode root, string name, FlowType oldType, FlowType newType)
    {
        if (oldType == newType || oldType == FlowType.Any)
            return null;

        if (newType == FlowType.Placeholder && oldType != FlowType.Placeholder)
            return null;

        var resultType = oldType != FlowType.Placeholder ? FlowType.Any : newType;
        var index = root.VariableDefinitions.FindIndex(variable => variable.Name == name);
        if (index >= 0)
        {
            root.VariableDefinitions[index] = root.VariableDefinitions[index] with { Type = resultType };
            return resultType;
        }

        root.VariableDefinitions.Add(new VariableDefinition(name, resultType, null));
        return resultType;
    }

}

public sealed class ActorDefinition(ActorName name)
{
    public ActorName Name { get; } = name;
    public string? EntryPointName { get; init; }
    public Dictionary<string, ActionSignature> Actions { get; } = [];
    public Dictionary<string, QuerySignature> Queries { get; } = [];
}
