using System;
using System.Collections.Generic;
using System.Reflection;

namespace DwarfsMod
{
    // builds and drives "detached" worlds: extra Game1 instances that share the
    // host's already-loaded device/content but own all their own per-world state,
    // and that the host loop ticks by hand instead of XNA driving them. this is
    // the spike's proof-of-concept pattern (mod/MultiWorldSpike.cs) promoted to
    // production and split so a world can be rebuilt on every RESET.
    //
    // all reflection is bound once off the Game1 type (handles are type-level, so
    // they work for every instance) the first time a primary is handed in.
    internal static class WorldSim
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        const BindingFlags Pub = BindingFlags.Public | BindingFlags.Instance;

        // the per-world entity lists -- exactly the ones Game1.ClearGame() clears.
        // these MUST be reallocated per world so the game's own ClearGame().Clear()
        // hits our lists, not the host's, and so the sim mutates our state alone.
        // every OTHER List<> field on Game1 (death-tip textures, high-score tables,
        // input buffers, ...) is shared read-only content -- reallocating those to
        // empty is what used to crash death handling (SelectToolTip indexing an
        // emptied txWaterDeathTips), so we leave them sharing the host's copies.
        static readonly string[] EntityLists =
        {
            "lArrow", "lDwarf", "lCave", "lDuel", "lDynamicEffects", "lDynamite",
            "lEnemy", "lHole", "lGrowingGrass", "lLavaFront", "lLavaMark", "lMine",
            "lPointFront", "lStaticEffects", "lStoneWall", "lWallMerge", "lWaterFront",
            "lTreasure", "lWarning", "lDisarmedWaterSource", "lDisarmedLavaSource",
            "lParticles", "lParticlesGround", "lOutposts", "lDynamicMaps",
        };

        // building a level (ClearGame + GenerateLevel) reaches into shared infra
        // -- the sound system, texture handler -- in ways per-frame stepping does
        // not, so two worlds generating at once can corrupt each other. resets are
        // rare, so serialize generation behind this lock; STEP stays fully parallel.
        static readonly object genLock = new object();

        static bool bound, bindFailed;
        static Type tGame;
        static ConstructorInfo ctor;
        static FieldInfo fWrap, fStats;
        static FieldInfo fGameMap, fDifficulty, fRandomizer, fGameState, fMainMenu, fResources;
        static FieldInfo fEnTime, fRush, fDark;
        static ConstructorInfo mapCtor;
        static MethodInfo mSetDifficulty, mClearGame, mGenerateLevel;
        static MethodInfo mUpdateGamefield, mNonSpeedEvents, mUpdateDynamicMaps, mUpdateCampaignQuests;
        static MethodInfo mCheckAccount, mBalanceAccount;

        static bool Bind(object primary)
        {
            if (bound) return true;
            if (bindFailed) return false;
            try
            {
                tGame = primary.GetType();
                foreach (ConstructorInfo c in tGame.GetConstructors(Any))
                    if (c.GetParameters().Length == 2) { ctor = c; break; }

                fWrap = tGame.GetField("_xSteamWrap", Any);
                fStats = tGame.GetField("_xSteamStats", Any);
                fGameMap = tGame.GetField("xGameMap", Any);
                fDifficulty = tGame.GetField("xDifficulty", Any);
                fRandomizer = tGame.GetField("randomizer", Any);
                fGameState = tGame.GetField("iGameState", Any);
                fMainMenu = tGame.GetField("iMainMenu", Any);
                fResources = tGame.GetField("resources", Any);

                mapCtor = fGameMap.FieldType.GetConstructor(new[] { typeof(int), typeof(int) });
                fEnTime = fDifficulty.FieldType.GetField("m_enTime", Pub);
                fRush = fDifficulty.FieldType.GetField("m_bRushMode", Pub);
                fDark = fDifficulty.FieldType.GetField("m_bDarkMode", Pub);

                mSetDifficulty = tGame.GetMethod("SetDifficulty", Any, null, new[] { typeof(string) }, null);
                mClearGame = tGame.GetMethod("ClearGame", Any, null, Type.EmptyTypes, null);
                mGenerateLevel = tGame.GetMethod("GenerateLevel", Any, null, Type.EmptyTypes, null);
                mUpdateGamefield = tGame.GetMethod("UpdateGamefield", Any, null, Type.EmptyTypes, null);
                mNonSpeedEvents = tGame.GetMethod("NonSpeedEvents", Any, null, Type.EmptyTypes, null);
                mUpdateDynamicMaps = tGame.GetMethod("UpdateDynamicMaps", Any, null, Type.EmptyTypes, null);
                mUpdateCampaignQuests = tGame.GetMethod("Update_CampaignQuests", Any, null, Type.EmptyTypes, null);

                Type tRes = fResources.FieldType;
                mCheckAccount = tRes.GetMethod("CheckTheAccount", Any, null, Type.EmptyTypes, null);
                mBalanceAccount = tRes.GetMethod("BalanceTheAccount", Any, null, Type.EmptyTypes, null);

                bound = ctor != null && fGameMap != null && mSetDifficulty != null
                    && mClearGame != null && mGenerateLevel != null && mUpdateGamefield != null;
                if (!bound) bindFailed = true;
                return bound;
            }
            catch (Exception e)
            {
                bindFailed = true;
                Bridge.LogLine("worldsim bind threw: " + e.Message);
                return false;
            }
        }

