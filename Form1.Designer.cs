namespace PathNote;

partial class Form1
{
    private System.ComponentModel.IContainer components = null;
    private NotifyIcon notifyIcon;
    private ContextMenuStrip trayMenu;
    private ToolStripMenuItem exitMenuItem;
    private FlowLayoutPanel flowPanel;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
            components.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();

        AutoScaleMode = AutoScaleMode.Font;
        Text = "PathNote";
        ClientSize = new Size(480, 760);
        MinimumSize = new Size(300, 200);
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.Manual;
        Opacity = 0.92;
        BackColor = Color.FromArgb(248, 250, 252);
        Icon = CreateAppIcon();

        notifyIcon = new NotifyIcon(components);
        notifyIcon.Text = "PathNote";
        notifyIcon.Icon = CreateAppIcon();
        notifyIcon.Visible = true;
        notifyIcon.DoubleClick += NotifyIcon_DoubleClick;

        trayMenu = new ContextMenuStrip();
        exitMenuItem = new ToolStripMenuItem("退出");
        exitMenuItem.Click += (_, _) => ExitApp();
        trayMenu.Items.Add(exitMenuItem);
        notifyIcon.ContextMenuStrip = trayMenu;

        flowPanel = new FlowLayoutPanel();
        flowPanel.Dock = DockStyle.Fill;
        flowPanel.FlowDirection = FlowDirection.TopDown;
        flowPanel.WrapContents = false;
        flowPanel.AutoScroll = true;
        flowPanel.Padding = new Padding(4);
        flowPanel.Resize += FlowPanel_Resize;

        Controls.Add(flowPanel);
    }
}
