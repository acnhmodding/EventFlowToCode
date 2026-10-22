using BfevLibrary;
using BfevLibrary.Core;
using EventFlowToCode.Core.Bfev;
using EventFlowToCode.Core.Model;

namespace EventFlowToCode.Core.Compile;

internal sealed class BfevCompilerEmitter
{
    public static BfevFile Emit(string name, CompileDocument document)
    {
        Flowchart flowchart = new(name);
        BfevFile file = new()
        {
            FileName = name,
        };
        file.Flowcharts.Add(name, flowchart);

        var roots = LocalFlowInliner.Process(document.Roots);
        OptimizeNames(roots);
        var nodes = CollectNodes(roots);
        var entryPoints = roots.Where(root => root.EmitEntryPoint).Select(root => root.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var call in nodes.OfType<CompileSubflowNode>())
        {
            if (call.FlowchartName.Length == 0 && !entryPoints.Contains(call.CalledRootName))
            {
                throw new InvalidDataException($"Subflow '{call.CalledRootName}' has no matching flow or entrypoint.");
            }
        }
        var actorIndices = AddActors(flowchart, document.Actors, nodes);
        var eventIndices = AddEvents(flowchart, nodes);
        AddEntryPoints(flowchart, UniqueEntryPoints(roots), eventIndices);
        foreach (var actor in document.Actors.Where(actor => actor.EntryPointName is not null))
        {
            var index = flowchart.EntryPoints.IndexOfKey(actor.EntryPointName!);
            if (index < 0) throw new InvalidDataException($"Actor '{actor.Name.Name}' is bound to missing exported entrypoint '{actor.EntryPointName}'.");
            flowchart.Actors[actorIndices[actor.Name]].EntryPointIndex = checked((short)index);
        }
        FillEventLinks(flowchart, nodes, eventIndices, actorIndices);

        return file;
    }

    private static void OptimizeNames(IReadOnlyList<CompileRootNode> roots)
    {
        var entryPointNames = roots
            .Where(root => !root.WasLocal)
            .Select(root => root.Name)
            .ToHashSet(StringComparer.Ordinal);
        Dictionary<string, string> renamedRoots = new(StringComparer.Ordinal);
        var nextId = 0;

        string NextName()
        {
            while (true)
            {
                var name = $"Event{nextId++}";
                if (entryPointNames.Add(name))
                {
                    return name;
                }
            }
        }

        foreach (var root in roots)
        {
            if (!root.WasLocal)
            {
                continue;
            }

            var oldName = root.Name;
            root.Name = NextName();
            renamedRoots[oldName] = root.Name;
        }

        HashSet<CompileNode> visited = [];
        foreach (var root in roots)
        {
            foreach (var node in EvflParser.FindPostorder(root))
            {
                if (!visited.Add(node))
                {
                    continue;
                }

                if (node is not (CompileRootNode or CompileTerminalNode))
                {
                    node.Name = NextName();
                }
                if (node is CompileSubflowNode { FlowchartName.Length: 0 } subflow
                    && renamedRoots.TryGetValue(subflow.CalledRootName, out var renamed))
                {
                    subflow.CalledRootName = renamed;
                }
            }
        }
    }

    private static List<CompileRootNode> UniqueEntryPoints(IReadOnlyList<CompileRootNode> roots)
    {
        List<CompileRootNode> result = [];
        HashSet<string> names = [];
        foreach (var root in roots)
        {
            if (!root.EmitEntryPoint)
            {
                continue;
            }
            if (names.Add(root.Name))
            {
                result.Add(root);
            }
        }

        return result;
    }

