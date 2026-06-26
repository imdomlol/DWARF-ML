using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace DwarfsMod
{
    // one game world = one Game1 instance + its own lockstep/episode/reward state
    // and its own socket to a single env worker. all the state that used to be
    // static on Bridge (it assumed one game) lives here now, so M of these can run
    // side by side in one process, each driven independently. today there is
    // exactly one, the host; the multi-world host loop (Path C) will tick extra
    // worlds by calling Update() on them directly.
    //
    // host-level concerns that belong to the single real window/device -- the
    // rendering toggle, the clock knobs, draw suppression, frame pacing -- stay on
    // Bridge; a World calls back into it for those.
    internal sealed class World
    {
        internal enum Phase { Free, Resetting, Running }

        // Hosted = the one Game1 XNA drives through Game.Run; its frames advance by
        // returning from the woven Update hook and it resets through the game's own
        // fade transition. Detached = an extra world the multi-world host loop ticks
        // by hand (WorldSim.DriveFrame) and resets by rebuilding the level directly,
        // no window/fade involved.
        internal enum Mode { Hosted, Detached }

        // 4 cameras, each 30x30, centered on extreme digger dwarves (N/E/S/W)
        const int CamW = 30;
        const int CamH = 30;

        // give up on lockstep if the env goes quiet for this long, that way a dead
        // trainer hands the game back instead of freezing it forever
        const int CommandTimeoutMs = 120000;

        // a resets fade transition normally lands in ~16 frames once the game is on
        // the menu but at boot theres intro screens to ride out first
        const int ResetTimeoutFrames = 7200;
        const int ForceMenuAfterFrames = 600;

        // the Game1 this world wraps. attached once known (first Update hook for
        // the host; right after CreateDetached for extra worlds)
        internal object Game;

        readonly Mode mode;

        // networking, one socket per world so each talks to its own env worker.
        // set DWARFS_BRIDGE_PORT before launch to move the host off 8765
        readonly string host;
        readonly int port;
        readonly string logPath;
        WsClient ws;
        volatile bool envConnected;

        // single slot mailbox, the env is strictly lockstep so theres never more
        // than one command in flight
        readonly object gate = new object();
        Dictionary<string, object> mailbox;

        // lockstep / episode
        Phase phase = Phase.Free;
        long frame;
        int stepFramesLeft;    // frames left before the current STEP replies
        int actionRepeat = 1;  // frames each STEP advances, comes from RESET
        bool lastActionOk;     // did the last action actually go through
        bool levelGenerated;   // set by the GenerateLevel hook
        bool resetTriggered;   // fade kicked, waiting on generation
        bool forcedMenu;
        long resetStartedAt;
        string resetDifficulty = "Easy";
        int resetTimeMode;
        int pendingSeed;
        bool hasPendingSeed;
        long lastScore;
        int lastHazardTiles;   // flooded tile count last step, for the spread penalty

        // reward shaping, set per episode from RESET so tuning happens on the
        // python side. losing the city before the timer costs death_penalty,
        // running out the clock is the natural end so that costs nothing
        float rewardDeathPenalty = 1500f;
        float rewardInvalidAction;
        float rewardHazard;    // charged per active water/lava front each step
        float rewardInstantSeal; // bonus per instant-seal achievement triggered this step
        int lastCaveCount;     // lCave list count at last BuildState, for cave event detection
        int lastInstantSeal;   // iInstantSeal counter at last StepReward, for delta
        int stepInstantSealDelta; // filled by StepReward, read by the next BuildState call

        // per-camera crop origins on the full map (0=N 1=E 2=S 3=W, -1 means no dwarf).
        // updated each BuildState so the next action translates window coords correctly
        readonly int[] camOriginX = new int[] { -1, -1, -1, -1 };
        readonly int[] camOriginY = new int[] { -1, -1, -1, -1 };

        // last reported scalars, the control panel reads these off the host world
        // (via Bridge) so theyre at most one step stale
        internal long StatScore;
        internal int StatGold;
        internal int StatDwarves;
        internal int StatTimeLeft;

        internal World(string host, int port, string logPath, Mode mode = Mode.Hosted)
        {
            this.host = host;
            this.port = port;
            this.logPath = logPath;
            this.mode = mode;
        }

        internal long Frame { get { return frame; } }
        internal int Port { get { return port; } }
        internal bool EnvConnected { get { return envConnected; } }
        internal bool EpisodeActive { get { return phase != Phase.Free; } }

        internal void StartSocket()
        {
            var t = new Thread(SocketLoop);
            t.IsBackground = true;
            t.Name = "bridge-socket-" + port;
            t.Start();
        }

        // the per-frame lockstep gate. while an episode is live the frame doesnt
        // run until the env says so. for the host this is called from the woven
        // Game1.Update hook; the multi-world loop calls it on extra worlds too
        internal void Update()
        {
            object game = Game;
            frame++;
            if (!Bridge.RenderingOn)
                Bridge.SuppressDraw(game);
            if (phase != Phase.Free)
            {
                Bridge.KeepFast(game);
                Bridge.Pace();
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

                bool terminated;
                float reward = StepReward(game, out terminated);
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
                            Bridge.SlowClock(game);
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

        // delivered by the socket thread, drop into the single slot and wake the
        // game thread thats parked in the command loop
        internal void Deliver(Dictionary<string, object> cmd)
        {
            lock (gate)
            {
                mailbox = cmd;
                Monitor.PulseAll(gate); // wakes a hosted world blocked in Update
            }
            if (mode == Mode.Detached)
                Bridge.NotifyWork(); // wakes the multi-world scheduler if it's idling
        }

        // true means "advance the frame now"
        bool HandleCommand(Dictionary<string, object> cmd, object game)
        {
            string name = MiniJson.GetString(cmd, "command", "");
            switch (name)
            {
                case "RESET":
                {
                    string modeStr = ApplyResetParams(cmd);

                    // episodes run headless unless asked, watching is opt in.
                    // the panel checkbox can still flip it back on mid run
                    Bridge.SetRenderState(MiniJson.GetBool(cmd, "render", false),
                        MiniJson.GetInt(cmd, "render_fps", 0));

                    Log("RESET: " + resetDifficulty + " " + modeStr +
                        (hasPendingSeed ? " seed " + pendingSeed : " unseeded") +
                        ", repeat " + actionRepeat +
                        ", game in state " + GameControl.GameStateId(game));
                    Bridge.FastClock(game);
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
                    int camera = MiniJson.GetInt(cmd, "camera", 0);
                    int x = MiniJson.GetInt(cmd, "x", -1);
                    int y = MiniJson.GetInt(cmd, "y", -1);
                    lastActionOk = ApplyAction(game, action, camera, x, y);
                    stepFramesLeft = actionRepeat;
                    return true;
                }

                case "RENDER":
                    Bridge.SetRenderState(MiniJson.GetBool(cmd, "enabled", true),
                        MiniJson.GetInt(cmd, "max_fps", 0));
                    Log("rendering -> " + Bridge.RenderingOn +
                        (Bridge.MaxFps > 0 ? " at " + Bridge.MaxFps + " fps" : " unthrottled"));
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

        // ---- detached worlds (multi-world host loop ticks these by hand) ----

        // the threaded driver: each detached world runs this on its own thread, so
        // worlds tick in parallel across cores instead of one-at-a-time on the game
        // thread. blocks until its env sends a command, processes it (which
        // hand-drives the sim), replies, repeats. one sender per socket, isolation
        // proven by multiworld_test.py (same-seed worlds stay byte-identical).
        internal void RunLoop()
        {
            while (true)
            {
                Dictionary<string, object> cmd;
                lock (gate)
                {
                    while (mailbox == null) Monitor.Wait(gate);
                    cmd = mailbox;
                    mailbox = null;
                }
                HandleDetachedSafely(cmd);
            }
        }

        // run a detached command, but guarantee SOME reply gets back to the env so
        // a throw (RESET build failure, etc.) can't hang it waiting on request().
        // the STEP path catches its own sim throws to send a real terminated state;
        // this is the backstop for everything else.
        void HandleDetachedSafely(Dictionary<string, object> cmd)
        {
            try { HandleDetached(cmd, Game); }
            catch (Exception e)
            {
                Log("detached command failed, replying terminated: " + e);
                phase = Phase.Free;
                try { Send(MinimalTerminatedState(0f)); } catch { }
            }
        }

        // the serial driver alternative: called once per scheduler pass. non-
        // blocking: process at most one queued command and return whether there was
        // one (the scheduler uses that to decide whether to idle-wait). used when
        // DWARFS_BRIDGE_SERIAL=1 forces every world onto the one game thread.
        internal bool Pump()
        {
            if (Game == null) return false;
            Dictionary<string, object> cmd;
            lock (gate)
            {
                cmd = mailbox;
                mailbox = null;
            }
            if (cmd == null) return false;
            HandleDetachedSafely(cmd);
            return true;
        }

        void HandleDetached(Dictionary<string, object> cmd, object game)
        {
            string name = MiniJson.GetString(cmd, "command", "");
            switch (name)
            {
                case "RESET":
                {
                    string modeStr = ApplyResetParams(cmd);
                    Log("RESET (detached): " + resetDifficulty + " " + modeStr +
                        (hasPendingSeed ? " seed " + pendingSeed : " unseeded") +
                        ", repeat " + actionRepeat);
                    // no fade, no intro screens: rebuild the level in place and
                    // reply with the first observation immediately
                    WorldSim.BuildLevel(game, resetDifficulty, resetTimeMode, pendingSeed, hasPendingSeed);
                    FinishReset(game);
                    return;
                }

                case "STEP":
                {
                    int action = MiniJson.GetInt(cmd, "action", 0);
                    int camera = MiniJson.GetInt(cmd, "camera", 0);
                    int x = MiniJson.GetInt(cmd, "x", -1);
                    int y = MiniJson.GetInt(cmd, "y", -1);
                    bool terminated;
                    float reward;
                    try
                    {
                        lastActionOk = ApplyAction(game, action, camera, x, y);
                        // drive the burst of sim frames by hand, then score + report
                        for (int i = 0; i < actionRepeat; i++)
                        {
                            frame++;
                            WorldSim.DriveFrame(game);
                        }
                        reward = StepReward(game, out terminated);
                    }
                    catch (Exception e)
                    {
                        // a sim-internal throw (e.g. a game-over path the headless
                        // build doesnt fully set up) must not leave the env hanging
                        // on a reply. end the episode and let the trainer reset.
                        Log("STEP sim threw, ending episode: " + e);
                        reward = 0f;
                        terminated = true;
                        phase = Phase.Free;
                    }
                    SendStateSafe(game, reward, terminated);
                    return;
                }

                case "RENDER":
                    // detached worlds have no window of their own, nothing to toggle
                    return;

                case "QUIT":
                    // one env worker leaving doesnt tear down the shared process or
                    // its siblings; just park this world until its next RESET
                    Log("QUIT received (detached), parking world");
                    phase = Phase.Free;
                    return;

                default:
                    Log("unknown command (detached): " + name);
                    return;
            }
        }

        // parse the RESET fields shared by both drive modes (difficulty, time mode,
        // seed, action_repeat, reward weights). returns the raw mode string for
        // logging. render/clock are host-level and handled by the hosted caller only
        string ApplyResetParams(Dictionary<string, object> cmd)
        {
            string modeStr = MiniJson.GetString(cmd, "mode", "m5");
            resetDifficulty = MiniJson.GetString(cmd, "difficulty", "Easy");
            resetTimeMode = GameControl.TimeModeFromString(modeStr);
            int seed = MiniJson.GetInt(cmd, "seed", int.MinValue);
            hasPendingSeed = seed != int.MinValue;
            pendingSeed = seed;
            actionRepeat = MiniJson.GetInt(cmd, "action_repeat", 1);
            if (actionRepeat < 1) actionRepeat = 1;
            if (actionRepeat > 240) actionRepeat = 240;
            rewardDeathPenalty = MiniJson.GetFloat(cmd, "death_penalty", 1500f);
            rewardInvalidAction = MiniJson.GetFloat(cmd, "invalid_action", 0f);
            rewardHazard = MiniJson.GetFloat(cmd, "hazard_penalty", 0f);
            rewardInstantSeal = MiniJson.GetFloat(cmd, "instant_seal", 0f);
            return modeStr;
        }

        // score delta this step, plus the penalties. shared by hosted + detached.
        // sets terminated: state 1 is the only "still playing" state, 2 is the game
        // over sequence (city flooded / blown up) and anything else is a menu
        float StepReward(object game, out bool terminated)
        {
            long score = GameState.Score(game);
            float reward = score - lastScore;
            lastScore = score;

            int state = GameControl.GameStateId(game);
            int timeLeft = GameState.TimeLeft(game);
            terminated = state != 1 || timeLeft <= 0 || GameState.CityHP(game) <= 0;

            if (!lastActionOk)
                reward -= rewardInvalidAction;
            if (terminated && timeLeft > 0)
                reward -= rewardDeathPenalty; // died, didnt make the timer
            // sting for water/lava actively spreading. count how many new flooded
            // tiles showed up since last step, so a sealed cave costs nothing but a
            // flood on the move racks it up til its walled off
            if (rewardHazard != 0f)
            {
                int hz = GameState.HazardTiles(game);
                int spread = hz - lastHazardTiles;
                lastHazardTiles = hz;
                if (spread > 0)
                    reward -= rewardHazard * spread;
            }
            int currentSeal = GameState.InstantSealCount(game);
            stepInstantSealDelta = currentSeal - lastInstantSeal;
            lastInstantSeal = currentSeal;
            if (stepInstantSealDelta > 0 && rewardInstantSeal != 0f)
                reward += stepInstantSealDelta * rewardInstantSeal;
            return reward;
        }

        // translate window-space (camera, x, y) to map coords and dispatch the action.
        // camera 0-3 selects which of the 4 digger-following views to use;
        // -1 origin means no digger assigned there, the action is refused
        bool ApplyAction(object game, int action, int camera, int x, int y)
        {
            if (action == 0) return true; // idle always fine, coords ignored

            if (camera < 0 || camera >= 4) return false;
            int ox = camOriginX[camera];
            int oy = camOriginY[camera];
            if (ox < 0 || oy < 0) return false; // no digger in this camera slot

            if (x < 0 || y < 0 || x >= CamW || y >= CamH) return false;
            int mx = ox + x, my = oy + y;

            switch (action)
            {
                case 1: return GameAction.PlaceDynamite(game, mx, my);
                case 2: return GameAction.PlaceWall(game, mx, my);
                case 3: // green arrows, 3 up 4 right 5 down 6 left
                case 4:
                case 5:
                case 6: return GameAction.PlaceArrow(game, mx, my, action - 3);
                case 7: return GameAction.PlaceOutpost(game, mx, my);
                case 8: return GameAction.ReinforceWall(game, mx, my);
                case 9: return GameAction.OutpostToggleDigger(game, mx, my);
                case 10: return GameAction.OutpostSpawnWarrior(game, mx, my);
                case 11: return GameAction.OutpostRecall(game, mx, my);
                case 12: return GameAction.OutpostCannon(game, mx, my);
                case 13: return GameAction.OutpostToggleTrain(game, mx, my);
                default: return false;
            }
        }

        void FinishReset(object game)
        {
            levelGenerated = false;
            hasPendingSeed = false;
            phase = Phase.Running;
            lastActionOk = true;
            lastScore = GameState.Score(game);
            lastHazardTiles = rewardHazard != 0f ? GameState.HazardTiles(game) : 0;
            lastCaveCount = GameState.CaveCount(game);
            lastInstantSeal = GameState.InstantSealCount(game);
            stepInstantSealDelta = 0;
            Send(BuildState(game, 0f, false, false));
        }

        // woven into the start of Game1.GenerateLevel (host) or called from the
        // multi-world loop right before an extra world's level gets built.
        // reseeding here means nothing else can pull numbers from the rng between
        // the seed and the generation. heads up, restarting from in game runs the
        // generator TWICE in one transition so we keep reseeding til the reset is
        // done. the last generation is the one that sticks and this way every pass
        // starts from the same rng
        internal void BeforeGenerateLevel(object game)
        {
            if (phase != Phase.Resetting) return; // campaign / manual starts
            if (hasPendingSeed)
            {
                GameControl.Reseed(game, pendingSeed);
                Log("level rng seeded with " + pendingSeed);
            }
            levelGenerated = true;
        }

        // ---- protocol ----

        // the message the env consumes. every field in it every time cause the
        // env side indexes straight into this dict. 4 cameras (30x30 each) centered
        // on the northmost/eastmost/southmost/westmost digger dwarves, zero-filled
        // when fewer than 4 diggers exist
        string BuildState(object game, float reward, bool terminated, bool truncated)
        {
            int gold = GameState.Gold(game);
            long score = GameState.Score(game);
            int dwarves = GameState.DwarfCount(game);
            int timeLeft = GameState.TimeLeft(game);
            StatGold = gold; StatScore = score; StatDwarves = dwarves; StatTimeLeft = timeLeft;

            int costWall, costDynamite, costArrow, costTower, costWarrior;
            GameAction.ReadCosts(game, out costWall, out costDynamite,
                out costArrow, out costTower, out costWarrior);

            // 4 extreme digger positions (N=0 E=1 S=2 W=3), -1 if absent
            int nx, ny, ex, ey, sx, sy, wx, wy;
            GameState.FindExtremeDiggers(game,
                out nx, out ny, out ex, out ey, out sx, out sy, out wx, out wy);
            int[] dwX = new int[] { nx, ex, sx, wx };
            int[] dwY = new int[] { ny, ey, sy, wy };

            var terrains = new int[4][];
            var dwfGrids = new int[4][];
            var enmGrids = new int[4][];
            for (int i = 0; i < 4; i++)
            {
                if (dwX[i] < 0)
                {
                    terrains[i] = new int[CamW * CamH];
                    dwfGrids[i] = new int[CamW * CamH];
                    enmGrids[i] = new int[CamW * CamH];
                    camOriginX[i] = -1;
                    camOriginY[i] = -1;
                }
                else
                {
                    int cx, cy;
                    terrains[i] = GameState.ReadGridAt(game, CamW, CamH, dwX[i], dwY[i], out cx, out cy);
                    dwfGrids[i] = GameState.ReadDwarfGrid(game, CamW, CamH, cx, cy);
                    enmGrids[i] = GameState.ReadEnemyGrid(game, CamW, CamH, cx, cy);
                    camOriginX[i] = cx;
                    camOriginY[i] = cy;
                }
            }

            // cave event: did a new cave open since the last step?
            int currentCaveCount = GameState.CaveCount(game);
            int caveOpened = 0, caveX = -1, caveY = -1;
            if (currentCaveCount > lastCaveCount)
            {
                int caveType;
                GameState.GetCave(game, 0, out caveX, out caveY, out caveType);
                caveOpened = caveType;
            }
            lastCaveCount = currentCaveCount;

            var sb = new StringBuilder(4 * 3 * CamW * CamH * 2 + 512);
            for (int i = 0; i < 4; i++)
            {
                sb.Append(i == 0 ? "{" : ",");
                sb.Append("\"cam").Append(i).Append("_terrain\":");
                MiniJson.AppendIntArray(sb, terrains[i]);
                sb.Append(",\"cam").Append(i).Append("_dwarves\":");
                MiniJson.AppendIntArray(sb, dwfGrids[i]);
                sb.Append(",\"cam").Append(i).Append("_enemies\":");
                MiniJson.AppendIntArray(sb, enmGrids[i]);
            }
            sb.Append(",\"cam_origins\":[");
            sb.Append(nx).Append(',').Append(ny).Append(',');
            sb.Append(ex).Append(',').Append(ey).Append(',');
            sb.Append(sx).Append(',').Append(sy).Append(',');
            sb.Append(wx).Append(',').Append(wy).Append(']');
            sb.Append(",\"immediate_reward\":").Append(reward.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"terminated\":").Append(terminated ? "true" : "false");
            sb.Append(",\"truncated\":").Append(truncated ? "true" : "false");
            sb.Append(",\"gold\":").Append(gold);
            sb.Append(",\"score\":").Append(score);
            sb.Append(",\"dwarves\":").Append(dwarves);
            sb.Append(",\"time_left\":").Append(timeLeft);
            sb.Append(",\"city_hp\":").Append(GameState.CityHP(game));
            sb.Append(",\"cost_wall\":").Append(costWall);
            sb.Append(",\"cost_dynamite\":").Append(costDynamite);
            sb.Append(",\"cost_arrow\":").Append(costArrow);
            sb.Append(",\"cost_tower\":").Append(costTower);
            sb.Append(",\"cost_warrior\":").Append(costWarrior);
            sb.Append(",\"cave_opened\":").Append(caveOpened);
            sb.Append(",\"cave_x\":").Append(caveX);
            sb.Append(",\"cave_y\":").Append(caveY);
            sb.Append(",\"instant_seal_delta\":").Append(stepInstantSealDelta);
            sb.Append(",\"action_ok\":").Append(lastActionOk ? "true" : "false");
            sb.Append(",\"tick\":").Append(frame);
            sb.Append('}');
            return sb.ToString();
        }

        // send the normal observation, but if BuildState itself throws on a
        // half-wrecked world fall back to a minimal terminated reply so the env
        // still gets a response and can reset instead of blocking
        void SendStateSafe(object game, float reward, bool terminated)
        {
            try { Send(BuildState(game, reward, terminated, false)); }
            catch (Exception e)
            {
                Log("BuildState threw, sending minimal terminated reply: " + e);
                Send(MinimalTerminatedState(reward));
            }
        }

        // zero grids + scalars, terminated. just enough for the env to unpack a
        // reply and end the episode when the real state can't be read
        string MinimalTerminatedState(float reward)
        {
            var empty = new int[CamW * CamH];
            var sb = new StringBuilder(4 * 3 * CamW * CamH * 2 + 256);
            for (int i = 0; i < 4; i++)
            {
                sb.Append(i == 0 ? "{" : ",");
                sb.Append("\"cam").Append(i).Append("_terrain\":");
                MiniJson.AppendIntArray(sb, empty);
                sb.Append(",\"cam").Append(i).Append("_dwarves\":");
                MiniJson.AppendIntArray(sb, empty);
                sb.Append(",\"cam").Append(i).Append("_enemies\":");
                MiniJson.AppendIntArray(sb, empty);
            }
            sb.Append(",\"cam_origins\":[-1,-1,-1,-1,-1,-1,-1,-1]");
            sb.Append(",\"immediate_reward\":").Append(reward.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"terminated\":true,\"truncated\":false");
            sb.Append(",\"gold\":0,\"score\":0,\"dwarves\":0,\"time_left\":0,\"city_hp\":0");
            sb.Append(",\"cost_wall\":0,\"cost_dynamite\":0,\"cost_arrow\":0,\"cost_tower\":0,\"cost_warrior\":0");
            sb.Append(",\"cave_opened\":0,\"cave_x\":-1,\"cave_y\":-1,\"instant_seal_delta\":0");
            sb.Append(",\"action_ok\":").Append(lastActionOk ? "true" : "false");
            sb.Append(",\"tick\":").Append(frame);
            sb.Append('}');
            return sb.ToString();
        }

        void Send(string json)
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
        void SocketLoop()
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
                        Deliver(MiniJson.Parse(msg));
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

        // crude file log so theres something to read when the game is running
        // fullscreen and eating every input. never let this throw into the game
        void Log(string msg)
        {
            try
            {
                File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
