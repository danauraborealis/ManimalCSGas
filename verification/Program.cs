using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using CSGas;
using CSGas.Router;
using Mono.Cecil;
using Mono.Cecil.Cil;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

if (args.Length != 2) throw new ArgumentException("Usage: verification <SPT install> <CSGas repo>");
var meta = new ModMetadata();
Check(meta.Version.ToString() == "1.1.0", "Release version");
Check(meta.SptVersion.IsSatisfied("4.1.5") && !meta.SptVersion.IsSatisfied("4.0.13") && !meta.SptVersion.IsSatisfied("4.2.0"), "SPT compatibility range");
Check(meta.ModDependencies!["com.wtt.commonlib"].IsSatisfied("3.0.6") && !meta.ModDependencies["com.wtt.commonlib"].IsSatisfied("2.0.20"), "CommonLib floor");
Check(typeof(CsGasMasksRouter).GetCustomAttribute<Injectable>()!.TypePriority == OnLoadOrder.Routers + 1, "Custom route priority");
var store = new CsGasMaskConfigStore();
await store.LoadAsync(CancellationToken.None);
using (var json = JsonDocument.Parse(store.Json)) Check(json.RootElement.GetProperty("fullMasks").GetArrayLength() > 0, "Missing config retains mask defaults");
try { await store.LoadAsync(new CancellationToken(true)); throw new Exception("Cancellation was swallowed"); }
catch (OperationCanceledException) { Console.WriteLine("PASS Config cancellation propagates"); }

var clientPath = Path.Combine(args[1], "CSGasClient", "bin", "Release", "netstandard2.1", "CSGasClient.dll");
using var client = AssemblyDefinition.ReadAssembly(clientPath);
using var game = AssemblyDefinition.ReadAssembly(Path.Combine(args[0], "EscapeFromTarkov_Data", "Managed", "Assembly-CSharp.dll"));
var types = AllTypes(game.MainModule.Types).ToDictionary(t => t.FullName);
int count = 0;
foreach (var patch in AllTypes(client.MainModule.Types))
{
    var getter = patch.Methods.FirstOrDefault(m => m.Name == "GetTargetMethod");
    if (getter == null) continue;
    var il = getter.Body.Instructions;
    if (il.Any(i => i.OpCode == OpCodes.Ldstr && ((string)i.Operand).StartsWith("Fika."))) { Console.WriteLine("SKIP Fika target (no compatible install)"); continue; }
    var tokens = il.Where(i => i.OpCode == OpCodes.Ldtoken).Select(i => i.Operand).OfType<TypeReference>().ToArray();
    var methodName = il.First(i => i.OpCode == OpCodes.Ldstr).Operand.ToString()!;
    var type = types[tokens[0].FullName];
    var candidates = new List<MethodDefinition>();
    for (var current = type; current != null; current = current.BaseType != null ? types.GetValueOrDefault(current.BaseType.FullName) : null)
    {
        candidates = current.Methods.Where(m => m.Name == methodName).ToList();
        if (tokens.Length > 1) candidates = candidates.Where(m => m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(tokens.Skip(1).Select(t => t.FullName))).ToList();
        if (candidates.Count > 0) break;
    }
    Check(candidates.Count == 1, patch.Name + " resolves uniquely");
    foreach (var hook in patch.Methods.Where(m => m.CustomAttributes.Any(a => a.AttributeType.Name is "PatchPrefixAttribute" or "PatchPostfixAttribute" or "PatchFinalizerAttribute" or "PatchPrefix" or "PatchPostfix" or "PatchFinalizer")))
    foreach (var p in hook.Parameters)
        if (!p.Name.StartsWith("__")) Check(candidates[0].Parameters.Any(q => q.Name == p.Name), patch.Name + " binds " + p.Name);
    count++;
}
Check(count >= 10, "All non-Fika module patches checked");
Check(types["EFT.Grenade"].Fields.Any(f => f.Name == "_collider"), "Grenade collider reflection target");
var hurt = types["EFT.Player"].Methods.Single(m => m.Name == "OnAudioHealthApplyDamage");
Check(hurt.Parameters.Count == 3, "Hurt audio reflection target");
var health = types["EFT.HealthSystem.ActiveHealthController"];
Check(health.NestedTypes.Any(t => t.Name == "Tremor") && health.Methods.Count(m => m.Name == "AddEffect" && m.HasGenericParameters && m.Parameters.Count == 6) == 1, "Tremor reflection targets");

using var zip = ZipFile.OpenRead(Path.Combine(args[1], "Manimal-CSGas-1.1.0.zip"));
var entries = zip.Entries.ToDictionary(e => e.FullName.Replace('\\', '/'));
foreach (var path in new[] { "BepInEx/plugins/CSGas/CSGasClient.dll", "SPT_Runtime/user/mods/CSGasServer/CSGasServer.dll", "SPT_Runtime/user/mods/CSGasServer/bundles.json", "SPT_Runtime/user/mods/CSGasServer/config/gas_masks.json", "LICENSE", "README.md" }) Check(entries.ContainsKey(path), "Archive contains " + path);
Check(!entries.Keys.Any(p => p.StartsWith("SPT/") || p.Contains("WTT-ClientCommonLib.dll")), "Archive excludes old layout and dependency DLLs");
foreach (var file in Directory.GetFiles(Path.Combine(args[1], "ServerModFiles"), "*", SearchOption.AllDirectories))
{
    var key = "SPT_Runtime/user/mods/CSGasServer/" + Path.GetRelativePath(Path.Combine(args[1], "ServerModFiles"), file).Replace('\\', '/');
    using var data = entries[key].Open(); using var source = File.OpenRead(file);
    Check(System.Security.Cryptography.SHA256.HashData(data).SequenceEqual(System.Security.Cryptography.SHA256.HashData(source)), "Packaged data matches " + Path.GetFileName(file));
}
Console.WriteLine("CS Gas SPT 4.1 verification passed.");
static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) { foreach (var t in types) { yield return t; foreach (var n in AllTypes(t.NestedTypes)) yield return n; } }
