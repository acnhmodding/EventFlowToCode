using EventFlowToCode.CLI;
using EventFlowToCode.Core.Compile;
using EventFlowToCode.Core.Decompile;
using EventFlowToCode.Core.Metadata;
using EventFlowToCode.Core.Model;
using System.CommandLine;

internal class Program
{
    private static int Main(string[] args)
    {
        RootCommand rootCommand = new("EventFlow compiler and decompiler");

        Argument<FileInfo> inputArgument = new("input")
        {
            Description = "Input .bfevfl file.",
        };

        Option<FileInfo?> functionsOption = new("--functions")
        {
            Description = "functions.csv file for action/query signatures.",
            DefaultValueFactory = _ => new FileInfo("functions.csv"),
        };

        Option<FileInfo?> hintsOption = new("--hints")
        {
            Description = "hints.json file for suggestion comments.",
        };
        Option<FileInfo?> outputOption = new("-o", "--output")
        {
            Description = "Output file.",
        };
        RestructureCommandOptions restructureOptions = new();
        Command inspectCommand = new("inspect", "Read a .bfevfl file and print graph counts.")
        {
            Arguments = { inputArgument },
        };
        Command decompileCommand = new("decompile", "Convert a .bfevfl file to EVFL text.")
        {
            Arguments = { inputArgument },
            Options = {
                functionsOption,
                hintsOption,
                outputOption,
            },
        };
        Argument<DirectoryInfo> outputDirectoryArgument = new("output-directory")
        {
            Description = "Directory to receive generated .evfl files.",
        };
        Argument<FileInfo> sourceArgument = new("source")
        {
            Description = "Input .evfl text file.",
        };
        Command compileCommand = new("compile", "Convert EVFL text to a .bfevfl file.")
        {
            Arguments = { sourceArgument },
            Options = { functionsOption, outputOption },
        };
        Command validateCommand = new("validate", "Validate EVFL text without creating a .bfevfl file.")
        {
            Arguments = { sourceArgument },
            Options = { functionsOption },
        };
        Argument<DirectoryInfo> inputDirectoryArgument = new("input-directory")
        {
            Description = "Directory containing .bfevfl files.",
        };
        Command decompileDirectoryCommand = new("decompile-dir", "Convert every .bfevfl file in a directory to EVFL text.")
        {
            Arguments = { inputDirectoryArgument, outputDirectoryArgument },
            Options = {
                functionsOption,
                hintsOption,
            },
        };
        inspectCommand.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArgument)
                ?? throw new InvalidOperationException("Input file is required.");

            var graph = EventFlowDecompiler.ReadGraph(input.FullName);

            Console.WriteLine($"Flowchart: {graph.Name}");
            Console.WriteLine($"Roots:     {graph.Roots.Count}");
            Console.WriteLine($"Actors:    {graph.Actors.Count}");
            Console.WriteLine($"Nodes:     {graph.Nodes.Count}");
        });

        decompileCommand.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArgument)
                ?? throw new InvalidOperationException("Input file is required.");
            var hintsFile = parseResult.GetValue(hintsOption);
            var functions = LoadFunctions(parseResult);
            var hints = HintsLoader.Load(hintsFile?.FullName);

            var source = EventFlowDecompiler.Decompile(input.FullName, functions, hints, restructureOptions.GetValue(parseResult));
            var output = parseResult.GetValue(outputOption);
            if (output is not null)
            {
                File.WriteAllText(output.FullName, source);
                return;
            }

            Console.Write(source);
        });

        decompileDirectoryCommand.SetAction(parseResult =>
        {
            var inputDirectory = parseResult.GetValue(inputDirectoryArgument)
                ?? throw new InvalidOperationException("Input directory is required.");
            var outputDirectory = parseResult.GetValue(outputDirectoryArgument)
                ?? throw new InvalidOperationException("Output directory is required.");
            var functionsFile = parseResult.GetValue(functionsOption)
                ?? throw new InvalidOperationException("functions.csv is required.");

            outputDirectory.Create();
            var functions = FunctionsCsvLoader.Load(functionsFile.FullName);
            var hints = HintsLoader.Load(parseResult.GetValue(hintsOption)?.FullName);
            var inputs = inputDirectory
                .EnumerateFiles("*.bfevfl")
                .OrderBy(file => file.Name, StringComparer.Ordinal)
                .ToArray();
            var options = restructureOptions.GetValue(parseResult);

            Parallel.ForEach(inputs, input =>
            {
                var source = EventFlowDecompiler.Decompile(input.FullName, functions, hints, options);
                var outputPath = Path.Combine(outputDirectory.FullName, $"{Path.GetFileNameWithoutExtension(input.Name)}.evfl");
                File.WriteAllText(outputPath, source);
            });
        });

        compileCommand.SetAction(parseResult =>
        {
            var source = parseResult.GetValue(sourceArgument)
                ?? throw new InvalidOperationException("Source file is required.");
            var output = parseResult.GetValue(outputOption);
            var functions = LoadFunctions(parseResult);

            var outputPath = output?.FullName ?? Path.ChangeExtension(source.FullName, ".bfevfl");
            EventFlowCompiler.CompileFile(source.FullName, outputPath, functions);
        });

        validateCommand.SetAction(parseResult =>
        {
            var source = parseResult.GetValue(sourceArgument)
                ?? throw new InvalidOperationException("Source file is required.");
            var functions = LoadFunctions(parseResult);

            EventFlowCompiler.Validate(File.ReadAllText(source.FullName), functions);
        });

        restructureOptions.AddTo(decompileCommand);
        restructureOptions.AddTo(decompileDirectoryCommand);

        rootCommand.Subcommands.Add(inspectCommand);
        rootCommand.Subcommands.Add(decompileCommand);
        rootCommand.Subcommands.Add(decompileDirectoryCommand);
        rootCommand.Subcommands.Add(compileCommand);
        rootCommand.Subcommands.Add(validateCommand);

        FunctionCatalog LoadFunctions(ParseResult parseResult) =>
            parseResult.GetValue(functionsOption) is { } file ? FunctionsCsvLoader.Load(file.FullName) : new FunctionCatalog();

        return rootCommand.Parse(args).Invoke();
    }
}