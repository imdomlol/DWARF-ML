using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DwarfsLoader
{
    // patches a copy of Dwarfs.exe so it boots our mod and calls into it from
    // the update / input / draw methods, then drops the result in a runnable
    // folder. never touches the real steam install, the patched copy lives in
    // game-patched/ with Content/ junctioned back to the original so we arent
    // copying hundreds of mb of assets around
    internal static class Program
    {
        const string DefaultGameDir =
            @"C:\Program Files (x86)\Steam\steamapps\common\Dwarfs - F2P";

        static int Main(string[] args)
        {
            string gameDir = ArgOr(args, "--game", DefaultGameDir);
            string repo = FindRepoRoot();
            string modDll = ArgOr(args, "--mod",
                Path.Combine(repo, "mod", "bin", "Release", "net35", "DwarfsMod.dll"));
            string outDir = ArgOr(args, "--out", Path.Combine(repo, "game-patched"));

            string srcExe = Path.Combine(gameDir, "Dwarfs.exe");
            if (!File.Exists(srcExe)) { Console.Error.WriteLine("missing game exe: " + srcExe); return 1; }
            if (!File.Exists(modDll)) { Console.Error.WriteLine("missing mod dll (build the mod first): " + modDll); return 1; }

            try
            {
                Patch(gameDir, srcExe, modDll, outDir);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("patch failed: " + e);
                return 2;
            }
            return 0;
        }

        static void Patch(string gameDir, string srcExe, string modDll, string outDir)
        {
            // pull the hook methods out of the mod assembly
            var mod = ModuleDefinition.ReadModule(modDll);
            var bridge = mod.GetType("DwarfsMod.Bridge")
                ?? throw new Exception("DwarfsMod.Bridge not found in the mod dll");

            MethodDefinition Hook(string name) =>
                bridge.Methods.FirstOrDefault(m => m.Name == name)
                ?? throw new Exception("mod is missing Bridge." + name);

            var hBoot = Hook("Boot");
            var hBefore = Hook("BeforeUpdate");
            var hShould = Hook("ShouldRender");
            var hBeforeGen = Hook("BeforeGenerateLevel");
            var hShouldInput = Hook("ShouldReadInput");

            // open the game exe for rewriting
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(gameDir);
            resolver.AddSearchDirectory(Path.GetDirectoryName(modDll));
            var game = ModuleDefinition.ReadModule(srcExe,
                new ReaderParameters { AssemblyResolver = resolver });

            var rBoot = game.ImportReference(hBoot);
            var rBefore = game.ImportReference(hBefore);
            var rShould = game.ImportReference(hShould);
            var rBeforeGen = game.ImportReference(hBeforeGen);
            var rShouldInput = game.ImportReference(hShouldInput);

            // 1) Program.Main, start the bridge before the game spins up
            var main = game.GetType("Dwarves.Program").Methods.First(m => m.Name == "Main");
            var mil = main.Body.GetILProcessor();
            var mFirst = main.Body.Instructions[0];
            mil.InsertBefore(mFirst, mil.Create(OpCodes.Call, rBoot));

            var game1 = game.GetType("Dwarves.Game1");

            // 2) Game1.Update, the lockstep gate goes at the very top
            var update = game1.Methods.First(m => m.Name == "Update" && m.Parameters.Count == 1);
            var uil = update.Body.GetILProcessor();
            var uFirst = update.Body.Instructions[0];
            uil.InsertBefore(uFirst, uil.Create(OpCodes.Ldarg_0));
            uil.InsertBefore(uFirst, uil.Create(OpCodes.Call, rBefore));

            // 3) Game1.GenerateLevel, fires right before the world gets built so
            //    the bridge can reseed the rng and tell that a reset landed
            var genLevel = game1.Methods.First(m => m.Name == "GenerateLevel" && m.Parameters.Count == 0);
            var gil = genLevel.Body.GetILProcessor();
            var gFirst = genLevel.Body.Instructions[0];
            gil.InsertBefore(gFirst, gil.Create(OpCodes.Ldarg_0));
            gil.InsertBefore(gFirst, gil.Create(OpCodes.Call, rBeforeGen));

            // 4) the input readers, skipped entirely while an episode runs so the
            //    human mouse/keyboard cant touch a training game
            foreach (var name in new[] { "UpdateInputWindows", "UpdateInput360" })
            {
                var im = game1.Methods.FirstOrDefault(m => m.Name == name && m.Parameters.Count == 0);
                if (im == null) continue;
                var iil = im.Body.GetILProcessor();
                var iFirst = im.Body.Instructions[0];
                iil.InsertBefore(iFirst, iil.Create(OpCodes.Call, rShouldInput));
                iil.InsertBefore(iFirst, iil.Create(OpCodes.Brtrue, iFirst));
                iil.InsertBefore(iFirst, iil.Create(OpCodes.Ret));
            }

            // 5) Game1.Draw, bail out early when the bridge says were headless
            var draw = game1.Methods.First(m => m.Name == "Draw" && m.Parameters.Count == 1);
            var dil = draw.Body.GetILProcessor();
            var dFirst = draw.Body.Instructions[0];
            dil.InsertBefore(dFirst, dil.Create(OpCodes.Call, rShould));
            dil.InsertBefore(dFirst, dil.Create(OpCodes.Brtrue, dFirst));
            dil.InsertBefore(dFirst, dil.Create(OpCodes.Ret));

            // lay out a runnable copy and write the patched exe into it
            PrepareFolder(gameDir, outDir);
            string outExe = Path.Combine(outDir, "Dwarfs.exe");
            game.Write(outExe);
            File.Copy(modDll, Path.Combine(outDir, Path.GetFileName(modDll)), true);

            Console.WriteLine("patched -> " + outExe);
            Verify(outExe);
        }

        // mirror the game folder into outDir. junction the big Content/ dir, copy
        // the rest and skip Dwarfs.exe since the patched one gets written there
        static void PrepareFolder(string gameDir, string outDir)
        {
            Directory.CreateDirectory(outDir);

            foreach (var dir in Directory.GetDirectories(gameDir))
            {
                string name = Path.GetFileName(dir);
                string dest = Path.Combine(outDir, name);
                if (string.Equals(name, "Content", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(dest)) Junction(dest, dir);
                }
                else
                {
                    CopyDir(dir, dest);
                }
            }

            foreach (var file in Directory.GetFiles(gameDir))
            {
                string name = Path.GetFileName(file);
                if (string.Equals(name, "Dwarfs.exe", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(outDir, name), true);
            }
        }

        static void CopyDir(string src, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
        }

        static void Junction(string link, string target)
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var p = Process.Start(psi);
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new Exception("mklink /J failed (" + p.StandardError.ReadToEnd().Trim() + ")");
        }

        // read the patched exe back and make sure each hook call actually got in
        static void Verify(string exe)
        {
            using (var m = ModuleDefinition.ReadModule(exe))
            {
                Console.WriteLine("verify: Boot={0} BeforeUpdate={1} ShouldRender={2} BeforeGenerateLevel={3} InputGate={4}",
                    HasCall(m, "Dwarves.Program", "Main", "Boot"),
                    HasCall(m, "Dwarves.Game1", "Update", "BeforeUpdate"),
                    HasCall(m, "Dwarves.Game1", "Draw", "ShouldRender"),
                    HasCall(m, "Dwarves.Game1", "GenerateLevel", "BeforeGenerateLevel"),
                    HasCall(m, "Dwarves.Game1", "UpdateInputWindows", "ShouldReadInput"));
            }
        }

        static bool HasCall(ModuleDefinition m, string type, string method, string calleeName)
        {
            var t = m.GetType(type);
            foreach (var me in t.Methods.Where(x => x.Name == method && x.Body != null))
                if (me.Body.Instructions.Any(i =>
                        (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                        i.Operand is MethodReference mr && mr.Name == calleeName))
                    return true;
            return false;
        }

        static string ArgOr(string[] args, string flag, string fallback)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == flag) return args[i + 1];
            return fallback;
        }

        static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "PROTOCOL.md")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
