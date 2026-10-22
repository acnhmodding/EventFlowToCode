using EventFlowToCode.Core.Graph;
using EventFlowToCode.Core.Model;
using EventFlowToCode.Core.Syntax;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EventFlowToCode.Core.Compile;

internal sealed partial class EvflParser
{
    private readonly IReadOnlyList<EvflToken> _tokens;
    private readonly FunctionCatalog _functions;
    private readonly Dictionary<ActorName, CompileActor> _actors = [];
    private readonly Dictionary<string, IReadOnlyList<VariableDefinition>> _flowVariables = [];
    private readonly Dictionary<string, CompileNode> _sharedNodes = [];
    private readonly HashSet<string> _usedEventNames = [];
    private readonly List<CompileRootNode> _entryPointAliases = [];
    private readonly List<(string Name, CompileNode Entry, IReadOnlyList<VariableDefinition> Variables)> _variableChecks = [];
    private readonly List<(string Target, CompileConnectorNode Node, int Line, int Column, bool IsEvent)> _jumps = [];
    private int _position;
    private int _nextId;

    public EvflParser(IReadOnlyList<EvflToken> tokens, FunctionCatalog functions)
    {
        _tokens = tokens;
        _functions = functions;
        ReserveEntryPointNames();
    }

    public CompileDocument Parse()
    {
        List<CompileRootNode> roots = [];
        while (!Check(EvflTokenKind.End))
        {
            if (Match(EvflTokenKind.NewLine) || Match(EvflTokenKind.Dedent))
            {
                continue;
            }

            if (CheckKeyword("entrypoint"))
            {
                roots.Add(ParseTopLevelEntryPoint());
            }
            else if (CheckKeyword("actor"))
            {
                ParseActorDeclaration();
            }
            else if (CheckKeyword("label"))
            {
                roots.Add(ParseTopLevelLabel());
            }
            else if (CheckKeyword("case") || CheckKeyword("default") || CheckKeyword("else") || CheckKeyword("elif"))
            {
                throw Error($"Unexpected {Peek().Text} outside its enclosing statement.");
            }
            else
            {
                roots.AddRange(ParseFlow());
            }
        }

        // Bind local calls after all signatures are known, including forward declarations.
        foreach (var check in _variableChecks)
        {
            VerifyVariables(check.Name, check.Entry, check.Variables);
        }
        ResolveJumps(roots);
        if (_jumps.Count > 0)
        {
            foreach (var check in _variableChecks) VerifyVariables(check.Name, check.Entry, check.Variables);
        }
        CollapseConnectors(roots);
        foreach (var root in roots)
        {
            ReplaceNode(root, CompileTerminalNode.Instance, null);
        }

        return new CompileDocument(roots, _actors.Values.ToList());
    }

    private List<CompileRootNode> ParseFlow()
    {
        var local = MatchKeyword("local");
        if (MatchKeyword("inline"))
        {
            local = true;
        }
        ExpectKeyword("flow");
        var name = ParseIdentifier();
        Expect(EvflTokenKind.LeftParen);
        var variables = ParseFlowParameters();
        if (!_flowVariables.TryAdd(name, variables))
        {
            throw Error($"Duplicate flow '{name}'.");
        }
        Expect(EvflTokenKind.RightParen);
        var aliasStart = _entryPointAliases.Count;
        var block = ParseBlock();

        _variableChecks.Add((name, block.Entry, variables));
        foreach (var alias in _entryPointAliases.Skip(aliasStart))
        {
            alias.Variables.AddRange(variables.Select(variable => new VariableDefinition(variable.Name, variable.Type, null)));
        }
        CompileRootNode root = new(name, local, entrypoint: false, variables: variables);
        root.AddOutEdge(block.Entry);
        if (!IsTerminator(block.Exit))
        {
            block.Exit.AddOutEdge(CompileTerminalNode.Instance);
        }

        List<CompileRootNode> roots = [.. block.EntryPoints, root];
        return roots;
    }

    private void ResolveJumps(IReadOnlyList<CompileRootNode> roots)
    {
        if (_jumps.Count == 0) return;
        var targets = roots.GroupBy(root => root.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var jump in _jumps)
        {
            if (jump.IsEvent)
            {
                if (!_sharedNodes.TryGetValue(jump.Target, out var instruction))
                    throw new InvalidDataException($"Jump event label '{jump.Target}' is not defined. At line {jump.Line}, column {jump.Column}.");
                jump.Node.AddOutEdge(instruction);
                continue;
            }
            if (!targets.TryGetValue(jump.Target, out var matches)
                || matches.Length > 1 && matches.Any(root => !root.Entrypoint))
                throw new InvalidDataException($"Jump target '{jump.Target}' must identify exactly one flow or entrypoint. At line {jump.Line}, column {jump.Column}.");
            // Generated inline labels can occur in several structured branches. Use the same
            // first-definition convention as entrypoint emission.
            jump.Node.AddOutEdge(matches[0].OutEdges.FirstOrDefault() ?? CompileTerminalNode.Instance);
        }
        // A connector-only cycle has no executable event to encode as an index.
        HashSet<CompileNode> resolved = [];
        foreach (var jump in _jumps)
        {
            HashSet<CompileNode> visited = [];
            CompileNode current = jump.Node;
            while (current is CompileConnectorNode && current.OutEdges.Count == 1 && !resolved.Contains(current))
            {
                if (!visited.Add(current)) throw new InvalidDataException($"Jump cycle through '{jump.Target}' contains no event. At line {jump.Line}, column {jump.Column}.");
                current = current.OutEdges[0];
            }
            resolved.UnionWith(visited);
        }
    }

    private void ParseActorDeclaration()
    {
        ExpectKeyword("actor");
        var name = ParseIdentifier();
        var secondaryName = string.Empty;
        var argumentName = string.Empty;
        if (Match(EvflTokenKind.At))
        {
            secondaryName = ParseIdentifier();
        }
        if (Match(EvflTokenKind.LeftParen))
        {
            argumentName = ParseIdentifier();
            Expect(EvflTokenKind.RightParen);
        }
        var entryPointName = MatchKeyword("in") ? ParseIdentifier() : null;
        Expect(EvflTokenKind.NewLine);

        var actor = GetActor(new ActorName(name, secondaryName, argumentName));
        actor.Declared = true;
        if (entryPointName is not null)
        {
            if (actor.EntryPointName is not null && actor.EntryPointName != entryPointName)
                throw Error($"Actor '{name}' has conflicting entrypoint bindings.");
            actor.EntryPointName = entryPointName;
        }
    }

    private CompileRootNode ParseTopLevelEntryPoint()
    {
        ExpectKeyword("entrypoint");
        var name = ParseIdentifier();
        Expect(EvflTokenKind.Colon);
        Expect(EvflTokenKind.NewLine);
        Block block;
        if (Match(EvflTokenKind.Indent))
        {
            block = ParseRootBlock(null);
            Expect(EvflTokenKind.Dedent);
        }
        else
        {
            block = ParseRootBlock(null);
        }

        CompileRootNode root = new(name, entrypoint: true);
        root.AddOutEdge(block.Entry);
        if (!IsTerminator(block.Exit))
        {
            block.Exit.AddOutEdge(CompileTerminalNode.Instance);
        }

        return root;
    }

    private CompileRootNode ParseTopLevelLabel()
    {
        ExpectKeyword("label");
        var name = ParseIdentifier();
        Expect(EvflTokenKind.Colon);
        Expect(EvflTokenKind.NewLine);
        Block block;
        if (Match(EvflTokenKind.Indent))
        {
            block = ParseRootBlock(name);
            Expect(EvflTokenKind.Dedent);
        }
        else
        {
            block = ParseRootBlock(name);
        }

        CompileRootNode root = new(name, local: true, emitEntryPoint: false);
        root.AddOutEdge(block.Entry);
        if (!IsTerminator(block.Exit))
        {
            block.Exit.AddOutEdge(CompileTerminalNode.Instance);
        }

        return root;
    }

    private List<VariableDefinition> ParseFlowParameters()
    {
        List<VariableDefinition> variables = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        if (Check(EvflTokenKind.RightParen))
        {
            return variables;
        }

        do
        {
            var name = ParseIdentifier();
            if (!names.Add(name))
            {
                throw Error($"Duplicate flow parameter '{name}'.");
            }
            Expect(EvflTokenKind.Colon);
            FlowType type = new(ParseIdentifier());
            object? initialValue = null;
            if (Match(EvflTokenKind.Equals))
            {
                var value = ParseBaseValue();
                if (value.Type != type)
                {
                    throw Error($"Variable '{name}' is {type} but has default value {value.Type}.");
                }
                initialValue = value.Value;
            }
            variables.Add(new VariableDefinition(name, type, initialValue));
        } while (Match(EvflTokenKind.Comma) && !Check(EvflTokenKind.RightParen));

        return variables;
    }

