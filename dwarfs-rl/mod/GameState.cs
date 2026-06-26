using System;
using System.Collections;
using System.Reflection;

namespace DwarfsMod
{
    // reads the live game through reflection. all the interesting state hangs
    // off Game1 in private fields with stable names (xGameMap, resources, xCity,
    // lDwarf etc) and the grids inside GameMap are plain byte[,] / ushort[,] so
    // after one cast we get direct array access, no per cell reflection cost.
    //
    // base tile values in abyGameMap (worked this out from the level generator):
    //   1 = empty (dug out), 3 = rock, 0 and 2 are soil variants and the start
    //   of game fill is 0. water, lava and minerals live in separate layers and
    //   ReadGrid folds them into one code per cell on top of the base value,
    //   4 = mineral, 5 = water, 6 = lava. lava beats water beats mineral
    public static class GameState
    {
        const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
        const BindingFlags Pub = BindingFlags.Public | BindingFlags.Instance;

        static bool bound;
        static bool bindFailed;

        static FieldInfo fGameMap, fResources, fCity, fDwarfList;
        static FieldInfo fMapW, fMapH, fMap, fWater, fLava, fMinerals, fOverlay;
        static FieldInfo fTimeLeft, fCityPos, fCityHP;
        static FieldInfo fVecX, fVecY;
        static FieldInfo fDwarfPos, fDwarfClass;
        static FieldInfo fEnemyList, fEnemyPos, fEnemyCategory, fEnemyUnder, fEnemyStealth;
        static MethodInfo mGetScore;
        static FieldInfo fGold;

        // cave tracking: lCave list + Cave field accessors
        static FieldInfo fCaveList, fCaveOrigin, fCaveType;
        // PointlessData for iInstantSeal achievement counter
        static FieldInfo fPointlessData, fInstantSeal;

        public static bool Bind(object game)
        {
            if (bound) return true;
            if (bindFailed) return false;
            try
            {
                Type g = game.GetType(); // Dwarves.Game1

                fGameMap = g.GetField("xGameMap", Priv);
                fResources = g.GetField("resources", Priv);
                fCity = g.GetField("xCity", Priv);
                fDwarfList = g.GetField("lDwarf", Priv);
                fEnemyList = g.GetField("lEnemy", Priv);

                Type tMap = fGameMap.FieldType;
                fMapW = tMap.GetField("playFieldWidth", Pub);
                fMapH = tMap.GetField("playFieldHeight", Pub);
                fMap = tMap.GetField("abyGameMap", Pub);
                fWater = tMap.GetField("abyGameMapWater", Pub);
                fLava = tMap.GetField("abyGameMapLava", Pub);
                fMinerals = tMap.GetField("abyGameMapMinerals", Pub);
                fOverlay = tMap.GetField("abyGameMapOverlay", Pub);

                Type tCity = fCity.FieldType;
                fTimeLeft = tCity.GetField("m_iTimeLeft", Pub);
                fCityPos = tCity.GetField("m_v2Position", Pub);
                fCityHP = tCity.GetField("m_iHP", Pub);

                Type tVec = fCityPos.FieldType; // Microsoft.Xna.Framework.Vector2
                fVecX = tVec.GetField("X", Pub);
                fVecY = tVec.GetField("Y", Pub);

                // the dwarf layer reads each dwarfs tile and class off the list
                Type tDwarf = fDwarfList.FieldType.GetGenericArguments()[0];
                fDwarfPos = tDwarf.GetField("m_v2Position", Pub);
                fDwarfClass = tDwarf.GetField("m_sClass", Pub);

                // enemies the same way, plus the flags that say if the game is
                // hiding it (underground or stealthed) so we dont xray
                Type tEnemy = fEnemyList.FieldType.GetGenericArguments()[0];
                fEnemyPos = tEnemy.GetField("m_v2Position", Pub);
                fEnemyCategory = tEnemy.GetField("m_sEnemyCategory", Pub);
                fEnemyUnder = tEnemy.GetField("m_bUnderGround", Pub);
                fEnemyStealth = tEnemy.GetField("m_fStealthCounter", Pub);

                Type tRes = fResources.FieldType;
                fGold = tRes.GetField("m_iGold", Priv);
                mGetScore = tRes.GetMethod("GetScore", Pub);

                // cave list and instant-seal achievement counter (optional, degrade gracefully)
                fCaveList      = g.GetField("lCave", Priv);
                fPointlessData = g.GetField("xCurrentGamePointlessData", Priv);
                try
                {
                    if (fCaveList != null)
                    {
                        Type tCave = fCaveList.FieldType.GetGenericArguments()[0];
                        fCaveOrigin = tCave.GetField("m_v2Origin", Pub);
                        fCaveType   = tCave.GetField("m_iType",   Pub);
                    }
                    if (fPointlessData != null)
                    {
                        Type tPD = fPointlessData.FieldType;
                        fInstantSeal = tPD.GetField("iInstantSeal", Pub);
                    }
                }
                catch { }

                bound = fMap != null && fWater != null && fLava != null
                    && fMinerals != null && fTimeLeft != null && fGold != null;
                if (!bound) bindFailed = true;
                return bound;
            }
            catch
            {
                bindFailed = true;
                return false;
            }
        }

