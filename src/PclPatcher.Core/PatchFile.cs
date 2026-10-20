using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Writer;

namespace PclPatcher.Core;

public record Receipt(int Schema, string OriginalSha256, string PatchedSha256, string OriginalVersion, string[] Methods);
public record FileResult(string State, string Path, string Sha256, Analysis? Analysis = null);

public static class PatchFile
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static string BackupPath(string path) => path + ".pclpatcher.original";
    public static string ReceiptPath(string path) => path + ".pclpatcher.json";
    public static Analysis Check(string path, string donorPath, Action<string>? log = null)
    {
        using var m = ModuleDefMD.Load(File.ReadAllBytes(path));
        using var donor = ModuleDefMD.Load(File.ReadAllBytes(donorPath));
        return PatchEngine.Plan(m, donor, log: log).Analysis;
    }
    public static FileResult Verify(string path, string donorPath, Action<string>? log = null)
    {
        var analysis = Check(path, donorPath, log);
        if (analysis.State != "restored") throw new InvalidDataException("Not all features are restored");
        var bytes = File.ReadAllBytes(path);
        if (File.Exists(ReceiptPath(path)))
        {
            var receipt = ReadReceipt(path);
            if (receipt.PatchedSha256 != Il.Hash(bytes)) throw new InvalidDataException("Patched file integrity mismatch");
            VerifyBackup(path, receipt);
        }
        log?.Invoke($"[verify] all five features restored; EXE SHA256={Il.Hash(bytes)}; recovery record present={File.Exists(ReceiptPath(path))}");
        return new("verified", path, Il.Hash(bytes), analysis);
    }
    public static FileResult Patch(string path, string donorPath, Action<string>? log = null) => Patch(path, donorPath, null, log);
    internal static FileResult Patch(string path, string donorPath, Action? afterReplace, Action<string>? log = null)
    {
        path = Path.GetFullPath(path);
        RejectSymlink(path);
        log?.Invoke($"[patch] reading input={path}; donor={Path.GetFullPath(donorPath)}");
        var original = File.ReadAllBytes(path);
        log?.Invoke($"[integrity] input bytes={original.Length}; SHA256={Il.Hash(original)}; capturing methods, metadata and embedded resources");
        using var m = ModuleDefMD.Load(original);
        using var donor = ModuleDefMD.Load(File.ReadAllBytes(donorPath));
        var before = Integrity.Capture(m);
        log?.Invoke($"[snapshot] captured methods={before.Methods.Count}; embeddedResources={before.Resources.Count}");
        var plan = PatchEngine.Plan(m, donor, apply: true, log: log);
        if (plan.ChangedMethods.Count == 0)
        {
            Verify(path, donorPath, log);
            log?.Invoke("[patch] already restored; no EXE or backup rewritten");
            return new("already-restored", path, Il.Hash(original), plan.Analysis);
        }
        // Never overwrite a pre-existing recovery record or silently replace a different backup.
        if (File.Exists(ReceiptPath(path)))
        {
            var prior = ReadReceipt(path);
            VerifyBackup(path, prior);
            if (prior.OriginalSha256 != Il.Hash(original)) throw new InvalidDataException("Recovery record conflicts with input");
        }
        if (File.Exists(BackupPath(path)) && Il.Hash(File.ReadAllBytes(BackupPath(path))) != Il.Hash(original))
            throw new InvalidDataException("Existing backup differs from original input");
        var changed = plan.ChangedMethods.Select(x => x.FullName).ToHashSet();
        var expected = plan.ChangedMethods.ToDictionary(x => x.FullName, x => Il.Fingerprint(x));
        var temp = path + ".pclpatcher-" + Guid.NewGuid().ToString("N") + ".tmp";
        bool replaced = false;
        try
        {
            foreach (var method in plan.ChangedMethods) Il.Validate(method);
            var options = new ModuleWriterOptions(m);
            options.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
            log?.Invoke($"[write] writing temporary EXE={temp}; dnlib PreserveAll metadata mode");
            m.Write(temp, options);
            using (var written = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) written.Flush(flushToDisk: true);
            var patched = File.ReadAllBytes(temp);
            using (var reloaded = ModuleDefMD.Load(patched))
            {
                log?.Invoke("[verify] reloaded written PE/CLR image; comparing non-target methods, metadata and all embedded resources");
                before.Verify(reloaded, changed);
                foreach (var method in Il.Methods(reloaded).Where(x => changed.Contains(x.FullName)))
                {
                    Il.Validate(method);
                    if (Il.Fingerprint(method) != expected[method.FullName]) throw new InvalidDataException("Written IL does not match patch plan");
                }
                log?.Invoke($"[verify] {changed.Count} restored method bodies match plan; branch/EH and stack checks passed; {before.Methods.Count - changed.Count} non-target methods preserved");
                if (PatchEngine.Plan(reloaded, donor, log: log).Analysis.State != "restored") throw new InvalidDataException("Post-write feature verification failed");
            }
            var receipt = new Receipt(1, Il.Hash(original), Il.Hash(patched), plan.Analysis.AssemblyVersion, changed.Order().ToArray());
            if (!File.Exists(BackupPath(path))) DurableWrite(BackupPath(path), original, createNew: true);
            VerifyBackup(path, receipt);
            log?.Invoke($"[backup] original bytes persisted and SHA256 verified; path={BackupPath(path)}");
            // A durable receipt before rename allows recovery after interruption in either state.
            AtomicWrite(ReceiptPath(path), JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions));
            log?.Invoke($"[recovery] durable receipt={ReceiptPath(path)}; originalSHA256={receipt.OriginalSha256}; patchedSHA256={receipt.PatchedSha256}");
            if (Il.Hash(File.ReadAllBytes(path)) != receipt.OriginalSha256) throw new IOException("Input changed concurrently");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, File.GetUnixFileMode(path));
            log?.Invoke("[replace] original input rechecked; atomically replacing EXE");
            File.Move(temp, path, overwrite: true);
            replaced = true;
            afterReplace?.Invoke();
            if (Il.Hash(File.ReadAllBytes(path)) != receipt.PatchedSha256) throw new IOException("Final file integrity mismatch");
            log?.Invoke($"[success] final SHA256 verified={receipt.PatchedSha256}; output={path}");
            return new("patched", path, receipt.PatchedSha256, Check(path, donorPath, log));
        }
        catch (Exception error)
        {
            if (replaced) AtomicWrite(path, original);
            log?.Invoke($"[rollback] replacementPerformed={replaced}; original restored={replaced}; error={error}");
            throw;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static FileResult Restore(string path, Action<string>? log = null)
    {
        path = Path.GetFullPath(path); RejectSymlink(path);
        log?.Invoke($"[restore] validating recovery record and backup for {path}");
        var receipt = ReadReceipt(path);
        VerifyBackup(path, receipt);
        var hash = Il.Hash(File.ReadAllBytes(path));
        if (hash == receipt.OriginalSha256) return new("already-original", path, hash);
        if (hash != receipt.PatchedSha256) throw new InvalidDataException("Input was modified after patching; refusing destructive restore");
        AtomicWrite(path, File.ReadAllBytes(BackupPath(path)));
        if (Il.Hash(File.ReadAllBytes(path)) != receipt.OriginalSha256) throw new IOException("Restored file integrity mismatch");
        log?.Invoke($"[restore] original bytes restored; SHA256={receipt.OriginalSha256}");
        return new("restored-original", path, receipt.OriginalSha256);
    }
    private static Receipt ReadReceipt(string path)
    {
        RejectSymlink(ReceiptPath(path));
        var r = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(ReceiptPath(path)), JsonOptions) ?? throw new InvalidDataException("Invalid recovery record");
        if (r.Schema != 1 || !IsHash(r.OriginalSha256) || !IsHash(r.PatchedSha256)) throw new InvalidDataException("Invalid recovery schema/hash");
        return r;
    }
    private static bool IsHash(string? s) => s?.Length == 64 && s.All(c => "0123456789abcdef".Contains(c));
    private static void VerifyBackup(string path, Receipt receipt)
    {
        RejectSymlink(BackupPath(path));
        if (Il.Hash(File.ReadAllBytes(BackupPath(path))) != receipt.OriginalSha256) throw new InvalidDataException("Original backup integrity mismatch");
    }
    private static void RejectSymlink(string path)
    {
        if (new FileInfo(path).LinkTarget != null) throw new IOException("Symlink input/recovery files are unsupported: " + path);
    }
    private static void DurableWrite(string path, byte[] data, bool createNew = false)
    {
        using var file = new FileStream(path, createNew ? FileMode.CreateNew : FileMode.Create, FileAccess.Write, FileShare.None);
        file.Write(data); file.Flush(flushToDisk: true);
    }
    private static void AtomicWrite(string path, byte[] data)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            DurableWrite(temp, data, createNew: true);
            if (!OperatingSystem.IsWindows() && File.Exists(path)) File.SetUnixFileMode(temp, File.GetUnixFileMode(path));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
