using System.Buffers.Binary;
using System.Resources;
using System.Text.Json;
using dnlib.DotNet;
using PclPatcher.Core;

if (args.Length != 2) { Console.Error.WriteLine("Usage: PclPatcher.Audit <exe> <reference-output-directory>"); return 64; }
using var module = ModuleDefMD.Load(File.ReadAllBytes(args[0]));
Directory.CreateDirectory(args[1]);
var extracted = new List<string>();
foreach (var resource in module.Resources.OfType<EmbeddedResource>().Where(r => r.Name.String.EndsWith(".resources", StringComparison.Ordinal)))
{
    using var stream = new MemoryStream(resource.CreateReader().ToArray());
    using var reader = new ResourceReader(stream);
    var iterator = reader.GetEnumerator();
    while (iterator.MoveNext())
    {
        // Read raw byte-array resources; never deserialize arbitrary resource object types.
        reader.GetResourceData((string)iterator.Key, out var type, out var raw);
        if (type != "ResourceTypeCode.ByteArray" || raw.Length < 4) continue;
        int size = BinaryPrimitives.ReadInt32LittleEndian(raw);
        if (size < 2 || size != raw.Length - 4 || raw[4] != 'M' || raw[5] != 'Z') continue;
        var bytes = raw.AsSpan(4).ToArray();
        ModuleDefMD dll;
        try { dll = ModuleDefMD.Load(bytes); }
        catch (BadImageFormatException) { continue; } // Native libwebp is not a CLR reference.
        using (dll)
        {
            var name = dll.Assembly?.Name.String;
            if (name == null || name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\')) throw new InvalidDataException("Invalid embedded assembly name");
            File.WriteAllBytes(Path.Combine(args[1], name + ".dll"), bytes);
            extracted.Add(dll.Assembly!.FullName);
        }
    }
}
Console.WriteLine(JsonSerializer.Serialize(new { assembly = module.Assembly.FullName, methods = Il.Methods(module).Count(), types = module.GetTypes().Count(), references = extracted }, PatchFile.JsonOptions));
return 0;