        // one shot diagnostic, logs mask coverage on the next grid read
        public static bool LogNextGrid;

        // convenience for callers that dont need the crop origin back (the spike)
        public static int[] ReadGrid(object game, int w, int h)
        {
            int cropX, cropY;
            return ReadGrid(game, w, h, out cropX, out cropY);
        }

        // one combined code per cell, cropped to a w x h window centered on the
        // city. row major (row * w + col) to match how the env reshapes it. the
        // crop origin comes back via out params (NOT a shared static) so worlds
        // running on separate threads can't clobber each other's crop, and so the
        // matching dwarf/enemy layers read against the very same window
        public static int[] ReadGrid(object game, int w, int h, out int cropX, out int cropY)
        {
            cropX = 0;
            cropY = 0;
            var outGrid = new int[w * h];
            if (!Bind(game)) return outGrid;

            object map = fGameMap.GetValue(game);
            if (map == null) return outGrid;

            int mapW = (int)fMapW.GetValue(map);
            int mapH = (int)fMapH.GetValue(map);
            var content = (byte[,])fMap.GetValue(map);
            var water = (ushort[,])fWater.GetValue(map);
            var lava = (ushort[,])fLava.GetValue(map);
            var minerals = (byte[,])fMinerals.GetValue(map);
            var overlay = (short[,])fOverlay.GetValue(map);
            if (content == null) return outGrid;

            // center the window on the city, clamped inside the playfield
            int cx = mapW / 2, cy = mapH / 2;
            object city = fCity.GetValue(game);
            if (city != null)
            {
                object pos = fCityPos.GetValue(city);
                cx = (int)(float)fVecX.GetValue(pos);
                cy = (int)(float)fVecY.GetValue(pos);
            }
            int x0 = Clamp(cx - w / 2, 0, Math.Max(0, mapW - w));
            int y0 = Clamp(cy - h / 2, 0, Math.Max(0, mapH - h));
            cropX = x0;
            cropY = y0;

            int masked = 0;
            for (int row = 0; row < h; row++)
            {
                int y = y0 + row;
                if (y >= mapH) break;
                for (int col = 0; col < w; col++)
                {
                    int x = x0 + col;
                    if (x >= mapW) break;
                    int v;
                    // sealed caves carry an overlay id til the dwarves break in.
                    // a human just sees solid ground there so thats what the
                    // model gets too, no xray
                    if (overlay != null && overlay[x, y] != 0 && content[x, y] != 3)
                    {
                        v = 0;
                        masked++;
                    }
                    else if (lava[x, y] > 0) v = 6;
                    else if (water[x, y] > 0) v = 5;
                    else if (minerals[x, y] > 0) v = 4;
                    else v = content[x, y];
                    outGrid[row * w + col] = v;
                }
            }
            if (LogNextGrid)
            {
                LogNextGrid = false;
                Bridge.LogLine("grid: crop (" + x0 + "," + y0 + "), masked " + masked +
                    " of " + (w * h) + " cells, center overlay " +
                    (overlay != null ? overlay[x0 + w / 2, y0 + h / 2].ToString() : "n/a"));
            }
            return outGrid;
        }

        // a second layer the same size as ReadGrid marking where our dwarves
        // are, 0 nobody, 1 a regular (digger), 2 a warrior, warrior wins if they
        // share a tile. no fog masking, you always see your own dwarves. pass the
        // crop origin ReadGrid returned so this lands on the same window
        public static int[] ReadDwarfGrid(object game, int w, int h, int cropX, int cropY)
        {
            var outGrid = new int[w * h];
            if (!Bind(game)) return outGrid;
            try
            {
                var list = fDwarfList.GetValue(game) as IList;
                if (list == null || fDwarfPos == null || fDwarfClass == null)
                    return outGrid;
                int x0 = cropX, y0 = cropY;
                for (int i = 0; i < list.Count; i++)
                {
                    object d = list[i];
                    if (d == null) continue;
                    object pos = fDwarfPos.GetValue(d);
                    int dx = (int)(float)fVecX.GetValue(pos) - x0;
                    int dy = (int)(float)fVecY.GetValue(pos) - y0;
                    if (dx < 0 || dx >= w || dy < 0 || dy >= h) continue;
                    int code = (string)fDwarfClass.GetValue(d) == "Warrior" ? 2 : 1;
                    int idx = dy * w + dx;
                    if (code > outGrid[idx]) outGrid[idx] = code;
                }
                return outGrid;
            }
            catch { return outGrid; }
        }

