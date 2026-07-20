using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PathNote;

public partial class Form1 : Form
{
    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_SHOWNOACTIVATE = 4;

    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const string PathsFile = "paths.json";
    private const string WindowFile = "window.json";

    private static readonly Color NormalBack = Color.FromArgb(240, 245, 255);
    private static readonly Color PinnedBack = Color.FromArgb(255, 248, 220);
    private static readonly Color NormalHover = Color.FromArgb(220, 235, 255);
    private static readonly Color PinnedHover = Color.FromArgb(255, 240, 200);

    private readonly string dataDir;
    private readonly string pathsFile;
    private readonly string windowFile;
    private bool forceClose;
    private bool ignoreNextClipboardChange;
    private readonly HashSet<string> pinnedPaths = new();

    public Form1()
    {
        InitializeComponent();

        dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PathNote");
        pathsFile = Path.Combine(dataDir, PathsFile);
        windowFile = Path.Combine(dataDir, WindowFile);

        LoadPaths();
        RestoreWindowBounds();
        ResizeEnd += (_, _) => SaveWindowBounds();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AddClipboardFormatListener(Handle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        RemoveClipboardFormatListener(Handle);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE)
            OnClipboardChanged();
        base.WndProc(ref m);
    }

    private void OnClipboardChanged()
    {
        if (ignoreNextClipboardChange)
        {
            ignoreNextClipboardChange = false;
            return;
        }

        string? path = null;

        if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList();
            if (files.Count > 0)
                path = files[0];
        }

        if (path == null && Clipboard.ContainsText())
        {
            var text = Clipboard.GetText()?.Trim().Trim('"');
            if (!string.IsNullOrEmpty(text))
                path = text;
        }

        if (string.IsNullOrEmpty(path)) return;

        try { path = Path.GetFullPath(path); }
        catch { return; }

        if (!File.Exists(path) && !Directory.Exists(path)) return;

