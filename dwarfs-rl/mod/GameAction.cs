using System;
using System.Collections;
using System.Reflection;

namespace DwarfsMod
{
    // applies agent actions by calling the same internal methods the games own
    // click handlers use, with the same validation in front of them. so the
    // agent cant do anything a human couldnt, no free walls and no building on
    // top of a dwarf. cosmetic stuff (sounds, coin particles, wall decorations)
    // gets skipped, and we also skip the cost escalation thing the game does
    // past Hard difficulty since that only matters on TediHardcore
    public static class GameAction
    {
        // the game is all over the place with method visibility so always search both
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        static bool bound;
        static bool bindFailed;

        static ConstructorInfo vecCtor;
        static MethodInfo mCheckPosition, mIsClear, mIsClearArrow;
        static MethodInfo mDwarfAt, mDwarfAtBuf, mEnemyAt, mEnemyAtBuf, mDynamiteAt;
        static MethodInfo mAddWall, mAddDynamite, mAddArrow;
        static MethodInfo mGetGold, mAddBonanis;
        static FieldInfo fResources, fCostWall, fCostDynamite, fCostArrow, fCostArrowStart;

        // force both reflection binds up front, on one thread, before the
        // multi-world driver starts ticking worlds in parallel -- so the worker
        // threads only ever read these caches, never race to populate them
        public static void Warmup(object game)
        {
            Bind(game);
            BindEx(game);
        }

        static bool Bind(object game)
        {
            if (bound) return true;
            if (bindFailed) return false;
            try
            {
                Type g = game.GetType();

                fResources = g.GetField("resources", Any);
                Type tRes = fResources.FieldType;
                mGetGold = tRes.GetMethod("GetGold", Any);
                mAddBonanis = tRes.GetMethod("AddBonanis", Any);

                Type tVec = g.GetField("xCity", Any).FieldType
                    .GetField("m_v2Position", Any).FieldType;
                vecCtor = tVec.GetConstructor(new[] { typeof(float), typeof(float) });

                mCheckPosition = g.GetMethod("CheckPosition", Any, null,
                    new[] { tVec, typeof(int), typeof(string) }, null);
                mIsClear = g.GetMethod("SquareCheck_IsClear", Any, null, new[] { tVec }, null);
                mDwarfAt = g.GetMethod("Dwarf_GetIndex", Any, null, new[] { tVec }, null);
                mDwarfAtBuf = g.GetMethod("Dwarf_GetIndex", Any, null, new[] { tVec, typeof(bool) }, null);
                mEnemyAt = g.GetMethod("Enemy_GetIndex", Any, null, new[] { tVec }, null);
                mEnemyAtBuf = g.GetMethod("Enemy_GetIndex", Any, null, new[] { tVec, typeof(bool) }, null);
                mDynamiteAt = g.GetMethod("Dynamite_GetIndex", Any, null, new[] { tVec }, null);
                mAddWall = g.GetMethod("AddElement_Wall", Any, null, new[] { tVec }, null);
                mAddDynamite = g.GetMethod("AddElement_Dynamite", Any, null, new[] { tVec }, null);
                mIsClearArrow = g.GetMethod("SquareCheck_IsClear", Any, null,
                    new[] { tVec, typeof(string) }, null);
                mAddArrow = g.GetMethod("AddElement_Arrow", Any, null,
                    new[] { tVec, typeof(int), typeof(string), typeof(int), typeof(string), typeof(int) }, null);

                fCostWall = g.GetField("ciCostStoneWall", Any);
                fCostDynamite = g.GetField("ciCostDynamite", Any);
                fCostArrow = g.GetField("ciCostGreenArrow", Any);
                fCostArrowStart = g.GetField("ciCostGreenArrowStart", Any);

                string missing = "";
                if (vecCtor == null) missing += " vecCtor";
                if (mCheckPosition == null) missing += " CheckPosition";
                if (mIsClear == null) missing += " SquareCheck_IsClear";
                if (mDwarfAt == null) missing += " Dwarf_GetIndex";
                if (mDwarfAtBuf == null) missing += " Dwarf_GetIndex(buf)";
                if (mEnemyAt == null) missing += " Enemy_GetIndex";
                if (mEnemyAtBuf == null) missing += " Enemy_GetIndex(buf)";
                if (mDynamiteAt == null) missing += " Dynamite_GetIndex";
                if (mAddWall == null) missing += " AddElement_Wall";
                if (mAddDynamite == null) missing += " AddElement_Dynamite";
                if (mGetGold == null) missing += " GetGold";
                if (mAddBonanis == null) missing += " AddBonanis";
                if (fCostWall == null) missing += " ciCostStoneWall";
                if (fCostDynamite == null) missing += " ciCostDynamite";
                if (mIsClearArrow == null) missing += " SquareCheck_IsClear(case)";
                if (mAddArrow == null) missing += " AddElement_Arrow";
                if (fCostArrow == null) missing += " ciCostGreenArrow";
                if (fCostArrowStart == null) missing += " ciCostGreenArrowStart";

                bound = missing.Length == 0;
                if (!bound)
                {
                    bindFailed = true;
                    Bridge.LogLine("GameAction bind missing:" + missing);
                }
                return bound;
            }
            catch (Exception e)
            {
                bindFailed = true;
                Bridge.LogLine("GameAction bind threw: " + e.Message);
                return false;
            }
        }

