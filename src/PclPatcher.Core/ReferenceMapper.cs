using dnlib.DotNet;

namespace PclPatcher.Core;

// Every restored reference must bind to an existing compatible target definition.
// In particular, never create a reference back to the donor EXE.
internal sealed class ReferenceMapper(ModuleDef target, ModuleDef donor) : ImportMapper
{
    public override ITypeDefOrRef Map(ITypeDefOrRef source)
    {
        if (source is TypeDef d && d.Module == donor)
            return target.GetTypes().SingleOrDefault(t => t.FullName == d.FullName)
                ?? throw new InvalidDataException("Missing target type: " + source.FullName);
        return null!;
    }
    public override IMethod Map(MethodDef source)
    {
        if (source.Module != donor) return null!;
        var type = (TypeDef)Map(source.DeclaringType);
        var exact = type.Methods.SingleOrDefault(m => m.Name == source.Name && m.IsStatic == source.IsStatic && m.MethodSig.ToString() == source.MethodSig.ToString());
        if (exact != null)
        {
            if (source.Name.String.StartsWith("_Lambda$", StringComparison.Ordinal) && Il.Fingerprint(source, true) != Il.Fingerprint(exact, true))
                throw new InvalidDataException("Changed closure semantics: " + source.FullName);
            return exact;
        }
        if (source.Name.String.StartsWith("_Lambda$", StringComparison.Ordinal))
        {
            var candidates = type.Methods.Where(m => m.Name.String.StartsWith("_Lambda$", StringComparison.Ordinal) &&
                m.IsStatic == source.IsStatic && m.MethodSig.ToString() == source.MethodSig.ToString() && Il.Fingerprint(source, true) == Il.Fingerprint(m, true)).ToArray();
            // Equivalent adapters may be emitted twice for the same source handler.
            if (candidates.Length > 0) return candidates[0];
        }
        throw new InvalidDataException("Missing/ambiguous target method: " + source.FullName);
    }
    public override MemberRef Map(MemberRef source)
    {
        if (source.DeclaringType.DefinitionAssembly?.Name != donor.Assembly.Name) return null!;
        // Generic-instance calls are MemberRefs rather than MethodDefs. Validate their
        // underlying definition too, before allowing dnlib to import the instantiated signature.
        var owner = source.DeclaringType.ResolveTypeDef() ?? throw new InvalidDataException("Unresolved member owner: " + source.FullName);
        var comparison = SigComparerOptions.RawSignatureCompare | SigComparerOptions.DontCompareTypeScope;
        if (source.IsMethodRef) Map(owner.FindMethod(source.Name, source.MethodSig, comparison) ?? throw new InvalidDataException("Unresolved donor method: " + source.FullName));
        else Map(owner.FindField(source.Name, source.FieldSig, comparison) ?? throw new InvalidDataException("Unresolved donor field: " + source.FullName));
        return null!;
    }
    public override IField Map(FieldDef source)
    {
        if (source.Module != donor) return null!;
        var type = (TypeDef)Map(source.DeclaringType);
        return type.Fields.SingleOrDefault(f => f.Name == source.Name && f.IsStatic == source.IsStatic && f.FieldType.FullName == source.FieldType.FullName)
            ?? throw new InvalidDataException("Missing target field: " + source.FullName);
    }
}
