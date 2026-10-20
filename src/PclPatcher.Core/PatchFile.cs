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
    public static Analysis Check(string path, string donorPath)
    {
        using var m = ModuleDefMD.Load(File.ReadAllBytes(path));
        using var donor = ModuleDefMD.Load(File.ReadAllBytes(donorPath));
        return PatchEngine.Plan(m, donor).Analysis;
    }
    public static FileResult Verify(string path, string donorPath)
    {
        var analysis = Check(path, donorPath);
        if (analysis.State != "restored") throw new InvalidDataException("Not all features are restored");
        var bytes = File.ReadAllBytes(path);
        if (File.Exists(ReceiptPath(path)))
        {
            var receipt = ReadReceipt(path);
            if (receipt.PatchedSha256 != Il.Hash(bytes)) throw new InvalidDataException("Patched file integrity mismatch");
            VerifyBackup(path, receipt);
        }
        return new("verified", path, Il.Hash(bytes), analysis);
    }
    public static FileResult Patch(string path, string donorPath) => Patch(path, donorPath, null);
    internal static FileResult Patch(string path, string donorPath, Action? afterReplace)
    {
        path = Path.GetFullPath(path);
        RejectSymlink(path);
        var original = File.ReadAllBytes(path);
        using var m = ModuleDefMD.Load(original);
        using var donor = ModuleDefMD.Load(File.ReadAllBytes(donorPath));
        var before = Integrity.Capture(m);
        var plan = PatchEngine.Plan(m, donor, apply: true);
        if (plan.ChangedMethods.Count == 0)
        {
            Verify(path, donorPath);
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
            m.Write(temp, options);
            using (var written = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) written.Flush(flushToDisk: true);
            var patched = File.ReadAllBytes(temp);
            using (var reloaded = ModuleDefMD.Load(patched))
            {
                before.Verify(reloaded, changed);
                foreach (var method in Il.Methods(reloaded).Where(x => changed.Contains(x.FullName)))
                {
                    Il.Validate(method);
                    if (Il.Fingerprint(method) != expected[method.FullName]) throw new InvalidDataException("Written IL does not match patch plan");
                }
                if (PatchEngine.Plan(reloaded, donor).Analysis.State != "restored") throw new InvalidDataException("Post-write feature verification failed");
            }
            var receipt = new Receipt(1, Il.Hash(original), Il.Hash(patched), plan.Analysis.AssemblyVersion, changed.Order().ToArray());
            if (!File.Exists(BackupPath(path))) DurableWrite(BackupPath(path), original, createNew: true);
            VerifyBackup(path, receipt);
            // A durable receipt before rename allows recovery after interruption in either state.
            AtomicWrite(ReceiptPath(path), JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions));
            if (Il.Hash(File.ReadAllBytes(path)) != receipt.OriginalSha256) throw new IOException("Input changed concurrently");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, File.GetUnixFileMode(path));
            File.Move(temp, path, overwrite: true);
            replaced = true;
            afterReplace?.Invoke();
            if (Il.Hash(File.ReadAllBytes(path)) != receipt.PatchedSha256) throw new IOException("Final file integrity mismatch");
            return new("patched", path, receipt.PatchedSha256, Check(path, donorPath));
        }
        catch
        {
            if (replaced) AtomicWrite(path, original);
            throw;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static FileResult Restore(string path)
    {
        path = Path.GetFullPath(path); RejectSymlink(path);
        var receipt = ReadReceipt(path);
        VerifyBackup(path, receipt);
        var hash = Il.Hash(File.ReadAllBytes(path));
        if (hash == receipt.OriginalSha256) return new("already-original", path, hash);
        if (hash != receipt.PatchedSha256) throw new InvalidDataException("Input was modified after patching; refusing destructive restore");
        AtomicWrite(path, File.ReadAllBytes(BackupPath(path)));
        if (Il.Hash(File.ReadAllBytes(path)) != receipt.OriginalSha256) throw new IOException("Restored file integrity mismatch");
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