    private static Dictionary<ActorName, short> AddActors(
        Flowchart flowchart,
        IReadOnlyList<CompileActor> actors,
        IReadOnlyList<CompileNode> nodes)
    {
        var referencedActors = nodes
            .SelectMany(NodeParameters)
            .SelectMany(value => ActorMarkers(value.Value))
            .ToHashSet();
        var actorByName = actors.ToDictionary(actor => actor.Name);
        foreach (var actorName in referencedActors)
        {
            actorByName.TryAdd(actorName, new CompileActor(actorName));
        }

        Dictionary<ActorName, short> indices = [];
        foreach (var actor in actorByName.Values.Where(actor =>
            actor.Declared
            ||
            actor.UsedActions.Count > 0
            || actor.UsedQueries.Count > 0
            || referencedActors.Contains(actor.Name)))
        {
            Actor bfevActor = new()
            {
                Name = actor.Name.Name,
                SecondaryName = actor.Name.SecondaryName,
                ArgumentName = actor.Name.ArgumentName,
                EntryPointIndex = -1,
                CutNumber = 1,
            };

            bfevActor.Actions.AddRange(actor.Actions.Keys.Where(actor.UsedActions.Contains).Order(StringComparer.Ordinal));
            bfevActor.Queries.AddRange(actor.Queries.Keys.Where(actor.UsedQueries.Contains).Order(StringComparer.Ordinal));
            indices[actor.Name] = (short)flowchart.Actors.Count;
            flowchart.Actors.Add(bfevActor);
        }

        return indices;
    }

    private static IEnumerable<CompileValue> NodeParameters(CompileNode node) =>
        node switch
        {
            CompileActionNode action => action.Parameters.Values,
            CompileSwitchNode switchNode => switchNode.Parameters.Values,
            CompileSubflowNode subflow => subflow.Parameters.Values,
            _ => [],
        };

    private static IEnumerable<ActorName> ActorMarkers(object? value)
    {
        switch (value)
        {
            case FlowValue.ActorMarker actor:
                yield return new ActorName(actor.Name, actor.SecondaryName);
                break;
            case IReadOnlyDictionary<string, CompileValue> values:
                foreach (var actor in values.Values.SelectMany(item => ActorMarkers(item.Value)))
                {
                    yield return actor;
                }
                break;
            case IReadOnlyDictionary<string, object?> values:
                foreach (var actor in values.Values.SelectMany(ActorMarkers))
                {
                    yield return actor;
                }
                break;
            case string:
                break;
            case IEnumerable<CompileValue> values:
                foreach (var actor in values.SelectMany(item => ActorMarkers(item.Value)))
                {
                    yield return actor;
                }
                break;
            case IEnumerable<object?> values:
                foreach (var actor in values.SelectMany(ActorMarkers))
                {
                    yield return actor;
                }
                break;
        }
    }

    private static List<CompileNode> CollectNodes(IReadOnlyList<CompileRootNode> roots)
    {
        List<CompileNode> result = [];
        HashSet<CompileNode> visited = [];
        foreach (var root in roots)
        {
            foreach (var node in EvflParser.FindPostorder(root).AsEnumerable().Reverse())
            {
                if (node is CompileRootNode or CompileConnectorNode or CompileTerminalNode)
                {
                    continue;
                }
                if (visited.Add(node))
                {
                    result.Add(node);
                }
            }
        }

        return result;
    }

    private static Dictionary<CompileNode, short> AddEvents(Flowchart flowchart, IReadOnlyList<CompileNode> nodes)
    {
        Dictionary<CompileNode, short> eventIndices = [];
        foreach (var node in nodes)
        {
            Event ev = node switch
            {
                CompileActionNode action => new ActionEvent(action.Name),
                CompileSwitchNode switchNode => new SwitchEvent(switchNode.Name),
                CompileForkNode fork => new ForkEvent(fork.Name),
                CompileJoinNode join => new JoinEvent(join.Name),
                CompileSubflowNode subflow => new SubflowEvent(subflow.Name),
                _ => throw new InvalidOperationException($"Cannot emit node '{node.GetType().Name}'."),
            };
            eventIndices[node] = (short)flowchart.Events.Count;
            flowchart.Events.Add(ev);
        }

        return eventIndices;
    }

    private static void AddEntryPoints(
        Flowchart flowchart,
        IReadOnlyList<CompileRootNode> roots,
        IReadOnlyDictionary<CompileNode, short> eventIndices)
    {
        foreach (var root in roots)
        {
            EntryPoint entryPoint = new()
            {
                SubFlowEventIndices = SubflowIndices(root, eventIndices),
                Variables = BfevValueConverter.ToVariableDefinitions(root.Variables),
                EventIndex = FirstEventIndex(root, eventIndices),
            };

            flowchart.EntryPoints.Add(root.Name, entryPoint);
        }
    }

