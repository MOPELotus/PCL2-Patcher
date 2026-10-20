using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using PclPatcher.Core;
using Xunit;

namespace PclPatcher.Tests;

public sealed class OfficialBinaryTests : IDisposable
{
    private readonly string root = FindRoot();
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pcl-patcher-tests-" + Guid.NewGuid().ToString("N"));
    private string Donor => Path.Combine(root, ".artifacts/official/PCL-2.14.0.0.exe");
    private string Original => Path.Combine(root, ".artifacts/official/PCL-2.99.0.0.exe");
    public OfficialBinaryTests() => Directory.CreateDirectory(dir);
    private static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PclPatcher.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Run tests inside the repository");
    }
    private string Copy()
    {
        Assert.True(File.Exists(Original) && File.Exists(Donor), "Run python3 scripts/fetch-official.py first");
        var p = Path.Combine(dir, "PCL.exe"); File.Copy(Original, p); return p;
    }
    private static void Rewrite(string p, Action<ModuleDef> edit)
    {
        using var m = ModuleDefMD.Load(File.ReadAllBytes(p)); edit(m); m.Write(p);
    }
    [Fact]
    public void OfficialRestrictedAndUnrestrictedAreDetectedWithoutWrites()
    {
        var p = Copy(); var bytes = File.ReadAllBytes(p);
        var result = PatchFile.Check(p, Donor);
        Assert.Equal("restricted", result.State); Assert.Equal(5, result.Features.Count);
        Assert.Equal("restored", PatchFile.Check(Donor, Donor).State);
        Assert.Equal(bytes, File.ReadAllBytes(p)); Assert.False(File.Exists(PatchFile.BackupPath(p)));
    }
    [Fact]
    public void RealExeRoundTripPreservesEveryUntouchedMethodAndResource()
    {
        var p = Copy(); var original = File.ReadAllBytes(p);
        using var before = ModuleDefMD.Load(original);
        var snapshot = Integrity.Capture(before);
        Assert.Equal("patched", PatchFile.Patch(p, Donor).State);
        Assert.Equal(original, File.ReadAllBytes(PatchFile.BackupPath(p)));
        Assert.Equal("verified", PatchFile.Verify(p, Donor).State);
        using var after = ModuleDefMD.Load(File.ReadAllBytes(p));
        var changed = Il.Methods(before).Where(m => Il.Fingerprint(m) != Il.Fingerprint(Il.Methods(after).Single(a => a.FullName == m.FullName))).Select(m => m.FullName).ToHashSet();
        Assert.Equal(5, changed.Count); snapshot.Verify(after, changed);
        Assert.DoesNotContain(changed, n => n.Contains("Login") || n.Contains("OAuth") || n.Contains("Secret"));
        using var pe = new PEReader(File.OpenRead(p));
        Assert.True(pe.HasMetadata);
        var md = pe.GetMetadataReader();
        foreach (var handle in md.MethodDefinitions)
        {
            var method = md.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress != 0) Assert.NotNull(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes());
        }
        var patched = File.ReadAllBytes(p);
        Assert.Equal("already-restored", PatchFile.Patch(p, Donor).State);
        Assert.Equal(patched, File.ReadAllBytes(p));
        Assert.Equal("restored-original", PatchFile.Restore(p).State);
        Assert.Equal(original, File.ReadAllBytes(p));
        Assert.Equal("already-original", PatchFile.Restore(p).State);
        Assert.Equal("patched", PatchFile.Patch(p, Donor).State);
    }
    [Theory]
    [InlineData("2.100.0.0")]
    [InlineData("3.0.0.0")]
    public void CompatibleFutureShapeAcceptsVersionTokenOffsetAndAdapterNameChanges(string version)
    {
        var p = Copy();
        Rewrite(p, m =>
        {
            m.Assembly.Version = Version.Parse(version);
            var loader = Il.Find(m, "PCL.PageDownloadInstall", "LoaderInit", 0);
            loader.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
            var callback = loader.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldftn).Select(i => (MethodDef)i.Operand).Single(x => x.DeclaringType.FullName == "PCL.PageDownloadInstall");
            callback.Name = "_Lambda$__FutureAdapter";
            m.GetTypes().Single(t => t.FullName == "PCL.ModSecret").Methods.First(x => x.HasBody).Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
        });
        Assert.Equal(version, PatchFile.Check(p, Donor).AssemblyVersion);
        Assert.Equal("patched", PatchFile.Patch(p, Donor).State);
        Assert.Equal("verified", PatchFile.Verify(p, Donor).State);
    }
    [Fact]
    public void ChangedRetainedAlgorithmRefusesWithoutWriting()
    {
        var p = Copy();
        Rewrite(p, m => Il.Find(m, "PCL.ModModpack", "ModpackInstall", 3).Body.Instructions.First(i => i.OpCode == OpCodes.Ldstr).Operand = "new upstream algorithm");
        var original = File.ReadAllBytes(p);
        Assert.Throws<InvalidDataException>(() => PatchFile.Patch(p, Donor));
        Assert.Equal(original, File.ReadAllBytes(p)); Assert.False(File.Exists(PatchFile.BackupPath(p)));
    }
    [Fact]
    public void MissingInstallerDependencyRefusesWithoutWriting()
    {
        var p = Copy();
        Rewrite(p, m => Il.Methods(m).Single(x => x.Name == "InstallPackMMC").Name = "RemovedInstaller");
        var original = File.ReadAllBytes(p);
        Assert.Throws<InvalidDataException>(() => PatchFile.Patch(p, Donor));
        Assert.Equal(original, File.ReadAllBytes(p));
    }
    [Fact]
    public void MissingGenericFieldDependencyRefuses()
    {
        var p = Copy();
        Rewrite(p, m => m.GetTypes().Single(t => t.FullName == "PCL.ModLoader/LoaderTask`2").Fields.Single(f => f.Name == "Input").Name = "RemovedInput");
        var original = File.ReadAllBytes(p);
        Assert.Throws<InvalidDataException>(() => PatchFile.Patch(p, Donor));
        Assert.Equal(original, File.ReadAllBytes(p));
    }
    [Fact]
    public void ChangedCapturedCallbackRefuses()
    {
        var p = Copy();
        Rewrite(p, m =>
        {
            var closure = m.GetTypes().Single(t => t.FullName == "PCL.ModModpack/_Closure$__0-0");
            var body = closure.Methods.Single(x => x.Name == "_Lambda$__0").Body;
            body.Instructions.First(i => i.Operand is string).Operand = "changed callback";
        });
        Assert.Throws<InvalidDataException>(() => PatchFile.Patch(p, Donor));
    }
    [Fact]
    public void PartialPatchIsCompletedAtomically()
    {
        var p = Copy();
        Rewrite(p, m =>
        {
            var originals = Il.Methods(m).Where(x => x.HasBody).ToDictionary(x => x, x => x.Body);
            using var donor = ModuleDefMD.Load(File.ReadAllBytes(Donor));
            var plan = PatchEngine.Plan(m, donor, apply: true);
            foreach (var method in plan.ChangedMethods.Skip(1)) method.Body = originals[method];
        });
        Assert.Equal("partial", PatchFile.Check(p, Donor).State);
        Assert.Equal("patched", PatchFile.Patch(p, Donor).State);
        Assert.Equal("verified", PatchFile.Verify(p, Donor).State);
    }
    [Fact]
    public void FailureAfterReplacementRollsBackExactOriginalAndCanRetry()
    {
        var p = Copy(); var original = File.ReadAllBytes(p);
        Assert.Throws<IOException>(() => PatchFile.Patch(p, Donor, () => throw new IOException("Injected post-rename failure")));
        Assert.Equal(original, File.ReadAllBytes(p));
        Assert.Equal(original, File.ReadAllBytes(PatchFile.BackupPath(p)));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        Assert.Equal("patched", PatchFile.Patch(p, Donor).State);
    }
    [Fact]
    public void CorruptBackupRefusesVerifyAndRestore()
    {
        var p = Copy(); PatchFile.Patch(p, Donor);
        File.AppendAllText(PatchFile.BackupPath(p), "corrupt");
        var patched = File.ReadAllBytes(p);
        Assert.Throws<InvalidDataException>(() => PatchFile.Verify(p, Donor));
        Assert.Throws<InvalidDataException>(() => PatchFile.Restore(p));
        Assert.Equal(patched, File.ReadAllBytes(p));
    }
    [Fact]
    public void TamperedPatchedExeCannotBeSilentlyRestoredOrRepatched()
    {
        var p = Copy(); PatchFile.Patch(p, Donor); File.AppendAllText(p, "external edit");
        var modified = File.ReadAllBytes(p);
        Assert.Throws<InvalidDataException>(() => PatchFile.Verify(p, Donor));
        Assert.Throws<InvalidDataException>(() => PatchFile.Restore(p));
        Assert.Throws<InvalidDataException>(() => PatchFile.Patch(p, Donor));
        Assert.Equal(modified, File.ReadAllBytes(p));
    }
    [Fact]
    public async Task FetchReusesVerifiedFilesAndRefusesCorruptCacheWithoutNetwork()
    {
        Copy();
        File.Copy(Original, Path.Combine(dir, "PCL-2.99.0.0.exe"));
        File.Copy(Donor, Path.Combine(dir, "PCL-2.14.0.0.exe"));
        Assert.Equal(2, (await OfficialFiles.FetchAsync(dir)).Count);
        var cached = Path.Combine(dir, "PCL-2.99.0.0.exe");
        File.AppendAllText(cached, "corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => OfficialFiles.FetchAsync(dir));
    }
    [Fact]
    public void ConflictingBackupIsPreserved()
    {
        var p = Copy(); File.WriteAllText(PatchFile.BackupPath(p), "other backup");
        Assert.Throws<InvalidDataException>(() => PatchFile.Patch(p, Donor));
        Assert.Equal("other backup", File.ReadAllText(PatchFile.BackupPath(p)));
    }
    [Fact]
    public void UnrecognizedRestrictionSiteRefusesEntirePatch()
    {
        var p = Copy();
        Rewrite(p, m =>
        {
            var extra = new MethodDefUser("FutureRestrictedEntry", MethodSig.CreateStatic(m.CorLibTypes.Void), MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() };
            extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, PatchEngine.Notice));
            extra.Body.Instructions.Add(Instruction.Create(OpCodes.Pop)); extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            m.GetTypes().Single(t => t.FullName == "PCL.ModModpack").Methods.Add(extra);
        });
        Assert.Throws<InvalidDataException>(() => PatchFile.Patch(p, Donor));
    }
    [Fact]
    public void WrongOrTruncatedInputRefusesWithoutCreatingBackup()
    {
        var p = Path.Combine(dir, "bad.exe"); File.WriteAllBytes(p, [0x4d, 0x5a]);
        Assert.ThrowsAny<Exception>(() => PatchFile.Patch(p, Donor));
        Assert.Equal(new byte[] { 0x4d, 0x5a }, File.ReadAllBytes(p));
        Assert.False(File.Exists(PatchFile.BackupPath(p)));
    }
    public void Dispose() => Directory.Delete(dir, recursive: true);
}
