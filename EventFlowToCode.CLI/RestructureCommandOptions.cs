using EventFlowToCode.Core.Decompile;
using System.CommandLine;

namespace EventFlowToCode.CLI;

internal sealed class RestructureCommandOptions
{
    private readonly Option<bool> _noSwitchToIf = Create("--no-switch-to-if", "Keep switch statements instead of converting two-way switches to if/else.");
    private readonly Option<bool> _noCollapseAndOr = Create("--no-collapse-andor", "Disable the and/or restructuring pass.");
    private readonly Option<bool> _noCollapseIf = Create("--no-collapse-if", "Disable the if/elif restructuring pass.");
    private readonly Option<bool> _noCollapseCase = Create("--no-collapse-case", "Disable the case grouping pass.");
    private readonly Option<bool> _noRemoveTrailingReturn = Create("--no-remove-trailing-return", "Disable trailing return removal.");
    private readonly Option<bool> _noExtractReusedBlocks = Create("--no-extract-reused-blocks", "Disable shared block extraction.");
    private readonly Option<bool> _removeRedundantEntryPoints = Create("--remove-redundant-entrypoints", "Remove redundant entrypoints to make decompiled text shorter.");
    private readonly Option<bool> _noCollapseSubflowOnly = Create("--no-collapse-subflow-only", "Disable collapsing roots that only call a single subflow.");
    private readonly Option<bool> _noSimplifyIfElseOrder = Create("--no-simplify-ifelse-order", "Disable if/else ordering simplification.");

    public void AddTo(Command command)
    {
        foreach (var option in new[]
        {
            _noSwitchToIf, _noCollapseAndOr, _noCollapseIf, _noCollapseCase,
            _noRemoveTrailingReturn, _noExtractReusedBlocks, _removeRedundantEntryPoints,
            _noCollapseSubflowOnly, _noSimplifyIfElseOrder,
        }) command.Options.Add(option);
    }

    public RestructureOptions GetValue(ParseResult parseResult) => new()
    {
        SwitchToIf = !parseResult.GetValue(_noSwitchToIf),
        CollapseAndOr = !parseResult.GetValue(_noCollapseAndOr),
        CollapseIf = !parseResult.GetValue(_noCollapseIf),
        CollapseCase = !parseResult.GetValue(_noCollapseCase),
        RemoveTrailingReturn = !parseResult.GetValue(_noRemoveTrailingReturn),
        ExtractReusedBlocks = !parseResult.GetValue(_noExtractReusedBlocks),
        RemoveRedundantEntryPoints = parseResult.GetValue(_removeRedundantEntryPoints),
        CollapseSubflowOnly = !parseResult.GetValue(_noCollapseSubflowOnly),
        SimplifyIfElseOrder = !parseResult.GetValue(_noSimplifyIfElseOrder),
    };

    private static Option<bool> Create(string name, string description) => new(name) { Description = description };
}
