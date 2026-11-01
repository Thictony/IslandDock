using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IslandSetup;

/// <summary>The one window of the installer (or uninstaller), dark like the island.</summary>
internal sealed class SetupForm : Form
{
    static readonly Color Back = Color.FromArgb(0x1A, 0x1A, 0x1D), Text1 = Color.FromArgb(0xF2, 0xF2, 0xF2),
        Text2 = Color.FromArgb(0xA0, 0xA0, 0xA6), Accent = Color.FromArgb(0x4C, 0xC2, 0xFF), Button2 = Color.FromArgb(0x33, 0x33, 0x38);

    readonly bool install;
    readonly Label heading, body, status;
    readonly CheckBox option, launch;
    readonly Button primary, secondary;
    readonly ProgressBar progress;
    bool finished;

    public SetupForm(bool install)
    {
        this.install = install;
        Text = install ? "Install IslandDock" : "Uninstall IslandDock";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(500, 300);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Back;
        ForeColor = Text1;
        Font = new Font("Segoe UI", 10f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var badge = new Panel { Location = new Point(32, 30), Size = new Size(52, 52) };
        badge.Paint += (_, e) =>
        {
            // A little island: a dark pill on an accent circle.
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var circle = new SolidBrush(Color.FromArgb(0x40, Accent));
            e.Graphics.FillEllipse(circle, 0, 0, badge.Width - 1, badge.Height - 1);
            using var pill = new GraphicsPath();
            float w = badge.Width * 0.56f, h = badge.Height * 0.2f, x = (badge.Width - w) / 2, y = (badge.Height - h) / 2;
            pill.AddArc(x, y, h, h, 90, 180);
            pill.AddArc(x + w - h, y, h, h, 270, 180);
            pill.CloseFigure();
            using var fill = new SolidBrush(Accent);
            e.Graphics.FillPath(fill, pill);
        };

        heading = new Label
        {
            Location = new Point(100, 30), AutoSize = true, Font = new Font("Segoe UI Semibold", 16f),
            Text = install ? (Setup.IsInstalled ? "Update IslandDock" : "Install IslandDock") : "Uninstall IslandDock",
        };
        body = new Label
        {
            Location = new Point(102, 68), Size = new Size(370, 64), ForeColor = Text2,
            Text = install
                ? "A dynamic island for Windows: dock windows as tabs, control media, see notifications.\nInstalls for you only; no admin rights needed. Everything it needs is included."
                : "This removes IslandDock, its shortcuts and its startup entry. Any windows docked in it are handed back first.",
        };
        option = new CheckBox
        {
            Location = new Point(102, 142), AutoSize = true, ForeColor = Text1,
            Text = install ? "Add a desktop shortcut" : "Also delete my settings",
        };
        launch = new CheckBox
        {
            Location = new Point(102, 170), AutoSize = true, ForeColor = Text1, Checked = true, Text = "Start IslandDock when done",
            Visible = install,
        };
        progress = new ProgressBar { Location = new Point(102, 206), Size = new Size(370, 6), Style = ProgressBarStyle.Marquee, Visible = false };
        status = new Label { Location = new Point(100, 218), Size = new Size(370, 22), ForeColor = Text2 };

        primary = MakeButton(install ? (Setup.IsInstalled ? "Update" : "Install") : "Uninstall", Accent, Color.FromArgb(0x0B, 0x1A, 0x24));
        primary.Location = new Point(ClientSize.Width - 32 - primary.Width, 246);
        primary.Click += async (_, _) => await Run();
        secondary = MakeButton("Cancel", Button2, Text1);
        secondary.Location = new Point(primary.Left - 8 - secondary.Width, 246);
        secondary.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { badge, heading, body, option, launch, progress, status, primary, secondary });
        AcceptButton = primary;
        CancelButton = secondary;
    }

    static Button MakeButton(string text, Color back, Color fore)
    {
        var b = new Button
        {
            Text = text, Size = new Size(104, 34), FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = fore,
            Font = new Font("Segoe UI Semibold", 10f), Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.15f);
        b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(back, 0.05f);
        return b;
    }

    async Task Run()
    {
        if (finished) { Close(); return; }
        primary.Enabled = secondary.Enabled = option.Enabled = launch.Enabled = false;
        progress.Visible = true;
        bool opt = option.Checked;
        void Report(string s) => BeginInvoke(new Action(() => status.Text = s));
        try
        {
            if (install) await Task.Run(() => Setup.Install(opt, Report));
            else await Task.Run(() => Setup.Uninstall(opt, Report));
        }
        catch (Exception ex)
        {
            progress.Visible = false;
            status.ForeColor = Color.FromArgb(0xFF, 0x6B, 0x60);
            status.Text = ex.Message;
            status.Size = new Size(370, 40);
            primary.Enabled = secondary.Enabled = option.Enabled = launch.Enabled = true;
            primary.Text = "Try again";
            return;
        }

        progress.Visible = false;
        finished = true;
        heading.Text = install ? "IslandDock is installed" : "IslandDock was removed";
        body.Text = install
            ? "Find it in the Start menu any time. It starts with Windows; turn that off from the island's menu.\nRemove it from Settings › Apps like any other app."
            : "Thanks for trying it.";
        option.Visible = launch.Visible = false;
        status.Text = "";
        secondary.Visible = false;
        primary.Text = "Close";
        primary.Enabled = true;
        if (install && launch.Checked) { try { Setup.Launch(); } catch { } }
    }
}
