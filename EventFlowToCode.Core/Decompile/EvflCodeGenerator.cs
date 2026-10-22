using EventFlowToCode.Core.Graph;
using EventFlowToCode.Core.Model;
using EventFlowToCode.Core.Syntax;

namespace EventFlowToCode.Core.Decompile;

public sealed class EvflCodeGenerator
{
    private readonly IReadOnlyDictionary<string, string> _hints;
    private readonly Dictionary<RootNode, EntryPointNode> _flowBodies = [];
    private readonly Dictionary<string, string> _namedEntries = new(StringComparer.Ordinal);
    private IReadOnlySet<string> _variableNames = new HashSet<string>(StringComparer.Ordinal);

    public EvflCodeGenerator(IReadOnlyDictionary<string, string>? hints = null)
    {
        _hints = hints ?? new Dictionary<string, string>();
    }

    public string Generate(ControlFlowGraph graph)
    {
        _flowBodies.Clear();
        _namedEntries.Clear();
        FindNamedEntryBodies(graph);
        List<string> roots = [];
        foreach (var actor in graph.Actors.Values.Where(actor => actor.EntryPointName is not null))
            roots.Add($"actor {EvflFormatter.FormatActorName(actor.Name)} in {EvflFormatter.Identifier(actor.EntryPointName!)}\n");
        foreach (var root in graph.Roots.OrderBy(root => root.Name, StringComparer.Ordinal))
        {
            roots.Add(GenerateNode(root));
        }

        return string.Join('\n', roots);
    }

    public string GenerateNode(FlowNode node, int indentLevel = 0, bool generatePass = false, FlowNode? stopAt = null)
    {
        if (ReferenceEquals(node, stopAt) || stopAt is not null && node.Name == stopAt.Name)
            return generatePass ? $"{EvflFormatter.Indent(indentLevel)}pass\n" : string.Empty;
        
        return node switch
        {
            RootNode root => GenerateRoot(root, indentLevel),
            ActionNode action => GenerateAction(action, indentLevel, stopAt),
            SwitchNode switchNode => GenerateSwitch(switchNode, indentLevel),
            ForkNode fork => GenerateFork(fork, indentLevel),
            JoinNode join => string.Join('\n', join.OutEdges.Select(edge => GenerateNode(edge, indentLevel, stopAt: stopAt))),
            SubflowNode subflow => GenerateSubflow(subflow, indentLevel, stopAt),
            DeadendTerminalNode => string.Empty,
            TerminalNode => $"{EvflFormatter.Indent(indentLevel)}return\n",
            NoopNode => generatePass ? $"{EvflFormatter.Indent(indentLevel)}pass\n" : string.Empty,
            EntryPointNode entryPoint => _namedEntries.TryGetValue(entryPoint.EntryLabel, out var flowName)
                ? $"{EvflFormatter.Indent(indentLevel)}jump {EvflFormatter.Identifier(flowName)}\n"
                : $"entrypoint {EvflFormatter.Identifier(entryPoint.EntryLabel)}:\n"
                    + string.Join('\n', entryPoint.OutEdges.Select(edge => GenerateNode(edge, indentLevel, stopAt: stopAt))),
            GroupNode group => GenerateNode(group.Root, indentLevel)
                + string.Join('\n', group.OutEdges.Select(edge => GenerateNode(edge, indentLevel, stopAt: stopAt))),
            IfElseNode ifElse => GenerateIfElse(ifElse, indentLevel),
            WhileNode whileNode => GenerateWhile(whileNode, indentLevel),
            DoWhileNode doWhile => GenerateDoWhile(doWhile, indentLevel),
            _ => throw new NotSupportedException($"Unsupported node type '{node.GetType().Name}'."),
        };
    }

    public string GeneratePredicate(Predicate predicate) =>
        predicate switch
        {
            ConstPredicate constant => EvflFormatter.FormatType(FlowType.Bool, constant.Value),
            QueryPredicate query => GenerateQueryPredicate(query),
            NotPredicate not => $"not ({GeneratePredicate(not.Inner)})",
            AndPredicate and => GenerateAndPredicate(and),
            OrPredicate or => GenerateOrPredicate(or),
            _ => throw new NotSupportedException($"Unsupported predicate type '{predicate.GetType().Name}'."),
        };

