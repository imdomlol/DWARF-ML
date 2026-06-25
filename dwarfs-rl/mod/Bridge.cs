using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

namespace DwarfsMod
{
    // the loader weaves calls to these static methods into Dwarfs.exe, the entry
    // point plus the update, input, draw and level gen methods. the mod never
    // references the game or xna directly, everything goes through reflection at
    // runtime so the build doesnt need any of the game dlls.
    //
    // Bridge is the coordinator. the actual lockstep/episode/reward/socket state
    // lives per world in World; one World wraps each Game1 instance and talks to
    // one env worker. Bridge keeps the registry of worlds keyed by Game1 instance,
    // routes the woven hooks to the right world, and owns the things that belong
    // to the single real window/device (the rendering toggle, the clock knobs,
    // draw suppression, frame pacing). today there is one world, the host; the
    // multi-world host loop (Path C) will build M-1 more and tick them by calling
    // World.Update() directly.
    //
    // how the wire works. the python gym env runs a websocket server per port and
    // each world connects to its own. env sends RESET / STEP / RENDER and every
    // state reply has map_grid, immediate_reward, terminated and truncated in it.
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

        // worlds keyed by their Game1 instance. only touched on the game thread
        // (the woven hooks and the multi-world loop all run there) so no lock
        // needed. hostWorld is the one XNA runs through Game.Run in single-instance
        // mode and the one the control panel reports on
        static readonly Dictionary<object, World> worlds = new Dictionary<object, World>();
        static World hostWorld;

        // multi-world (Path C). when DWARFS_BRIDGE_WORLDS > 1 the real Game1 XNA
        // boots becomes a headless driver that owns the device + content but plays
        // nothing; it builds N detached trainee worlds that share its infra and
        // ticks them all in lockstep from its Update hook, each on its own port.
        static bool multiWorld;
        static readonly List<World> trainees = new List<World>();
        static object multiHostGame;
        static bool traineesBuilt;
        // a detached world pulses this when a command lands so the scheduler can
        // park (instead of busy-spinning) whenever every world is idle
        static readonly object workSignal = new object();

        // the single window/device renders unless an episode asks for headless.
        // one real device so this is host-level, not per world
        static volatile bool rendering = true;
        static volatile int maxFps;

        // ---- woven entry points (loader matches these by name + IL) ----

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

                int worldCount = 1;
                string wc = Environment.GetEnvironmentVariable("DWARFS_BRIDGE_WORLDS");
                int parsedWc;
                if (!string.IsNullOrEmpty(wc) && int.TryParse(wc, out parsedWc) && parsedWc > 1)
                    worldCount = parsedWc;
                multiWorld = worldCount > 1;

                if (!multiWorld)
                {
                    Log("boot: bridge starting, will connect to ws://" + host + ":" + port);
                    // the host world's Game1 doesnt exist yet (Main hasnt built it),
                    // so attach it on the first Update hook. the socket can start
                    // knocking now though, the env retries until the game is up
                    hostWorld = new World(host, port, logPath);
                    hostWorld.StartSocket();
                }
                else
                {
                    // N detached trainee worlds on consecutive ports from the base.
                    // the Game1 instances get built on the first Update hook (once
                    // the real host has loaded content); the sockets can connect now
                    Log("boot: multi-world, " + worldCount + " worlds on ports " +
                        port + ".." + (port + worldCount - 1));
                    for (int i = 0; i < worldCount; i++)
                    {
                        int wp = port + i;
                        string wlog = Path.Combine(Path.GetTempPath(), "dwarfs_mod_" + wp + ".log");
                        var w = new World(host, wp, wlog, World.Mode.Detached);
                        trainees.Add(w);
                        w.StartSocket();
                    }
                }

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

        // woven into the start of Game1.Update. in single-instance mode it routes
        // to the world wrapping this Game1 and runs its lockstep gate. in
        // multi-world mode this Game1 is the headless driver: it builds the trainee
        // worlds once, then pumps them all in lockstep every frame.
        public static void BeforeUpdate(object game)
        {
            if (multiWorld)
            {
                if (!traineesBuilt) BuildTrainees(game);
                KeepFast(game); // the driver spins fast so trainees tick quickly
                SchedulerTick();
                return;
            }

            // Path C feasibility spike, one shot, only when DWARFS_BRIDGE_C_SPIKE=1
            MultiWorldSpike.MaybeRun(game);
            World w = Resolve(game);
            if (w != null) w.Update();
        }

        // build the detached trainee worlds off the real (host) Game1 once it has
        // loaded content. the host then plays nothing -- it just owns the device and
        // drives the trainees -- so suppress its drawing and run its clock flat out
        static void BuildTrainees(object hostGame)
        {
            multiHostGame = hostGame;
            int built = 0;
            foreach (World w in trainees)
            {
                object g = WorldSim.CreateDetached(hostGame);
                if (g != null)
                {
                    w.Game = g;
                    Register(w);
                    built++;
                }
                else
                {
                    Log("multiworld: a trainee world failed to build");
                }
            }
            rendering = false;   // headless driver, no window
            FastClock(hostGame); // strip xna's throttles so the loop runs full speed
            traineesBuilt = true;
            Log("multiworld: built " + built + " of " + trainees.Count + " trainee worlds");
        }

