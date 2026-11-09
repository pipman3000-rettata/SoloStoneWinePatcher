using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace SoloStoneWine
{
    /// <summary>
    /// BepInEx 5 preloader patcher for SoloStone.Plugin.dll.
    ///
    /// All patch logic is inside Patch(AssemblyDefinition) and uses Mono.Cecil.
    /// The patched assembly is written back from Finish() after BepInEx has
    /// released the Cecil file handle, because the chainloader later loads the
    /// plugin from disk.  No Harmony, no runtime hooks.
    /// </summary>
    public static class WinePatcher
    {
        private const string TargetName = "SoloStone.Plugin";
        private const string MarkerType = "SoloStoneWine.PatchedMarker";
        private static readonly ManualLogSource Log = Logger.CreateLogSource("SoloStoneWine");

        private static byte[] patchedBytes;
        private static string pluginPath;
        private static bool patchedThisRun;

        public static IEnumerable<string> TargetDLLs
        {
            get { yield return "SoloStone.Plugin.dll"; }
        }

        public static void Initialize()
        {
            Log.LogInfo("[SoloStoneWine] patcher initialized");
        }

        public static void Patch(AssemblyDefinition assembly)
        {
            if (assembly == null || assembly.Name.Name != TargetName)
                return;

            // ------------------------------------------------------------
            // Resolver setup (inline; required so Cecil can serialize the
            // plugin while resolving Assembly-CSharp and SabberStoneCore).
            // ------------------------------------------------------------
            var resolver = assembly.MainModule.AssemblyResolver as BaseAssemblyResolver;
            if (resolver != null)
            {
                if (!string.IsNullOrEmpty(Paths.ManagedPath))
                    resolver.AddSearchDirectory(Paths.ManagedPath);
                if (!string.IsNullOrEmpty(Paths.PluginPath))
                    resolver.AddSearchDirectory(Paths.PluginPath);
                if (!string.IsNullOrEmpty(assembly.MainModule.FileName))
                {
                    string directory = Path.GetDirectoryName(assembly.MainModule.FileName);
                    if (!string.IsNullOrEmpty(directory))
                        resolver.AddSearchDirectory(directory);
                }
            }

            ModuleDefinition module = assembly.MainModule;
            TypeDefinition installerRuntime = module.Types.FirstOrDefault(t => t.Name == "InstallerRuntime");
            TypeDefinition offlineGuard = module.Types.FirstOrDefault(t => t.Name == "OfflineGuard");
            TypeDefinition offlineProbe = module.Types.FirstOrDefault(t => t.Name == "OfflineProbe");

            if (installerRuntime == null || offlineGuard == null || offlineProbe == null)
            {
                Log.LogError("[SoloStoneWine] expected SoloStone types not found; not patching");
                return;
            }

            if (module.Types.Any(t => t.FullName == MarkerType))
            {
                Log.LogInfo("[SoloStoneWine] plugin is already patched; leaving it alone");
                return;
            }

            try
            {
                // --------------------------------------------------------
                // 1. InstallerRuntime.Resolve(...) -> %LOCALAPPDATA%\SoloStone
                // --------------------------------------------------------
                MethodDefinition resolve = installerRuntime.Methods.FirstOrDefault(m =>
                    m.Name == "Resolve" && m.Parameters.Count == 5 && m.HasBody);
                if (resolve == null)
                {
                    Log.LogError("[SoloStoneWine] InstallerRuntime.Resolve(5 args) not found");
                    return;
                }

                resolve.Body.Instructions.Clear();
                resolve.Body.Variables.Clear();
                resolve.Body.ExceptionHandlers.Clear();
                resolve.Body.InitLocals = true;
                resolve.Body.MaxStackSize = 3;
                var rootLocal = new VariableDefinition(module.TypeSystem.String);
                resolve.Body.Variables.Add(rootLocal);

                MethodReference getFolder = module.ImportReference(
                    typeof(Environment).GetMethod("GetFolderPath", new[] { typeof(Environment.SpecialFolder) }));
                MethodReference combine = module.ImportReference(
                    typeof(Path).GetMethod("Combine", new[] { typeof(string), typeof(string) }));
                MethodReference createDirectory = module.ImportReference(
                    typeof(Directory).GetMethod("CreateDirectory", new[] { typeof(string) }));

                ILProcessor resolveIl = resolve.Body.GetILProcessor();
                resolveIl.Emit(OpCodes.Ldc_I4, (int)Environment.SpecialFolder.LocalApplicationData);
                resolveIl.Emit(OpCodes.Call, getFolder);
                resolveIl.Emit(OpCodes.Ldstr, "SoloStone");
                resolveIl.Emit(OpCodes.Call, combine);
                resolveIl.Emit(OpCodes.Stloc, rootLocal);

                resolveIl.Emit(OpCodes.Ldloc, rootLocal);
                resolveIl.Emit(OpCodes.Ldstr, "reports");
                resolveIl.Emit(OpCodes.Call, combine);
                resolveIl.Emit(OpCodes.Call, createDirectory);
                resolveIl.Emit(OpCodes.Pop);

                resolveIl.Emit(OpCodes.Ldloc, rootLocal);
                resolveIl.Emit(OpCodes.Ldstr, "saves");
                resolveIl.Emit(OpCodes.Call, combine);
                resolveIl.Emit(OpCodes.Call, createDirectory);
                resolveIl.Emit(OpCodes.Pop);

                resolveIl.Emit(OpCodes.Ldloc, rootLocal);
                resolveIl.Emit(OpCodes.Ret);

                // --------------------------------------------------------
                // 2. Replace the -hs-native-ui-run selector call with a
                //    fresh Guid.NewGuid().ToString("D") sequence.
                // --------------------------------------------------------
                MethodDefinition resolveHeldLaunch = offlineGuard.Methods.FirstOrDefault(m => m.Name == "ResolveHeldLaunch");
                if (resolveHeldLaunch == null || !resolveHeldLaunch.HasBody)
                {
                    Log.LogError("[SoloStoneWine] OfflineGuard.ResolveHeldLaunch not found");
                    return;
                }

                var instructions = resolveHeldLaunch.Body.Instructions;
                ILProcessor guardIl = resolveHeldLaunch.Body.GetILProcessor();
                bool selectorPatched = false;

                for (int i = 0; i < instructions.Count; i++)
                {
                    if (instructions[i].OpCode != OpCodes.Ldstr ||
                        (string)instructions[i].Operand != "-hs-native-ui-run")
                    {
                        continue;
                    }

                    int callIndex = -1;
                    for (int j = i; j < Math.Min(i + 8, instructions.Count); j++)
                    {
                        if ((instructions[j].OpCode == OpCodes.Call || instructions[j].OpCode == OpCodes.Callvirt) &&
                            instructions[j].Operand is MethodReference selector &&
                            selector.Name == "Selector")
                        {
                            callIndex = j;
                            break;
                        }
                    }

                    if (callIndex < 0)
                        continue;

                    int start = i - 1;
                    if (start < 0)
                        start = i;
                    int count = callIndex - start + 1;
                    for (int j = 0; j < count && start < instructions.Count; j++)
                        instructions.RemoveAt(start);

                    MethodReference newGuid = module.ImportReference(typeof(Guid).GetMethod("NewGuid"));
                    TypeReference guidType = module.ImportReference(typeof(Guid));
                    MethodReference toString = module.ImportReference(
                        typeof(Convert).GetMethod("ToString", new[] { typeof(object) }));

                    instructions.Insert(start, guardIl.Create(OpCodes.Call, newGuid));
                    instructions.Insert(start + 1, guardIl.Create(OpCodes.Box, guidType));
                    instructions.Insert(start + 2, guardIl.Create(OpCodes.Call, toString));
                    selectorPatched = true;
                    break;
                }

                if (!selectorPatched)
                    Log.LogWarning("[SoloStoneWine] -hs-native-ui-run selector call not found");

                // --------------------------------------------------------
                // 3. OfflineProbe.InitializeProbe: TcpClient.Connected -> false
                // --------------------------------------------------------
                MethodDefinition initializeProbe = offlineProbe.Methods.FirstOrDefault(m => m.Name == "InitializeProbe" && m.HasBody);
                if (initializeProbe == null)
                {
                    Log.LogError("[SoloStoneWine] OfflineProbe.InitializeProbe not found");
                    return;
                }

                var probeInstructions = initializeProbe.Body.Instructions;
                var connectedIndices = new List<int>();
                for (int i = 0; i < probeInstructions.Count; i++)
                {
                    if ((probeInstructions[i].OpCode == OpCodes.Call || probeInstructions[i].OpCode == OpCodes.Callvirt) &&
                        probeInstructions[i].Operand is MethodReference method &&
                        method.DeclaringType.Name == "TcpClient" &&
                        method.Name == "get_Connected")
                    {
                        connectedIndices.Add(i);
                    }
                }

                ILProcessor probeIl = initializeProbe.Body.GetILProcessor();
                for (int k = connectedIndices.Count - 1; k >= 0; k--)
                {
                    int index = connectedIndices[k];
                    Instruction instruction = probeInstructions[index];
                    instruction.OpCode = OpCodes.Pop;
                    instruction.Operand = null;
                    probeInstructions.Insert(index + 1, probeIl.Create(OpCodes.Ldc_I4_0));
                }

                // --------------------------------------------------------
                // 4. Marker + serialized bytes
                // --------------------------------------------------------
                module.Types.Add(new TypeDefinition(
                    "SoloStoneWine",
                    "PatchedMarker",
                    TypeAttributes.NotPublic | TypeAttributes.Class | TypeAttributes.Abstract |
                    TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
                    module.TypeSystem.Object));

                pluginPath = assembly.MainModule.FileName;
                using (var stream = new MemoryStream())
                {
                    assembly.Write(stream);
                    patchedBytes = stream.ToArray();
                }
                patchedThisRun = true;
                Log.LogInfo("[SoloStoneWine] patched in memory; will write " + pluginPath);
            }
            catch (Exception ex)
            {
                patchedThisRun = false;
                patchedBytes = null;
                Log.LogError("[SoloStoneWine] patch failed: " + ex);
            }
        }

        /// <summary>
        /// BepInEx calls this after all preloader patches and after the Cecil
        /// file handles are released.  This is the write-back point.
        /// </summary>
        public static void Finish()
        {
            if (!patchedThisRun || patchedBytes == null || string.IsNullOrEmpty(pluginPath))
                return;

            string backup = pluginPath + ".winepatcher.bak";
            try
            {
                File.Copy(pluginPath, backup, true);

                for (int attempt = 0; attempt < 40; attempt++)
                {
                    try
                    {
                        File.WriteAllBytes(pluginPath, patchedBytes);
                        patchedThisRun = false;
                        Log.LogInfo("[SoloStoneWine] wrote patched plugin to " + pluginPath);
                        return;
                    }
                    catch (IOException)
                    {
                        System.Threading.Thread.Sleep(50);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        System.Threading.Thread.Sleep(50);
                    }
                }

                Log.LogError("[SoloStoneWine] could not replace " + pluginPath + " (still locked)");
            }
            catch (Exception ex)
            {
                Log.LogError("[SoloStoneWine] could not write patched plugin: " + ex);
            }
        }
    }
}
