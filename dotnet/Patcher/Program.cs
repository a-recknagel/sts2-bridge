// Vendored from sts2-cli's setup.sh (https://github.com/wuhao21/sts2-cli @084d1aa, MIT, (c) 2025 Hao Wu; see
// ../GodotStubs/LICENSE-sts2-cli). Two IL patches to sts2.dll, without which the game's async loop deadlocks headless:
//   1. every nested YieldAwaiter.get_IsCompleted in sts2.dll returns true;
//   2. ActionQueueSynchronizer.WaitUntilQueueIsEmptyOrWaitingOnNonPlayerDrivenAction returns Task.CompletedTask.
// Usage: Patcher <lib/sts2.dll>, on a fresh copy from the game (setup.sh always copies one first).
using Mono.Cecil;
using Mono.Cecil.Cil;

string dll = args[0];
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(dll))!);
var module = ModuleDefinition.ReadModule(dll, new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Deferred });

int patches = 0;
foreach (var method in module.Types.SelectMany(t => t.NestedTypes).SelectMany(n => n.NestedTypes)
             .Where(t => t.Name.Contains("YieldAwaiter") || t.Name == "<>c")
             .SelectMany(t => t.Methods).Where(m => m.Name == "get_IsCompleted" && m.Body != null))
{
    var il = method.Body.GetILProcessor();
    il.Body.Instructions.Clear();
    il.Emit(OpCodes.Ldc_I4_1);
    il.Emit(OpCodes.Ret);
    Console.WriteLine($"  {method.DeclaringType.FullName}.IsCompleted -> true");
    patches++;
}
foreach (var method in module.Types.SelectMany(t => t.Methods)
             .Where(m => m.Name == "WaitUntilQueueIsEmptyOrWaitingOnNonPlayerDrivenAction" && m.Body != null))
{
    var il = method.Body.GetILProcessor();
    il.Body.Instructions.Clear();
    il.Emit(OpCodes.Call, module.ImportReference(typeof(Task).GetProperty(nameof(Task.CompletedTask))!.GetGetMethod()!));
    il.Emit(OpCodes.Ret);
    Console.WriteLine($"  {method.DeclaringType.FullName}.{method.Name} -> Task.CompletedTask");
    patches++;
}
if (patches == 0) throw new InvalidOperationException("no patch sites found; the game changed shape");

module.Write(dll + ".patched");
module.Dispose();
File.Move(dll + ".patched", dll, overwrite: true);
Console.WriteLine($"Applied {patches} patches to {dll}");
