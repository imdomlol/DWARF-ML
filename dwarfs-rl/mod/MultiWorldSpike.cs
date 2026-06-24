using System;
using System.Reflection;

namespace DwarfsMod
{
    // Path C feasibility spike (docs/HEADLESS.md). Gated by DWARFS_BRIDGE_C_SPIKE=1,
    // runs once. Once the primary game is up, try to stand up a SECOND Game1 in the
    // same process with NO real graphics device and drive it through
    // Initialize -> GenerateLevel -> a few Update() frames -> UpdateGamefield,
    // logging exactly where each step throws.
    //
    // What we already know from the decompile: the world data (resources, xCity,
    // lDwarf, lEnemy, xDifficulty) is field-initialized so it exists right after
    // construction; xGameMap / xSoundSystem / Input are set up in
    // Initialize/LoadContent, which need a device. The sim itself (UpdateGamefield)
    // touches no device. The open questions this answers empirically:
    //   1. does XNA even let a second Game exist in one process?
    //   2. how far does a deviceless world get before it needs the device/content
    //      we want to skip (i.e. exactly what Path D would have to stub)?
    public static class MultiWorldSpike
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        static bool ran;

        public static void MaybeRun(object primaryGame)
        {
            if (ran) return;
            if (Environment.GetEnvironmentVariable("DWARFS_BRIDGE_C_SPIKE") != "1") return;
            ran = true;
            try { Run(primaryGame); }
            catch (Exception e) { Log("harness blew up: " + Flatten(e)); }
        }

        static void Run(object primary)
        {
            Log("==== Path C spike: second deviceless Game1 ====");
            Type tGame = primary.GetType();
            Log("game type: " + tGame.FullName);

            // reuse the primary's steam wrappers so we don't trip over null Steam
            object wrap = ReadField(primary, "_xSteamWrap");
            object stats = ReadField(primary, "_xSteamStats");
            Log("primary steam: wrap=" + (wrap != null) + " stats=" + (stats != null));

            // 1. can a second Game1 even be constructed in-process?
            object game2;
            try
            {
                ConstructorInfo ctor = FindTwoArgCtor(tGame);
                if (ctor == null) { Log("construct: no 2-arg ctor found, abort"); return; }
                game2 = ctor.Invoke(new object[] { wrap, stats });
                Log("construct: OK -- a second Game1 exists in-process");
            }
            catch (Exception e) { Log("construct THREW (XNA may forbid two Games): " + Flatten(e)); return; }

            // 2. Initialize (expected to reach the device via Content.Load fonts)
            InvokeNoArg(game2, "Initialize", "Initialize");

            // 3. GenerateLevel (data per the decompile, but assumes xGameMap/xSoundSystem)
            InvokeNoArg(game2, "GenerateLevel", "GenerateLevel");

            // 4. tick Update() a few frames
            object gameTime;
            MethodInfo mUpdate = FindUpdate(tGame, out gameTime);
            if (mUpdate == null) { Log("Update: method not found, skipping ticks"); }
            else
            {
                for (int i = 0; i < 3; i++)
                {
                    try { mUpdate.Invoke(game2, new object[] { gameTime }); Log("Update #" + (i + 1) + ": OK"); }
                    catch (Exception e) { Log("Update #" + (i + 1) + " THREW: " + Flatten(e)); break; }
                }
            }

            // 5. the sim in isolation -- the part the decompile says needs no device
            InvokeNoArg(game2, "UpdateGamefield", "UpdateGamefield (sim only)");

            Log("==== Path C spike: done ====");
        }

        // ---- reflection helpers ----

        static ConstructorInfo FindTwoArgCtor(Type t)
        {
            foreach (ConstructorInfo c in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (c.GetParameters().Length == 2) return c;
            return null;
        }

        static MethodInfo FindUpdate(Type t, out object gameTime)
        {
            gameTime = null;
            MethodInfo mUpdate = null;
            foreach (MethodInfo m in t.GetMethods(Any))
                if (m.Name == "Update" && m.GetParameters().Length == 1) { mUpdate = m; break; }
            if (mUpdate == null) return null;
            Type tGameTime = mUpdate.GetParameters()[0].ParameterType;
            try { gameTime = Activator.CreateInstance(tGameTime); }
            catch
            {
                // fall back to a TimeSpan-based ctor if GameTime has no default one
                foreach (ConstructorInfo c in tGameTime.GetConstructors())
                {
                    ParameterInfo[] ps = c.GetParameters();
                    bool allTimeSpan = ps.Length > 0;
                    foreach (ParameterInfo p in ps) if (p.ParameterType != typeof(TimeSpan)) allTimeSpan = false;
                    if (allTimeSpan)
                    {
                        object[] args = new object[ps.Length];
                        for (int i = 0; i < ps.Length; i++) args[i] = TimeSpan.Zero;
                        try { gameTime = c.Invoke(args); break; } catch { }
                    }
                }
            }
            return mUpdate;
        }

        static void InvokeNoArg(object obj, string name, string label)
        {
            try
            {
                MethodInfo m = obj.GetType().GetMethod(name, Any, null, Type.EmptyTypes, null);
                if (m == null) { Log(label + ": method not found"); return; }
                m.Invoke(obj, null);
                Log(label + ": OK");
            }
            catch (Exception e) { Log(label + " THREW: " + Flatten(e)); }
        }

        static object ReadField(object obj, string name)
        {
            try
            {
                FieldInfo f = obj.GetType().GetField(name, Any);
                return f == null ? null : f.GetValue(obj);
            }
            catch { return null; }
        }

        // unwrap reflection wrappers down to the real exception + a few stack frames
        static string Flatten(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            string s = e.GetType().Name + ": " + e.Message;
            if (!string.IsNullOrEmpty(e.StackTrace))
            {
                string[] lines = e.StackTrace.Replace("\r", "").Split('\n');
                int n = Math.Min(5, lines.Length);
                for (int i = 0; i < n; i++) s += "\n      " + lines[i].Trim();
            }
            return s;
        }

        static void Log(string msg) { Bridge.LogLine("spike: " + msg); }
    }
}
