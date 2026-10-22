using BfevLibrary;

namespace EventFlowToCode.Core.Bfev;

public static class BfevFileWriter
{
    public static void Write(BfevFile file, string path)
    {
        BfevGraphValidator.Validate(file);
        var destination = Path.GetFullPath(path);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            file.ToBinary(temporary);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void Write(BfevFile file, Stream stream)
    {
        BfevGraphValidator.Validate(file);
        file.ToBinary(stream);
    }
}
