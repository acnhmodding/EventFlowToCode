namespace EventFlowToCode.Core.Decompile;

public sealed record RestructureOptions
{
    public bool SwitchToIf { get; init; } = true;
    public bool CollapseAndOr { get; init; } = true;
    public bool CollapseIf { get; init; } = true;
    public bool CollapseCase { get; init; } = true;
    public bool RemoveTrailingReturn { get; init; } = true;
    public bool ExtractReusedBlocks { get; init; } = true;
    public bool ExtractSingleStatement { get; init; } = true;
    public bool RemoveRedundantEntryPoints { get; init; } = true;
    public bool CollapseSubflowOnly { get; init; } = true;
    public bool SimplifyIfElseOrder { get; init; } = true;
    public int SecondaryMaxIterations { get; init; } = 10_000;
}