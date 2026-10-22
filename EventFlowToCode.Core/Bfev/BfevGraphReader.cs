using BfevLibrary;
using BfevLibrary.Core;
using EventFlowToCode.Core.Decompile;
using EventFlowToCode.Core.Graph;
using EventFlowToCode.Core.Model;

namespace EventFlowToCode.Core.Bfev;

public sealed class BfevGraphReader
{
    public static ControlFlowGraph Read(string path, FunctionCatalog? functions = null)
    {
        var file = BfevFileReader.Read(path);
        return Read(file, functions);
    }

    public static ControlFlowGraph Read(BfevFile file, FunctionCatalog? functions = null)
    {
        functions ??= new FunctionCatalog();
        BfevGraphValidator.Validate(file);
        var flowchart = BfevGraphValidator.RequireSingleFlowchart(file);
        if (flowchart.Events.Select(node => node.Name).Distinct(StringComparer.Ordinal).Count() != flowchart.Events.Count)
            throw new NotSupportedException("Readable EVFL decompilation requires unique event names.");

        ControlFlowGraph graph = new(flowchart.Name);
        var eventNames = flowchart.Events
            .Select((ev, index) => (ev.Name, index))
            .ToDictionary(x => x.index, x => x.Name);

        ImportActors(flowchart, graph, functions);
        ImportEvents(flowchart, graph, eventNames, functions);
        var entryPointEdges = ImportEntryPoints(flowchart, graph, eventNames);
        ImportEdges(flowchart, graph, eventNames);

        foreach ((var source, var destination) in entryPointEdges)
            graph.AddEdge(source, destination);
        
        LinkForkJoinNodes(graph);
        graph.Prepare();

        return graph;
    }

    private static void ImportActors(Flowchart flowchart, ControlFlowGraph graph, FunctionCatalog functions)
    {
        var entryPointNames = flowchart.EntryPoints.Select(pair => pair.Key).ToArray();
        foreach (var actor in flowchart.Actors)
        {
            var actorName = CreateActorName(actor);
            ActorDefinition definition = new(actorName)
            {
                EntryPointName = actor.EntryPointIndex < 0 ? null : entryPointNames[actor.EntryPointIndex],
            };

            foreach (var actionName in actor.Actions)
            {
                var normalizedName = StripEventFlowPrefix(actionName, "EventFlowAction");
                definition.Actions.TryAdd(normalizedName, functions.ResolveAction(actorName, normalizedName));
            }

            foreach (var queryName in actor.Queries)
            {
                var normalizedName = StripEventFlowPrefix(queryName, "EventFlowQuery");
                definition.Queries.TryAdd(normalizedName, functions.ResolveQuery(actorName, normalizedName));
            }

            graph.AddActor(definition);
        }
    }

    private static void ImportEvents(Flowchart flowchart, ControlFlowGraph graph, IReadOnlyDictionary<int, string> eventNames, FunctionCatalog functions)
    {
        foreach (var ev in flowchart.Events)
        {
            graph.AddNode(ev switch
            {
                ActionEvent action => CreateActionNode(flowchart, action, functions),
                SwitchEvent switchEvent => CreateSwitchNode(flowchart, switchEvent, eventNames, functions),
                ForkEvent fork => CreateForkNode(fork, eventNames),
                JoinEvent join => new JoinNode(join.Name),
                SubflowEvent subflow => new SubflowNode(
                    subflow.Name,
                    subflow.FlowchartName,
                    subflow.EntryPointName,
                    BfevValueConverter.FromContainer(subflow.Parameters)),
                _ => throw new NotSupportedException($"Unsupported BFEV event type '{ev.GetType().Name}'."),
            });
        }
    }

    private static ActionNode CreateActionNode(Flowchart flowchart, ActionEvent action, FunctionCatalog functions)
    {
        var actor = flowchart.Actors[action.ActorIndex];
        var actorName = CreateActorName(actor);
        var actionName = StripEventFlowPrefix(actor.Actions[action.ActorActionIndex], "EventFlowAction");
        var parameters = BfevValueConverter.FromContainer(action.Parameters);
        var signature = functions.ResolveAction(actorName, actionName);

        return new ActionNode(action.Name, signature, parameters);
    }

