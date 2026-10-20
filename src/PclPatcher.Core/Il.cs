using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace PclPatcher.Core;

public static class Il
{
    public static IEnumerable<MethodDef> Methods(ModuleDef m) => m.GetTypes().SelectMany(t => t.Methods);
    public static MethodDef Find(ModuleDef m, string type, string name, int parameters) =>
        Methods(m).Single(x => x.DeclaringType.FullName == type && x.Name == name && x.MethodSig.Params.Count == parameters);

    // Normalize encodings only: no fixed byte offsets or tokens participate in matching.
    public static string Fingerprint(MethodDef m, bool semanticLambdas = false, int depth = 0)
    {
        if (!m.HasBody) return "no-body";
        // Work on a copy: merely inspecting an EXE must not rewrite untouched IL encodings.
        var normalized = new MethodDefUser(m.Name, m.MethodSig, m.ImplAttributes, m.Attributes);
        normalized.Body = Clone(m, normalized);
        normalized.Body.SimplifyMacros(normalized.Parameters);
        var all = normalized.Body.Instructions;
        var meaningful = all.Where(i => i.OpCode != OpCodes.Nop).ToList();
        var positions = new Dictionary<Instruction, int>();
        int position = 0;
        foreach (var instruction in all)
        {
            positions.Add(instruction, position);
            if (instruction.OpCode != OpCodes.Nop) position++;
        }
        int Position(Instruction? i)
        {
            if (i == null) return meaningful.Count;
            return positions.TryGetValue(i, out var n) ? n : throw new InvalidDataException("Foreign IL branch/EH boundary");
        }
        string Operand(object? o) => o switch
        {
            null => "",
            Instruction i => "@" + Position(i),
            IList<Instruction> list => string.Join(',', list.Select(i => "@" + Position(i))),
            Local l => "local:" + l.Index,
            Parameter p => "arg:" + p.Index,
            MethodDef d when semanticLambdas && d.Name.String.StartsWith("_Lambda$", StringComparison.Ordinal) && depth < 5 =>
                d.DeclaringType.FullName + "::lambda:" + d.MethodSig + ":" + Hash(Encoding.UTF8.GetBytes(Fingerprint(d, true, depth + 1))),
            IFullName n => n.FullName,
            string s => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => o.ToString() ?? ""
        };
        return $"init:{m.Body.InitLocals};locals:{string.Join(';', normalized.Body.Variables.Select(v => v.Type.FullName))}\n" +
               string.Join('\n', meaningful.Select(i => i.OpCode.Name + " " + Operand(i.Operand))) + "\nEH\n" +
               string.Join('\n', normalized.Body.ExceptionHandlers.Select(e => $"{e.HandlerType}:{Position(e.TryStart)}:{Position(e.TryEnd)}:{Position(e.HandlerStart)}:{Position(e.HandlerEnd)}:{Position(e.FilterStart)}:{e.CatchType?.FullName}"));
    }
    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static CilBody Clone(MethodDef source, MethodDef destination, Importer? importer = null)
    {
        var old = source.Body;
        var body = new CilBody { InitLocals = old.InitLocals, MaxStack = old.MaxStack };
        foreach (var v in old.Variables) body.Variables.Add(new Local(importer?.Import(v.Type) ?? v.Type));
        var map = old.Instructions.ToDictionary(i => i, i => new Instruction(i.OpCode));
        Instruction? Boundary(Instruction? i) => i == null ? null : map[i];
        foreach (var i in old.Instructions)
        {
            object? operand = i.Operand switch
            {
                Instruction branch => map[branch],
                IList<Instruction> branches => branches.Select(b => map[b]).ToArray(),
                Local l => body.Variables[l.Index],
                Parameter p => destination.Parameters[p.Index],
                ITypeDefOrRef t when importer != null => importer.Value.Import(t),
                IMethod method when importer != null => importer.Value.Import(method),
                IField field when importer != null => importer.Value.Import(field),
                MethodSig sig when importer != null => importer.Value.Import(sig),
                _ => i.Operand
            };
            map[i].Operand = operand;
            body.Instructions.Add(map[i]);
        }
        foreach (var e in old.ExceptionHandlers)
            body.ExceptionHandlers.Add(new ExceptionHandler(e.HandlerType)
            {
                TryStart = Boundary(e.TryStart), TryEnd = Boundary(e.TryEnd),
                HandlerStart = Boundary(e.HandlerStart), HandlerEnd = Boundary(e.HandlerEnd),
                FilterStart = Boundary(e.FilterStart), CatchType = e.CatchType == null ? null : importer?.Import(e.CatchType) ?? e.CatchType
            });
        return body;
    }

    public static void Validate(MethodDef m)
    {
        if (!m.HasBody) throw new InvalidDataException("Missing body: " + m.FullName);
        var b = m.Body;
        var set = b.Instructions.ToHashSet();
        foreach (var i in b.Instructions)
        {
            if (i.Operand is Instruction target && !set.Contains(target)) throw new InvalidDataException("Dangling branch");
            if (i.Operand is IList<Instruction> targets && targets.Any(t => !set.Contains(t))) throw new InvalidDataException("Dangling switch");
        }
        foreach (var e in b.ExceptionHandlers)
        {
            if (e.TryStart == null || e.HandlerStart == null) throw new InvalidDataException("Missing EH start");
            foreach (var i in new[] { e.TryStart, e.TryEnd, e.HandlerStart, e.HandlerEnd, e.FilterStart })
                if (i != null && !set.Contains(i)) throw new InvalidDataException("Dangling EH boundary");
            if (b.Instructions.IndexOf(e.TryStart) >= (e.TryEnd == null ? b.Instructions.Count : b.Instructions.IndexOf(e.TryEnd)))
                throw new InvalidDataException("Empty/reversed try region");
        }
        if (!MaxStackCalculator.GetMaxStack(b.Instructions, b.ExceptionHandlers, out _))
            throw new InvalidDataException("IL stack verification failed: " + m.FullName);
    }

    // Retarget every edge, including exception region boundaries, when removing a range.
    public static void Replace(CilBody body, int start, int count, params Instruction[] replacement)
    {
        var removed = body.Instructions.Skip(start).Take(count).ToHashSet();
        var next = replacement.FirstOrDefault() ?? body.Instructions.Skip(start + count).FirstOrDefault();
        Instruction? Rebind(Instruction? i) => i != null && removed.Contains(i) ? next : i;
        foreach (var i in body.Instructions)
        {
            if (i.Operand is Instruction branch) i.Operand = Rebind(branch)!;
            else if (i.Operand is IList<Instruction> branches) i.Operand = branches.Select(b => Rebind(b)!).ToArray();
        }
        foreach (var e in body.ExceptionHandlers)
        {
            e.TryStart = Rebind(e.TryStart); e.TryEnd = Rebind(e.TryEnd);
            e.HandlerStart = Rebind(e.HandlerStart); e.HandlerEnd = Rebind(e.HandlerEnd); e.FilterStart = Rebind(e.FilterStart);
        }
        for (int n = 0; n < count; n++) body.Instructions.RemoveAt(start);
        for (int n = 0; n < replacement.Length; n++) body.Instructions.Insert(start + n, replacement[n]);
    }
}