    private static List<short> SubflowIndices(CompileRootNode root, IReadOnlyDictionary<CompileNode, short> eventIndices) =>
        [.. EvflParser.FindPostorder(root)
            .OfType<CompileSubflowNode>()
            .Where(eventIndices.ContainsKey)
            .Select(node => eventIndices[node])
            .Distinct()];

    private static void FillEventLinks(Flowchart flowchart, IReadOnlyList<CompileNode> nodes,
        Dictionary<CompileNode, short> eventIndices, Dictionary<ActorName, short> actorIndices)
    {
        foreach (var node in nodes)
        {
            var ev = flowchart.Events[eventIndices[node]];
            switch (node, ev)
            {
                case (CompileActionNode action, ActionEvent actionEvent):
                    actionEvent.NextEventIndex = FirstEventIndexOrNone(action, eventIndices);
                    actionEvent.ActorIndex = actorIndices[action.Action.ActorName];
                    actionEvent.ActorActionIndex = ActionIndex(flowchart, actionEvent.ActorIndex, action.Action.Name);
                    actionEvent.Parameters = ToContainer(action.Parameters);
                    break;
                case (CompileSwitchNode switchNode, SwitchEvent switchEvent):
                    switchEvent.ActorIndex = actorIndices[switchNode.Query.ActorName];
                    switchEvent.ActorQueryIndex = QueryIndex(flowchart, switchEvent.ActorIndex, switchNode.Query.Name);
                    switchEvent.Parameters = ToContainer(switchNode.Parameters);
                    foreach ((var destination, var values) in switchNode.Cases)
                    {
                        var destinationIndex = FirstEventIndex(destination, eventIndices);
                        foreach (var value in values)
                        {
                            switchEvent.SwitchCases.Add(new SwitchEvent.SwitchCase(value, (ushort)destinationIndex));
                        }
                    }
                    break;
                case (CompileForkNode fork, ForkEvent forkEvent):
                    forkEvent.JoinEventIndex = FirstEventIndex(fork.JoinNode, eventIndices);
                    forkEvent.ForkEventIndicies.AddRange(fork.Forks
                        .Where(forkNode => fork.OutEdges.Contains(forkNode))
                        .Select(forkNode => FirstEventIndex(forkNode, eventIndices))
                        .Where(index => index != forkEvent.JoinEventIndex));
                    break;
                case (CompileJoinNode join, JoinEvent joinEvent):
                    joinEvent.NextEventIndex = FirstEventIndexOrNone(join, eventIndices);
                    break;
                case (CompileSubflowNode subflow, SubflowEvent subflowEvent):
                    subflowEvent.NextEventIndex = FirstEventIndexOrNone(subflow, eventIndices);
                    subflowEvent.FlowchartName = subflow.FlowchartName;
                    subflowEvent.EntryPointName = subflow.CalledRootName;
                    subflowEvent.Parameters = ToContainer(subflow.Parameters);
                    break;
            }
        }
    }

    private static Container ToContainer(IReadOnlyDictionary<string, CompileValue> values) =>
        BfevValueConverter.ToContainer(values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value.Value));

    private static short FirstEventIndexOrNone(CompileNode node, IReadOnlyDictionary<CompileNode, short> eventIndices) =>
        node.OutEdges.Count == 0 ? (short)-1 : FirstEventIndex(node.OutEdges[0], eventIndices);

    private static short FirstEventIndex(CompileNode node, IReadOnlyDictionary<CompileNode, short> eventIndices)
    {
        var current = node;
        HashSet<CompileNode> visited = [];
        short index;
        while (!eventIndices.TryGetValue(current, out index))
        {
            if (!visited.Add(current) || current.OutEdges.Count == 0)
            {
                return -1;
            }
            current = current.OutEdges[0];
        }

        return index;
    }

    private static short ActionIndex(Flowchart flowchart, short actorIndex, string actionName) =>
        (short)flowchart.Actors[actorIndex].Actions.IndexOf(actionName);

    private static short QueryIndex(Flowchart flowchart, short actorIndex, string queryName) =>
        (short)flowchart.Actors[actorIndex].Queries.IndexOf(queryName);
}
