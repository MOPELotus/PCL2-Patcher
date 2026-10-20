using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace PclPatcher.Core;

public record FeatureResult(string Feature, string Method, string State);
public record Analysis(string AssemblyVersion, string State, IReadOnlyList<FeatureResult> Features);
public record PatchPlan(Analysis Analysis, IReadOnlyList<MethodDef> ChangedMethods);

public static class PatchEngine
{
    public const string Notice = "为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能，非常抱歉 qwq……";
    private record Recipe(string Feature, string Type, string Name, int Parameters);
    private static readonly Recipe[] Recipes =
    [
        new("minecraft-download", "PCL.PageDownloadInstall", "LoaderInit", 0),
        new("modpack-file-picker", "PCL.ModModpack", "ModpackInstall", 0),
        new("modpack-dispatch", "PCL.ModModpack", "ModpackInstall", 3),
        new("resource-install-button", "PCL.PageDownloadResourceDetail", "InitializeStackResourceVersions", 1),
        new("minecraft-version-modify", "PCL.PageDownloadModify", "LoadInstanceFinished", 1)
    ];

    public static PatchPlan Plan(ModuleDef target, ModuleDef donor, bool apply = false, Action<string>? log = null)
    {
        CheckModule(target); CheckModule(donor);
        log?.Invoke($"[analysis] target={target.Assembly.FullName}; donor={donor.Assembly.FullName}; ILOnly={target.IsILOnly}; strongName={target.IsStrongNameSigned}");
        // Check external assembly identities before importing generic/member signatures.
        var targetRefs = target.GetAssemblyRefs().Select(r => r.FullName).ToHashSet();
        if (donor.GetAssemblyRefs().Any(r => !targetRefs.Contains(r.FullName)))
            throw new InvalidDataException("Donor external dependency identities are incompatible");
        var changes = new List<(MethodDef Target, CilBody Body)>();
        var features = new List<FeatureResult>();
        var mapper = new ReferenceMapper(target, donor);
        var importer = new Importer(target, ImporterOptions.TryToUseDefs | ImporterOptions.TryToUseExistingAssemblyRefs, default, mapper);
        foreach (var r in Recipes)
        {
            var t = Il.Find(target, r.Type, r.Name, r.Parameters);
            var d = Il.Find(donor, r.Type, r.Name, r.Parameters);
            Il.Validate(t); Il.Validate(d);
            CheckDonor(r.Feature, d);
            // Validate all restored references even when the feature is already unrestricted.
            var imported = Il.Clone(d, t, importer);
            var expected = Il.Fingerprint(d, true);
            if (Il.Fingerprint(t, true) == expected)
            {
                log?.Invoke($"[feature] {r.Feature}: already restored; method={t.FullName}; IL={t.Body.Instructions.Count}; references validated");
                features.Add(new(r.Feature, t.FullName, "restored")); continue;
            }
            CilBody restored;
            if (r.Feature == "minecraft-download")
            {
                var scratch = Scratch(t);
                RestoreLoader(scratch, donor);
                if (Il.Fingerprint(scratch, true) != expected) throw Incompatible(t);
                // Use the donor body so all references, including renamed VB adapters, are checked.
                restored = imported;
            }
            else
            {
                var restricted = Scratch(d);
                ProjectRestriction(r.Feature, restricted, donor);
                if (Il.Fingerprint(t, true) != Il.Fingerprint(restricted, true)) throw Incompatible(t);
                restored = imported;
            }
            var check = new MethodDefUser(t.Name, t.MethodSig, t.ImplAttributes, t.Attributes) { Body = restored };
            Il.Validate(check);
            log?.Invoke($"[feature] {r.Feature}: compatible restriction confirmed; method={t.FullName}; IL {t.Body.Instructions.Count} -> {restored.Instructions.Count}; control flow, EH and dependency checks passed");
            changes.Add((t, restored));
            features.Add(new(r.Feature, t.FullName, "restricted"));
        }
        // Fail closed if another download restriction was introduced outside known sites.
        var known = Recipes.Select(r => Il.Find(target, r.Type, r.Name, r.Parameters)).ToHashSet();
        foreach (var m in Il.Methods(target).Where(m => m.HasBody && !known.Contains(m)))
        {
            if (!m.Body.Instructions.Any(i => i.Operand is string s && s == Notice)) continue;
            if (m.DeclaringType.DeclaringType?.FullName == "PCL.FormMain" && Calls(m, "get_Message") && Calls(m, "CompareString") && Calls(m, "Hint")) continue;
            if (m.DeclaringType.FullName == "PCL.PageDownloadInstall/_Closure$__" && IsNoticeThrow(m)) continue;
            // The version changelog embeds a longer message and is intentionally untouched.
            throw new InvalidDataException("Unrecognized restriction site: " + m.FullName);
        }
        var state = changes.Count == 0 ? "restored" : changes.Count == Recipes.Length ? "restricted" : "partial";
        if (apply) foreach (var (method, body) in changes) method.Body = body;
        log?.Invoke($"[analysis] state={state}; recognizedFeatures={features.Count}; methodsToChange={changes.Count}; apply={apply}");
        return new(new(target.Assembly.Version.ToString(), state, features), changes.Select(c => c.Target).ToArray());
    }

