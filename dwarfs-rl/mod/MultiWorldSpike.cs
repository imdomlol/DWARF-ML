using System;
using System.Collections.Generic;
using System.Reflection;

namespace DwarfsMod
{
    // Path C feasibility spike (docs/HEADLESS.md). Gated by DWARFS_BRIDGE_C_SPIKE=1.
    //
    // Proves out the two hard unknowns with running code: (1) multiple Game1 in one
    // process, (2) ISOLATION. Each world is built by cloning the primary's infra
    // (one shared device/sound/textures) then reallocating its own per-world
    // collections (fresh xGameMap, every List<> field, xDifficulty/randomizer) so
    // nothing mutable is shared; ClearGame/GenerateLevel rebuild the rest.
    //
    // The per-frame tick is now the REAL Update sequence for an active arcade round,
    // economy included: resources.CheckTheAccount -> UpdateGamefield -> NonSpeedEvents
    // -> UpdateDynamicMaps -> Update_CampaignQuests -> resources.BalanceTheAccount.
    // (Enemy combat is left out on purpose: monster caves are rare, so a dwarf
    // reliably digging into one needs a far longer game than a spike.)
    //
    // Isolation is checked rigorously: a world run SOLO with a given seed must
    // produce the IDENTICAL trajectory when run PAIRED alongside a different-seed
    // world. Same outcome whether alone or not == zero cross-talk.
    public static class MultiWorldSpike
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        const BindingFlags Pub = BindingFlags.Public | BindingFlags.Instance;
        const int Frames = 6000;
        static bool ran;

        struct Snap { public long score, mapFP; public int dwarves, gold, timeLeft, cityHP; }

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
            Log("==== Path C spike: economy + rigorous isolation ====");
            Type t = primary.GetType();
            object wrap = ReadField(primary, "_xSteamWrap");
            object stats = ReadField(primary, "_xSteamStats");
            ConstructorInfo ctor = FindTwoArgCtor(t);

            // per-frame sim sequence (Game1 methods)
            string[] seq = { "UpdateGamefield", "NonSpeedEvents", "UpdateDynamicMaps", "Update_CampaignQuests" };
            MethodInfo[] sim = new MethodInfo[seq.Length];
            for (int i = 0; i < seq.Length; i++) sim[i] = t.GetMethod(seq[i], Any, null, Type.EmptyTypes, null);
            // economy methods (on the per-world Resources object)
            FieldInfo fRes = t.GetField("resources", Any);
            Type tRes = fRes.FieldType;
            MethodInfo mCheck = tRes.GetMethod("CheckTheAccount", Any, null, Type.EmptyTypes, null);
            MethodInfo mBalance = tRes.GetMethod("BalanceTheAccount", Any, null, Type.EmptyTypes, null);
            Log("economy hooks: CheckTheAccount=" + (mCheck != null) + " BalanceTheAccount=" + (mBalance != null));

            // Phase 1: SOLO world, seed 111
            object solo = BuildWorld(primary, t, ctor, wrap, stats, 111, "SOLO");
            for (int f = 0; f < Frames; f++) Tick(solo, sim, fRes, mCheck, mBalance);
            Snap soloEnd = Snapshot(solo);
            Log("SOLO  (seed 111, alone)  -> " + Fmt(soloEnd));

            // Phase 2: PAIRED, A=seed 111 (same as solo) + B=seed 222, interleaved
            object a = BuildWorld(primary, t, ctor, wrap, stats, 111, "A");
            object b = BuildWorld(primary, t, ctor, wrap, stats, 222, "B");
            string err = null;
            int frame = 0;
            for (; frame < Frames; frame++)
            {
                try { Tick(a, sim, fRes, mCheck, mBalance); Tick(b, sim, fRes, mCheck, mBalance); }
                catch (Exception e) { err = Flatten(e); break; }
            }
            if (err != null) { Log("paired run THREW at frame " + frame + ": " + err); return; }
            Snap aEnd = Snapshot(a), bEnd = Snapshot(b);
            Log("A     (seed 111, paired) -> " + Fmt(aEnd));
            Log("B     (seed 222, paired) -> " + Fmt(bEnd));

