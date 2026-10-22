using BfevLibrary;

namespace EventFlowToCode.Core.Bfev;

/// <summary>Loads a supported flowchart through BfevLibrary.</summary>
public static class BfevFileReader
{
    public static BfevFile Read(string path) => RequireSupportedFile(BfevFile.FromBinary(path));

    public static BfevFile Read(byte[] data) => RequireSupportedFile(BfevFile.FromBinary(data));

    private static BfevFile RequireSupportedFile(BfevFile file)
    {
        BfevGraphValidator.RequireSingleFlowchart(file);
        if (file.Version != "0.3.0.0")
            throw new NotSupportedException($"Unsupported BFEV version '{file.Version}'; this adapter supports 0.3.0.0.");
        return file;
    }
}