        // mirrors the WallMode click handler. needs an empty cave, square clear,
        // enough gold and nothing standing there
        public static bool PlaceWall(object game, int x, int y)
        {
            if (!Bind(game)) return false;
            try
            {
                object pos = vecCtor.Invoke(new object[] { (float)x, (float)y });
                object res = fResources.GetValue(game);
                int cost = (int)fCostWall.GetValue(game);

                string why = null;
                string spot = (string)mCheckPosition.Invoke(game, new[] { pos, (object)0, (object)"" });
                if (spot != "Empty Cave") why = "spot is '" + spot + "'";
                else if (!(bool)mIsClear.Invoke(game, new[] { pos })) why = "square not clear";
                else if ((int)mGetGold.Invoke(res, null) < cost) why = "not enough gold";
                else if ((int)mDwarfAt.Invoke(game, new[] { pos }) != -1) why = "dwarf there";
                else if ((int)mDwarfAtBuf.Invoke(game, new[] { pos, (object)true }) != -1) why = "dwarf nearby";
                else if ((int)mEnemyAt.Invoke(game, new[] { pos }) != -1) why = "enemy there";
                else if ((int)mEnemyAtBuf.Invoke(game, new[] { pos, (object)true }) != -1) why = "enemy nearby";

                if (why != null)
                {
                    Bridge.LogLine("wall at (" + x + "," + y + ") refused: " + why);
                    return false;
                }

                mAddWall.Invoke(game, new[] { pos });
                mAddBonanis.Invoke(res, new object[] { -cost });
                return true;
            }
            catch (Exception e)
            {
                Bridge.LogLine("wall at (" + x + "," + y + ") threw: " + e.Message);
                return false;
            }
        }

        // mirrors the green arrow path in the square menu. arrows steer the
        // dwarves so this is the actual scoring tool, walls and dynamite are
        // just defense. direction is 0 up, 1 right, 2 down, 3 left. the "Arrow"
        // case on the clear check lets them sit on undug soil too which is how
        // the game itself does it, and AddElement_Arrow handles merging or
        // trimming overlapping arrows on its own
        public static bool PlaceArrow(object game, int x, int y, int direction)
        {
            if (!Bind(game)) return false;
            try
            {
                object pos = vecCtor.Invoke(new object[] { (float)x, (float)y });
                object res = fResources.GetValue(game);
                int cost = (int)fCostArrow.GetValue(game) + (int)fCostArrowStart.GetValue(game);

                if (!(bool)mIsClearArrow.Invoke(game, new[] { pos, (object)"Arrow" })) return false;
                if ((int)mGetGold.Invoke(res, null) < cost) return false;

                mAddArrow.Invoke(game, new object[] { pos, direction % 4, "Green", 1, "Menu", 0 });
                mAddBonanis.Invoke(res, new object[] { -cost });
                return true;
            }
            catch (Exception e)
            {
                Bridge.LogLine("arrow at (" + x + "," + y + ") threw: " + e.Message);
                return false;
            }
        }