        // construct one extra Game1 that shares the host's loaded infrastructure
        // (device, sprite batch, sound, textures, fonts -- read-only at sim time)
        // but owns its own mutable state. the level itself is built later, per
        // RESET, by BuildLevel. returns null if the build fails.
        internal static object CreateDetached(object primary)
        {
            if (!Bind(primary)) return null;
            try
            {
                object wrap = fWrap != null ? fWrap.GetValue(primary) : null;
                object stats = fStats != null ? fStats.GetValue(primary) : null;
                object g = ctor.Invoke(new object[] { wrap, stats });

                // share everything the host loaded...
                foreach (FieldInfo fi in tGame.GetFields(Any))
                {
                    try { fi.SetValue(g, fi.GetValue(primary)); } catch { }
                }
                // ...then give this world its own copy of just the entity lists the
                // game treats as per-game state (the ones ClearGame clears), leaving
                // shared content lists alone
                foreach (string name in EntityLists) ReallocFresh(g, name);
                // and fresh per-world controllers + a placeholder map (SetDifficulty
                // resizes it to the difficulty's real playfield on the first RESET)
                ReallocFresh(g, "xTowerDefense");
                ReallocFresh(g, "xPlayerInteraction");
                ReallocFresh(g, "xDifficulty");
                if (mapCtor != null)
                    fGameMap.SetValue(g, mapCtor.Invoke(new object[] { 1000, 1000 }));
                return g;
            }
            catch (Exception e)
            {
                Bridge.LogLine("worldsim CreateDetached threw: " + Flatten(e));
                return null;
            }
        }

        // build (or rebuild) the arcade level on a detached world. mirrors the
        // host's menu path: SetDifficulty sizes the map, then ClearGame +
        // GenerateLevel build it. reseeding right before GenerateLevel matches the
        // host's BeforeGenerateLevel hook, so a detached world with a given seed is
        // byte-identical to a normal game with that seed.
        internal static void BuildLevel(object game, string difficulty, int timeMode,
                                        int seed, bool hasSeed)
        {
            if (!Bind(game)) return;
            // serialize the whole level build: SetDifficulty/ClearGame/GenerateLevel
            // all touch shared infra, and worlds resetting concurrently (which is
            // exactly what a vec env does at startup) would otherwise race
            lock (genLock)
            {
                mSetDifficulty.Invoke(game, new object[] { difficulty });

                object diff = fDifficulty.GetValue(game);
                if (diff != null)
                {
                    if (fEnTime != null) fEnTime.SetValue(diff, Enum.ToObject(fEnTime.FieldType, timeMode));
                    if (fRush != null) fRush.SetValue(diff, false);
                    if (fDark != null) fDark.SetValue(diff, false);
                }

                if (hasSeed) fRandomizer.SetValue(game, new Random(seed));
                mClearGame.Invoke(game, null);
                if (hasSeed) fRandomizer.SetValue(game, new Random(seed));
                mGenerateLevel.Invoke(game, null);

                if (fGameState != null) fGameState.SetValue(game, 1);
                if (fMainMenu != null) fMainMenu.SetValue(game, 0);
            }
        }

        // advance one detached world by a single frame: the real arcade Update
        // sequence with the economy, exactly what XNA runs for the host each tick.
        // (enemy combat is included via UpdateGamefield; monster caves are just
        // rare, see docs/MULTIWORLD.md.)
        internal static void DriveFrame(object game)
        {
            object res = fResources.GetValue(game);
            if (res != null && mCheckAccount != null) mCheckAccount.Invoke(res, null);
            mUpdateGamefield.Invoke(game, null);
            if (mNonSpeedEvents != null) mNonSpeedEvents.Invoke(game, null);
            if (mUpdateDynamicMaps != null) mUpdateDynamicMaps.Invoke(game, null);
            if (mUpdateCampaignQuests != null) mUpdateCampaignQuests.Invoke(game, null);
            if (res != null && mBalanceAccount != null) mBalanceAccount.Invoke(res, null);
        }

        static void ReallocFresh(object g, string name)
        {
            try
            {
                FieldInfo f = tGame.GetField(name, Any);
                if (f == null) return;
                ConstructorInfo c = f.FieldType.GetConstructor(Type.EmptyTypes);
                if (c != null) f.SetValue(g, c.Invoke(null));
            }
            catch { }
        }

        static string Flatten(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e.GetType().Name + ": " + e.Message;
        }
    }
}
