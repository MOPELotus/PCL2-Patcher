using System.Text.Json;
using dnlib.DotNet;

namespace PclPatcher.Core;

internal sealed record Integrity(Dictionary<string, string> Methods, Dictionary<string, string> Resources, string Metadata)
{
    public static Integrity Capture(ModuleDef m) => new(
        Il.Methods(m).ToDictionary(x => x.FullName, x => Il.Fingerprint(x)),
        m.Resources.ToDictionary(r => r.Name.String, r => r is EmbeddedResource e ? Il.Hash(e.CreateReader().ToArray()) : r.ResourceType + ":" + r.Name),
        JsonSerializer.Serialize(new
        {
            Assembly = m.Assembly.FullName, Module = m.Name.String, m.Kind, m.Machine, m.Cor20HeaderFlags,
            EntryPoint = m.EntryPoint?.FullName,
            AssemblyRefs = m.GetAssemblyRefs().Select(r => r.FullName).Order().ToArray(),
            Types = m.GetTypes().Select(t => new
            {
                t.FullName, t.Attributes, Base = t.BaseType?.FullName,
                Interfaces = t.Interfaces.Select(i => i.Interface.FullName).ToArray(),
                Fields = t.Fields.Select(f => new { f.FullName, f.Attributes, Constant = f.Constant?.Value?.ToString(), Initial = f.InitialValue == null ? null : Il.Hash(f.InitialValue) }).ToArray(),
                Methods = t.Methods.Select(x => new { x.FullName, x.Attributes, x.ImplAttributes }).ToArray()
            }).ToArray()
        }));

    public void Verify(ModuleDef result, IReadOnlySet<string> changed)
    {
        var after = Capture(result);
        if (Metadata != after.Metadata || !Resources.OrderBy(k => k.Key).SequenceEqual(after.Resources.OrderBy(k => k.Key)))
            throw new InvalidDataException("Assembly metadata or resources changed unexpectedly");
        foreach (var (name, body) in Methods)
            if (!changed.Contains(name) && (!after.Methods.TryGetValue(name, out var actual) || actual != body))
                throw new InvalidDataException("Unexpected change outside patch scope: " + name);
    }
}