    private string GenerateAndPredicate(AndPredicate and)
    {
        var inners = and.Inners;
        return string.Join(" and ", inners.Select(inner => $"({GeneratePredicate(inner)})"));
    }

    private string GenerateOrPredicate(OrPredicate or)
    {
        var inners = or.Inners;
        return string.Join(" or ", inners.Select(inner => $"({GeneratePredicate(inner)})"));
    }

    private string GenerateRoot(RootNode root, int indentLevel)
    {
        var previousVariables = _variableNames;
        _variableNames = root.VariableDefinitions.Select(variable => variable.Name).ToHashSet(StringComparer.Ordinal);
        try
        {
            var local = root.Local ? "local " : string.Empty;
            var variables = string.Join(", ", root.VariableDefinitions.Select(FormatVariableDefinition));
            var edges = _flowBodies.TryGetValue(root, out var entry) ? entry.OutEdges : root.OutEdges;
            var body = edges.Count == 1 && edges[0] is JoinNode { OutEdges.Count: 0 }
                ? $"{EvflFormatter.Indent(indentLevel + 1)}return\n"
                : string.Join('\n', edges.Select(edge => GenerateNode(edge, indentLevel + 1)));
            return $"{EvflFormatter.Indent(indentLevel)}{local}flow {EvflFormatter.Identifier(root.Name)}({variables}):\n"
                + body;
        }
        finally
        {
            _variableNames = previousVariables;
        }
    }

    private void FindNamedEntryBodies(ControlFlowGraph graph)
    {
        var nodes = GraphTraversal.EnumerateStructuredNodes(graph.Roots).ToArray();
        // Group continuations and loop conditions are emitted outside their bodies.
        // An entry inside either body cannot be moved without that remaining work.
        var continuingBodies = nodes.SelectMany(node => node switch
        {
            GroupNode group when group.OutEdges.Any(edge => edge is not TerminalNode) => new[] { group.Root },
            WhileNode loop => [loop.LoopBody],
            DoWhileNode loop => [loop.LoopBody],
            _ => [],
        });
        var partialEntries = continuingBodies
            .SelectMany(body => GraphTraversal.EnumerateStructuredNodes([body]).OfType<EntryPointNode>()).ToHashSet();
        var entries = nodes.OfType<EntryPointNode>().Where(entry => !partialEntries.Contains(entry))
            .ToLookup(entry => entry.EntryLabel, StringComparer.Ordinal);
        foreach (var root in graph.Roots.Where(root => !root.Local).OrderBy(root => root.Name, StringComparer.Ordinal))
        {
            if (root.OutEdges.Count != 1 || root.OutEdges[0] is not SubflowNode { IsSyntheticJump: true, FlowchartName.Length: 0 } jump
                || jump.OutEdges.Any(edge => edge is not TerminalNode)
                || jump.Parameters.Any(pair => pair.Value is not FlowValue.Argument argument || argument.Name != pair.Key)) continue;
            var matches = entries[jump.CalledRootName].ToArray();
            if (matches.Length != 1 || !_namedEntries.TryAdd(matches[0].EntryLabel, root.Name)) continue;
            // The exported flow already names this instruction. Define its body there
            // and use that flow name instead of repeating an inline entry label.
            _flowBodies[root] = matches[0];
        }
    }

    private string GenerateAction(ActionNode action, int indentLevel, FlowNode? stopAt) =>
        FormatHints(GetActionHints(action), indentLevel)
        + $"{EvflFormatter.Indent(indentLevel)}{EvflFormatter.FormatAction(action.Action, action.Parameters, _variableNames)}\n"
        + string.Join('\n', action.OutEdges.Select(edge => GenerateNode(edge, indentLevel, stopAt: stopAt)));