    private Block ParseBlock()
    {
        Expect(EvflTokenKind.Colon);
        Expect(EvflTokenKind.NewLine);
        var hasIndent = Match(EvflTokenKind.Indent);
        if (!hasIndent && !IsBlockLabel())
        {
            throw Error($"Expected {EvflTokenKind.Indent}.");
        }

        List<Block> blocks = [];
        while (!Check(EvflTokenKind.Dedent) && !Check(EvflTokenKind.End))
        {
            if (hasIndent && (CheckKeyword("case") || CheckKeyword("default") || CheckKeyword("else") || CheckKeyword("elif")))
            {
                break;
            }
            if (!hasIndent && !IsBlockLabel())
            {
                break;
            }

            string? entrypoint = null;
            var exportedEntryPoint = false;
            if (MatchKeyword("entrypoint"))
            {
                exportedEntryPoint = true;
                entrypoint = ParseIdentifier();
                Expect(EvflTokenKind.Colon);
                Expect(EvflTokenKind.NewLine);
                if (Match(EvflTokenKind.Indent))
                {
                    blocks.Add(ParseRootBlock(entrypoint, exportedEntryPoint));
                    if (Check(EvflTokenKind.Dedent))
                    {
                        Expect(EvflTokenKind.Dedent);
                    }
                    continue;
                }
            }
            else if (MatchKeyword("label"))
            {
                entrypoint = ParseIdentifier();
                Expect(EvflTokenKind.Colon);
                Expect(EvflTokenKind.NewLine);
                if (Match(EvflTokenKind.Indent))
                {
                    blocks.Add(ParseRootBlock(entrypoint, exportedEntryPoint));
                    if (Check(EvflTokenKind.Dedent))
                    {
                        Expect(EvflTokenKind.Dedent);
                    }
                    continue;
                }
            }

            blocks.Add(ParseRootBlock(entrypoint, exportedEntryPoint));
        }

        if (hasIndent && Check(EvflTokenKind.Dedent))
        {
            Expect(EvflTokenKind.Dedent);
        }
        return LinkBlocks(blocks);
    }

    private bool IsBlockLabel() => CheckKeyword("entrypoint") || CheckKeyword("label");

    private Block ParseRootBlock(string? entrypoint, bool exportedEntryPoint = false)
    {
        List<Block> statements = [];
        while (!Check(EvflTokenKind.Dedent)
            && !Check(EvflTokenKind.End)
            && !IsBlockLabel()
            && !CheckKeyword("flow")
            && !CheckKeyword("case")
            && !CheckKeyword("default")
            && !CheckKeyword("else")
            && !CheckKeyword("elif"))
        {
            var statement = ParseStatement();
            if (statement is not null)
            {
                statements.Add(statement);
                if (IsTerminator(statement.Exit))
                {
                    break;
                }
            }
        }

        var connector = NewConnector();
        if (statements.Count == 0)
        {
            return new Block([], connector, connector);
        }

        for (var i = 0; i < statements.Count; i++)
        {
            var currentExit = statements[i].Exit;
            var nextEntry = i + 1 < statements.Count ? statements[i + 1].Entry : connector;
            if (!IsTerminator(currentExit))
            {
                currentExit.AddOutEdge(nextEntry);
            }
        }

        var entrypoints = statements.SelectMany(statement => statement.EntryPoints).ToList();
        if (entrypoint is not null)
        {
            ReserveEventName(entrypoint);
            statements[0].Entry.Name = entrypoint;
            if (exportedEntryPoint)
            {
                CompileRootNode entrypointAlias = new(entrypoint, local: true, entrypoint: true);
                entrypointAlias.AddOutEdge(statements[0].Entry);
                _entryPointAliases.Add(entrypointAlias);
                entrypoints.Add(entrypointAlias);
            }
        }

        return new Block(entrypoints, statements[0].Entry, connector);
    }

    private Block LinkBlocks(List<Block> rootBlocks)
    {
        if (rootBlocks.Count == 0)
        {
            var connector = NewConnector();
            return new Block([], connector, connector);
        }

        for (var i = 0; i < rootBlocks.Count - 1; i++)
        {
            if (!IsTerminator(rootBlocks[i].Exit))
            {
                rootBlocks[i].Exit.AddOutEdge(rootBlocks[i + 1].Entry);
            }
        }

        return new Block(
            rootBlocks.SelectMany(block => block.EntryPoints).ToList(),
            rootBlocks[0].Entry,
            rootBlocks[^1].Exit);
    }

    private static bool IsTerminator(CompileNode node) =>
        node is CompileTerminalNode;

    private Block? ParseStatement()
    {
        if (Match(EvflTokenKind.NewLine))
        {
            return null;
        }
        if (Match(EvflTokenKind.At))
        {
            var name = ParseIdentifier();
            Expect(EvflTokenKind.NewLine);
            var statement = ParseStatement()
                ?? throw Error($"Expected statement after shared event label '{name}'.");
            if (_sharedNodes.TryGetValue(name, out var existing))
            {
                if (!SameSharedInstruction(existing, statement.Entry))
                    throw Error($"Shared event label '{name}' has conflicting instruction definitions.");
                return new Block(statement.EntryPoints, existing, existing);
            }

            statement.Entry.Name = name;
            ReserveEventName(name);
            _sharedNodes[name] = statement.Entry;
            return statement;
        }
        if (MatchKeyword("pass"))
        {
            Expect(EvflTokenKind.NewLine);
            return null;
        }
        if (MatchKeyword("return"))
        {
            Expect(EvflTokenKind.NewLine);
            return new Block([], CompileTerminalNode.Instance, CompileTerminalNode.Instance);
        }
        if (MatchKeyword("run"))
        {
            return ParseSubflow();
        }
        if (CheckKeyword("jump") && (Peek(1).Kind is EvflTokenKind.Identifier or EvflTokenKind.QuotedIdentifier
            || Peek(1).Kind == EvflTokenKind.At && Peek(3).Kind == EvflTokenKind.NewLine))
        {
            var token = Peek();
            ExpectKeyword("jump");
            var isEvent = Match(EvflTokenKind.At);
            var target = ParseIdentifier();
            Expect(EvflTokenKind.NewLine);
            var connector = NewConnector();
            _jumps.Add((target, connector, token.Line, token.Column, isEvent));
            return new Block([], connector, CompileTerminalNode.Instance);
        }
        if (MatchKeyword("switch"))
        {
            return ParseSwitch();
        }
        if (MatchKeyword("fork"))
        {
            return ParseFork();
        }
        if (MatchKeyword("if"))
        {
            return ParseIf();
        }
        if (MatchKeyword("while"))
        {
            return ParseWhile();
        }
        if (MatchKeyword("do"))
        {
            return ParseDoWhile();
        }
        if (TryParseIndexedAction(out var indexedAction))
        {
            return indexedAction!;
        }
        if (TryParseVariableAction(out var variableAction))
        {
            return variableAction!;
        }

        return ParseAction();
    }

    private Block ParseAction()
    {
        var call = ParseFunctionCall();
        Expect(EvflTokenKind.NewLine);
        var action = ResolveAction(call.Actor, call.Name);
        var parameters = PrepareParameters(action.Parameters, call.Parameters, action.Name);
        MarkActionUsed(action);
        CompileActionNode node = new(NewEventName(), action, parameters);
        return new Block([], node, node);
    }

    private bool TryParseIndexedAction(out Block? result)
    {
        result = null;
        var start = _position;
        if (!TryParseIndexedAccess(out var access))
        {
            _position = start;
            return false;
        }

        var plusAssign = Match(EvflTokenKind.PlusEquals);
        if (!plusAssign)
        {
            Expect(EvflTokenKind.Equals);
        }
        var value = ParseValue();
        Expect(EvflTokenKind.NewLine);

        var action = ResolveIndexedAction(access, plusAssign, value);
        var parameters = BuildIndexedActionParameters(action, access, value);
        MarkActionUsed(action);
        CompileActionNode node = new(NewEventName(), action, parameters);
        result = new Block([], node, node);
        return true;
    }

    private bool TryParseVariableAction(out Block? result)
    {
        result = null;
        var start = _position;
        if (!Check(EvflTokenKind.Identifier) && !Check(EvflTokenKind.QuotedIdentifier))
        {
            return false;
        }

        var variableName = ParseIdentifier();
        var plusAssign = Match(EvflTokenKind.PlusEquals);
        if (!plusAssign && !Match(EvflTokenKind.Equals))
        {
            _position = start;
            return false;
        }

        var value = ParseValue();
        Expect(EvflTokenKind.NewLine);
        var action = ResolveAction(new ActorName("EventFlowSystemActor"), plusAssign ? "EntryVariableAdd" : "EntryVariableSet");
        Dictionary<string, CompileValue> parameters = [];
        foreach (var parameter in action.Parameters)
        {
            parameters[parameter.Name] = parameter.Name == "Key"
                ? CoerceParameter(action.Name, parameter, new CompileValue(FlowType.Any, new FlowValue.Argument(variableName)))
                : CoerceParameter(action.Name, parameter, value);
        }

        MarkActionUsed(action);
        CompileActionNode node = new(NewEventName(), action, parameters);
        result = new Block([], node, node);
        return true;
    }

