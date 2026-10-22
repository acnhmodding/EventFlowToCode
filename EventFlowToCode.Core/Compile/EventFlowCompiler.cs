using BfevLibrary;
using EventFlowToCode.Core.Bfev;
using EventFlowToCode.Core.Model;

namespace EventFlowToCode.Core.Compile;

public sealed class EventFlowCompiler
{
    public static BfevFile Compile(string source, string name, FunctionCatalog? functions = null)
    {
        if (ExplicitFlowText.TryDeserialize(source, out var explicitFile))
        {
            BfevGraphValidator.Validate(explicitFile);
            return explicitFile;
        }

        var document = ParseDocument(source, functions);
        var file = BfevCompilerEmitter.Emit(name, document);
        BfevGraphValidator.Validate(file);
        return file;
    }

    /// <summary>Validates editable EVFL source without emitting a BFEV file.</summary>
    public static void Validate(string source, FunctionCatalog? functions = null)
    {
        _ = Compile(source, "Validation", functions);
    }

    public static void CompileFile(string inputPath, string outputPath, FunctionCatalog? functions = null)
    {
        var source = File.ReadAllText(inputPath);
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var file = Compile(source, name, functions);
        BfevFileWriter.Write(file, outputPath);
    }

    private static CompileDocument ParseDocument(string source, FunctionCatalog? functions)
    {
        functions ??= new FunctionCatalog();
        var tokens = EvflTokenizer.Tokenize(source);
        return new EvflParser(tokens, functions).Parse();
    }
}