        // one pass of the multi-world loop: pump every trainee once. if nobody had
        // a command, park briefly so an idle process doesnt peg a core (a landing
        // command pulses workSignal to wake us right back up)
        static void SchedulerTick()
        {
            bool any = false;
            for (int i = 0; i < trainees.Count; i++)
                any |= trainees[i].Pump();
            if (!any)
            {
                lock (workSignal) { Monitor.Wait(workSignal, 5); }
            }
        }

        // a detached world's socket thread calls this when a command lands
        internal static void NotifyWork()
        {
            lock (workSignal) { Monitor.PulseAll(workSignal); }
        }

        // woven into the start of Game1.GenerateLevel, right before the world gets
        // built, so the bridge can reseed the rng and tell that a reset landed
        public static void BeforeGenerateLevel(object game)
        {
            World w = Resolve(game);
            if (w != null) w.BeforeGenerateLevel(game);
        }

        // woven into the start of Game1.Draw, false skips the frame entirely
        public static bool ShouldRender()
        {
            return rendering;
        }

        // woven into the start of the input readers. while an episode is live the
        // human mouse/keyboard cant be allowed to reach the game. agent actions
        // go in through internal calls anyway, a stray hover or click would mess
        // up the episode, plus the raw input path is where the games silent crash
        // and exit handler lives (cursor coords outside the window). gated on the
        // host world since thats the one with the real window
        public static bool ShouldReadInput()
        {
            // multi-world is always a headless training driver, never hand it input
            if (multiWorld) return false;
            return hostWorld == null || !hostWorld.EpisodeActive;
        }

        // find the world wrapping this Game1, attaching the host on its first
        // sighting. extra worlds are pre-registered by the multi-world loop
        static World Resolve(object game)
        {
            World w;
            if (worlds.TryGetValue(game, out w))
                return w;
            if (hostWorld != null && hostWorld.Game == null)
            {
                hostWorld.Game = game;
                worlds[game] = hostWorld;
                return hostWorld;
            }
            return null;
        }

        // register a world the multi-world loop has built, so the GenerateLevel
        // hook can route to it (its Update gets driven by the loop, not the hook)
        internal static void Register(World w)
        {
            if (w != null && w.Game != null) worlds[w.Game] = w;
        }

        // ---- control panel surface ----
        // single-instance reports the host world; multi-world reports world 0 as a
        // representative (the panel is a human convenience, suppressed during the
        // GUI-less parallel runs anyway)

        static World Primary { get { return hostWorld ?? (trainees.Count > 0 ? trainees[0] : null); } }

        public static long Frame { get { var w = Primary; return w != null ? w.Frame : 0; } }
        public static bool RenderingOn { get { return rendering; } }

        public static bool EnvConnected
        {
            get
            {
                if (!multiWorld) { var w = hostWorld; return w != null && w.EnvConnected; }
                for (int i = 0; i < trainees.Count; i++) if (trainees[i].EnvConnected) return true;
                return false;
            }
        }

        public static bool EpisodeActive
        {
            get
            {
                if (!multiWorld) { var w = hostWorld; return w != null && w.EpisodeActive; }
                for (int i = 0; i < trainees.Count; i++) if (trainees[i].EpisodeActive) return true;
                return false;
            }
        }

        public static long StatScore { get { var w = Primary; return w != null ? w.StatScore : 0; } }
        public static int StatGold { get { var w = Primary; return w != null ? w.StatGold : 0; } }
        public static int StatDwarves { get { var w = Primary; return w != null ? w.StatDwarves : 0; } }
        public static int StatTimeLeft { get { var w = Primary; return w != null ? w.StatTimeLeft : 0; } }

        public static void SetRendering(bool on, int fps)
        {
            SetRenderState(on, fps);
            Log("panel: rendering -> " + on + (fps > 0 ? " at " + fps + " fps" : ""));
        }

        // set by RESET/RENDER commands and the panel. host-level, one window
        internal static void SetRenderState(bool on, int fps)
        {
            rendering = on;
            maxFps = fps;
        }

        internal static int MaxFps { get { return maxFps; } }

        // ---- host-level clock + pacing control ----
        // the game never touches these xna knobs itself so theyre all ours, and
        // they act on the single real window/device, so they live here not per world

        static bool fastClock;
        static MethodInfo suppressDraw;
        static PropertyInfo piFixedStep, piInactiveSleep;
        static readonly System.Diagnostics.Stopwatch pacer = System.Diagnostics.Stopwatch.StartNew();
        static long nextFrameDueMs;

        internal static void FastClock(object game)
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
        internal static void KeepFast(object game)
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

        internal static void SlowClock(object game)
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
        internal static void Pace()
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

        internal static void SuppressDraw(object game)
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

        // ---- logging ----

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