    private Block ParseSubflow()
    {
        var first = ParseIdentifier();
        var flowchartName = string.Empty;
        var rootName = first;
        if (Match(EvflTokenKind.DoubleColon))
        {
            flowchartName = first;
            rootName = ParseIdentifier();
        }
        Expect(EvflTokenKind.LeftParen);
        var parameters = ParseNamedParameters();
        Expect(EvflTokenKind.RightParen);
        Expect(EvflTokenKind.NewLine);

        CompileSubflowNode node = new(NewEventName(), flowchartName, rootName, parameters);
        return new Block([], node, node);
    }

    private static bool SameSharedInstruction(CompileNode first, CompileNode second) => (first, second) switch
    {
        (CompileActionNode a, CompileActionNode b) => a.Action.ActorName == b.Action.ActorName && a.Action.Name == b.Action.Name
            && SameParameters(a.Parameters, b.Parameters),
        (CompileSubflowNode a, CompileSubflowNode b) => a.FlowchartName == b.FlowchartName && a.CalledRootName == b.CalledRootName
            && SameParameters(a.Parameters, b.Parameters),
        (CompileSwitchNode a, CompileSwitchNode b) => a.Query.ActorName == b.Query.ActorName && a.Query.Name == b.Query.Name
            && SameParameters(a.Parameters, b.Parameters)
            && a.Cases.Values.SelectMany(values => values).Order().SequenceEqual(b.Cases.Values.SelectMany(values => values).Order()),
        _ => false,
    };

