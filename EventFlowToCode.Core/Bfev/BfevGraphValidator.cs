using BfevLibrary;
using BfevLibrary.Core;
using System.Text;

namespace EventFlowToCode.Core.Bfev;

internal static class BfevGraphValidator
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static Flowchart RequireSingleFlowchart(BfevFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Flowcharts is null || file.Timelines is null)
        {
            throw new InvalidDataException("BFEV flowchart and timeline collections are required.");
        }
        if (file.Flowcharts.Count != 1 || file.Timelines.Count != 0)
        {
            throw new NotSupportedException("This tool supports exactly one flowchart and no timelines; additional blocks cannot be discarded or written safely.");
        }
        return file.Flowcharts.Single().Value;
    }

    public static void Validate(BfevFile file)
    {
        var flow = RequireSingleFlowchart(file);
        if (flow is null || flow.Events is null || flow.Actors is null || flow.EntryPoints is null
            || flow.Events.Any(node => node is null) || flow.EntryPoints.Values.Any(entry => entry is null)
            || flow.Actors.Any(actor => actor is null || actor.Actions is null || actor.Queries is null))
            throw new InvalidDataException("Flowchart collections and their records cannot be null.");
        if (file.Version != "0.3.0.0")
        {
            throw new NotSupportedException($"Unsupported BFEV version '{file.Version}'; this adapter supports 0.3.0.0.");
        }
        Text(file.FileName, "file name");
        Text(flow.Name, "flowchart name");
        Limit(flow.Events.Count, short.MaxValue + 1, "events (signed BfevLibrary indices)");
        Limit(flow.Actors.Count, short.MaxValue + 1, "actors (signed BfevLibrary indices)");
        Limit(flow.EntryPoints.Count, short.MaxValue + 1, "entrypoints (signed BfevLibrary indices)");
        Limit(flow.Actors.Sum(actor => (long)actor.Actions.Count), ushort.MaxValue, "total actions");
        Limit(flow.Actors.Sum(actor => (long)actor.Queries.Count), ushort.MaxValue, "total queries");
        foreach (var actor in flow.Actors)
        {
            Text(actor.Name, "actor name");
            Text(actor.SecondaryName, "actor secondary name");
            Text(actor.ArgumentName, "actor argument name");
            Index(actor.EntryPointIndex, flow.EntryPoints.Count, "actor entrypoint", terminal: true);
            Limit(actor.Actions.Count, short.MaxValue + 1, "actor actions");
            Limit(actor.Queries.Count, short.MaxValue + 1, "actor queries");
            foreach (var name in actor.Actions.Concat(actor.Queries)) Text(name, "function name");
            Container(actor.Parameters, "actor parameters");
        }
        foreach (var pair in flow.EntryPoints)
        {
            Text(pair.Key, "entrypoint name");
            Index(pair.Value.EventIndex, flow.Events.Count, $"entrypoint {pair.Key}", terminal: true);
            if (pair.Value.SubFlowEventIndices is { } subflows)
            {
                Limit(subflows.Count, ushort.MaxValue, "entrypoint subflows");
                foreach (var index in subflows)
                {
                    Index(index, flow.Events.Count, "entrypoint subflow");
                    if (flow.Events[index] is not SubflowEvent) throw new InvalidDataException("Entrypoint subflow indices must reference subflow events.");
                }
            }
            if (pair.Value.Variables is { } variables)
            {
                Limit(variables.Count, ushort.MaxValue, "variable definitions");
                foreach (var variable in variables)
                {
                    Text(variable.Key, "variable name");
                    Variable(variable.Value);
                }
            }
        }
        foreach (var node in flow.Events)
        {
            Text(node.Name, "event name");
            switch (node)
            {
                case ActionEvent action:
                    Index(action.NextEventIndex, flow.Events.Count, "action successor", terminal: true);
                    Index(action.ActorIndex, flow.Actors.Count, "action actor");
                    Index(action.ActorActionIndex, flow.Actors[action.ActorIndex].Actions.Count, "actor action");
                    Container(action.Parameters, "action parameters");
                    break;
                case SwitchEvent query:
                    if (query.SwitchCases is null) throw new InvalidDataException("Switch cases cannot be null.");
                    Index(query.ActorIndex, flow.Actors.Count, "query actor");
                    Index(query.ActorQueryIndex, flow.Actors[query.ActorIndex].Queries.Count, "actor query");
                    Limit(query.SwitchCases.Count, ushort.MaxValue, "switch cases");
                    foreach (var branch in query.SwitchCases)
                        Index(branch.EventIndex == ushort.MaxValue ? -1 : branch.EventIndex, flow.Events.Count, "switch target", terminal: true);
                    Container(query.Parameters, "query parameters");
                    break;
                case ForkEvent fork:
                    if (fork.ForkEventIndicies is null) throw new InvalidDataException("Fork branches cannot be null.");
                    Index(fork.JoinEventIndex, flow.Events.Count, "fork join");
                    if (flow.Events[fork.JoinEventIndex] is not JoinEvent) throw new InvalidDataException("Fork join indices must reference join events.");
                    Limit(fork.ForkEventIndicies.Count, ushort.MaxValue, "fork branches");
                    foreach (var index in fork.ForkEventIndicies) Index(index, flow.Events.Count, "fork branch");
                    break;
                case JoinEvent join:
                    Index(join.NextEventIndex, flow.Events.Count, "join successor", terminal: true);
                    break;
                case SubflowEvent call:
                    Index(call.NextEventIndex, flow.Events.Count, "subflow successor", terminal: true);
                    Text(call.FlowchartName, "subflow flowchart");
                    Text(call.EntryPointName, "subflow entrypoint");
                    if (call.FlowchartName.Length == 0 && !flow.EntryPoints.ContainsKey(call.EntryPointName))
                        throw new InvalidDataException($"Missing internal entrypoint '{call.EntryPointName}'.");
                    Container(call.Parameters, "subflow parameters");
                    break;
                default:
                    throw new NotSupportedException($"Unsupported event type {node.GetType().Name}.");
            }
        }
        ValidateSwitchCases(file);
    }

    private static void Index(int value, int count, string context, bool terminal = false)
    {
        if (terminal && value == -1) return;
        if (value < 0 || value >= count) throw new InvalidDataException($"Invalid {context} index {value}; valid indices are 0..{count - 1}.");
    }

    private static void Limit(long count, int maximum, string context)
    {
        if (count > maximum) throw new InvalidDataException($"Too many {context}: {count}; maximum is {maximum}.");
    }

    private static void Text(string? value, string context)
    {
        if (value is null) throw new InvalidDataException($"Missing {context}.");
        try { Limit(Utf8.GetByteCount(value), ushort.MaxValue, $"UTF-8 bytes in {context}"); }
        catch (EncoderFallbackException ex) { throw new InvalidDataException($"Invalid Unicode in {context}.", ex); }
    }

    private static void Variable(VariableDef value)
    {
        if (value is null) throw new InvalidDataException("Variable definitions cannot be null.");
        var kinds = (value.Int.HasValue ? 1 : 0) + (value.Bool.HasValue ? 1 : 0) + (value.Float.HasValue ? 1 : 0)
            + (value.IntArray is null ? 0 : 1) + (value.BoolArray is null ? 0 : 1) + (value.FloatArray is null ? 0 : 1);
        if (kinds != 1) throw new InvalidDataException("A variable definition must contain exactly one value type.");
        Limit(((Array?)value.IntArray ?? (Array?)value.BoolArray ?? value.FloatArray)?.Length ?? 1, ushort.MaxValue, "variable array elements");
    }

    private static void Container(IEnumerable<KeyValuePair<string, ContainerItem>>? items, string context, int depth = 0)
    {
        if (items is null) return;
        if (depth > 64) throw new NotSupportedException("Container nesting exceeds 64 levels or contains a cycle.");
        var count = 0;
        foreach (var pair in items)
        {
            count++;
            Text(pair.Key, "parameter name");
            var value = pair.Value ?? throw new InvalidDataException("Container items cannot be null.");
            object?[] fields = [value.Argument, value.Items, value.Int, value.Bool, value.Float, value.String, value.WString,
                value.IntArray, value.BoolArray, value.FloatArray, value.StringArray, value.WStringArray, value.ActorIdentifier];
            if (fields.Count(field => field is not null) != 1) throw new InvalidDataException($"{context}/{pair.Key} must contain exactly one value type.");
            foreach (var field in fields)
            {
                if (field is string text) Text(text, "parameter string");
                if (field is Array array) Limit(array.Length, ushort.MaxValue, "parameter array elements");
                if (field is string[] strings) foreach (var item in strings) Text(item, "parameter array string");
                if (field is Tuple<string, string> actor) { Text(actor.Item1, "actor marker"); Text(actor.Item2, "secondary actor marker"); }
            }
            Container(value.Items, context + "/" + pair.Key, depth + 1);
        }
        Limit(count, ushort.MaxValue, "container items");
    }

    public static void ValidateSwitchCases(BfevFile file)
    {
        var events = file.Flowcharts.Values.SelectMany(flowchart => flowchart.Events.OfType<SwitchEvent>().Select(node => (flowchart, node)));
        foreach (var (flowchart, node) in events)
        {
            HashSet<int> values = [];
            foreach (var branch in node.SwitchCases)
                if (!values.Add(branch.Value))
                    throw new InvalidDataException($"{flowchart.Name}/{node.Name}: duplicate switch case value {branch.Value}.");
        }
    }
}