    private static InvalidDataException Incompatible(MethodDef t) => new("IL structure is incompatible; refusing patch: " + t.FullName);
    private static void CheckModule(ModuleDef m)
    {
        if (m.Assembly?.Name != "Plain Craft Launcher 2" || m.EntryPoint == null || !m.IsILOnly || m.IsStrongNameSigned)
            throw new InvalidDataException("Expected unsigned IL-only official PCL2 executable");
    }
    private static MethodDef Scratch(MethodDef m)
    {
        var copy = new MethodDefUser(m.Name, m.MethodSig, m.ImplAttributes, m.Attributes);
        copy.Body = Il.Clone(m, copy);
        copy.Body.SimplifyMacros(copy.Parameters);
        // NOPs are not semantic anchors. Retargeting preserves EH and branch edges.
        for (int i = copy.Body.Instructions.Count - 1; i >= 0; i--)
            if (copy.Body.Instructions[i].OpCode == OpCodes.Nop) Il.Replace(copy.Body, i, 1);
        return copy;
    }
    private static bool Calls(MethodDef m, string name) => m.Body.Instructions.Any(i => i.Operand is IMethod call && call.Name == name);
    private static void CheckDonor(string feature, MethodDef d)
    {
        bool ok = feature switch
        {
            "minecraft-download" => d.Body.Instructions.Any(i => i.Operand is IField f && f.Name == "DlClientListLoader"),
            "modpack-file-picker" => Calls(d, "SelectFile") && Calls(d, "RunInThread"),
            "modpack-dispatch" => new[] { "InstallPackCurseForge", "InstallPackHMCL", "InstallPackMMC", "InstallPackMCBBS", "InstallPackModrinth", "InstallPackLauncherPack", "InstallPackCompress" }.All(n => Calls(d, n)),
            "resource-install-button" => d.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldftn && i.Operand is MethodDef a && Calls(a, "Install_Click")),
            "minecraft-version-modify" => VisibilityValue(d).GetLdcI4Value() == 0,
            _ => false
        };
        if (!ok || d.Body.Instructions.Any(i => i.Operand is string s && s == Notice))
            throw new InvalidDataException("Donor does not contain unrestricted feature: " + feature);
    }
    private static Instruction VisibilityValue(MethodDef m)
    {
        var ins = m.Body.Instructions;
        var matches = Enumerable.Range(0, ins.Count - 2).Where(i =>
            ins[i].Operand is IMethod get && get.Name == "get_CardMinecraft" &&
            ins[i + 1].IsLdcI4() && ins[i + 2].Operand is IMethod set &&
            set.FullName == "System.Void System.Windows.UIElement::set_Visibility(System.Windows.Visibility)").ToArray();
        if (matches.Length != 1) throw Incompatible(m);
        return ins[matches[0] + 1];
    }
    private static void ProjectRestriction(string feature, MethodDef m, ModuleDef donor)
    {
        var b = m.Body; var ins = b.Instructions;
        switch (feature)
        {
            case "modpack-file-picker":
                var hint = Il.Find(donor, "PCL.ModMain", "Hint", 3);
                if (ins[0].OpCode != OpCodes.Newobj || ins[1].OpCode != OpCodes.Stloc) throw Incompatible(m);
                Il.Replace(b, 2, ins.Count - 2,
                    Instruction.Create(OpCodes.Ldstr, Notice), Instruction.CreateLdcI4(2), Instruction.CreateLdcI4(1),
                    Instruction.Create(OpCodes.Call, hint), Instruction.Create(OpCodes.Ret));
                break;
            case "modpack-dispatch":
                var sw = ins.Select((x, n) => (x, n)).Single(p => p.x.OpCode == OpCodes.Switch).n;
                // The outer finally covers the switch; the other finally disposes an enumerator.
                var final = b.ExceptionHandlers.Single(e => e.HandlerType == ExceptionHandlerType.Finally && ins.IndexOf(e.TryStart) < sw && ins.IndexOf(e.HandlerStart) > sw);
                var ctor = donor.GetMemberRefs().Single(r => r.FullName == "System.Void System.Exception::.ctor(System.String)");
                var finallyStart = ins.IndexOf(final.HandlerStart);
                Il.Replace(b, sw - 1, finallyStart - sw + 1, Instruction.Create(OpCodes.Ldstr, Notice), Instruction.Create(OpCodes.Newobj, ctor), Instruction.Create(OpCodes.Throw));
                var end = ins.IndexOf(final.HandlerEnd);
                Il.Replace(b, end, ins.Count - end);
                break;
            case "resource-install-button":
                var branch = ins.Select((x, n) => (x, n)).Single(p => p.x.OpCode == OpCodes.Bne_Un && p.n >= 2 &&
                    ins[p.n - 2].Operand is IField f && f.Name == "TargetResourceType" &&
                    ins.Skip(p.n + 1).Take(ins.IndexOf((Instruction)p.x.Operand) - p.n - 1).Any(i => i.OpCode == OpCodes.Ldftn && i.Operand is MethodDef adapter && Calls(adapter, "Install_Click")));
                var otherwise = ins.IndexOf((Instruction)branch.x.Operand);
                Il.Replace(b, branch.n - 3, otherwise - (branch.n - 3));
                break;
            case "minecraft-version-modify":
                var v = VisibilityValue(m); v.OpCode = OpCodes.Ldc_I4; v.Operand = 2;
                break;
            default: throw new InvalidDataException("Unknown recipe");
        }
    }

    private static bool IsNoticeThrow(MethodDef m)
    {
        var code = m.Body.Instructions.Where(i => i.OpCode != OpCodes.Nop).ToArray();
        return code.Length == 3 && code[0].OpCode == OpCodes.Ldstr && code[0].Operand as string == Notice &&
            code[1].OpCode == OpCodes.Newobj && code[1].Operand is IMethod c && c.FullName == "System.Void System.Exception::.ctor(System.String)" && code[2].OpCode == OpCodes.Throw;
    }
    private static void RestoreLoader(MethodDef m, ModuleDef donor)
    {
        var b = m.Body; var ins = b.Instructions;
        var start = ins.Select((x, n) => (x, n)).Single(p => p.x.OpCode == OpCodes.Ldstr).n;
        var part = ins.Skip(start).Take(13).ToArray();
        var expected = new[] { Code.Ldstr, Code.Ldsfld, Code.Brfalse, Code.Ldsfld, Code.Br, Code.Ldsfld, Code.Ldftn, Code.Newobj, Code.Dup, Code.Stsfld, Code.Ldnull, Code.Ldc_I4, Code.Newobj };
        if (!part.Select(i => i.OpCode.Code).SequenceEqual(expected) || part[2].Operand != part[5] ||
            part[4].Operand != part[10] || ((IField)part[1].Operand).FullName != ((IField)part[3].Operand).FullName ||
            ((IField)part[1].Operand).FullName != ((IField)part[9].Operand).FullName ||
            part[12].Operand is not IMethod ctor || !ctor.DeclaringType.FullName.StartsWith("PCL.ModLoader/LoaderTask`2<", StringComparison.Ordinal) ||
            part[6].Operand is not MethodDef wrapper || !wrapper.Body.Instructions.Any(i => i.Operand is MethodDef leaf && IsNoticeThrow(leaf)))
            throw Incompatible(m);
        var field = donor.GetTypes().Single(t => t.FullName == "PCL.ModDownload").Fields.Single(f => f.Name == "DlClientListLoader");
        Il.Replace(b, start, 13, Instruction.Create(OpCodes.Ldsfld, field));
    }
}