    private static bool SameParameters(IReadOnlyDictionary<string, CompileValue> first, IReadOnlyDictionary<string, CompileValue> second) =>
        first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var value)
            && SameValue(pair.Value.Value, value.Value));

    private static bool SameValue(object? first, object? second) => (first, second) switch
    {
        (IReadOnlyDictionary<string, object?> a, IReadOnlyDictionary<string, object?> b) => a.Count == b.Count
            && a.All(pair => b.TryGetValue(pair.Key, out var value) && SameValue(pair.Value, value)),
        (Array a, Array b) => a.GetType() == b.GetType() && System.Collections.StructuralComparisons.StructuralEqualityComparer.Equals(a, b),
        _ => Equals(first, second),
    };

    private Block ParseSwitch()
    {
        var exact = CheckKeyword("exact") && Peek(1).Kind is EvflTokenKind.Identifier or EvflTokenKind.QuotedIdentifier && MatchKeyword("exact");
        QuerySignature query;
        Dictionary<string, CompileValue> parameters;
        var start = _position;
        if (TryParseIndexedAccess(out var access))
        {
            if (Match(EvflTokenKind.Compare, out var opToken))
            {
                var comparison = ParseValue();
                query = ResolveIndexedQuery(access, predicate: true);
                parameters = BuildIndexedQueryParameters(
                    query,
                    access,
                    opToken!.Text,
                    comparison);
            }
            else
            {
                query = ResolveIndexedQuery(access, predicate: false);
                parameters = BuildIndexedQueryParameters(query, access, op: null, comparisonValue: null);
            }
        }
        else if (TryParseVariableSwitch(out var variableQuery, out var variableParameters))
        {
            query = variableQuery;
            parameters = variableParameters;
        }
        else
        {
            _position = start;
            var call = ParseFunctionCall();
            query = ResolveQuery(call.Actor, call.Name);
            if (Match(EvflTokenKind.Compare, out var opToken))
            {
                if (IsPredicateRule(query))
                {
                    var comparison = ParseValue();
                    (parameters, _) = PreparePredicateRule(query, call.Parameters, opToken!.Text, comparison);
                }
                else
                {
                    var value = ParseCaseValue(query.ReturnType);
                    parameters = PreparePredicateParameters(
                        query,
                        call.Parameters,
                        opToken!.Text,
                        new CompileValue(FlowType.Int, value));
                }
            }
            else
            {
                parameters = PrepareParameters(query.Parameters, call.Parameters, query.Name);
            }
        }
        Expect(EvflTokenKind.Colon);
        Expect(EvflTokenKind.NewLine);
        Expect(EvflTokenKind.Indent);
        var caseColumn = CheckKeyword("case") || CheckKeyword("default") ? Peek().Column : -1;

        MarkQueryUsed(query);
        CompileSwitchNode node = new(NewEventName(), query, parameters) { PreserveTerminalCases = exact };
        List<(List<int> Values, Block Block)> cases = [];
        Block? defaultBlock = null;

        while (true)
        {
            ConsumeDedentsBeforeSwitchCase(caseColumn);
            if (Check(EvflTokenKind.Dedent) || Check(EvflTokenKind.End))
            {
                break;
            }

            if (exact && cases.Count == 0 && MatchKeyword("pass"))
            {
                Expect(EvflTokenKind.NewLine);
                break;
            }

            if (MatchKeyword("case"))
            {
                var values = ParseIntList(exact ? FlowType.Int : query.ReturnType);
                var block = ParseBlock();
                cases.Add((values, block));
            }
            else if (MatchKeyword("default"))
            {
                if (exact) throw Error("An exact switch lists only its stored cases and cannot have a default branch.");
                if (defaultBlock is not null)
                {
                    throw Error("A switch cannot have more than one default branch.");
                }
                defaultBlock = ParseBlock();
            }
            else
            {
                break;
            }
        }
        if (Check(EvflTokenKind.Dedent))
        {
            Expect(EvflTokenKind.Dedent);
        }

        List<CompileRootNode> entrypoints = [];
        HashSet<int> explicitValues = [];
        foreach ((var values, var block) in cases)
        {
            entrypoints.AddRange(block.EntryPoints);
            node.AddOutEdge(block.Entry);
            if (!IsTerminator(block.Exit))
            {
                block.Exit.AddOutEdge(node.Connector);
            }
            foreach (var value in values)
            {
                if (!explicitValues.Add(value))
                {
                    throw Error($"Duplicate switch case value {value}.");
                }
                node.AddCase(block.Entry, value);
            }
        }

        var numValues = exact ? 0 : QueryDomainSize(query);
        var defaultValues = Enumerable.Range(0, numValues).Where(value => !explicitValues.Contains(value)).ToList();
        if (defaultValues.Count > 0)
        {
            var defaultEntry = defaultBlock?.Entry ?? node.Connector;
            if (defaultBlock is not null)
            {
                entrypoints.AddRange(defaultBlock.EntryPoints);
                if (!IsTerminator(defaultBlock.Exit))
                {
                    defaultBlock.Exit.AddOutEdge(node.Connector);
                }
            }
            node.AddOutEdge(defaultEntry);
            foreach (var value in defaultValues)
            {
                node.AddCase(defaultEntry, value);
            }
        }

        return new Block(entrypoints, node, node.Connector);
    }

    private bool TryParseVariableSwitch(
        out QuerySignature query,
        out Dictionary<string, CompileValue> parameters)
    {
        query = null!;
        parameters = null!;
        var start = _position;
        if (!Check(EvflTokenKind.Identifier) && !Check(EvflTokenKind.QuotedIdentifier))
        {
            return false;
        }

        var variableName = ParseIdentifier();
        if (Match(EvflTokenKind.Compare, out var opToken))
        {
            var comparison = ParseValue();
            query = ResolveQuery(new ActorName("EventFlowSystemActor"), "CheckEntryVariableInt");
            List<(string? Name, CompileValue Value)> provided = [(null, new CompileValue(FlowType.Any, new FlowValue.Argument(variableName)))];
            (parameters, _) = PreparePredicateRule(query, provided, opToken!.Text, comparison);
            return true;
        }

        if (Check(EvflTokenKind.Colon))
        {
            query = ResolveQuery(new ActorName("EventFlowSystemActor"), "CheckEntryVariableBool");
            parameters = [];
            foreach (var parameter in query.Parameters.Where(parameter => parameter.Name == "Value"))
            {
                parameters[parameter.Name] = CoerceParameter(
                    query.Name,
                    parameter,
                    new CompileValue(FlowType.Any, new FlowValue.Argument(variableName)));
            }
            return true;
        }

        _position = start;
        return false;
    }

    private Block ParseFork()
    {
        Expect(EvflTokenKind.Colon);
        Expect(EvflTokenKind.NewLine);
        if (!Match(EvflTokenKind.Indent))
        {
            var connector = NewConnector();
            return new Block([], connector, connector);
        }

        List<Block> branches = [];
        while (!Check(EvflTokenKind.Dedent))
        {
            ExpectKeyword("branch");
            branches.Add(ParseBlock());
        }
        if (Check(EvflTokenKind.Dedent))
        {
            Expect(EvflTokenKind.Dedent);
        }
        if (branches.Count == 0)
        {
            var connector = NewConnector();
            return new Block([], connector, connector);
        }

        CompileJoinNode join = new(NewEventName());
        CompileForkNode fork = new(NewEventName(), join, branches.Select(branch => branch.Entry));
        List<CompileRootNode> entrypoints = [];
        foreach (var branch in branches)
        {
            entrypoints.AddRange(branch.EntryPoints);
            fork.AddOutEdge(branch.Entry);
        }
        fork.AddOutEdge(join);

        return new Block(entrypoints, fork, join);
    }

    private Block ParseIf()
    {
        var condition = ParsePredicate();
        var body = ParseBlock();
        List<(PredicatePlan Condition, Block Body)> branches = [(condition, body)];
        Block? defaultBlock = null;

        while (MatchKeyword("elif"))
        {
            branches.Add((ParsePredicate(), ParseBlock()));
        }

        if (MatchKeyword("else"))
        {
            defaultBlock = ParseBlock();
        }

        List<CompileRootNode> entrypoints = [];
        CompileNode next = NewConnector();
        var nextConnector = next;
        if (defaultBlock is not null)
        {
            entrypoints.AddRange(defaultBlock.EntryPoints);
            next = defaultBlock.Entry;
            nextConnector = defaultBlock.Exit;
        }

        foreach ((var branchCondition, var branchBody) in branches.AsEnumerable().Reverse())
        {
            entrypoints.AddRange(branchBody.EntryPoints);
            var expanded = ExpandPredicate(branchCondition, branchBody.Entry, next);
            var connector = NewConnector();
            if (!IsTerminator(branchBody.Exit))
            {
                branchBody.Exit.AddOutEdge(connector);
            }
            if (!IsTerminator(nextConnector))
            {
                nextConnector.AddOutEdge(connector);
            }
            nextConnector = connector;
            next = expanded;
        }

        return new Block(entrypoints, next, nextConnector);
    }

    private Block ParseWhile()
    {
        var condition = ParsePredicate();
        var body = ParseBlock();
        CompileNode exit = NewConnector();
        var entry = ExpandPredicate(condition, body.Entry, exit);
        if (!IsTerminator(body.Exit))
        {
            body.Exit.AddOutEdge(entry);
        }

        return new Block(body.EntryPoints, entry, exit);
    }

    private Block ParseDoWhile()
    {
        var body = ParseBlock();
        List<string> conditionEntries = [];
        while (MatchKeyword("entrypoint"))
        {
            var name = ParseIdentifier();
            ReserveEventName(name);
            conditionEntries.Add(name);
            Expect(EvflTokenKind.Colon);
            Expect(EvflTokenKind.NewLine);
        }
        ExpectKeyword("while");
        var condition = ParsePredicate();
        Expect(EvflTokenKind.NewLine);
        CompileNode exit = NewConnector();
        var test = ExpandPredicate(condition, body.Entry, exit);
        if (!IsTerminator(body.Exit))
        {
            body.Exit.AddOutEdge(test);
        }
        List<CompileRootNode> entries = [.. body.EntryPoints];
        foreach (var name in conditionEntries)
        {
            CompileRootNode alias = new(name, local: true, entrypoint: true);
            alias.AddOutEdge(test);
            _entryPointAliases.Add(alias);
            entries.Add(alias);
        }
        return new Block(entries, body.Entry, exit);
    }

    private CompileNode ExpandPredicate(PredicatePlan predicate, CompileNode trueNode, CompileNode falseNode)
    {
        return predicate switch
        {
            ConstantPredicatePlan constant => constant.Value ? trueNode : falseNode,
            PredicateTable table => ExpandPredicateTable(table, trueNode, falseNode),
            _ => throw Error("Unsupported predicate."),
        };
    }

    private CompileNode ExpandPredicateTable(PredicateTable table, CompileNode trueNode, CompileNode falseNode)
        => ExpandPredicateTable(table, trueNode, falseNode, []);

    private CompileNode ExpandPredicateTable(
        PredicateTable table, CompileNode trueNode, CompileNode falseNode,
        Dictionary<PredicateTable, CompileSwitchNode> expanded)
    {
        if (expanded.TryGetValue(table, out var existing))
        {
            return existing;
        }
        CompileSwitchNode switchNode = new(NewEventName(), table.Query, table.Parameters);
        expanded.Add(table, switchNode);
        MarkQueryUsed(table.Query);
        foreach (var branch in table.Branches)
        {
            var destination = branch.Action switch
            {
                ConstantPredicatePlan { Value: true } => trueNode,
                ConstantPredicatePlan { Value: false } => falseNode,
                PredicateTable nested => ExpandPredicateTable(nested, trueNode, falseNode, expanded),
                _ => throw Error("Incomplete predicate table."),
            };
            switchNode.AddOutEdge(destination);
            foreach (var value in branch.Values)
            {
                switchNode.AddCase(destination, value);
            }
        }

        return switchNode;
    }
    private PredicatePlan ParsePredicate()
    {
        var left = ParseAndPredicate();
        if (MatchKeyword("or"))
        {
            return CombineOr(left, ParsePredicate());
        }

        return left;
    }

    private PredicatePlan ParseAndPredicate()
    {
        var left = ParseNotPredicate();
        if (MatchKeyword("and"))
        {
            return CombineAnd(left, ParseAndPredicate());
        }

        return left;
    }

    private PredicatePlan ParseNotPredicate()
    {
        if (MatchKeyword("not"))
        {
            return NegatePredicate(ParseNotPredicate());
        }

        return ParsePredicateAtom();
    }

    private PredicatePlan ParsePredicateAtom()
    {
        if (MatchKeyword("true"))
        {
            return new ConstantPredicatePlan(true);
        }
        if (MatchKeyword("false"))
        {
            return new ConstantPredicatePlan(false);
        }
        if (Match(EvflTokenKind.LeftParen))
        {
            var result = ParsePredicate();
            Expect(EvflTokenKind.RightParen);
            return result;
        }

        if (TryParseIndexedPredicate(out var indexedPredicate))
        {
            return indexedPredicate!;
        }

        if (TryParseVariablePredicate(out var variablePredicate))
        {
            return variablePredicate!;
        }

        var call = ParseFunctionCall();
        var query = ResolveQuery(call.Actor, call.Name);
        return ParseQueryPredicate(query, call.Parameters);
    }

    private PredicatePlan ParseQueryPredicate(QuerySignature query, IReadOnlyList<(string? Name, CompileValue Value)> provided)
    {
        var numValues = QueryDomainSize(query);
        if (MatchKeyword("in"))
        {
            var parameters = PrepareParameters(query.Parameters, provided, query.Name);
            var matched = ParseIntList(query.ReturnType).ToHashSet();
            return PredicateTable.FromSets(query, parameters, matched, Enumerable.Range(0, numValues).Where(value => !matched.Contains(value)).ToHashSet());
        }
        if (MatchKeyword("not"))
        {
            ExpectKeyword("in");
            var parameters = PrepareParameters(query.Parameters, provided, query.Name);
            var unmatched = ParseIntList(query.ReturnType).ToHashSet();
            var matched = Enumerable.Range(0, numValues).Where(value => !unmatched.Contains(value)).ToHashSet();
            return new PredicateTable(query, parameters, [(unmatched, false), (matched, true)]);
        }
        if (Match(EvflTokenKind.Compare, out var opToken))
        {
            if (IsPredicateRule(query))
            {
                var comparison = ParseValue();
                (var predicateParameters, var negated) = PreparePredicateRule(query, provided, opToken!.Text, comparison);
                return BoolPredicateTable(query, predicateParameters, numValues, negated);
            }

            var value = ParseCaseValue(query.ReturnType);
            var parameters = PreparePredicateParameters(query, provided, opToken!.Text, new CompileValue(FlowType.Int, value));
            (var matched, var unmatched) = ComparisonSets(opToken.Text, value, numValues);
            return PredicateTable.FromSets(query, parameters, matched, unmatched);
        }

        var boolParameters = PrepareParameters(query.Parameters, provided, query.Name);
        return BoolPredicateTable(query, boolParameters, numValues);
    }

    private static PredicatePlan CombineOr(PredicatePlan left, PredicatePlan right)
    {
        if (left is ConstantPredicatePlan constant)
        {
            return right is ConstantPredicatePlan other
                ? new ConstantPredicatePlan(constant.Value || other.Value)
                : constant.Value ? constant : right;
        }

        ((PredicateTable)left).ReplaceResult(false, right);
        return left;
    }

    private static PredicatePlan CombineAnd(PredicatePlan left, PredicatePlan right)
    {
        if (left is ConstantPredicatePlan constant)
        {
            return right is ConstantPredicatePlan other
                ? new ConstantPredicatePlan(constant.Value && other.Value)
                : constant.Value ? right : constant;
        }

        ((PredicateTable)left).ReplaceResult(true, right);
        return left;
    }

    private static PredicatePlan NegatePredicate(PredicatePlan predicate)
    {
        if (predicate is ConstantPredicatePlan constant)
        {
            return new ConstantPredicatePlan(!constant.Value);
        }

        var table = (PredicateTable)predicate;
        table.InvertResults();
        return table;
    }

    private bool TryParseVariablePredicate(out PredicatePlan? result)
    {
        result = null;
        var start = _position;
        if (!Check(EvflTokenKind.Identifier) && !Check(EvflTokenKind.QuotedIdentifier))
        {
            return false;
        }

        var variableName = ParseIdentifier();
        if (!Match(EvflTokenKind.Compare, out var opToken))
        {
            if (Check(EvflTokenKind.Dot) || Check(EvflTokenKind.At) || Check(EvflTokenKind.LeftParen) || Check(EvflTokenKind.LeftSquare))
            {
                _position = start;
                return false;
            }

            var boolQuery = ResolveQuery(new ActorName("EventFlowSystemActor"), "CheckEntryVariableBool");
            if (boolQuery.Parameters.Count == 0)
            {
                _position = start;
                return false;
            }

            Dictionary<string, CompileValue> boolParameters = [];
            foreach (var parameter in boolQuery.Parameters)
            {
                if (parameter.Name == "Value")
                {
                    boolParameters[parameter.Name] = CoerceParameter(boolQuery.Name, parameter, new CompileValue(FlowType.Any, new FlowValue.Argument(variableName)));
                }
            }

            var boolNumValues = QueryDomainSize(boolQuery);
            result = BoolPredicateTable(boolQuery, boolParameters, boolNumValues);
            return true;
        }

        var comparison = ParseValue();
        var query = ResolveQuery(new ActorName("EventFlowSystemActor"), "CheckEntryVariableInt");
        List<(string? Name, CompileValue Value)> provided = [(null, new CompileValue(FlowType.Any, new FlowValue.Argument(variableName)))];
        (var parameters, var negated) = PreparePredicateRule(query, provided, opToken!.Text, comparison);

        var numValues = QueryDomainSize(query);
        result = BoolPredicateTable(query, parameters, numValues, negated);
        return true;
    }

    private bool TryParseIndexedPredicate(out PredicatePlan? result)
    {
        result = null;
        if (!TryParseIndexedAccess(out var access)) return false;

        var predicate = Check(EvflTokenKind.Compare);
        var query = ResolveIndexedQuery(access, predicate);
        result = ParseQueryPredicate(query, IndexedQueryArguments(query, access));
        return true;
    }

    private static (HashSet<int> Matched, HashSet<int> Unmatched) ComparisonSets(string op, int value, int numValues)
    {
        HashSet<int> matched = [];
        HashSet<int> unmatched = [];
        for (var i = 0; i < numValues; i++)
        {
            var match = op switch
            {
                "==" => i == value,
                "!=" => i != value,
                "<" => i < value,
                "<=" => i <= value,
                ">" => i > value,
                ">=" => i >= value,
                _ => false,
            };
            (match ? matched : unmatched).Add(i);
        }

        // Metadata may lag behind the actual table. An explicitly written boundary
        // remains a valid case even when it lies outside the inferred domain.
        if (value < 0 || value >= numValues)
            (op is "==" or "<=" or ">=" ? matched : unmatched).Add(value);

        return (matched, unmatched);
    }

    private bool TryParseIndexedAccess(out IndexedAccess access)
    {
        access = default!;
        var start = _position;
        string first;
        try
        {
            first = ParseIdentifier();
        }
        catch (InvalidDataException)
        {
            _position = start;
            return false;
        }

        var firstSecondaryName = string.Empty;
        if (Check(EvflTokenKind.At)
            && Peek(1).Kind is EvflTokenKind.Identifier or EvflTokenKind.QuotedIdentifier
            && Peek(2).Kind == EvflTokenKind.Dot)
        {
            Match(EvflTokenKind.At);
            firstSecondaryName = ParseIdentifier();
        }

        if (!Match(EvflTokenKind.Dot))
        {
            if (!Check(EvflTokenKind.At) && !Check(EvflTokenKind.LeftSquare))
            {
                _position = start;
                return false;
            }

            var secondaryName = string.Empty;
            string? variant = null;
            if (Match(EvflTokenKind.At))
            {
                if (Match(EvflTokenKind.Integer, out var integer))
                {
                    variant = integer!.Text;
                }
                else
                {
                    secondaryName = ParseIdentifier();
                    if (Match(EvflTokenKind.At))
                    {
                        if (Match(EvflTokenKind.Integer, out var secondInteger))
                        {
                            variant = secondInteger!.Text;
                        }
                        else
                        {
                            variant = ParseIdentifier();
                        }
                    }
                    else
                    {
                        variant = secondaryName;
                        secondaryName = string.Empty;
                    }
                }
            }
            if (!Check(EvflTokenKind.LeftSquare))
            {
                _position = start;
                return false;
            }
            Expect(EvflTokenKind.LeftSquare);
            var bareKey = ParseValue();
            Expect(EvflTokenKind.RightSquare);
            access = new IndexedAccess(new ActorName("EventFlowSystemActor", secondaryName), false, first, bareKey, variant);
            return true;
        }

        var collection = ParseIdentifier();
        if (!Match(EvflTokenKind.LeftSquare))
        {
            _position = start;
            return false;
        }

        var key = ParseValue();
        Expect(EvflTokenKind.RightSquare);
        var actor = first == "System"
            ? new ActorName("EventFlowSystemActor", firstSecondaryName)
            : new ActorName(first, firstSecondaryName);
        var systemPrefix = first == "System";
        access = new IndexedAccess(actor, systemPrefix, collection, key, null);
        return true;
    }

    private QuerySignature ResolveIndexedQuery(IndexedAccess access, bool predicate)
    {
        IEnumerable<KeyValuePair<string, QueryPrototype>> candidates = predicate
            ? _functions.Queries.OrderByDescending(pair => pair.Value.ParseType == "predicate")
            : _functions.Queries;

        foreach ((var name, var prototype) in candidates)
        {
            if (!MatchesIndexedRule(prototype.ParseRule, access))
            {
                continue;
            }
            if (!predicate && prototype.ParseType != "function")
            {
                continue;
            }
            if (predicate && prototype.ParseType is not ("predicate" or "function"))
            {
                continue;
            }
            var rule = prototype.ParseRule!;
            if (prototype.ParseType == "predicate" && !rule.Contains("CMP(", StringComparison.Ordinal))
            {
                continue;
            }

            QuerySignature query = new(
                access.Actor,
                Prefix(name, "EventFlowQuery"),
                prototype.Parameters,
                prototype.Varargs,
                prototype.ReturnType,
                prototype.Inverted,
                prototype.Conversion,
                prototype.NegatedConversion);
            GetActor(access.Actor).Queries[query.Name] = query;
            return query;
        }

        throw Error($"No indexed query rule found for {access.Collection}.");
    }

    private ActionSignature ResolveIndexedAction(IndexedAccess access, bool plusAssign, CompileValue value)
    {
        foreach ((var name, var prototype) in _functions.Actions)
        {
            if (!MatchesIndexedRule(prototype.ParseRule, access))
            {
                continue;
            }
            var rule = prototype.ParseRule!;
            if (plusAssign != rule.Contains("PASSIGN()", StringComparison.Ordinal))
            {
                continue;
            }
            if (!plusAssign)
            {
                if (value.Value is true && !rule.Contains("ID(\"true\")", StringComparison.Ordinal))
                {
                    continue;
                }
                if (value.Value is false && !rule.Contains("ID(\"false\")", StringComparison.Ordinal))
                {
                    continue;
                }
                if (value.Value is not bool && !prototype.Parameters.Any(parameter => parameter.Name != IndexedKeyParameter(rule)))
                {
                    continue;
                }
            }

            ActionSignature action = new(
                access.Actor,
                Prefix(name, "EventFlowAction"),
                prototype.Parameters,
                prototype.Varargs,
                prototype.Conversion);
            GetActor(access.Actor).Actions[action.Name] = action;
            return action;
        }

        throw Error($"No indexed action rule found for {access.Collection}.");
    }

    private Dictionary<string, CompileValue> BuildIndexedQueryParameters(QuerySignature query, IndexedAccess access, string? op, CompileValue? comparisonValue)
    {
        var provided = IndexedQueryArguments(query, access);
        return op is null
            ? PrepareParameters(query.Parameters, provided, query.Name)
            : PreparePredicateRule(query, provided, op, comparisonValue!).Parameters;
    }

    private IReadOnlyList<(string? Name, CompileValue Value)> IndexedQueryArguments(QuerySignature query, IndexedAccess access) =>
        [(IndexedKeyParameter(GetQueryPrototype(query)!.ParseRule!), access.Key)];

    private static string IndexedKeyParameter(string rule) =>
        Regex.Match(rule, @"LSQUARE\(\)\s*VALUE\(([^=]+)=").Groups[1].Value.Trim();

    private static bool MatchesIndexedRule(string? rule, IndexedAccess access) =>
        !string.IsNullOrWhiteSpace(rule)
        && rule.Contains("LSQUARE()", StringComparison.Ordinal)
        && rule.Contains($"ID(\"{access.Collection}\")", StringComparison.Ordinal)
        && rule.Contains("ID(\"System\")", StringComparison.Ordinal) == access.SystemPrefix
        && (access.Variant is not null
            ? rule.Contains($"INT(\"{access.Variant}\")", StringComparison.Ordinal)
            : !rule.Contains("AT()", StringComparison.Ordinal));

    private QueryPrototype? GetQueryPrototype(QuerySignature query)
    {
        const string prefix = "EventFlowQuery";
        var name = query.Name.StartsWith(prefix, StringComparison.Ordinal) ? query.Name[prefix.Length..] : query.Name;
        return _functions.Queries.GetValueOrDefault(name);
    }

    private bool IsPredicateRule(QuerySignature query) => GetQueryPrototype(query)?.ParseType == "predicate";

    private (Dictionary<string, CompileValue> Parameters, bool Negated) PreparePredicateRule(
        QuerySignature query,
        IReadOnlyList<(string? Name, CompileValue Value)> provided,
        string op,
        CompileValue comparisonValue)
    {
        var parameters = PreparePredicateParameters(query, provided, op, comparisonValue);
        if (TryApplyPredicateConversion(query, parameters, op, negated: false))
        {
            return (parameters, false);
        }

        if (TryApplyPredicateConversion(query, parameters, op, negated: true))
        {
            return (parameters, true);
        }

        var operators = PredicateRuleOperators(GetQueryPrototype(query)?.ParseRule);
        var index = Array.IndexOf(operators, op);
        if (index < 0) throw Error($"{query.Name}: unsupported comparison operator '{op}'.");
        return (parameters, GetQueryPrototype(query)?.ParseRule?.Contains("CMP(.negated=", StringComparison.Ordinal) == true && index == 1);
    }

    private bool TryApplyPredicateConversion(
        QuerySignature query,
        IDictionary<string, CompileValue> parameters,
        string op,
        bool negated)
    {
        var conversionObject = negated ? query.NegatedConversion : query.Conversion;
        if (conversionObject is string conversion)
        {
            return ExtractComparisonOperator(conversion) == op;
        }
        if (conversionObject is not ConversionTable table)
        {
            return false;
        }

        for (var index = 0; index < table.Values.Count; index++)
        {
            if (ExtractComparisonOperator(table.Values[index]) != op)
            {
                continue;
            }

            var parameter = query.Parameters.FirstOrDefault(p => p.Name == table.Key);
            parameters[table.Key] = parameter is null
                ? new CompileValue(FlowType.Int, index)
                : CoerceParameter(query.Name, parameter, new CompileValue(FlowType.Int, index));
            return true;
        }

        return false;
    }

    private static string[] PredicateRuleOperators(string? parseRule)
    {
        var match = Regex.Match(parseRule ?? string.Empty, @"CMP\([^=\r\n]+=[^\r\n]*?\[(.*?)\]\.index");
        return match.Success
            ? match.Groups[1].Value.Split(',').Select(value => value.Trim().Trim('\'', '"')).ToArray()
            : [];
    }

    private static string ExtractComparisonOperator(string conversion)
    {
        var withoutPlaceholders = Regex.Replace(conversion, @"<[A-Za-z0-9_.]+>", string.Empty);
        var match = Regex.Match(withoutPlaceholders, @"(?:<=|>=|==|!=|<|>)");
        return match.Success ? match.Value : string.Empty;
    }

    // Legacy EVFL predicates enumerate a finite domain when metadata has no bounds.
    // Exact switches bypass this policy and retain only explicitly stored case values.
    private static int QueryDomainSize(QuerySignature query) => query.ReturnType.NumValues == FlowType.UnboundedValueCount
        ? 50
        : query.ReturnType.NumValues;

    private static PredicateTable BoolPredicateTable(QuerySignature query, IReadOnlyDictionary<string, CompileValue> parameters, int numValues, bool negated = false)
    {
        HashSet<int> falseValues = [0];
        var trueValues = Enumerable.Range(1, Math.Max(0, numValues - 1)).ToHashSet();
        return query.Inverted ^ negated
            ? PredicateTable.FromSets(query, parameters, falseValues, trueValues)
            : PredicateTable.FromSets(query, parameters, trueValues, falseValues);
    }

    private Dictionary<string, CompileValue> BuildIndexedActionParameters(ActionSignature action, IndexedAccess access, CompileValue value)
    {
        var rule = _functions.Actions[action.Name["EventFlowAction".Length..]].ParseRule!;
        var key = IndexedKeyParameter(rule);
        var parameters = PrepareParameters(action.Parameters,
            Regex.Matches(rule, @"VALUE\(([^=]+)=")
                .Select(match => ((string?)match.Groups[1].Value.Trim(), match.Groups[1].Value.Trim() == key ? access.Key : value))
                .ToList(), action.Name);
        // The rule binds source values; the signature retains the established binary field order.
        return action.Parameters.ToDictionary(parameter => parameter.Name, parameter => parameters[parameter.Name]);
    }

    private FunctionCall ParseFunctionCall()
    {
        var actorName = ParseIdentifier();
        var secondaryName = string.Empty;
        var argumentName = string.Empty;
        if (Match(EvflTokenKind.At))
        {
            secondaryName = ParseIdentifier();
        }
        if (Match(EvflTokenKind.LeftParen))
        {
            argumentName = ParseIdentifier();
            Expect(EvflTokenKind.RightParen);
        }
        Expect(EvflTokenKind.Dot);
        var functionName = ParseIdentifier();
        Expect(EvflTokenKind.LeftParen);
        var parameters = ParseCallParameters();
        Expect(EvflTokenKind.RightParen);
        return new FunctionCall(new ActorName(actorName, secondaryName, argumentName), functionName, parameters);
    }

    private List<(string? Name, CompileValue Value)> ParseCallParameters()
    {
        List<(string? Name, CompileValue Value)> parameters = [];
        if (Check(EvflTokenKind.RightParen))
        {
            return parameters;
        }

        do
        {
            string? name = null;
            if ((Check(EvflTokenKind.Identifier) || Check(EvflTokenKind.QuotedIdentifier)) && Peek(1).Kind == EvflTokenKind.Equals)
            {
                name = ParseIdentifier();
                Expect(EvflTokenKind.Equals);
            }
            parameters.Add((name, ParseValue()));
        } while (Match(EvflTokenKind.Comma) && !Check(EvflTokenKind.RightParen));

        return parameters;
    }

    private Dictionary<string, CompileValue> ParseNamedParameters()
    {
        Dictionary<string, CompileValue> parameters = [];
        foreach ((var name, var value) in ParseCallParameters())
        {
            if (name is null) throw Error("Expected named parameter.");
            if (!parameters.TryAdd(name, value)) throw Error($"Duplicate argument '{name}'.");
        }
        return parameters;
    }

    private List<int> ParseIntList(FlowType type)
    {
        if (Match(EvflTokenKind.LeftParen))
        {
            var result = ParseIntList(type);
            Expect(EvflTokenKind.RightParen);
            return result;
        }

        List<int> values = [];
        do
        {
            values.Add(ParseCaseValue(type));
        } while (Match(EvflTokenKind.Comma) && !Check(EvflTokenKind.Colon) && !Check(EvflTokenKind.RightParen));
        return values;
    }

    private int ParseCaseValue(FlowType type)
    {
        if (Match(EvflTokenKind.Integer, out var integer))
        {
            var value = long.Parse(integer!.Text, CultureInfo.InvariantCulture);
            if (value > int.MaxValue && value <= uint.MaxValue)
            {
                return unchecked((int)(uint)value);
            }

            return checked((int)value);
        }

        var enumValue = ParseIdentifier();
        if (!type.Name.StartsWith("enum", StringComparison.Ordinal))
        {
            throw Error($"Query does not return an enum value.");
        }
        var values = type.Name[5..^1].Split(',').Select(value => value.Trim()).ToArray();
        var index = Array.IndexOf(values, enumValue);
        if (index < 0)
        {
            throw Error($"'{enumValue}' is not a valid enum value.");
        }
        return index;
    }

    private CompileValue ParseValue()
    {
        if (Match(EvflTokenKind.Integer, out var integer))
        {
            return new CompileValue(FlowType.Int, int.Parse(integer!.Text, CultureInfo.InvariantCulture));
        }
        if (Match(EvflTokenKind.Float, out var floating))
        {
            return new CompileValue(FlowType.Float, float.Parse(floating!.Text, CultureInfo.InvariantCulture));
        }
        if (Match(EvflTokenKind.String, out var text))
        {
            return new CompileValue(FlowType.String, EvflStringLiteral.Decode(text!));
        }
        if (MatchKeyword("true"))
        {
            return new CompileValue(FlowType.Bool, true);
        }
        if (MatchKeyword("false"))
        {
            return new CompileValue(FlowType.Bool, false);
        }

        var name = ParseIdentifier();
        if (Match(EvflTokenKind.At))
        {
            return new CompileValue(FlowType.Actor, new FlowValue.ActorMarker(name, ParseIdentifier()));
        }
        return new CompileValue(FlowType.Any, new FlowValue.Argument(name));
    }

    private CompileValue ParseBaseValue()
    {
        var value = ParseValue();
        if (value.Value is FlowValue.Argument)
        {
            throw Error("Expected literal value.");
        }
        return value;
    }

    private Dictionary<string, CompileValue> PrepareParameters(
        IReadOnlyList<FunctionParameter> signature,
        IReadOnlyList<(string? Name, CompileValue Value)> provided,
        string functionName,
        IReadOnlyList<FunctionParameter>? positionalSignature = null)
    {
        positionalSignature ??= signature;
        if (provided.Count(pair => pair.Name is null) > positionalSignature.Count)
        {
            throw Error($"{functionName}: expected {positionalSignature.Count} positional parameters, got {provided.Count(pair => pair.Name is null)}.");
        }

        Dictionary<string, CompileValue> result = [];
        var positional = 0;
        var sawNamed = false;
        foreach ((var name, var value) in provided)
        {
            FunctionParameter? parameter;
            string parameterName;
            if (name is not null)
            {
                sawNamed = true;
                parameterName = name;
                parameter = signature.FirstOrDefault(candidate => candidate.Name == name);
            }
            else
            {
                if (sawNamed)
                {
                    throw Error($"{functionName}: positional arguments must precede named arguments.");
                }
                parameter = positionalSignature[positional++];
                parameterName = parameter.Name;
            }
            // Unknown named fields are preserved for binaries with newer metadata than functions.csv.
            var bound = parameter is null ? value : CoerceParameter(functionName, parameter, value);
            if (!result.TryAdd(parameterName, bound))
            {
                throw Error($"{functionName}: duplicate argument '{parameterName}'.");
            }
        }
        foreach (var parameter in signature)
        {
            if (!result.ContainsKey(parameter.Name))
            {
                throw Error($"{functionName}: missing argument '{parameter.Name}'.");
            }
        }

        return result;
    }

    private Dictionary<string, CompileValue> PreparePredicateParameters(
        QuerySignature query,
        IReadOnlyList<(string? Name, CompileValue Value)> provided,
        string op,
        CompileValue comparisonValue)
    {
        var prototype = GetQueryPrototype(query);
        if (prototype?.ParseType != "predicate"
            || string.IsNullOrWhiteSpace(prototype.ParseRule))
        {
            return PrepareParameters(query.Parameters, provided, query.Name);
        }

        var valueParameters = Regex.Matches(prototype.ParseRule, @"VALUE\(([^=]+)=")
            .Select(match => match.Groups[1].Value.Trim())
            .ToList();
        var cmpIndex = prototype.ParseRule.IndexOf("CMP(", StringComparison.Ordinal);
        var beforeCompare = valueParameters
            .Where(name => prototype.ParseRule.IndexOf($"VALUE({name}=", StringComparison.Ordinal) < cmpIndex)
            .ToList();
        var afterCompare = valueParameters
            .Where(name => prototype.ParseRule.IndexOf($"VALUE({name}=", StringComparison.Ordinal) > cmpIndex)
            .ToList();

        // Bind source arguments and syntax-derived fields together so neither can
        // silently overwrite the other. Positional arguments follow the rule order.
        List<(string? Name, CompileValue Value)> arguments = [.. provided];
        foreach (var parameterName in afterCompare)
        {
            arguments.Add((parameterName, comparisonValue));
        }

        var cmpMatch = Regex.Match(prototype.ParseRule, @"CMP\(([^=]+)=");
        if (cmpMatch.Success)
        {
            var parameterName = cmpMatch.Groups[1].Value.Trim();
            var parameter = query.Parameters.FirstOrDefault(p => p.Name == parameterName);
            if (parameter is not null)
            {
                var checkType = Array.IndexOf(PredicateRuleOperators(prototype.ParseRule), op);
                if (checkType < 0) throw Error($"{query.Name}: unsupported comparison operator '{op}'.");
                arguments.Add((parameter.Name, new CompileValue(FlowType.Int, checkType)));
            }
        }

        return PrepareParameters(query.Parameters, arguments, query.Name,
            beforeCompare.Select(name => query.Parameters.First(parameter => parameter.Name == name)).ToArray());
    }

    private CompileValue CoerceParameter(string functionName, FunctionParameter parameter, CompileValue value)
    {
        if (value.Value is FlowValue.Argument argument
            && IsRawIdentifierParameter(functionName, parameter))
        {
            return new CompileValue(parameter.Type, argument.Name);
        }

        if (parameter.Name.StartsWith("EntryVariableKey", StringComparison.Ordinal) && value.Value is FlowValue.Argument)
        {
            return new CompileValue(parameter.Type, ((FlowValue.Argument)value.Value).Name);
        }

        if (parameter.Type.Name.StartsWith("enum[", StringComparison.Ordinal) && value.Type == FlowType.String)
        {
            var fields = parameter.Type.Name[5..^1].Split(',').Select(field => field.Trim()).ToArray();
            var index = Array.IndexOf(fields, (string)value.Value);
            if (index < 0)
            {
                throw new InvalidDataException($"{functionName}: '{value.Value}' is not a valid key for {parameter.Type}.");
            }
            return new CompileValue(FlowType.Int, index);
        }

        if (value.Value is FlowValue.Argument)
        {
            // Resolve identifiers after declarations are known, before considering
            // enum keys or implicit actor names.
            return value with { Type = parameter.Type };
        }

        CompileArgumentBinder.CheckType(value.Type, parameter.Type, $"{functionName} parameter {parameter.Name}");
        return value;
    }

    private bool IsRawIdentifierParameter(string functionName, FunctionParameter parameter)
    {
        const string prefix = "EventFlowAction";
        if (!functionName.StartsWith(prefix, StringComparison.Ordinal)
            || !_functions.Actions.TryGetValue(functionName[prefix.Length..], out var prototype)
            || string.IsNullOrWhiteSpace(prototype.ParseRule)
            || string.IsNullOrWhiteSpace(prototype.Conversion))
        {
            return false;
        }

        return prototype.ParseRule.Contains($"ID({parameter.Name})", StringComparison.Ordinal)
            && prototype.Conversion.Contains($"<<{parameter.Name}>>", StringComparison.Ordinal);
    }

    private ActionSignature ResolveAction(ActorName actorName, string name)
    {
        var actor = GetActor(actorName);
        var prefixedName = Prefix(name, "EventFlowAction");
        if (!actor.Actions.TryGetValue(prefixedName, out var action))
        {
            var resolved = _functions.ResolveAction(actorName, prefixedName["EventFlowAction".Length..]);
            action = resolved with { Name = prefixedName };
            actor.Actions[prefixedName] = action;
        }
        return action;
    }

    private QuerySignature ResolveQuery(ActorName actorName, string name)
    {
        var actor = GetActor(actorName);
        var prefixedName = Prefix(name, "EventFlowQuery");
        if (!actor.Queries.TryGetValue(prefixedName, out var query))
        {
            var resolved = _functions.ResolveQuery(actorName, prefixedName["EventFlowQuery".Length..]);
            query = resolved with { Name = prefixedName };
            actor.Queries[prefixedName] = query;
        }
        return query;
    }

    private CompileActor GetActor(ActorName actorName)
    {
        if (_actors.TryGetValue(actorName, out var actor))
        {
            return actor;
        }

        actor = new CompileActor(actorName);
        actor.Actions["EventFlowActionExitFlowchart"] = new ActionSignature(actorName, "EventFlowActionExitFlowchart", [], false, null);
        _actors[actorName] = actor;
        return actor;
    }

    private void MarkActionUsed(ActionSignature action) => GetActor(action.ActorName).UsedActions.Add(action.Name);

    private void MarkQueryUsed(QuerySignature query) => GetActor(query.ActorName).UsedQueries.Add(query.Name);

    private static string Prefix(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.Ordinal) ? name : prefix + name;

    private void VerifyVariables(string rootName, CompileNode root, IReadOnlyList<VariableDefinition> variables)
    {
        var known = variables.ToDictionary(variable => variable.Name, variable => variable.Type, StringComparer.Ordinal);
        foreach (var node in FindPostorder(root))
        {
            var values = node switch
            {
                CompileActionNode action => action.Parameters,
                CompileSwitchNode switchNode => switchNode.Parameters,
                CompileSubflowNode subflow => subflow.Parameters,
                _ => null,
            };
            if (values is null)
            {
                continue;
            }
            IReadOnlyList<VariableDefinition>? signature = null;
            if (node is CompileSubflowNode { FlowchartName.Length: 0 } call)
            {
                _flowVariables.TryGetValue(call.CalledRootName, out signature);
            }
            foreach ((var parameterName, var value) in values.ToList())
            {
                var expected = signature?.FirstOrDefault(variable => variable.Name == parameterName)?.Type ?? value.Type;
                values[parameterName] = CompileArgumentBinder.Bind(value, expected, known,
                    allowImplicitActor: node is CompileSubflowNode { FlowchartName.Length: > 0 },
                    context: $"flow {rootName}, parameter '{parameterName}'");
            }
        }
    }

    internal static List<CompileNode> FindPostorder(CompileNode root) =>
        PostorderTraversal.Find(root, node => node.OutEdges, node => node);

    private static void CollapseConnectors(IEnumerable<CompileRootNode> roots)
    {
        foreach (var root in roots)
        {
            Dictionary<CompileNode, CompileNode?> remap = [];
            foreach (var node in FindPostorder(root))
            {
                foreach (var outEdge in node.OutEdges.ToList())
                {
                    if (!remap.TryGetValue(outEdge, out var replacement))
                    {
                        continue;
                    }
                    if (replacement is null)
                    {
                        node.RemoveOutEdge(outEdge);
                    }
                    else
                    {
                        node.RerouteOutEdge(outEdge, replacement);
                    }
                }

                if (node is CompileConnectorNode)
                {
                    remap[node] = node.OutEdges.Count == 1 ? node.OutEdges[0] : null;
                }
            }
        }
    }
    private static void ReplaceNode(CompileNode root, CompileNode replace, CompileNode? replacement)
    {
        foreach (var node in FindPostorder(root))
        {
            if (!node.OutEdges.Contains(replace))
                continue;
            
            if (replacement is null)
            {
                if (node is CompileSwitchNode switchNode && ReferenceEquals(replace, CompileTerminalNode.Instance))
                {
                    if (switchNode.PreserveTerminalCases) continue;
                    // A local flow can later acquire a caller continuation. Keep the
                    // removed return branches so inlining can reconnect those paths.
                    if (switchNode.Cases.TryGetValue(replace, out var values))
                        switchNode.RemovedTerminalCases.AddRange(values);
                }
                node.RemoveOutEdge(replace);
            }
            else
            {
                node.RerouteOutEdge(replace, replacement);
            }
        }
    }

    private string NewEventName()
    {
        string name;
        do
        {
            name = $"Event{_nextId++}";
        } while (!_usedEventNames.Add(name));

        return name;
    }

    private void ReserveEventName(string name)
    {
        _usedEventNames.Add(name);
        var eventName = EventRegex().Match(name);
        if (eventName.Success && int.TryParse(eventName.Groups[1].Value, out var id) && id >= _nextId)
            _nextId = id + 1;
        
    }

    private void ReserveEntryPointNames()
    {
        for (var i = 0; i + 1 < _tokens.Count; i++)
        {
            if (_tokens[i].Kind == EvflTokenKind.Identifier
                && _tokens[i].Text == "entrypoint"
                && _tokens[i + 1].Kind is EvflTokenKind.Identifier or EvflTokenKind.QuotedIdentifier)
                ReserveEventName(EvflFormatter.Identifier(_tokens[i + 1].Text));
            
        }
    }

    private CompileConnectorNode NewConnector() => new($"Connector{_nextId++}");

    private void ConsumeDedentsBeforeSwitchCase(int caseColumn)
    {
        if (caseColumn < 0 || !Check(EvflTokenKind.Dedent))
        {
            return;
        }

        var offset = 0;
        while (Peek(offset).Kind == EvflTokenKind.Dedent)
        {
            offset++;
        }

        var next = Peek(offset);
        if (next.Column != caseColumn
            || next.Kind != EvflTokenKind.Identifier
            || next.Text is not ("case" or "default"))
        {
            return;
        }

        while (Check(EvflTokenKind.Dedent))
            Expect(EvflTokenKind.Dedent);
    }

    private string ParseIdentifier()
    {
        if (Match(EvflTokenKind.Identifier, out var id))
            return id!.Text;
        
        if (Match(EvflTokenKind.QuotedIdentifier, out var quoted))
            return ParseQuotedIdentifier(quoted!.Text);
        
        throw Error("Expected identifier.");
    }

    private EvflToken Expect(EvflTokenKind kind)
    {
        if (Match(kind, out var token))
            return token!;
        
        throw Error($"Expected {kind}.");
    }

    private void ExpectKeyword(string keyword)
    {
        if (!MatchKeyword(keyword))
            throw Error($"Expected '{keyword}'.");
    }

    private bool Match(EvflTokenKind kind) => Match(kind, out _);

    private bool Match(EvflTokenKind kind, out EvflToken? token)
    {
        if (!Check(kind))
        {
            token = null;
            return false;
        }
        token = _tokens[_position++];
        return true;
    }

    private bool MatchKeyword(string keyword)
    {
        if (!CheckKeyword(keyword))
            return false;
        
        _position++;
        return true;
    }

    private bool Check(EvflTokenKind kind) => Peek().Kind == kind;

    private bool CheckKeyword(string keyword) =>
        Peek().Kind == EvflTokenKind.Identifier && string.Equals(Peek().Text, keyword, StringComparison.Ordinal);

    private EvflToken Peek(int offset = 0) => _tokens[Math.Min(_position + offset, _tokens.Count - 1)];

    private InvalidDataException Error(string message) =>
        new($"{message} At line {Peek().Line}, column {Peek().Column}.");

    private static string ParseQuotedIdentifier(string text)
    {
        var inner = text[1..^1];
        return inner.Replace("\\`", "`", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private sealed record FunctionCall(ActorName Actor, string Name, IReadOnlyList<(string? Name, CompileValue Value)> Parameters);
    private sealed record IndexedAccess(ActorName Actor, bool SystemPrefix, string Collection, CompileValue Key, string? Variant);
    private sealed record Block(IReadOnlyList<CompileRootNode> EntryPoints, CompileNode Entry, CompileNode Exit);

    private abstract class PredicatePlan;

    private sealed class ConstantPredicatePlan(bool value) : PredicatePlan
    {
        public bool Value { get; } = value;
    }

    private sealed class PredicateTable(
        QuerySignature query,
        IReadOnlyDictionary<string, CompileValue> parameters,
        IEnumerable<(IReadOnlySet<int> Values, bool Result)> branches) : PredicatePlan
    {
        public QuerySignature Query { get; } = query;
        public IReadOnlyDictionary<string, CompileValue> Parameters { get; } = parameters;
        public List<PredicateBranch> Branches { get; } = [.. branches.Select(branch => new PredicateBranch(
                branch.Values,
                new ConstantPredicatePlan(branch.Result)))];

        public static PredicateTable FromSets(
            QuerySignature query,
            IReadOnlyDictionary<string, CompileValue> parameters,
            IReadOnlySet<int> trueValues,
            IReadOnlySet<int> falseValues) =>
            new(query, parameters, [(trueValues, true), (falseValues, false)]);

        public void ReplaceResult(bool oldValue, PredicatePlan replacement)
            => ReplaceResults(result => result.Value == oldValue ? replacement : result);

        public void InvertResults()
            => ReplaceResults(result => new ConstantPredicatePlan(!result.Value));

        private void ReplaceResults(Func<ConstantPredicatePlan, PredicatePlan> replace)
        {
            Stack<PredicateTable> pending = new([this]);
            HashSet<PredicateTable> visited = [];
            while (pending.TryPop(out var table))
            {
                if (!visited.Add(table)) continue;
                foreach (var branch in table.Branches)
                {
                    // Traverse only the existing expression; the inserted continuation
                    // must not have its own results replaced by this operation.
                    if (branch.Action is PredicateTable nested) pending.Push(nested);
                    else if (branch.Action is ConstantPredicatePlan result) branch.Action = replace(result);
                }
            }
        }
    }

    private sealed class PredicateBranch(IReadOnlySet<int> values, PredicatePlan action)
    {
        public IReadOnlySet<int> Values { get; } = values;
        public PredicatePlan Action { get; set; } = action;
    }

    [GeneratedRegex(@"^Event(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex EventRegex();
}