    private static SwitchNode CreateSwitchNode(Flowchart flowchart, SwitchEvent switchEvent, IReadOnlyDictionary<int, string> eventNames, FunctionCatalog functions)
    {
        var actor = flowchart.Actors[switchEvent.ActorIndex];
        var actorName = CreateActorName(actor);
        var queryName = StripEventFlowPrefix(actor.Queries[switchEvent.ActorQueryIndex], "EventFlowQuery");
        var parameters = BfevValueConverter.FromContainer(switchEvent.Parameters);
        FlowType fallbackReturnType = new($"int{Math.Max(switchEvent.SwitchCases.Select(c => c.Value).DefaultIfEmpty(0).Max() + 1, 2)}");
        var signature = functions.ResolveQuery(actorName, queryName, fallbackReturnType);
        SwitchNode node = new(switchEvent.Name, signature, parameters);

        foreach (var switchCase in switchEvent.SwitchCases)
        {
            if (switchCase.EventIndex == ushort.MaxValue)
            {
                node.ExplicitTerminalCases.Add(NormalizeSwitchCaseValue(switchCase.Value));
                continue;
            }

            if (eventNames.TryGetValue(switchCase.EventIndex, out var eventName))
                node.AddCase(eventName, NormalizeSwitchCaseValue(switchCase.Value));    
        }

        return node;
    }

    private static int NormalizeSwitchCaseValue(int value) => value < 0 ? unchecked(value) : value;

    private static ActorName CreateActorName(Actor actor) => new(actor.Name, actor.SecondaryName, actor.ArgumentName);

    private static ForkNode CreateForkNode(ForkEvent fork, IReadOnlyDictionary<int, string> eventNames)
    {
        JoinNode join = new(NameAt(eventNames, fork.JoinEventIndex) ?? $"Join{fork.JoinEventIndex}");
        var forks = fork.ForkEventIndicies
            .Select(index => NameAt(eventNames, index))
            .Where(name => name is not null)
            .Select(name => name!);

        return new ForkNode(fork.Name, join, forks);
    }

    private static List<(string Source, string Destination)> ImportEntryPoints(Flowchart flowchart, ControlFlowGraph graph, IReadOnlyDictionary<int, string> eventNames)
    {
        List<(string Source, string Destination)> edges = [];
        foreach ((var name, var entryPoint) in flowchart.EntryPoints)
        {
            var entryPointAlias = graph.Nodes.ContainsKey(name);
            var rootName = entryPointAlias ? UniqueRootNodeName(graph, name) : name;
            RootNode root = new(
                rootName,
                BfevValueConverter.FromVariableDefinitions(entryPoint.Variables),
                entryPointAlias: entryPointAlias,
                entryPointLabel: name);
            graph.AddNode(root);

            var mainEvent = NameAt(eventNames, entryPoint.EventIndex);
            if (mainEvent is not null)
            {
                edges.Add((root.Name, mainEvent));
            }
        }

        return edges;
    }

    private static string UniqueRootNodeName(ControlFlowGraph graph, string baseName)
    {
        string name;
        var suffix = 0;
        do
        {
            name = $"entrypoint_{baseName}_{suffix++}";
        } while (graph.Nodes.ContainsKey(name));

        return name;
    }

    private static void ImportEdges(Flowchart flowchart, ControlFlowGraph graph, IReadOnlyDictionary<int, string> eventNames)
    {
        foreach (var ev in flowchart.Events)
        {
            switch (ev)
            {
                case ActionEvent action:
                    AddIndexedEdge(graph, ev.Name, action.NextEventIndex, eventNames);
                    break;
                case SwitchEvent switchEvent:
                    foreach (var switchCase in switchEvent.SwitchCases)
                        AddIndexedEdge(graph, ev.Name, switchCase.EventIndex, eventNames);
                    
                    break;
                case ForkEvent fork:
                    foreach (var forkIndex in fork.ForkEventIndicies)
                        AddIndexedEdge(graph, ev.Name, forkIndex, eventNames);
                    
                    AddIndexedEdge(graph, ev.Name, fork.JoinEventIndex, eventNames);
                    break;
                case JoinEvent join:
                    AddIndexedEdge(graph, ev.Name, join.NextEventIndex, eventNames);
                    break;
                case SubflowEvent subflow:
                    AddIndexedEdge(graph, ev.Name, subflow.NextEventIndex, eventNames);
                    break;
            }
        }
    }

    private static void LinkForkJoinNodes(ControlFlowGraph graph)
    {
        foreach (var fork in graph.Nodes.Values.OfType<ForkNode>())
            if (graph.Nodes.TryGetValue(fork.JoinNode.Name, out var joinNode) && joinNode is JoinNode realJoinNode)
                fork.JoinNode = realJoinNode;
    }

    private static void AddIndexedEdge(ControlFlowGraph graph, string source, int eventIndex, IReadOnlyDictionary<int, string> eventNames)
    {
        var destination = NameAt(eventNames, eventIndex);
        if (destination is not null)
            graph.AddEdge(source, destination);
    }

    private static string? NameAt(IReadOnlyDictionary<int, string> eventNames, int index) =>
        index >= 0 && eventNames.TryGetValue(index, out var name) ? name : null;

    private static string StripEventFlowPrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;
}
