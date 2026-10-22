using System.Globalization;

namespace EventFlowToCode.Core.Model;

public sealed record FlowType(string Name)
{
    internal const int UnboundedValueCount = 999_999_999;

    public int NumValues =>
        Name switch
        {
            "bool" => 2,
            _ when Name.StartsWith("int", StringComparison.Ordinal) && Name != "int" =>
                int.Parse(Name[3..], CultureInfo.InvariantCulture),
            _ when Name.StartsWith("enum", StringComparison.Ordinal) =>
                Name[5..^1].Split(',').Length,
            _ => UnboundedValueCount,
        };

    public override string ToString() => Name;

    public static readonly FlowType Any = new("any");
    public static readonly FlowType Bool = new("bool");
    public static readonly FlowType Float = new("float");
    public static readonly FlowType Int = new("int");
    public static readonly FlowType String = new("str");
    public static readonly FlowType Actor = new("actor");
    internal static readonly FlowType Placeholder = new("_placeholder");
}