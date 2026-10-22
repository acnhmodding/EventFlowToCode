# EventFlowToCode

C# compiler and decompiler for readable EventFlow (`.bfevfl`) source.

## Build

Requires the .NET 10 SDK. [Our BfevLibrary fork](https://github.com/acnhmodding/BfevLibrary) is included as a Git submodule in `BfevLibrary/`, pinned to a specific commit.

Clone this repository with `git clone --recurse-submodules`, or initialize the submodule in an existing checkout:

```sh
git submodule update --init --recursive
```

From this directory:

```sh
dotnet build EventFlowToCode.slnx -c Release
dotnet run --project EventFlowToCode.CLI -c Release -- --help
```