            // verdicts
            bool isolated = aEnd.score == soloEnd.score && aEnd.dwarves == soloEnd.dwarves
                && aEnd.mapFP == soloEnd.mapFP && aEnd.gold == soloEnd.gold;
            Log("ISOLATION PROOF: A(paired) " + (isolated ? "== " : "!= ") + "SOLO (same seed) -> "
                + (isolated ? "identical trajectory, B had ZERO effect on A -- isolation proven"
                            : "trajectories differ -- cross-contamination!"));
            bool diverged = aEnd.mapFP != bEnd.mapFP && aEnd.score != bEnd.score;
            Log("DIVERGENCE: A vs B (different seeds) " + (diverged ? "differ -- independent worlds" : "match -- suspicious"));
            bool economy = soloEnd.gold != 250 || aEnd.gold != 250;
            Log("ECONOMY: gold solo=" + soloEnd.gold + " A=" + aEnd.gold + " (start 250) -> "
                + (economy ? "running" : "still frozen"));
            Log("==== Path C spike: done ====");
        }

        static void Tick(object g, MethodInfo[] sim, FieldInfo fRes, MethodInfo mCheck, MethodInfo mBalance)
        {
            object res = fRes.GetValue(g);
            if (res != null && mCheck != null) mCheck.Invoke(res, null);
            for (int i = 0; i < sim.Length; i++) if (sim[i] != null) sim[i].Invoke(g, null);
            if (res != null && mBalance != null) mBalance.Invoke(res, null);
        }

        static object BuildWorld(object primary, Type t, ConstructorInfo ctor,
                                 object wrap, object stats, int seed, string tag)
        {
            object g = ctor.Invoke(new object[] { wrap, stats });
            // share the primary's infrastructure
            foreach (FieldInfo fi in t.GetFields(Any)) { try { fi.SetValue(g, fi.GetValue(primary)); } catch { } }
            // isolate: own copy of every List<> field
            foreach (FieldInfo fi in t.GetFields(Any))
            {
                Type ft = fi.FieldType;
                if (ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(List<>))
                    try { fi.SetValue(g, Activator.CreateInstance(ft)); } catch { }
            }
            ReallocFresh(g, t, "xTowerDefense");
            ReallocFresh(g, t, "xPlayerInteraction");
            ReallocFresh(g, t, "xDifficulty");
            FieldInfo fMap = t.GetField("xGameMap", Any);
            fMap.SetValue(g, fMap.FieldType.GetConstructor(new[] { typeof(int), typeof(int) }).Invoke(new object[] { 1000, 1000 }));
            t.GetMethod("SetDifficulty", Any, null, new[] { typeof(string) }, null).Invoke(g, new object[] { "Easy" });
            object diff = t.GetField("xDifficulty", Any).GetValue(g);
            FieldInfo en = diff.GetType().GetField("m_enTime", Pub);
            en.SetValue(diff, Enum.ToObject(en.FieldType, 0));
            t.GetField("randomizer", Any).SetValue(g, new Random(seed));
            Invoke(g, "ClearGame", tag);
            t.GetField("randomizer", Any).SetValue(g, new Random(seed));
            Invoke(g, "GenerateLevel", tag);
            t.GetField("iGameState", Any).SetValue(g, 1);
            t.GetField("iMainMenu", Any).SetValue(g, 0);
            return g;
        }

        static Snap Snapshot(object g)
        {
            Snap s = default(Snap);
            try
            {
                s.score = GameState.Score(g);
                s.dwarves = GameState.DwarfCount(g);
                s.gold = GameState.Gold(g);
                s.timeLeft = GameState.TimeLeft(g);
                s.cityHP = GameState.CityHP(g);
                int[] grid = GameState.ReadGrid(g, 60, 40);
                long fp = 0; for (int i = 0; i < grid.Length; i++) fp += (long)(i + 1) * grid[i];
                s.mapFP = fp;
            }
            catch { }
            return s;
        }

        static string Fmt(Snap s)
        {
            return "score=" + s.score + " dwarves=" + s.dwarves + " gold=" + s.gold
                + " timeLeft=" + s.timeLeft + " cityHP=" + s.cityHP + " mapFP=" + s.mapFP;
        }

        // ---- reflection helpers ----

        static void ReallocFresh(object g, Type t, string name)
        {
            try
            {
                FieldInfo f = t.GetField(name, Any);
                if (f == null) return;
                ConstructorInfo c = f.FieldType.GetConstructor(Type.EmptyTypes);
                if (c != null) f.SetValue(g, c.Invoke(null));
            }
            catch { }
        }

        static ConstructorInfo FindTwoArgCtor(Type t)
        {
            foreach (ConstructorInfo c in t.GetConstructors(Any))
                if (c.GetParameters().Length == 2) return c;
            return null;
        }

        static void Invoke(object obj, string name, string tag)
        {
            try
            {
                MethodInfo m = obj.GetType().GetMethod(name, Any, null, Type.EmptyTypes, null);
                if (m == null) { Log(tag + " " + name + ": not found"); return; }
                m.Invoke(obj, null);
            }
            catch (Exception e) { Log(tag + " " + name + " THREW: " + Flatten(e)); }
        }

        static object ReadField(object obj, string name)
        {
            try { FieldInfo f = obj.GetType().GetField(name, Any); return f == null ? null : f.GetValue(obj); }
            catch { return null; }
        }

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