        // enemy layer, same idea as the dwarf one, 0 none, 1 a minion, 2 a boss,
        // boss wins a shared tile. we only mark the ones a human can actually see,
        // the game hides enemies that are underground or fully stealthed
        // (m_fStealthCounter >= 1000) so we skip those, no xray. pass the same crop
        // origin ReadGrid returned
        public static int[] ReadEnemyGrid(object game, int w, int h, int cropX, int cropY)
        {
            var outGrid = new int[w * h];
            if (!Bind(game)) return outGrid;
            try
            {
                var list = fEnemyList.GetValue(game) as IList;
                if (list == null || fEnemyPos == null || fEnemyCategory == null)
                    return outGrid;
                int x0 = cropX, y0 = cropY;
                for (int i = 0; i < list.Count; i++)
                {
                    object e = list[i];
                    if (e == null) continue;
                    if (fEnemyUnder != null && (bool)fEnemyUnder.GetValue(e)) continue;
                    if (fEnemyStealth != null && (float)fEnemyStealth.GetValue(e) >= 1000f) continue;
                    object pos = fEnemyPos.GetValue(e);
                    int ex = (int)(float)fVecX.GetValue(pos) - x0;
                    int ey = (int)(float)fVecY.GetValue(pos) - y0;
                    if (ex < 0 || ex >= w || ey < 0 || ey >= h) continue;
                    int code = (string)fEnemyCategory.GetValue(e) == "Boss" ? 2 : 1;
                    int idx = ey * w + ex;
                    if (code > outGrid[idx]) outGrid[idx] = code;
                }
                return outGrid;
            }
            catch { return outGrid; }
        }

        public static int Gold(object game)
        {
            if (!Bind(game)) return 0;
            try
            {
                object res = fResources.GetValue(game);
                return res == null ? 0 : (int)fGold.GetValue(res);
            }
            catch { return 0; }
        }

        public static long Score(object game)
        {
            if (!Bind(game)) return 0;
            try
            {
                object res = fResources.GetValue(game);
                if (res == null || mGetScore == null) return 0;
                return Convert.ToInt64(mGetScore.Invoke(res, null));
            }
            catch { return 0; }
        }

        public static int DwarfCount(object game)
        {
            if (!Bind(game)) return 0;
            try
            {
                var list = fDwarfList.GetValue(game) as IList;
                return list == null ? 0 : list.Count;
            }
            catch { return 0; }
        }

        // total tiles holding water or lava right now. a sealed reservoir is a
        // fixed count, so what matters is the GROWTH of this between steps, thats
        // the water/lava actively spreading. only gets scanned when the hazard
        // penalty is switched on so it costs nothing otherwise
        public static int HazardTiles(object game)
        {
            if (!Bind(game)) return 0;
            try
            {
                object map = fGameMap.GetValue(game);
                if (map == null) return 0;
                int mapW = (int)fMapW.GetValue(map);
                int mapH = (int)fMapH.GetValue(map);
                var water = (ushort[,])fWater.GetValue(map);
                var lava = (ushort[,])fLava.GetValue(map);
                if (water == null || lava == null) return 0;
                int count = 0;
                for (int x = 0; x < mapW; x++)
                    for (int y = 0; y < mapH; y++)
                    {
                        if (water[x, y] > 0) count++;
                        if (lava[x, y] > 0) count++;
                    }
                return count;
            }
            catch { return 0; }
        }

        public static int TimeLeft(object game)
        {
            if (!Bind(game)) return 0;
            try
            {
                object city = fCity.GetValue(game);
                return city == null ? 0 : (int)fTimeLeft.GetValue(city);
            }
            catch { return 0; }
        }

        public static int CityHP(object game)
        {
            if (!Bind(game)) return 0;
            try
            {
                object city = fCity.GetValue(game);
                return city == null ? 0 : (int)fCityHP.GetValue(city);
            }
            catch { return 0; }
        }

        // number of caves opened so far this episode
        public static int CaveCount(object game)
        {
            if (!Bind(game) || fCaveList == null) return 0;
            try
            {
                var list = fCaveList.GetValue(game) as IList;
                return list == null ? 0 : list.Count;
            }
            catch { return 0; }
        }

