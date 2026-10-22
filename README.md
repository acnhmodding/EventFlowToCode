# EventFlowToCode

C# compiler and decompiler for readable EventFlow (`.bfevfl`) source.

## Build

Requires the .NET 10 SDK. Default builds restore [AcnhModding.BfevLibrary](https://www.nuget.org/packages/AcnhModding.BfevLibrary) 1.0.0 from NuGet.

From this directory:

```sh
dotnet build EventFlowToCode.slnx -c Release
dotnet run --project EventFlowToCode.CLI -c Release -- --help
```

For development against [our BfevLibrary fork](https://github.com/acnhmodding/BfevLibrary), initialize the pinned submodule and opt into its project reference:

```sh
git submodule update --init --recursive
dotnet build EventFlowToCode.slnx -c Release -p:UseLocalBfevLibrary=true
```

## Library

Install into a .NET 10 project:

```sh
dotnet add package AcnhModding.EventFlowToCode.Core --version 1.0.0
```

```csharp
using EventFlowToCode.Core.Bfev;
using EventFlowToCode.Core.Compile;
using EventFlowToCode.Core.Decompile;

string source = EventFlowDecompiler.Decompile("input.bfevfl");
var compiled = EventFlowCompiler.Compile(source, "input");
BfevFileWriter.Write(compiled, "edited.bfevfl");
```

For game-specific parameter types and query signatures, load a catalog with `FunctionsCsvLoader.Load("functions.csv")` from `EventFlowToCode.Core.Metadata` and pass it to `Compile` and `Decompile`. Game metadata and samples are external inputs and are not included in the package.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE). The BfevLibrary fork is also distributed under GPL-3.0-or-later.