        // mirrors the square menu dynamite handler. square clear, enough gold,
        // no dynamite already sitting there
        public static bool PlaceDynamite(object game, int x, int y)
        {
            if (!Bind(game)) return false;
            try
            {
                object pos = vecCtor.Invoke(new object[] { (float)x, (float)y });
                object res = fResources.GetValue(game);
                int cost = (int)fCostDynamite.GetValue(game);

                if (!(bool)mIsClear.Invoke(game, new[] { pos })) return false;
                if ((int)mGetGold.Invoke(res, null) < cost) return false;
                if ((int)mDynamiteAt.Invoke(game, new[] { pos }) != -1) return false;

                mAddDynamite.Invoke(game, new[] { pos });
                mAddBonanis.Invoke(res, new object[] { -cost });
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ---- outpost / wall actions ----
        // these came later so they get their own bind group with its own
        // failed flag. that way if the game ever renames one of these the core
        // walls/dynamite/arrows keep working instead of the whole action layer
        // going dark. the cheap shared bits (gold, bonanis, the vector ctor)
        // still come from the main Bind above

        static bool boundEx;
        static bool bindExFailed;

        static FieldInfo fGameMap, fMapW, fMapH, fOverlay;
        static FieldInfo fCity, fCityPos, fCityOffset;
        static FieldInfo fOutposts, fStoneWalls, fDwarves;
        static FieldInfo fCostOutpost, fCostWarrior, fCannonRadius;
        static MethodInfo mAddOutpost, mOutpostIndex, mCallHome, mNextOutpostCost, mCannonFire;
        static FieldInfo fVecX, fVecY;
        static FieldInfo opPos, opDigger, opTrain, opWarriors, opMaxWarriors, opBuyQueue;
        static FieldInfo wallPos, wallHP, wallMaxHP;
        static FieldInfo dwClass, dwHome, dwAction, dwPos;

        static bool BindEx(object game)
        {
            if (boundEx) return true;
            if (bindExFailed) return false;
            try
            {
                Type g = game.GetType();

                fGameMap = g.GetField("xGameMap", Any);
                Type tMap = fGameMap.FieldType;
                fMapW = tMap.GetField("playFieldWidth", Any);
                fMapH = tMap.GetField("playFieldHeight", Any);
                fOverlay = tMap.GetField("abyGameMapOverlay", Any);

                fCity = g.GetField("xCity", Any);
                Type tCity = fCity.FieldType;
                fCityPos = tCity.GetField("m_v2Position", Any);
                fCityOffset = tCity.GetField("m_iOutpostOffset", Any);

                fOutposts = g.GetField("lOutposts", Any);
                fStoneWalls = g.GetField("lStoneWall", Any);
                fDwarves = g.GetField("lDwarf", Any);

                fCostOutpost = g.GetField("ciCostOutpost", Any);
                fCostWarrior = g.GetField("ciCostWarrior", Any);
                fCannonRadius = g.GetField("ciOutpostCannonRadius", Any);

                Type tVec = fCityPos.FieldType;
                fVecX = tVec.GetField("X", Any);
                fVecY = tVec.GetField("Y", Any);

                mAddOutpost = g.GetMethod("AddElement_Outpost", Any, null, new[] { tVec }, null);
                mOutpostIndex = g.GetMethod("Outpost_GetIndex", Any, null, new[] { tVec }, null);
                mCallHome = g.GetMethod("Outpost_CallHome", Any, null, new[] { typeof(int) }, null);
                mNextOutpostCost = g.GetMethod("Outpost_CalculateNextCost", Any, null, new Type[0], null);
                mCannonFire = g.GetMethod("Dwarf_CannonFire", Any, null,
                    new[] { tVec, typeof(int), typeof(int) }, null);

                Type tOut = fOutposts.FieldType.GetGenericArguments()[0];
                opPos = tOut.GetField("m_v2Position", Any);
                opDigger = tOut.GetField("m_bDiggerSpawn", Any);
                opTrain = tOut.GetField("m_bTrainingActive", Any);
                opWarriors = tOut.GetField("m_iWarriors", Any);
                opMaxWarriors = tOut.GetField("m_iMaxWarriors", Any);
                opBuyQueue = tOut.GetField("m_iBuyQueue", Any);

                Type tWall = fStoneWalls.FieldType.GetGenericArguments()[0];
                wallPos = tWall.GetField("m_v2Position", Any);
                wallHP = tWall.GetField("m_fHealth", Any);
                wallMaxHP = tWall.GetField("m_fMaxHealth", Any);

                Type tDwarf = fDwarves.FieldType.GetGenericArguments()[0];
                dwClass = tDwarf.GetField("m_sClass", Any);
                dwHome = tDwarf.GetField("m_iHomeBase", Any);
                dwAction = tDwarf.GetField("m_iAction", Any);
                dwPos = tDwarf.GetField("m_v2Position", Any);

                string missing = "";
                if (fOverlay == null) missing += " abyGameMapOverlay";
                if (fCityOffset == null) missing += " m_iOutpostOffset";
                if (fOutposts == null) missing += " lOutposts";
                if (fStoneWalls == null) missing += " lStoneWall";
                if (fDwarves == null) missing += " lDwarf";
                if (fCostOutpost == null) missing += " ciCostOutpost";
                if (fCostWarrior == null) missing += " ciCostWarrior";
                if (fCannonRadius == null) missing += " ciOutpostCannonRadius";
                if (mAddOutpost == null) missing += " AddElement_Outpost";
                if (mOutpostIndex == null) missing += " Outpost_GetIndex";
                if (mCallHome == null) missing += " Outpost_CallHome";
                if (mNextOutpostCost == null) missing += " Outpost_CalculateNextCost";
                if (mCannonFire == null) missing += " Dwarf_CannonFire";
                if (opPos == null || opDigger == null || opTrain == null) missing += " outpost fields";
                if (wallPos == null || wallHP == null) missing += " stonewall fields";
                if (dwHome == null || dwPos == null) missing += " dwarf fields";

                boundEx = missing.Length == 0;
                if (!boundEx)
                {
                    bindExFailed = true;
                    Bridge.LogLine("GameAction outpost bind missing:" + missing);
                }
                return boundEx;
            }
            catch (Exception e)
            {
                bindExFailed = true;
                Bridge.LogLine("GameAction outpost bind threw: " + e.Message);
                return false;
            }
        }

        // pull the index of the outpost sitting under a tile, or -1. the game
        // counts a hit anywhere in the 3x3 footprint so the agent doesnt have to
        // land exactly on the corner
        static int OutpostAt(object game, int x, int y)
        {
            object pos = vecCtor.Invoke(new object[] { (float)x, (float)y });
            return (int)mOutpostIndex.Invoke(game, new[] { pos });
        }

        // mirrors the build outpost handler. the cursor square has to be clear,
        // the whole 7x7 footprint around it has to be discovered open ground (the
        // border ring read off the overlay, the inner 5x5 through the games own
        // Outpost clear check), it cant sit on top of the city, and you need the
        // gold. on success it drops the tower a bit up/left of the cursor just
        // like the handler does and bumps the next ones cost. dark mode has a
        // different rule but training never runs it so we do the normal path
        public static bool PlaceOutpost(object game, int x, int y)
        {
            if (!Bind(game)) return false;
            if (!BindEx(game)) return false;
            try
            {
                object res = fResources.GetValue(game);
                int cost = (int)fCostOutpost.GetValue(game);
                object map = fGameMap.GetValue(game);
                int mapW = (int)fMapW.GetValue(map);
                int mapH = (int)fMapH.GetValue(map);

                // the footprint reads tiles from x-3..x+3 and y-4..y+2, bail near
                // the edge instead of indexing out of the array
                if (x - 3 < 0 || x + 3 >= mapW || y - 4 < 0 || y + 2 >= mapH)
                    return false;

                object cursor = vecCtor.Invoke(new object[] { (float)x, (float)y });
                if (!(bool)mIsClear.Invoke(game, new[] { cursor }))
                    return false;

                var overlay = (short[,])fOverlay.GetValue(map);
                for (int i = 0; i < 7; i++)
                {
                    for (int j = 0; j < 7; j++)
                    {
                        int tx = x - 3 + i;
                        int ty = y - 4 + j;
                        if (i == 0 || i == 6 || j == 0 || j == 6)
                        {
                            // border ring just has to be discovered (overlay 0)
                            if (overlay != null && overlay[tx, ty] != 0)
                                return false;
                        }
                        else
                        {
                            object p = vecCtor.Invoke(new object[] { (float)tx, (float)ty });
                            if (!(bool)mIsClearArrow.Invoke(game, new[] { p, (object)"Outpost" }))
                                return false;
                        }
                    }
                }

                // cant build right on top of the city
                object city = fCity.GetValue(game);
                object cityPos = fCityPos.GetValue(city);
                float cityX = (float)fVecX.GetValue(cityPos);
                float cityY = (float)fVecY.GetValue(cityPos);
                int off = (int)fCityOffset.GetValue(city);
                float dx = x - (cityX + 1f);
                float dy = y - 1f - (cityY + 1f);
                if (dx >= -off && dx <= off && dy >= -off && dy <= off)
                    return false;

                if ((int)mGetGold.Invoke(res, null) < cost)
                    return false;

                object drop = vecCtor.Invoke(new object[] { (float)(x - 1), (float)(y - 2) });
                mAddOutpost.Invoke(game, new[] { drop });
                mAddBonanis.Invoke(res, new object[] { -cost });
                fCostOutpost.SetValue(game, (int)mNextOutpostCost.Invoke(game, null));
                return true;
            }
            catch (Exception e)
            {
                Bridge.LogLine("outpost at (" + x + "," + y + ") threw: " + e.Message);
                return false;
            }
        }

        // there is no reinforce in the base game, walls just have health that
        // gets chewed down. so we define it as paying the wall cost to patch a
        // damaged wall back to full. no wall there or already full = refused, so
        // the agent gets no free reinforces to spam
        public static bool ReinforceWall(object game, int x, int y)
        {
            if (!Bind(game)) return false;
            if (!BindEx(game)) return false;
            try
            {
                object res = fResources.GetValue(game);
                int cost = (int)fCostWall.GetValue(game);
                var walls = (IList)fStoneWalls.GetValue(game);
                if (walls == null) return false;

                object wall = null;
                for (int i = 0; i < walls.Count; i++)
                {
                    object w = walls[i];
                    object wp = wallPos.GetValue(w);
                    if ((int)(float)fVecX.GetValue(wp) == x && (int)(float)fVecY.GetValue(wp) == y)
                    {
                        wall = w;
                        break;
                    }
                }
                if (wall == null) return false;

                float hp = (float)wallHP.GetValue(wall);
                float max = (float)wallMaxHP.GetValue(wall);
                if (hp >= max) return false; // nothing to fix
                if ((int)mGetGold.Invoke(res, null) < cost) return false;

                wallHP.SetValue(wall, max);
                mAddBonanis.Invoke(res, new object[] { -cost });
                return true;
            }
            catch (Exception e)
            {
                Bridge.LogLine("reinforce at (" + x + "," + y + ") threw: " + e.Message);
                return false;
            }
        }

        // flip the digger spawner on the outpost under the cursor (6a). picks the
        // outpost by tile so the agent points at the tower, no index needed
        public static bool OutpostToggleDigger(object game, int x, int y)
        {
            return OutpostToggleBool(game, x, y, opDigger);
        }

        // flip warrior training on the outpost (6e)
        public static bool OutpostToggleTrain(object game, int x, int y)
        {
            return OutpostToggleBool(game, x, y, opTrain);
        }

        static bool OutpostToggleBool(object game, int x, int y, FieldInfo flag)
        {
            if (!Bind(game)) return false;
            if (!BindEx(game)) return false;
            try
            {
                int idx = OutpostAt(game, x, y);
                if (idx < 0) return false;
                var outs = (IList)fOutposts.GetValue(game);
                object op = outs[idx];
                bool cur = (bool)flag.GetValue(op);
                flag.SetValue(op, !cur);
                return true;
            }
            catch (Exception e)
            {
                Bridge.LogLine("outpost toggle at (" + x + "," + y + ") threw: " + e.Message);
                return false;
            }
        }

        // queue a warrior at the outpost under the cursor (6b). same gold and
        // max warrior checks the menu does
        public static bool OutpostSpawnWarrior(object game, int x, int y)
        {
            if (!Bind(game)) return false;
            if (!BindEx(game)) return false;
            try
            {
                int idx = OutpostAt(game, x, y);
                if (idx < 0) return false;
                var outs = (IList)fOutposts.GetValue(game);
                object op = outs[idx];
                object res = fResources.GetValue(game);
                int cost = (int)fCostWarrior.GetValue(game);

                if ((int)mGetGold.Invoke(res, null) < cost) return false;
                int warriors = (int)opWarriors.GetValue(op);
                int max = (int)opMaxWarriors.GetValue(op);
                if (warriors >= max) return false;

                mAddBonanis.Invoke(res, new object[] { -cost });
                opBuyQueue.SetValue(op, (int)opBuyQueue.GetValue(op) + 1);
                opWarriors.SetValue(op, warriors + 1);
                return true;
            }
            catch (Exception e)
            {
                Bridge.LogLine("spawn warrior at (" + x + "," + y + ") threw: " + e.Message);
                return false;
            }
        }

        // ring the bell on the outpost under the cursor, calls every warrior of
        // that base back home (6c). the game does the same on the city bell with
        // index -1 but the agents version is per tower
        public static bool OutpostRecall(object game, int x, int y)
        {
            if (!Bind(game)) return false;
            if (!BindEx(game)) return false;
            try
            {
                int idx = OutpostAt(game, x, y);
                if (idx < 0) return false;
                mCallHome.Invoke(game, new object[] { idx });
                return true;
            }
            catch (Exception e)
            {
                Bridge.LogLine("recall at (" + x + "," + y + ") threw: " + e.Message);
                return false;
            }
        }

        // read the five action costs off the live game. cost_tower escalates each
        // time a tower is built; the others are fixed for the run. returns false
        // (and zeros) if the binds haven't fired yet, which only happens before
        // the first episode so the env will just see zeros that step
        public static bool ReadCosts(object game,
            out int wall, out int dynamite, out int arrow, out int tower, out int warrior)
        {
            wall = dynamite = arrow = tower = warrior = 0;
            if (!Bind(game)) return false;
            if (!BindEx(game)) return false;
            try
            {
                wall     = (int)fCostWall.GetValue(game);
                dynamite = (int)fCostDynamite.GetValue(game);
                arrow    = (int)fCostArrow.GetValue(game) + (int)fCostArrowStart.GetValue(game);
                tower    = (int)fCostOutpost.GetValue(game);
                warrior  = (int)fCostWarrior.GetValue(game);
                return true;
            }
            catch { return false; }
        }

        // cannon strike (6d). the agent gives a target tile, we find an outpost
        // close enough to lob from (inside the cannon radius) that still has a
        // warrior sitting home, and fire all its eligible warriors at the spot
        // like the games own CannonStrike handler. no outpost in range or none
        // with warriors home = refused
        public static bool OutpostCannon(object game, int x, int y)
        {
            if (!Bind(game)) return false;
            if (!BindEx(game)) return false;
            try
            {
                object target = vecCtor.Invoke(new object[] { (float)x, (float)y });
                var outs = (IList)fOutposts.GetValue(game);
                var dwarves = (IList)fDwarves.GetValue(game);
                if (outs == null || dwarves == null) return false;
                float radius = (int)fCannonRadius.GetValue(game);

                for (int o = 0; o < outs.Count; o++)
                {
                    object op = outs[o];
                    object opP = opPos.GetValue(op);
                    float ox = (float)fVecX.GetValue(opP);
                    float oy = (float)fVecY.GetValue(opP);
                    // handler measures from the middle of the tower
                    float ddx = x - (ox + 1f);
                    float ddy = y - (oy + 1f);
                    if (ddx * ddx + ddy * ddy > radius * radius) continue;

                    bool fired = false;
                    for (int d = 0; d < dwarves.Count; d++)
                    {
                        object dw = dwarves[d];
                        if ((string)dwClass.GetValue(dw) != "Warrior") continue;
                        if ((int)dwHome.GetValue(dw) != o) continue;
                        if ((int)dwAction.GetValue(dw) == 3) continue; // already mid action
                        object dp = dwPos.GetValue(dw);
                        float wx = (float)fVecX.GetValue(dp);
                        float wy = (float)fVecY.GetValue(dp);
                        if (wx < ox - 2f || wx > ox + 3f || wy < oy - 2f || wy > oy + 3f) continue;
                        if ((bool)mCannonFire.Invoke(game, new object[] { target, d, o }))
                            fired = true;
                    }
                    if (fired) return true;
                }
                return false;
            }
            catch (Exception e)
            {
                Bridge.LogLine("cannon at (" + x + "," + y + ") threw: " + e.Message);
                return false;
            }
        }
    }
}
