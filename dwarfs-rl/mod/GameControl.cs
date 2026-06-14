using System;
using System.Reflection;

namespace DwarfsMod
{
    // drives the game into a fresh arcade round the same way the menu does. set
    // difficulty + time category, clear any leftover mode flags, then kick the
    // gamestate fade at target 1. the fade transition itself calls ClearGame()
    // + GenerateLevel() whether you come from the menu or a running game so
    // this works for the first episode and every restart after
    public static class GameControl
    {
        const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
        const BindingFlags Pub = BindingFlags.Public | BindingFlags.Instance;

        static bool bound;
        static bool bindFailed;

        static MethodInfo mSetDifficulty;
        static FieldInfo fGameState, fFade, fFadeState, fFadeTarget;
        static FieldInfo fDifficulty, fEnTime, fRush, fDark;
        static FieldInfo fCampaign, fCampaignActive;
        static FieldInfo fSandbox, fSandboxActive;
        static FieldInfo fTowerDef, fTowerDefActive;
        static FieldInfo fTutorial, fTutorialActive, fTutorialTakeOver;
        static FieldInfo fOptions, fStartedArcade;
        static FieldInfo fRandomizer;

        static bool Bind(object game)
        {
            if (bound) return true;
            if (bindFailed) return false;
            try
            {
                Type g = game.GetType();

                mSetDifficulty = g.GetMethod("SetDifficulty", Priv, null,
                    new[] { typeof(string) }, null);
                fGameState = g.GetField("iGameState", Priv);
                fRandomizer = g.GetField("randomizer", Priv);

                fFade = g.GetField("xGamestateFade", Priv);
                fFadeState = fFade.FieldType.GetField("iFadeState", Pub);
                fFadeTarget = fFade.FieldType.GetField("iTargetGameState", Pub);

                fDifficulty = g.GetField("xDifficulty", Priv);
                fEnTime = fDifficulty.FieldType.GetField("m_enTime", Pub);
                fRush = fDifficulty.FieldType.GetField("m_bRushMode", Pub);
                fDark = fDifficulty.FieldType.GetField("m_bDarkMode", Pub);

                fCampaign = g.GetField("xCampaignControls", Priv);
                fCampaignActive = fCampaign.FieldType.GetField("bCampaignActive", Pub);
                fSandbox = g.GetField("xSandboxControls", Priv);
                fSandboxActive = fSandbox.FieldType.GetField("bSandboxActive", Pub);
                fTowerDef = g.GetField("xTowerDefense", Priv);
                fTowerDefActive = fTowerDef.FieldType.GetField("bIsActive", Pub);
                fTutorial = g.GetField("xTutorial", Priv);
                fTutorialActive = fTutorial.FieldType.GetField("bIsActive", Pub);
                fTutorialTakeOver = fTutorial.FieldType.GetField("bTakeOver", Pub);
                fOptions = g.GetField("xOptions", Priv);
                fStartedArcade = fOptions.FieldType.GetField("m_bHasStartedArcade", Pub);

                bound = mSetDifficulty != null && fGameState != null
                    && fFadeState != null && fEnTime != null;
                if (!bound) bindFailed = true;
                return bound;
            }
            catch
            {
                bindFailed = true;
                return false;
            }
        }

        // Difficulty.TimeSetting: custom=-1, m5=0, m15=1, m30=2, m60=3, Endless=4
        public static int TimeModeFromString(string mode)
        {
            switch (mode)
            {
                case "m15": return 1;
                case "m30": return 2;
                case "m60": return 3;
                default: return 0; // m5
            }
        }

        public static bool StartArcade(object game, string difficulty, int timeMode)
        {
            if (!Bind(game)) return false;
            try
            {
                // difficulty sets the costs, flow speeds and the playfield size
                // (Easy 500x500 up to TediHardcore 1000x1000)
                mSetDifficulty.Invoke(game, new object[] { difficulty });

                object diff = fDifficulty.GetValue(game);
                fEnTime.SetValue(diff, Enum.ToObject(fEnTime.FieldType, timeMode));
                fRush.SetValue(diff, false);
                fDark.SetValue(diff, false);

                // clear whatever a previous mode left behind so GenerateLevel
                // takes the plain arcade path
                SetFlag(fCampaign, fCampaignActive, game, false);
                SetFlag(fSandbox, fSandboxActive, game, false);
                SetFlag(fTowerDef, fTowerDefActive, game, false);
                SetFlag(fTutorial, fTutorialActive, game, false);
                SetFlag(fTutorial, fTutorialTakeOver, game, false);

                // the very first arcade start ever pops an intro screen that
                // blocks play til you click it away, just pretend weve seen it
                SetFlag(fOptions, fStartedArcade, game, true);

                // same trigger the menu uses. the fade transition does
                // ClearGame + GenerateLevel and lands in gamestate 1
                object fade = fFade.GetValue(game);
                fFadeTarget.SetValue(fade, 1);
                fFadeState.SetValue(fade, 1);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // swap the level gen rng for a seeded one. gets called from the
        // GenerateLevel hook so nothing else can eat values in between
        public static void Reseed(object game, int seed)
        {
            if (!Bind(game) || fRandomizer == null) return;
            try { fRandomizer.SetValue(game, new Random(seed)); }
            catch { }
        }

        public static int GameStateId(object game)
        {
            if (!Bind(game)) return -1;
            try { return (int)fGameState.GetValue(game); }
            catch { return -1; }
        }

        // the fade transition only runs GenerateLevel when leaving the menu (0)
        // or an active game (1/2), anywhere else it just switches state
        public static bool CanStartArcade(object game)
        {
            int s = GameStateId(game);
            return s == 0 || s == 1 || s == 2;
        }

        // drive whatever screen were on back to the main menu (target 0 cleans
        // up any mode and loads menu textures from every state)
        public static void ForceToMenu(object game)
        {
            if (!Bind(game)) return;
            try
            {
                object fade = fFade.GetValue(game);
                fFadeTarget.SetValue(fade, 0);
                fFadeState.SetValue(fade, 1);
            }
            catch { }
        }

        // gamestate -100 is the menus own quit path, the transition calls
        // Game.Exit() once the fade completes
        public static void QuitGame(object game)
        {
            if (!Bind(game)) return;
            try
            {
                object fade = fFade.GetValue(game);
                fFadeTarget.SetValue(fade, -100);
                fFadeState.SetValue(fade, 1);
            }
            catch { }
        }

        static void SetFlag(FieldInfo holder, FieldInfo flag, object game, bool value)
        {
            if (holder == null || flag == null) return;
            object o = holder.GetValue(game);
            if (o != null) flag.SetValue(o, value);
        }
    }
}
