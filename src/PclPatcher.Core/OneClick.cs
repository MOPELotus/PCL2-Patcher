namespace PclPatcher.Core;

public static class OneClick
{
    public const string LauncherName = "Plain Craft Launcher 2.exe";
    public static async Task<FileResult> RunAsync(string outputDirectory, Action<string>? log = null, CancellationToken cancellation = default)
    {
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        log?.Invoke($"[auto] starting official download -> patch -> verification; output={output}");
        var cache = Path.Combine(output, ".official");
        await OfficialFiles.FetchAsync(cache, cancellation, log);
        var original = Path.Combine(cache, OfficialFiles.Releases[0].FileName);
        var donor = Path.Combine(cache, OfficialFiles.Releases[1].FileName);
        var launcher = Path.Combine(output, LauncherName);
        cancellation.ThrowIfCancellationRequested();
        if (!File.Exists(launcher))
        {
            File.Copy(original, launcher, overwrite: false);
            log?.Invoke($"[auto] copied verified official EXE to working output={launcher}");
        }
        else
        {
            var hash = Il.Hash(await File.ReadAllBytesAsync(launcher, cancellation));
            if (hash != OfficialFiles.Releases[0].Sha256 && !File.Exists(PatchFile.ReceiptPath(launcher)))
                throw new InvalidDataException("Existing output is neither the pinned official EXE nor a recorded patch; refusing overwrite: " + launcher);
            log?.Invoke($"[auto] existing output found; SHA256={hash}; patch/recovery checks will determine whether it can be reused");
        }
        var result = PatchFile.Patch(launcher, donor, log);
        PatchFile.Verify(launcher, donor, log);
        log?.Invoke($"[auto] COMPLETE; launcher={launcher}; backup={PatchFile.BackupPath(launcher)}; the launcher was not started");
        return result;
    }
}
