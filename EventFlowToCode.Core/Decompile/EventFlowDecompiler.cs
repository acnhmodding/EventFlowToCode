using BfevLibrary;
using EventFlowToCode.Core.Bfev;
using EventFlowToCode.Core.Model;

namespace EventFlowToCode.Core.Decompile;

public sealed class EventFlowDecompiler
{
    public static ControlFlowGraph ReadGraph(string path, FunctionCatalog? functions = null) => BfevGraphReader.Read(path, functions);

    public static string Decompile(string path, FunctionCatalog? functions = null,
        IReadOnlyDictionary<string, string>? hints = null, RestructureOptions? restructureOptions = null) =>
        Decompile(BfevFileReader.Read(path), functions, hints, restructureOptions, path);

    /// <summary>Decompiles an already-loaded BFEV file into readable EVFL.</summary>
    public static string Decompile(BfevFile file,
                                   FunctionCatalog? functions = null,
                                   IReadOnlyDictionary<string, string>? hints = null,
                                   RestructureOptions? restructureOptions = null,
                                   string? sourceName = null)
    {
        var graph = BfevGraphReader.Read(file, functions);
        graph.Restructure(restructureOptions);
        graph.FinalPass();
        return new EvflCodeGenerator(hints).Generate(graph);
    }
}