        if (InvokeRequired)
            Invoke(() => AddPath(path));
        else
            AddPath(path);
    }

    private void AddPath(string path)
    {
        var existing = flowPanel.Controls
            .Cast<Control>()
            .FirstOrDefault(c => c.Tag?.ToString() == path);

        if (existing != null)
        {
            if (!pinnedPaths.Contains(path))
            {
                var pinnedItemCount = flowPanel.Controls
                    .Cast<Control>().Count(c => pinnedPaths.Contains(c.Tag?.ToString() ?? ""));
                var currentIndex = flowPanel.Controls.GetChildIndex(existing);
                var targetIndex = pinnedItemCount;
                if (currentIndex != targetIndex)
                {
                    flowPanel.Controls.SetChildIndex(existing, targetIndex);
                }
                existing.BackColor = NormalHover;
            }
            SavePaths();
            return;
        }

        var item = CreatePathItem(path);
        var pinCount = flowPanel.Controls
            .Cast<Control>().Count(c => pinnedPaths.Contains(c.Tag?.ToString() ?? ""));
        flowPanel.Controls.Add(item);
        flowPanel.Controls.SetChildIndex(item, pinCount);
        SavePaths();
        RestoreFromMinimized();
    }

    private void RestoreFromMinimized()
    {
        if (WindowState != FormWindowState.Minimized) return;
        ShowWindow(Handle, SW_SHOWNOACTIVATE);
    }

    private Button CreatePathItem(string path)
    {
        var isPinned = pinnedPaths.Contains(path);
        var btn = new Button
        {
            Text = "",
            Height = 44,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 1, MouseOverBackColor = isPinned ? PinnedHover : NormalHover },
            BackColor = isPinned ? PinnedBack : NormalBack,
            ForeColor = Color.FromArgb(30, 30, 30),
            Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold),
            Tag = path,
            Cursor = Cursors.Hand,
            TabStop = false,
            UseVisualStyleBackColor = false,
            Width = flowPanel.ClientSize.Width - flowPanel.Padding.Horizontal - 4
        };

        btn.Paint += (s, e) =>
        {
            if (s is not Button b) return;
            var text = (string?)b.Tag ?? "";
            var rect = new Rectangle(14, 0, b.ClientSize.Width - 18, b.ClientSize.Height);

            if (pinnedPaths.Contains(text))
            {
                using var brush = new SolidBrush(Color.FromArgb(200, 150, 50));
                e.Graphics.FillEllipse(brush, 5, b.ClientSize.Height / 2 - 3, 6, 6);
            }

            var textSize = TextRenderer.MeasureText(e.Graphics, text, b.Font, Size.Empty, TextFormatFlags.Default);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine;

            if (textSize.Width <= rect.Width)
            {
                TextRenderer.DrawText(e.Graphics, text, b.Font, rect, b.ForeColor, flags);
            }
            else
            {
                TextRenderer.DrawText(e.Graphics, TruncatePathMiddle(text, b.Font, rect.Width, e.Graphics), b.Font, rect, b.ForeColor, flags);
            }
        };

        btn.MouseEnter += (_, _) =>
        {
            btn.BackColor = pinnedPaths.Contains(path) ? PinnedHover : NormalHover;
        };
        btn.MouseLeave += (_, _) =>
        {
            btn.BackColor = pinnedPaths.Contains(path) ? PinnedBack : NormalBack;
        };

        btn.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            if (Control.ModifierKeys == Keys.Control)
                ClearAllPaths();
            else
                RemovePath(btn);
        };

        btn.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            var p = btn.Tag?.ToString() ?? "";
            if (Control.ModifierKeys == Keys.Control)
                OpenInExplorer(p);
            else if (Control.ModifierKeys == Keys.Shift)
                TogglePin(btn);
            else
                CopyPath(p);
        };

        btn.DoubleClick += (_, _) => OpenPath(path);

        return btn;
    }

    private void CopyPath(string path)
    {
        ignoreNextClipboardChange = true;
        try { Clipboard.SetText(path); }
        catch { }
    }

    private void TogglePin(Button btn)
    {
        var path = btn.Tag?.ToString() ?? "";
        if (string.IsNullOrEmpty(path)) return;

        if (pinnedPaths.Contains(path))
            pinnedPaths.Remove(path);
        else
            pinnedPaths.Add(path);

        var isPinned = pinnedPaths.Contains(path);
        btn.BackColor = isPinned ? PinnedBack : NormalBack;
        btn.FlatAppearance.MouseOverBackColor = isPinned ? PinnedHover : NormalHover;
        btn.Invalidate();
        SavePaths();
    }

    private static string TruncatePathMiddle(string path, Font font, int maxWidth, Graphics g)
    {
        const string ellipsis = "...";

        if (TextRenderer.MeasureText(g, path, font, Size.Empty, TextFormatFlags.Default).Width <= maxWidth)
            return path;

        var ellipsisWidth = TextRenderer.MeasureText(g, ellipsis, font, Size.Empty, TextFormatFlags.Default).Width;
        if (maxWidth - ellipsisWidth <= 10) return ellipsis;

        var sep = path.Contains('\\') ? '\\' : '/';
        var parts = path.Split(sep, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1)
        {
            while (path.Length > 1 &&
                   TextRenderer.MeasureText(g, path, font, Size.Empty, TextFormatFlags.Default).Width > maxWidth)
                path = path[1..];
            return path;
        }

        var prefix = path.StartsWith("\\\\") ? "\\\\" :
                     path.Length >= 2 && path[1] == ':' ? path[..3] :
                     path.StartsWith(sep.ToString()) ? sep.ToString() : "";

        if (parts.Length <= 2)
        {
            var display = prefix + string.Join(sep, parts);
            while (display.Length > 0 &&
                   TextRenderer.MeasureText(g, display, font, Size.Empty, TextFormatFlags.Default).Width > maxWidth)
                display = display[1..];
            return display;
        }

        var endText = string.Join(sep, parts[^2..]);
        var baseText = ellipsis + sep + endText;

        if (TextRenderer.MeasureText(g, baseText, font, Size.Empty, TextFormatFlags.Default).Width <= maxWidth)
        {
            var middleParts = parts[..^2];
            var bestLeading = "";
            for (var i = 0; i < middleParts.Length; i++)
            {
                var leading = prefix + string.Join(sep, middleParts[..(i + 1)]) + sep;
                if (TextRenderer.MeasureText(g, leading + baseText, font, Size.Empty, TextFormatFlags.Default).Width <= maxWidth)
                    bestLeading = leading;
                else
                    break;
            }
            return bestLeading + baseText;
        }

        while (endText.Length > 1 &&
               TextRenderer.MeasureText(g, ellipsis + sep + endText, font, Size.Empty, TextFormatFlags.Default).Width > maxWidth)
        {
            var idx = endText.IndexOf(sep);
            if (idx >= 0 && idx < endText.Length - 1)
                endText = endText[(idx + 1)..];
            else
                endText = endText[1..];
        }

        return ellipsis + sep + endText;
    }

    private void RemovePath(Button btn)
    {
        flowPanel.Controls.Remove(btn);
        pinnedPaths.Remove(btn.Tag?.ToString() ?? "");
        btn.Dispose();
        SavePaths();
    }

    private void ClearAllPaths()
    {
        if (flowPanel.Controls.Count == 0) return;

        var result = MessageBox.Show(
            "确认删除所有路径条目？", "PathNote",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (result != DialogResult.Yes) return;

        while (flowPanel.Controls.Count > 0)
        {
            var c = flowPanel.Controls[0];
            flowPanel.Controls.RemoveAt(0);
            pinnedPaths.Remove(c.Tag?.ToString() ?? "");
            c.Dispose();
        }
        SavePaths();
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start("explorer.exe", path);
            else if (File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch { }
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start("explorer.exe", path);
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
    }

    private void SavePaths()
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            var entries = flowPanel.Controls
                .Cast<Control>()
                .Select(c => c.Tag?.ToString())
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => new PathEntry { Path = p!, IsPinned = pinnedPaths.Contains(p!) })
                .ToList();
            File.WriteAllText(pathsFile, JsonSerializer.Serialize(entries));
        }
        catch { }
    }

    private void LoadPaths()
    {
        try
        {
            if (!File.Exists(pathsFile)) return;
            var json = File.ReadAllText(pathsFile);
            var entries = DeserializePathEntries(json);
            if (entries == null) return;
            foreach (var entry in entries)
            {
                if (!string.IsNullOrEmpty(entry.Path) && (File.Exists(entry.Path) || Directory.Exists(entry.Path)))
                {
                    if (entry.IsPinned)
                        pinnedPaths.Add(entry.Path);
                    flowPanel.Controls.Add(CreatePathItem(entry.Path));
                }
            }
        }
        catch { }
    }

    private static List<PathEntry>? DeserializePathEntries(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<PathEntry>>(json);
        }
        catch
        {
            try
            {
                var old = JsonSerializer.Deserialize<List<string>>(json);
                return old?.Select(p => new PathEntry { Path = p, IsPinned = false }).ToList();
            }
            catch
            {
                return null;
            }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!forceClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        SaveWindowBounds();
        notifyIcon.Visible = false;
        base.OnFormClosing(e);
    }

    private void ExitApp()
    {
        forceClose = true;
        Close();
    }

    private void RestoreWindowBounds()
    {
        try
        {
            if (!File.Exists(windowFile)) return;
            var cfg = JsonSerializer.Deserialize<WindowConfig>(File.ReadAllText(windowFile));
            if (cfg == null) return;
            if (cfg.Width > 0 && cfg.Height > 0)
                Size = new Size(cfg.Width, cfg.Height);
            if (cfg.X >= 0 && cfg.Y >= 0)
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(cfg.X, cfg.Y);
            }
        }
        catch { }
    }

    private void SaveWindowBounds()
    {
        try
        {
            if (WindowState != FormWindowState.Normal) return;
            Directory.CreateDirectory(dataDir);
            var cfg = new WindowConfig { Width = Width, Height = Height, X = Left, Y = Top };
            File.WriteAllText(windowFile, JsonSerializer.Serialize(cfg));
        }
        catch { }
    }

    private void FlowPanel_Resize(object? sender, EventArgs e)
    {
        var w = flowPanel.ClientSize.Width - flowPanel.Padding.Horizontal - 4;
        foreach (Control c in flowPanel.Controls)
            c.Width = w;
    }

    private void NotifyIcon_DoubleClick(object? sender, EventArgs e)
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private static Icon CreateAppIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(50, 120, 210));
        using var font = new Font("Segoe UI", 9, FontStyle.Bold);
        g.DrawString("P", font, Brushes.White, 3, 2);
        var hIcon = bmp.GetHicon();
        using var temp = Icon.FromHandle(hIcon);
        using var ms = new MemoryStream();
        temp.Save(ms);
        ms.Position = 0;
        return new Icon(ms);
    }

    private record WindowConfig
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
    }

    private record PathEntry
    {
        public string Path { get; set; } = "";
        public bool IsPinned { get; set; }
    }
}
