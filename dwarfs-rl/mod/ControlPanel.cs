using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace DwarfsMod
{
    // little status window that lives next to the game. shows what the bridge is
    // doing, lets you flip rendering on to actually watch a run (and slow it
    // down to something a human can follow) and can start/stop the training
    // script so the whole loop runs from one place. runs on its own thread,
    // everything it touches on the bridge is a volatile static
    public class ControlPanel : Form
    {
        Label lblBridge, lblEpisode, lblStats, lblRate;
        CheckBox chkRender;
        NumericUpDown numFps;
        TextBox txtCommand;
        Button btnRun;
        Process trainer;

        long lastFrame;
        DateTime lastTick = DateTime.Now;
        bool syncing; // true while the refresh timer is updating the checkbox

        public static void Open()
        {
            try
            {
                Application.Run(new ControlPanel());
            }
            catch (Exception)
            {
                // a dead panel shouldnt take the game down with it
            }
        }

        public ControlPanel()
        {
            Text = "Dwarfs bridge";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(40, 40);
            ClientSize = new Size(320, 240);

            lblBridge = AddLabel(12);
            lblEpisode = AddLabel(34);
            lblStats = AddLabel(56);
            lblRate = AddLabel(78);

            chkRender = new CheckBox();
            chkRender.Text = "render the game";
            chkRender.Checked = true;
            chkRender.Location = new Point(12, 106);
            chkRender.Width = 140;
            chkRender.CheckedChanged += OnRenderChanged;
            Controls.Add(chkRender);

            var lblFps = new Label();
            lblFps.Text = "fps cap (0 = none)";
            lblFps.Location = new Point(160, 108);
            lblFps.Width = 100;
            Controls.Add(lblFps);

            numFps = new NumericUpDown();
            numFps.Minimum = 0;
            numFps.Maximum = 240;
            numFps.Value = 0;
            numFps.Location = new Point(262, 105);
            numFps.Width = 46;
            numFps.ValueChanged += OnRenderChanged;
            Controls.Add(numFps);

            var lblCmd = new Label();
            lblCmd.Text = "training command:";
            lblCmd.Location = new Point(12, 140);
            lblCmd.Width = 296;
            Controls.Add(lblCmd);

            txtCommand = new TextBox();
            txtCommand.Text = "python python/train.py";
            txtCommand.Location = new Point(12, 160);
            txtCommand.Width = 296;
            Controls.Add(txtCommand);

            btnRun = new Button();
            btnRun.Text = "start training";
            btnRun.Location = new Point(12, 190);
            btnRun.Width = 296;
            btnRun.Height = 30;
            btnRun.Click += OnRunClicked;
            Controls.Add(btnRun);

            var refresh = new Timer();
            refresh.Interval = 500;
            refresh.Tick += OnRefresh;
            refresh.Start();
        }

        Label AddLabel(int y)
        {
            var l = new Label();
            l.Location = new Point(12, y);
            l.Width = 296;
            Controls.Add(l);
            return l;
        }

        void OnRefresh(object sender, EventArgs e)
        {
            // a RESET can flip rendering off underneath us, keep the box honest
            if (chkRender.Checked != Bridge.RenderingOn)
            {
                syncing = true;
                chkRender.Checked = Bridge.RenderingOn;
                syncing = false;
            }

            lblBridge.Text = "env: " + (Bridge.EnvConnected ? "connected" : "waiting for connection");
            lblEpisode.Text = Bridge.EpisodeActive
                ? "episode running, " + Bridge.StatTimeLeft + " ticks left"
                : "no episode (game runs free)";
            lblStats.Text = "score " + Bridge.StatScore + "   gold " + Bridge.StatGold +
                "   dwarves " + Bridge.StatDwarves;

            var now = DateTime.Now;
            double secs = (now - lastTick).TotalSeconds;
            long f = Bridge.Frame;
            double fps = secs > 0 ? (f - lastFrame) / secs : 0;
            lastFrame = f;
            lastTick = now;
            lblRate.Text = fps > 0
                ? fps.ToString("0") + " frames/s (" + (fps / 60.0).ToString("0.0") + "x real)"
                : "";

            // training script ended on its own
            if (trainer != null && trainer.HasExited)
            {
                trainer = null;
                btnRun.Text = "start training";
            }
        }

        void OnRenderChanged(object sender, EventArgs e)
        {
            if (syncing) return; // not a human click, just the box catching up
            Bridge.SetRendering(chkRender.Checked, (int)numFps.Value);
        }

        void OnRunClicked(object sender, EventArgs e)
        {
            if (trainer != null)
            {
                try { trainer.Kill(); }
                catch { }
                trainer = null;
                btnRun.Text = "start training";
                return;
            }
            try
            {
                // run it through cmd in its own console so the scripts output
                // has somewhere to go
                trainer = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/k " + txtCommand.Text,
                    UseShellExecute = true,
                });
                btnRun.Text = "stop training";
            }
            catch (Exception ex)
            {
                MessageBox.Show("couldn't start it: " + ex.Message);
            }
        }
    }
}