    private string GenerateSwitch(SwitchNode switchNode, int indentLevel)
    {
        var hintText = FormatHints(GetQueryHints(switchNode.Query, switchNode.Parameters), indentLevel);
        var query = EvflFormatter.FormatQuery(switchNode.Query, switchNode.Parameters, negated: false, _variableNames);
        if (switchNode.Cases.Count == 0)
        {
            return hintText
                + $"{EvflFormatter.Indent(indentLevel)}switch {query}:\n"
                + $"{EvflFormatter.Indent(indentLevel + 1)}default:\n"
                + $"{EvflFormatter.Indent(indentLevel + 2)}return\n";
        }

        List<string> cases = [];
        foreach ((var eventName, var values) in switchNode.Cases.OrderBy(pair => MinCaseValue(pair.Value)))
        {
            var destination = switchNode.OutEdges.First(edge => edge.Name == eventName);
            var valuesText = string.Join(", ", values.Select(value => EvflFormatter.FormatType(switchNode.Query.ReturnType, value, forceBoolToInt: true)));
            cases.Add($"{EvflFormatter.Indent(indentLevel + 1)}case {valuesText}:\n"
                + GenerateNode(destination, indentLevel + 2, generatePass: true));
        }

        var defaultCase = string.Empty;
        if (switchNode.Cases.Values.Sum(values => values.Count) < switchNode.Query.ReturnType.NumValues)
        {
            defaultCase = $"{EvflFormatter.Indent(indentLevel + 1)}default:\n{EvflFormatter.Indent(indentLevel + 2)}return\n";
        }

        return hintText + $"{EvflFormatter.Indent(indentLevel)}switch {query}:\n" + string.Concat(cases) + defaultCase;
    }

    private string GenerateFork(ForkNode fork, int indentLevel)
    {
        var code = $"{EvflFormatter.Indent(indentLevel)}fork:\n";
        foreach (var branch in fork.Forks)
        {
            FlowNode edge = fork.OutEdges.Single(edge => edge.Name == branch);
            code += $"{EvflFormatter.Indent(indentLevel + 1)}branch:\n{GenerateNode(edge, indentLevel + 2, generatePass: true)}";
        }

        return code + GenerateNode(fork.JoinNode, indentLevel);
    }

    private string GenerateSubflow(SubflowNode subflow, int indentLevel, FlowNode? stopAt)
    {
        if (subflow.IsSyntheticJump && subflow.FlowchartName.Length == 0
            && subflow.OutEdges.All(edge => edge is TerminalNode)
            && subflow.Parameters.All(pair => pair.Value is FlowValue.Argument argument && argument.Name == pair.Key))
        {
            var target = _namedEntries.GetValueOrDefault(subflow.CalledRootName, subflow.CalledRootName);
            return $"{EvflFormatter.Indent(indentLevel)}jump {EvflFormatter.Identifier(target)}\n";
        }
        var ns = string.IsNullOrEmpty(subflow.FlowchartName) ? string.Empty : $"{EvflFormatter.Identifier(subflow.FlowchartName)}::";
        var parameters = string.Join(", ", subflow.Parameters
            .Select(pair => $"{EvflFormatter.Identifier(pair.Key)}={EvflFormatter.FormatType(FlowType.Any, pair.Value)}"));
        return $"{EvflFormatter.Indent(indentLevel)}run {ns}{EvflFormatter.Identifier(subflow.CalledRootName)}({parameters})\n"
            + string.Join('\n', subflow.OutEdges.Select(edge => GenerateNode(edge, indentLevel, stopAt: stopAt)));
    }

    private string GenerateIfElse(IfElseNode ifElse, int indentLevel)
    {
        var code = string.Empty;
        foreach (var rule in ifElse.Rules)
        {
            var elsePrefix = code.Length > 0 ? "el" : string.Empty;
            code += FormatHints(GetPredicateHints(rule.Predicate), indentLevel);
            code += $"{EvflFormatter.Indent(indentLevel)}{elsePrefix}if {GeneratePredicate(rule.Predicate)}:\n"
                + GenerateNode(rule.Node, indentLevel + 1, generatePass: true);
        }

        if (ifElse.Default is not NoopNode)
        {
            code += $"{EvflFormatter.Indent(indentLevel)}else:\n"
                + GenerateNode(ifElse.Default, indentLevel + 1, generatePass: true);
        }

        return code;
    }