        // iInstantSeal achievement counter (game increments when a cave is sealed fast)
        public static int InstantSealCount(object game)
        {
            if (!Bind(game) || fPointlessData == null || fInstantSeal == null) return 0;
            try
            {
                object pd = fPointlessData.GetValue(game);
                return pd == null ? 0 : (int)fInstantSeal.GetValue(pd);
            }
            catch { return 0; }
        }

        // read the cave at reverse index from the end of lCave (0 = most recent).
        // returns false if the list is empty or the index is out of range
        public static bool GetCave(object game, int reverseIdx, out int x, out int y, out int type)
        {
            x = y = type = -1;
            if (!Bind(game) || fCaveList == null || fCaveOrigin == null || fCaveType == null)
                return false;
            try
            {
                var list = fCaveList.GetValue(game) as IList;
                if (list == null || list.Count == 0) return false;
                int idx = list.Count - 1 - reverseIdx;
                if (idx < 0) return false;
                object cave = list[idx];
                if (cave == null) return false;
                object origin = fCaveOrigin.GetValue(cave);
                x    = (int)(float)fVecX.GetValue(origin);
                y    = (int)(float)fVecY.GetValue(origin);
                type = (int)fCaveType.GetValue(cave);
                return true;
            }
            catch { return false; }
        }

        // finds the 4 extreme digger dwarf positions (n=northmost, e=eastmost,
        // s=southmost, w=westmost). warriors are excluded. returns -1,-1 for any
        // camera slot where no digger could be assigned
        public static void FindExtremeDiggers(object game,
            out int nx, out int ny,
            out int ex, out int ey,
            out int sx, out int sy,
            out int wx, out int wy)
        {
            nx = ny = ex = ey = sx = sy = wx = wy = -1;
            if (!Bind(game)) return;
            try
            {
                var list = fDwarfList.GetValue(game) as IList;
                if (list == null) return;
                int bestN = int.MaxValue, bestE = int.MinValue,
                    bestS = int.MinValue, bestW = int.MaxValue;
                for (int i = 0; i < list.Count; i++)
                {
                    object d = list[i];
                    if (d == null) continue;
                    if ((string)fDwarfClass.GetValue(d) == "Warrior") continue;
                    object pos = fDwarfPos.GetValue(d);
                    int px = (int)(float)fVecX.GetValue(pos);
                    int py = (int)(float)fVecY.GetValue(pos);
                    if (py < bestN) { bestN = py; nx = px; ny = py; }
                    if (px > bestE) { bestE = px; ex = px; ey = py; }
                    if (py > bestS) { bestS = py; sx = px; sy = py; }
                    if (px < bestW) { bestW = px; wx = px; wy = py; }
                }
            }
            catch { nx = ny = ex = ey = sx = sy = wx = wy = -1; }
        }

        // terrain grid centered on an explicit map position (cx, cy).
        // same masking rules as ReadGrid. use this for per-dwarf camera views
        public static int[] ReadGridAt(object game, int w, int h, int cx, int cy,
            out int cropX, out int cropY)
        {
            cropX = 0; cropY = 0;
            var outGrid = new int[w * h];
            if (!Bind(game)) return outGrid;

            object map = fGameMap.GetValue(game);
            if (map == null) return outGrid;

            int mapW = (int)fMapW.GetValue(map);
            int mapH = (int)fMapH.GetValue(map);
            var content  = (byte[,])fMap.GetValue(map);
            var water    = (ushort[,])fWater.GetValue(map);
            var lava     = (ushort[,])fLava.GetValue(map);
            var minerals = (byte[,])fMinerals.GetValue(map);
            var overlay  = (short[,])fOverlay.GetValue(map);
            if (content == null) return outGrid;

            int x0 = Clamp(cx - w / 2, 0, Math.Max(0, mapW - w));
            int y0 = Clamp(cy - h / 2, 0, Math.Max(0, mapH - h));
            cropX = x0; cropY = y0;

            for (int row = 0; row < h; row++)
            {
                int y = y0 + row;
                if (y >= mapH) break;
                for (int col = 0; col < w; col++)
                {
                    int x = x0 + col;
                    if (x >= mapW) break;
                    int v;
                    if (overlay != null && overlay[x, y] != 0 && content[x, y] != 3)
                        v = 0;
                    else if (lava[x, y] > 0) v = 6;
                    else if (water[x, y] > 0) v = 5;
                    else if (minerals[x, y] > 0) v = 4;
                    else v = content[x, y];
                    outGrid[row * w + col] = v;
                }
            }
            return outGrid;
        }

        static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
