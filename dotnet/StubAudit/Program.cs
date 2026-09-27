// Which Godot members does sts2.dll reference that GodotStubs' GodotSharp.dll does not define, and which game methods use them?
// A missing member throws MissingMethodException when a method calling it is compiled, so each one is a fight, event or
// rest option that dies headless once play reaches it. Most callers sit under MegaCrit.Sts2.Core.Nodes (UI); the ones
// outside it are the ones to read, and sts2-cli-stubs.patch covers those on gameplay paths. Rerun after a game or
// sts2-cli bump:
//
//   W=research/combat_parity/.work/sts2-cli
//   dotnet run --project research/combat_parity/StubAudit -- $W/lib/sts2.dll $W/src/GodotStubs/bin/Debug/net9.0/GodotSharp.dll
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

var game = args[0]; var stubs = args[1];
using var gpe = new PEReader(File.OpenRead(game)); var g = gpe.GetMetadataReader();
using var spe = new PEReader(File.OpenRead(stubs)); var s = spe.GetMetadataReader();

string TypeDefName(MetadataReader r, TypeDefinitionHandle h) {
    var t = r.GetTypeDefinition(h);
    var decl = t.GetDeclaringType();
    var name = r.GetString(t.Name);
    return decl.IsNil ? $"{r.GetString(t.Namespace)}.{name}" : $"{TypeDefName(r, decl)}/{name}";
}
string TypeRefName(MetadataReader r, TypeReferenceHandle h, out bool godot) {
    var t = r.GetTypeReference(h);
    var name = r.GetString(t.Name);
    if (t.ResolutionScope.Kind == HandleKind.TypeReference) { var p = TypeRefName(r, (TypeReferenceHandle)t.ResolutionScope, out godot); return $"{p}/{name}"; }
    godot = t.ResolutionScope.Kind == HandleKind.AssemblyReference && r.GetString(r.GetAssemblyReference((AssemblyReferenceHandle)t.ResolutionScope).Name) == "GodotSharp";
    return $"{r.GetString(t.Namespace)}.{name}";
}
// stub members: type -> set of "name/paramcount" (methods) and "name" (fields); walk base types within stubs
var stubMembers = new Dictionary<string, HashSet<string>>();
var stubBase = new Dictionary<string, string>();
foreach (var th in s.TypeDefinitions) {
    var tn = TypeDefName(s, th); var t = s.GetTypeDefinition(th);
    var set = stubMembers[tn] = new HashSet<string>();
    foreach (var mh in t.GetMethods()) { var m = s.GetMethodDefinition(mh); var ms = m.DecodeSignature(new Sigs(), null); set.Add($"{s.GetString(m.Name)}({Norm(string.Join(",", ms.ParameterTypes))}){Norm(ms.ReturnType)}"); }
    foreach (var fh in t.GetFields()) set.Add(s.GetString(s.GetFieldDefinition(fh).Name));
    if (!t.BaseType.IsNil) {
        stubBase[tn] = t.BaseType.Kind switch {
            HandleKind.TypeDefinition => TypeDefName(s, (TypeDefinitionHandle)t.BaseType),
            HandleKind.TypeReference => TypeRefName(s, (TypeReferenceHandle)t.BaseType, out _),
            _ => null };
    }
}
static string Norm(string x) => System.Text.RegularExpressions.Regex.Replace(x, @"[A-Za-z0-9_.]*[./]([A-Za-z0-9_`]+)", "$1");
bool Has(string type, string key) {
    for (var t = type; t != null; t = stubBase.GetValueOrDefault(t)) if (stubMembers.TryGetValue(t, out var set) && set.Contains(key)) return true;
    return false;
}
// missing memberrefs
var missing = new Dictionary<EntityHandle, string>();
foreach (var mrh in g.MemberReferences) {
    var mr = g.GetMemberReference(mrh);
    if (mr.Parent.Kind != HandleKind.TypeReference) continue;
    var tn = TypeRefName(g, (TypeReferenceHandle)mr.Parent, out bool godot);
    if (!godot) continue;
    var name = g.GetString(mr.Name);
    string key;
    if (mr.GetKind() == MemberReferenceKind.Method) {
        var blob = g.GetBlobReader(mr.Signature); var hdr = blob.ReadSignatureHeader(); if (hdr.IsGeneric) blob.ReadCompressedInteger();
        var msig = mr.DecodeMethodSignature(new Sigs(), null);
        key = $"{name}({Norm(string.Join(",", msig.ParameterTypes))}){Norm(msig.ReturnType)}";
    } else key = name;
    if (!stubMembers.ContainsKey(tn)) { missing[mrh] = $"{tn} (TYPE MISSING)::{key}"; continue; }
    if (!Has(tn, key)) {
        string sig = mr.GetKind() == MemberReferenceKind.Method
            ? ((Func<string>)(() => { var ms = mr.DecodeMethodSignature(new Sigs(), null); return $"{ms.ReturnType} ({string.Join(", ", ms.ParameterTypes)}){(ms.Header.IsInstance ? "" : " static")}"; }))()
            : mr.DecodeFieldSignature(new Sigs(), null);
        missing[mrh] = $"{tn}::{key} :: {sig}";
    }
}
var missingTypes = new HashSet<string>();
foreach (var trh in g.TypeReferences) { var n = TypeRefName(g, trh, out bool gd); if (gd && !stubMembers.ContainsKey(n)) missingTypes.Add(n); }
// IL walk
var oneByte = new OpCode[0x100]; var twoByte = new OpCode[0x100];
foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)) { var op = (OpCode)f.GetValue(null); var v = (ushort)op.Value; if (v < 0x100) oneByte[v] = op; else if ((v & 0xff00) == 0xfe00) twoByte[v & 0xff] = op; }
var uses = new Dictionary<string, SortedSet<string>>();
foreach (var th in g.TypeDefinitions) {
    var tn = TypeDefName(g, th);
    foreach (var mh in g.GetTypeDefinition(th).GetMethods()) {
        var md = g.GetMethodDefinition(mh); if (md.RelativeVirtualAddress == 0) continue;
        var il = gpe.GetMethodBody(md.RelativeVirtualAddress).GetILReader();
        while (il.RemainingBytes > 0) {
            int b = il.ReadByte(); OpCode op = b == 0xfe ? twoByte[il.ReadByte()] : oneByte[b];
            switch (op.OperandType) {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: il.ReadByte(); break;
                case OperandType.InlineVar: il.ReadInt16(); break;
                case OperandType.InlineI8: case OperandType.InlineR: il.ReadInt64(); break;
                case OperandType.InlineSwitch: { int n = il.ReadInt32(); for (int i = 0; i < n; i++) il.ReadInt32(); break; }
                case OperandType.InlineField: case OperandType.InlineMethod: case OperandType.InlineTok: case OperandType.InlineType: {
                    var tok = MetadataTokens.EntityHandle(il.ReadInt32());
                    if (tok.Kind == HandleKind.MethodSpecification) tok = g.GetMethodSpecification((MethodSpecificationHandle)tok).Method;
                    if (tok.Kind == HandleKind.MemberReference && missing.TryGetValue(tok, out var mm)) {
                        if (!uses.TryGetValue(mm, out var set)) uses[mm] = set = new SortedSet<string>();
                        set.Add($"{tn}::{g.GetString(md.Name)}");
                    }
                    break; }
                default: il.ReadInt32(); break;
            }
        }
    }
}
Console.WriteLine($"missing types referenced: {missingTypes.Count}"); foreach (var t in missingTypes.OrderBy(x => x)) Console.WriteLine("  T " + t);
Console.WriteLine($"missing members: {missing.Values.Distinct().Count()}, used from IL: {uses.Count}");
foreach (var (m, set) in uses.OrderBy(kv => kv.Key)) {
    Console.WriteLine($"M {m}  [{set.Count} callers]");
    foreach (var c in set) Console.WriteLine($"    {c}");
}