    private string GenerateWhile(WhileNode whileNode, int indentLevel) =>
        FormatHints(GetPredicateHints(whileNode.LoopCondition), indentLevel)
        + $"{EvflFormatter.Indent(indentLevel)}while {GeneratePredicate(whileNode.LoopCondition)}:\n"
        + GenerateNode(whileNode.LoopBody, indentLevel + 1, generatePass: true)
        + GenerateNode(whileNode.LoopExit, indentLevel);

    private string GenerateDoWhile(DoWhileNode doWhile, int indentLevel) =>
        $"{EvflFormatter.Indent(indentLevel)}do:\n"
        + GenerateNode(doWhile.LoopBody, indentLevel + 1, generatePass: true)
        + FormatHints(GetPredicateHints(doWhile.LoopCondition), indentLevel)
        + $"{EvflFormatter.Indent(indentLevel)}while {GeneratePredicate(doWhile.LoopCondition)}\n"
        + GenerateNode(doWhile.LoopExit, indentLevel);

    private string GenerateQueryPredicate(QueryPredicate query)
    {
        if (query.Query.ReturnType == FlowType.Bool)
        {
            return query.Values.Count == 1
                ? EvflFormatter.FormatQuery(query.Query, query.Parameters, query.Negated, _variableNames)
                : query.Negated ? "false" : "true";
        }

        if (query.Values.Count == 1)
        {
            var formattedQuery = EvflFormatter.FormatQuery(query.Query, query.Parameters, negated: false, _variableNames);
            var op = query.Negated ? "!=" : "==";
            return $"{formattedQuery} {op} {EvflFormatter.FormatType(query.Query.ReturnType, query.Values[0], forceBoolToInt: true)}";
        }

        var inOp = query.Negated ? "not in" : "in";
        var values = string.Join(", ", query.Values.Select(value => EvflFormatter.FormatType(query.Query.ReturnType, value, forceBoolToInt: true)));
        return $"{EvflFormatter.FormatQuery(query.Query, query.Parameters, negated: false, _variableNames)} {inOp} ({values})";
    }

    private static string FormatVariableDefinition(VariableDefinition variable)
    {
        var prefix = $"{EvflFormatter.Identifier(variable.Name)}: {EvflFormatter.Identifier(variable.Type.ToString())}";
        return variable.InitialValue is null ? prefix : $"{prefix} = {EvflFormatter.FormatType(variable.Type, variable.InitialValue)}";
    }

    private static long MinCaseValue(IEnumerable<object?> values) =>
        values.Select(value => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)).Min();

    private IEnumerable<string> GetActionHints(ActionNode action)
    {
        foreach (var value in action.Parameters.Values)
        {
            if (value is string stringValue && _hints.TryGetValue(stringValue, out var hint))
            {
                yield return hint;
            }
        }

        var knownParameters = action.Action.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        if (!action.Action.Varargs)
        {
            var extraParameters = string.Join("; ", action.Parameters
                .Where(pair => !knownParameters.Contains(pair.Key))
                .Select(pair => $"{pair.Key} = {EvflFormatter.FormatPythonRepr(pair.Value)}"));

            if (extraParameters.Length > 0)
            {
                yield return $"Extra Params: {extraParameters}";
            }
        }
    }

    private IEnumerable<string> GetQueryHints(QuerySignature query, IReadOnlyDictionary<string, object?> parameters)
    {
        foreach (var value in parameters.Values)
        {
            if (value is string stringValue && _hints.TryGetValue(stringValue, out var hint))
            {
                yield return hint;
            }
        }

        var knownParameters = query.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        if (!query.Varargs)
        {
            var extraParameters = string.Join("; ", parameters
                .Where(pair => !knownParameters.Contains(pair.Key))
                .Select(pair => $"{pair.Key} = {pair.Value}")
                .Order(StringComparer.Ordinal));

            if (extraParameters.Length > 0)
            {
                yield return $"Extra Params: {extraParameters}";
            }
        }
    }

    private IEnumerable<string> GetPredicateHints(Predicate predicate) =>
        predicate.GetQueries().SelectMany(query => GetQueryHints(query.Query, query.Parameters));

    private static string FormatHints(IEnumerable<string> hints, int indentLevel) =>
        string.Concat(hints.Select(hint => $"{EvflFormatter.Indent(indentLevel)}# {hint}\n"));
}
