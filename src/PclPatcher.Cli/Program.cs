using System.Text.Json;
using PclPatcher.Core;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    Console.WriteLine("""
        PCL2 Universal Patcher (independent third-party CLI)
        Usage:
          pcl-patcher fetch   <directory>
          pcl-patcher check   <PCL.exe> --donor <official-2.14.exe>
          pcl-patcher patch   <PCL.exe> --donor <official-2.14.exe>
          pcl-patcher verify  <PCL.exe> --donor <official-2.14.exe>
          pcl-patcher restore <PCL.exe>
        JSON output. Exit codes: 0 success, 2 incompatible/integrity error, 3 I/O error, 64 usage.
        check reports restricted / partial / restored without writing.
        patch saves .pclpatcher.original and .pclpatcher.json beside the executable.
        """);
    return 0;
}
if (args.Length == 2 && args[0] == "fetch")
{
    try
    {
        Console.WriteLine(JsonSerializer.Serialize(await OfficialFiles.FetchAsync(args[1]), PatchFile.JsonOptions));
        return 0;
    }
    catch (InvalidDataException ex)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { error = ex.Message, kind = "download-integrity" }));
        return 2;
    }
    catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { error = ex.Message, kind = "download" }));
        return 3;
    }
}
if (args.Length < 2 || !new[] { "check", "patch", "verify", "restore" }.Contains(args[0]) ||
    (args[0] == "restore" ? args.Length != 2 : args.Length != 4 || args[2] != "--donor"))
{
    Console.Error.WriteLine("Invalid arguments. Use --help.");
    return 64;
}
try
{
    object result = args[0] switch
    {
        "check" => PatchFile.Check(args[1], args[3]),
        "patch" => PatchFile.Patch(args[1], args[3]),
        "verify" => PatchFile.Verify(args[1], args[3]),
        "restore" => PatchFile.Restore(args[1]),
        _ => throw new InvalidOperationException()
    };
    Console.WriteLine(JsonSerializer.Serialize(result, result.GetType(), PatchFile.JsonOptions));
    return 0;
}
catch (Exception ex) when (ex is InvalidDataException or BadImageFormatException or InvalidOperationException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = ex.Message, kind = "incompatible-or-integrity" }));
    return 2;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = ex.Message, kind = "io" }));
    return 3;
}
