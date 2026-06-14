using System;
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
    }
}
