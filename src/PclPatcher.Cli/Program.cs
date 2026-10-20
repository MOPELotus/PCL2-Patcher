using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PclPatcher.Cli;
using PclPatcher.Core;

Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = Encoding.UTF8;
bool doubleClick = args.Length == 0;
if (args.Length == 1 && args[0] is "--help" or "-h")
{
    Console.WriteLine("""
        PCL2 Universal Patcher — independent third-party tool
        Double-click / no arguments: automatically download official EXEs, patch and verify.
        Commands:
          pcl-patcher auto [--output <directory>]
          pcl-patcher fetch <directory>
          pcl-patcher check <PCL.exe> --donor <official-2.14.exe>
          pcl-patcher patch <PCL.exe> --donor <official-2.14.exe>
          pcl-patcher verify <PCL.exe> --donor <official-2.14.exe>
          pcl-patcher restore <PCL.exe>
        Default auto output: PCL2-Patched beside this tool. Detailed UTF-8 logs are saved automatically.
        Manual commands output JSON to stdout, detailed progress to stderr and logs/.
        Exit codes: 0 success, 1 unexpected error, 2 incompatible/integrity, 3 I/O/network, 64 usage.
        """);
    return 0;
}
if (doubleClick) args = ["auto"];
bool auto = args[0] == "auto";
string output = Path.Combine(AppContext.BaseDirectory, "PCL2-Patched");
if (auto)
{
    if (args.Length == 3 && args[1] == "--output") output = args[2];
    else if (args.Length != 1) return Usage();
}
else if (!(args.Length == 2 && args[0] is "fetch" or "restore") &&
         !(args.Length == 4 && args[0] is "check" or "patch" or "verify" && args[2] == "--donor")) return Usage();

RunLog? log = null;
int exitCode;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    log = new RunLog(auto ? Path.Combine(output, "logs") : Path.Combine(Directory.GetCurrentDirectory(), "pcl-patcher-logs"));
    log.Write($"[start] toolVersion={typeof(PatchFile).Assembly.GetName().Version}; runtime={RuntimeInformation.FrameworkDescription}; OS={RuntimeInformation.OSDescription}; processArchitecture={RuntimeInformation.ProcessArchitecture}");
    log.Write($"[start] command={JsonSerializer.Serialize(args)}; workingDirectory={Directory.GetCurrentDirectory()}; applicationDirectory={AppContext.BaseDirectory}; log={log.Path}");
    object result = args[0] switch
    {
        "auto" => await OneClick.RunAsync(output, log.Write, cancellation.Token),
        "fetch" => await OfficialFiles.FetchAsync(args[1], cancellation.Token, log.Write),
        "check" => PatchFile.Check(args[1], args[3], log.Write),
        "patch" => PatchFile.Patch(args[1], args[3], log.Write),
        "verify" => PatchFile.Verify(args[1], args[3], log.Write),
        "restore" => PatchFile.Restore(args[1], log.Write),
        _ => throw new InvalidOperationException("Invalid command")
    };
    Console.WriteLine(JsonSerializer.Serialize(result, result.GetType(), PatchFile.JsonOptions));
    log.Write("[done] SUCCESS; exitCode=0");
    exitCode = 0;
}
catch (Exception error)
{
    exitCode = error switch
    {
        InvalidDataException or BadImageFormatException or InvalidOperationException => 2,
        IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException => 3,
        _ => 1
    };
    // Full exception, inner errors and stack go to the file; keep a short final message visible too.
    try { log?.Write($"[ERROR] exitCode={exitCode}; {error}"); } catch (IOException) { }
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = error.Message, exitCode, log = log?.Path }));
}
finally { log?.Dispose(); }
if (doubleClick && OperatingSystem.IsWindows() && !Console.IsInputRedirected && !Console.IsOutputRedirected)
{
    Console.WriteLine($"\n日志 / Log: {log?.Path}\n按任意键关闭 / Press any key to close...");
    Console.ReadKey(intercept: true);
}
return exitCode;

static int Usage() { Console.Error.WriteLine("Invalid arguments. Use --help."); return 64; }
