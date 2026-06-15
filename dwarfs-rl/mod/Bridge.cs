using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace DwarfsMod
{
    // everything the game calls into lives here. the loader weaves calls to these
    // static methods into Dwarfs.exe, the entry point plus the update, input,
    // draw and level gen methods. the mod never references the game or xna
    // directly, everything goes through reflection at runtime so the build doesnt
    // need any of the game dlls.
    //
    // how the wire works. the python gym env runs a websocket server on
    // localhost:8765 and we connect to it. env sends RESET / STEP / RENDER and
    // every state reply has map_grid, immediate_reward, terminated and truncated
    // in it.
    //
    // timing wise the games sim is purely frame based, Update() never even reads
    // the clock, so the env is basically the clock. RESET drives the game into a
    // fresh arcade round through the same fade transition the menu uses, STEP
    // advances exactly one frame, and xnas throttles get stripped while an
    // episode runs.
    public static class Bridge
    {
        // overridable per instance so you can run a bunch of games side by side
        // each talking to its own env worker, set DWARFS_BRIDGE_PORT before launch
        static string host = "127.0.0.1";
        static int port = 8765;
        static string logPath = Path.Combine(Path.GetTempPath(), "dwarfs_mod.log");

        // give up on lockstep if the env goes quiet for this long, that way a dead
        // trainer hands the game back instead of freezing it forever
        const int CommandTimeoutMs = 120000;

        // a resets fade transition normally lands in like 16 frames once the game
        // is on the menu but at boot theres intro screens to ride out first
        const int ResetTimeoutFrames = 7200;
        const int ForceMenuAfterFrames = 600;

        // observation crop around the city. doms env expects 40 rows x 60 cols
        // and the real playfield is 500x500+ so we were gonna crop anyway
        const int ObsW = 60;
        const int ObsH = 40;

        enum Phase { Free, Resetting, Running }

        static long frame;
        static volatile bool rendering = true;

        // single slot mailbox, the env is strictly lockstep so theres never more
        // than one command in flight
        static readonly object gate = new object();
        static Dictionary<string, object> mailbox;
        static bool envConnected;

        static Phase phase = Phase.Free;
        static int stepFramesLeft;    // frames left before the current STEP replies
        static int actionRepeat = 1;  // frames each STEP advances, comes from RESET
        static bool lastActionOk;     // did the last action actually go through
        static bool levelGenerated;   // set by the GenerateLevel hook
        static bool resetTriggered;   // fade kicked, waiting on generation
        static bool forcedMenu;
        static long resetStartedAt;
        static string resetDifficulty = "Easy";
        static int resetTimeMode;
        static int pendingSeed;
        static bool hasPendingSeed;
        static long lastScore;
        static int lastHazardTiles;  // flooded tile count last step, for the spread penalty

        // reward shaping, set per episode from RESET so tuning happens on the
        // python side. losing the city before the timer costs death_penalty,
        // running out the clock is the natural end so that costs nothing
        static float rewardDeathPenalty = 1500f;
        static float rewardInvalidAction;
        static float rewardHazard;  // charged per active water/lava front each step

        // the control panel reads these, the game thread writes them as replies
        // go out so theyre at most one step stale
        public static long StatScore;
        public static int StatGold;
        public static int StatDwarves;
        public static int StatTimeLeft;

        public static long Frame { get { return frame; } }
        public static bool EnvConnected { get { return envConnected; } }
        public static bool RenderingOn { get { return rendering; } }
        public static bool EpisodeActive { get { return phase != Phase.Free; } }

        public static void SetRendering(bool on, int fps)
        {
            rendering = on;
            maxFps = fps;
            Log("panel: rendering -> " + on + (fps > 0 ? " at " + fps + " fps" : ""));
        }

        static bool fastClock;
        static MethodInfo suppressDraw;
        static PropertyInfo piFixedStep, piInactiveSleep;

        // optional frame pacing, for watching at human speed. 0 = unlimited.
        // training wants this off; flip it on (60 = real time) to spectate or
        // record, then back off to resume full speed.
        static volatile int maxFps;
        static readonly System.Diagnostics.Stopwatch pacer = System.Diagnostics.Stopwatch.StartNew();
        static long nextFrameDueMs;

        static WsClient ws;

        // woven into the start of Dwarves.Program.Main
        public static void Boot()
        {
            try
            {
                string h = Environment.GetEnvironmentVariable("DWARFS_BRIDGE_HOST");
                if (!string.IsNullOrEmpty(h)) host = h;
                string p = Environment.GetEnvironmentVariable("DWARFS_BRIDGE_PORT");
                int parsed;
                if (!string.IsNullOrEmpty(p) && int.TryParse(p, out parsed)) port = parsed;
                if (port != 8765) // parallel instances get their own log files
                    logPath = Path.Combine(Path.GetTempPath(), "dwarfs_mod_" + port + ".log");

                Log("boot: bridge starting, will connect to ws://" + host + ":" + port);
                var t = new Thread(SocketLoop);
                t.IsBackground = true;
                t.Name = "bridge-socket";
                t.Start();

                // the control panel is for humans, parallel training sets
                // DWARFS_BRIDGE_GUI=0 so you dont get 8 windows popping up
                if (Environment.GetEnvironmentVariable("DWARFS_BRIDGE_GUI") != "0")
                {
                    var ui = new Thread(ControlPanel.Open);
                    ui.SetApartmentState(ApartmentState.STA);
                    ui.IsBackground = true;
                    ui.Name = "bridge-panel";
                    ui.Start();
                }
            }
            catch (Exception e) { Log("boot failed: " + e); }
        }

        // woven into the start of Game1.Update. while an episode is live this is
        // the lockstep gate, the frame doesnt run until the env says so
        public static void BeforeUpdate(object game)
        {
            frame++;
            if (!rendering)
                SuppressDraw(game);
            if (phase != Phase.Free)
            {
                KeepFast(game);
                Pace();
                if (frame % 1800 == 0) // breadcrumbs in case of a silent death
                    Log("heartbeat: tick " + frame + ", state " + GameControl.GameStateId(game) +
                        ", time " + GameState.TimeLeft(game) + ", dwarves " + GameState.DwarfCount(game));
            }

            // mid reset. let frames run free while we herd the game into a fresh
            // arcade round then hand the env its first observation. first wait
            // til we're somewhere the transition actually generates from (menu
            // or in game, at boot that means riding out the intro screens), then
            // once the fade is kicked just wait for GenerateLevel to fire
            if (phase == Phase.Resetting)
            {
                if (!resetTriggered)
                {
                    if (GameControl.CanStartArcade(game))
                    {
                        if (GameControl.StartArcade(game, resetDifficulty, resetTimeMode))
                        {
                            resetTriggered = true;
                            Log("arcade start triggered from state " + GameControl.GameStateId(game));
                        }
                        else
                        {
                            Log("arcade start failed, replying with current state");
                            FinishReset(game);
                        }
                    }
                    else if (!forcedMenu && frame - resetStartedAt > ForceMenuAfterFrames)
                    {
                        forcedMenu = true;
                        Log("stuck in state " + GameControl.GameStateId(game) + ", forcing menu");
                        GameControl.ForceToMenu(game);
                    }
                    else if (frame - resetStartedAt > ResetTimeoutFrames)
                    {
                        Log("never reached a startable state (state " +
                            GameControl.GameStateId(game) + "), replying anyway");
                        FinishReset(game);
                    }
                    if (phase == Phase.Resetting) return;
                }
                else if (levelGenerated)
                {
                    Log("reset complete (state " + GameControl.GameStateId(game) +
                        ", time " + GameState.TimeLeft(game) + ")");
                    FinishReset(game);
                }
                else if (frame - resetStartedAt > ResetTimeoutFrames)
                {
                    Log("fade triggered but level never generated, replying anyway");
                    FinishReset(game);
                }
                else
                {
                    return;
                }
            }

            // burn through the current STEPs frames and score + report after the
            // last one (action_repeat > 1 means one decision spans a bunch of frames)
            if (stepFramesLeft > 0)
            {
                stepFramesLeft--;
                if (stepFramesLeft > 0) return;

                long score = GameState.Score(game);
                float reward = score - lastScore;
                lastScore = score;

                // state 1 is the only "still playing" state. 2 is the game over
                // sequence (city flooded / blown up) and anything else is a menu
                int state = GameControl.GameStateId(game);
                int timeLeft = GameState.TimeLeft(game);
                bool terminated = state != 1
                    || timeLeft <= 0
                    || GameState.CityHP(game) <= 0;

                if (!lastActionOk)
                    reward -= rewardInvalidAction;
                if (terminated && timeLeft > 0)
                    reward -= rewardDeathPenalty; // died, didnt make the timer
                // sting for water/lava actively spreading. count how many new
                // flooded tiles showed up since last step, so a sealed cave costs
                // nothing but a flood on the move racks it up til its walled off
                if (rewardHazard != 0f)
                {
                    int hz = GameState.HazardTiles(game);
                    int spread = hz - lastHazardTiles;
                    lastHazardTiles = hz;
                    if (spread > 0)
                        reward -= rewardHazard * spread;
                }
                Send(BuildState(game, reward, terminated, false));
            }

            while (true)
            {
                Dictionary<string, object> cmd = null;
                lock (gate)
                {
                    if (mailbox != null)
                    {
                        cmd = mailbox;
                        mailbox = null;
                    }
                    else if (phase == Phase.Running)
                    {
                        if (!envConnected || !Monitor.Wait(gate, CommandTimeoutMs))
                        {
                            phase = Phase.Free;
                            Log(envConnected
                                ? "no command for " + CommandTimeoutMs / 1000 + "s, releasing the game"
                                : "env disconnected, releasing the game");
                            SlowClock(game);
                            return;
                        }
                        continue; // woke up, check the mailbox again
                    }
                    else
                    {
                        return; // free running, nothing waiting
                    }
                }

                try
                {
                    if (HandleCommand(cmd, game))
                        return; // a frame needs to run now
                }
                catch (Exception e) { Log("command failed: " + e); }
            }
        }

        // true means "advance the frame now"
        static bool HandleCommand(Dictionary<string, object> cmd, object game)
        {
            string name = MiniJson.GetString(cmd, "command", "");
            switch (name)
            {
                case "RESET":
                {
                    string mode = MiniJson.GetString(cmd, "mode", "m5");
                    resetDifficulty = MiniJson.GetString(cmd, "difficulty", "Easy");
                    resetTimeMode = GameControl.TimeModeFromString(mode);
                    int seed = MiniJson.GetInt(cmd, "seed", int.MinValue);
                    hasPendingSeed = seed != int.MinValue;
                    pendingSeed = seed;

                    actionRepeat = MiniJson.GetInt(cmd, "action_repeat", 1);
                    if (actionRepeat < 1) actionRepeat = 1;
                    if (actionRepeat > 240) actionRepeat = 240;
                    rewardDeathPenalty = MiniJson.GetFloat(cmd, "death_penalty", 1500f);
                    rewardInvalidAction = MiniJson.GetFloat(cmd, "invalid_action", 0f);
                    rewardHazard = MiniJson.GetFloat(cmd, "hazard_penalty", 0f);

                    // episodes run headless unless asked, watching is opt in.
                    // the panel checkbox can still flip it back on mid run
                    rendering = MiniJson.GetBool(cmd, "render", false);
                    maxFps = MiniJson.GetInt(cmd, "render_fps", 0);

                    Log("RESET: " + resetDifficulty + " " + mode +
                        (hasPendingSeed ? " seed " + seed : " unseeded") +
                        ", repeat " + actionRepeat +
                        ", game in state " + GameControl.GameStateId(game));
                    FastClock(game);
                    levelGenerated = false;
                    resetTriggered = false;
                    forcedMenu = false;
                    resetStartedAt = frame;
                    phase = Phase.Resetting;
                    return true; // run frames so the transition can play out
                }

                case "STEP":
                {
                    int action = MiniJson.GetInt(cmd, "action", 0);
                    int x = MiniJson.GetInt(cmd, "x", -1);
                    int y = MiniJson.GetInt(cmd, "y", -1);
                    lastActionOk = ApplyAction(game, action, x, y);
                    stepFramesLeft = actionRepeat;
                    return true;
                }

                case "RENDER":
                    rendering = MiniJson.GetBool(cmd, "enabled", true);
                    maxFps = MiniJson.GetInt(cmd, "max_fps", 0);
                    Log("rendering -> " + rendering +
                        (maxFps > 0 ? " at " + maxFps + " fps" : " unthrottled"));
                    return false;

                case "QUIT":
                    // go out through the games own fade path so the trainer
                    // doesnt have to kill the process
                    Log("QUIT received, shutting the game down");
                    phase = Phase.Free;
                    GameControl.QuitGame(game);
                    return true; // run frames so the exit fade can play out

                default:
                    Log("unknown command: " + name);
                    return false;
            }
        }

        // coordinates come in relative to the window (thats all the model sees)
        // so translate to map tiles using where the last observations crop sat
        static bool ApplyAction(object game, int action, int x, int y)
        {
            switch (action)
            {
                case 0:
                    return true; // idle is always fine
                case 1:
                    if (x < 0 || y < 0 || x >= ObsW || y >= ObsH) return false;
                    return GameAction.PlaceDynamite(game,
                        GameState.LastCropX + x, GameState.LastCropY + y);
                case 2:
                    if (x < 0 || y < 0 || x >= ObsW || y >= ObsH) return false;
                    return GameAction.PlaceWall(game,
                        GameState.LastCropX + x, GameState.LastCropY + y);
                case 3: // green arrows, 3 up 4 right 5 down 6 left
                case 4:
                case 5:
                case 6:
                    if (x < 0 || y < 0 || x >= ObsW || y >= ObsH) return false;
                    return GameAction.PlaceArrow(game,
                        GameState.LastCropX + x, GameState.LastCropY + y, action - 3);
                default:
                    return false;
            }
        }

        static void FinishReset(object game)
        {
            levelGenerated = false;
            hasPendingSeed = false;
            phase = Phase.Running;
            lastActionOk = true; // don't carry a stale flag into the new episode
            lastScore = GameState.Score(game);
            lastHazardTiles = rewardHazard != 0f ? GameState.HazardTiles(game) : 0;
            GameState.LogNextGrid = true; // log mask coverage once per episode
            Send(BuildState(game, 0f, false, false));
        }

        // woven into the start of Game1.GenerateLevel, right before the world
        // gets built. reseeding here means nothing else can pull numbers from
        // the rng between the seed and the generation. heads up, restarting from
        // in game runs the generator TWICE in one transition so we keep
        // reseeding til the reset is done. the last generation is the one that
        // sticks and this way every pass starts from the same rng
        public static void BeforeGenerateLevel(object game)
        {
            if (phase != Phase.Resetting) return; // campaign / manual starts
            if (hasPendingSeed)
            {
                GameControl.Reseed(game, pendingSeed);
                Log("level rng seeded with " + pendingSeed);
            }
            levelGenerated = true;
        }

        // woven into the start of Game1.Draw, false skips the frame entirely
        public static bool ShouldRender()
        {
            return rendering;
        }

        // woven into the start of the input readers. while an episode is live the
        // human mouse/keyboard cant be allowed to reach the game. agent actions
        // go in through internal calls anyway, a stray hover or click would mess
        // up the episode, plus the raw input path is where the games silent
        // crash and exit handler lives (cursor coords outside the window)
        public static bool ShouldReadInput()
        {
            return phase == Phase.Free;
        }

        // ---- clock control ----
        // the game never touches these xna knobs itself so theyre all ours

        static void FastClock(object game)
        {
            if (fastClock) return;
            try
            {
                var t = game.GetType();
                t.GetProperty("IsFixedTimeStep").SetValue(game, false, null);
                t.GetProperty("InactiveSleepTime").SetValue(game, TimeSpan.Zero, null);
                var gfx = t.GetField("graphics", BindingFlags.NonPublic | BindingFlags.Instance);
                object gdm = gfx != null ? gfx.GetValue(game) : null;
                if (gdm != null)
                {
                    gdm.GetType().GetProperty("SynchronizeWithVerticalRetrace").SetValue(gdm, false, null);
                    gdm.GetType().GetMethod("ApplyChanges").Invoke(gdm, null);
                }
                fastClock = true;
                Log("fast clock on (fixed step + vsync off)");
            }
            catch (Exception e) { Log("fast clock failed: " + e.Message); }
        }

        // re-assert the throttle killers every frame. setting them once in
        // FastClock wasnt always sticking, a backgrounded window would fall back
        // to xnas default InactiveSleepTime and cap around 40fps, so just keep
        // hammering them while an episode runs. cheap, two property sets a frame
        static void KeepFast(object game)
        {
            try
            {
                if (piFixedStep == null)
                {
                    var t = game.GetType();
                    piFixedStep = t.GetProperty("IsFixedTimeStep");
                    piInactiveSleep = t.GetProperty("InactiveSleepTime");
                }
                if (piFixedStep != null) piFixedStep.SetValue(game, false, null);
                if (piInactiveSleep != null) piInactiveSleep.SetValue(game, TimeSpan.Zero, null);
            }
            catch { }
        }

        static void SlowClock(object game)
        {
            if (!fastClock) return;
            try
            {
                var t = game.GetType();
                t.GetProperty("IsFixedTimeStep").SetValue(game, true, null);
                t.GetProperty("InactiveSleepTime").SetValue(game, TimeSpan.FromMilliseconds(20.0), null);
                var gfx = t.GetField("graphics", BindingFlags.NonPublic | BindingFlags.Instance);
                object gdm = gfx != null ? gfx.GetValue(game) : null;
                if (gdm != null)
                {
                    gdm.GetType().GetProperty("SynchronizeWithVerticalRetrace").SetValue(gdm, true, null);
                    gdm.GetType().GetMethod("ApplyChanges").Invoke(gdm, null);
                }
                fastClock = false;
                rendering = true;
                Log("normal clock restored");
            }
            catch (Exception e) { Log("clock restore failed: " + e.Message); }
        }

        // hold each frame back til its slot comes up. resyncs after stalls so a
        // slow python step doesnt cause a fast forward burst right after
        static void Pace()
        {
            int fps = maxFps;
            if (fps <= 0)
            {
                nextFrameDueMs = 0;
                return;
            }
            long interval = 1000 / fps;
            long now = pacer.ElapsedMilliseconds;
            if (nextFrameDueMs == 0 || now > nextFrameDueMs + 1000)
                nextFrameDueMs = now;
            long wait = nextFrameDueMs - now;
            if (wait > 0)
                Thread.Sleep((int)wait);
            nextFrameDueMs += interval;
        }

        static void SuppressDraw(object game)
        {
            try
            {
                if (suppressDraw == null)
                    suppressDraw = game.GetType().GetMethod("SuppressDraw");
                if (suppressDraw != null)
                    suppressDraw.Invoke(game, null);
            }
            catch { } // worst case the ShouldRender weave still skips our Draw body
        }

        // ---- protocol ----

        // the message the env consumes. every field in it every time cause the
        // env side indexes straight into this dict. besides the grid we also
        // ship the scalars the model needs to act on, you cant decide to buy
        // dynamite without knowing your gold
        static string BuildState(object game, float reward, bool terminated, bool truncated)
        {
            // read each scalar once, the control panel reuses them off the statics
            int gold = GameState.Gold(game);
            long score = GameState.Score(game);
            int dwarves = GameState.DwarfCount(game);
            int timeLeft = GameState.TimeLeft(game);
            StatGold = gold;
            StatScore = score;
            StatDwarves = dwarves;
            StatTimeLeft = timeLeft;

            var sb = new StringBuilder(ObsW * ObsH * 2 + 256);
            sb.Append("{\"map_grid\":");
            MiniJson.AppendIntArray(sb, GameState.ReadGrid(game, ObsW, ObsH));
            sb.Append(",\"immediate_reward\":").Append(reward.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"terminated\":").Append(terminated ? "true" : "false");
            sb.Append(",\"truncated\":").Append(truncated ? "true" : "false");
            sb.Append(",\"gold\":").Append(gold);
            sb.Append(",\"score\":").Append(score);
            sb.Append(",\"dwarves\":").Append(dwarves);
            sb.Append(",\"time_left\":").Append(timeLeft);
            sb.Append(",\"city_hp\":").Append(GameState.CityHP(game));
            sb.Append(",\"action_ok\":").Append(lastActionOk ? "true" : "false");
            sb.Append(",\"crop_x\":").Append(GameState.LastCropX);
            sb.Append(",\"crop_y\":").Append(GameState.LastCropY);
            sb.Append(",\"tick\":").Append(frame);
            sb.Append('}');
            return sb.ToString();
        }

        static void Send(string json)
        {
            var sock = ws;
            if (sock == null || !sock.Connected)
            {
                Log("send skipped, not connected");
                return;
            }
            try { sock.SendText(json); }
            catch (Exception e) { Log("send failed: " + e.Message); }
        }

        // background thread that keeps a connection up, drops whatever arrives
        // into the mailbox and wakes the game thread
        static void SocketLoop()
        {
            while (true)
            {
                try
                {
                    var client = new WsClient();
                    if (!client.Connect(host, port, "/"))
                    {
                        Thread.Sleep(3000); // env not up yet, keep knocking
                        continue;
                    }
                    ws = client;
                    lock (gate) { envConnected = true; }
                    Log("connected to env");

                    while (true)
                    {
                        string msg = client.ReceiveText();
                        if (msg == null) break;
                        var cmd = MiniJson.Parse(msg);
                        lock (gate)
                        {
                            mailbox = cmd;
                            Monitor.PulseAll(gate);
                        }
                    }

                    Log("connection lost, retrying");
                    ws = null;
                    lock (gate)
                    {
                        envConnected = false;
                        Monitor.PulseAll(gate); // unblock the game thread
                    }
                }
                catch (Exception e)
                {
                    Log("socket loop: " + e.Message);
                    ws = null;
                    lock (gate) { envConnected = false; Monitor.PulseAll(gate); }
                    Thread.Sleep(3000);
                }
            }
        }

        internal static void LogLine(string msg)
        {
            Log(msg);
        }

        // crude file log so theres something to read when the game is running
        // fullscreen and eating every input. never let this throw into the game
        static void Log(string msg)
        {
            try
            {
                File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
