using System.Text;

namespace PclPatcher.Cli;

internal sealed class RunLog : IDisposable
{
    private readonly StreamWriter writer;
    public string Path { get; }
    public RunLog(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, $"pcl-patcher-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.log"));
        writer = new StreamWriter(new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
    }
    public void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:O} {message}";
        writer.WriteLine(line);
        Console.Error.WriteLine(line);
    }
    public void Dispose() => writer.Dispose();
}
