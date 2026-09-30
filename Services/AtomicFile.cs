using System.Text;

namespace SFW.Services;

internal static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        var temporary = Path.GetFullPath(path) + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public static async Task WriteAllTextAsync(string path, string content, CancellationToken token = default)
    {
        var temporary = Path.GetFullPath(path) + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
