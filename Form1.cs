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

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern uint GlobalSize(IntPtr hMem);

    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_MINIMIZE = 6;

    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int WM_HOTKEY = 0x0312;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_TEXT = 1;
    private const string PathsFile = "paths.json";
    private const string WindowFile = "window.json";

    // 全局快捷键：Ctrl + Alt + `（反引号，VK_OEM_3）
    // 选它的理由：左手单手可按；Ctrl+Alt 组合在 Windows / Office / VS Code / VS 里没有默认占用，
    // 也避开了国内常驻软件的热键（微信 Alt+A、QQ Ctrl+Alt+A、QQ Ctrl+Alt+Z）；
    // 而"Ctrl+Alt+字母"是 Office 的重灾区（Ctrl+Alt+M/D/N/F/S…），用反引号正好绕开。
    // MOD_NOREPEAT：长按不连续触发。要换键只改下面两行即可。
    private const int HotkeyId = 0x504E;                              // 'PN'
    private const uint HotkeyModifiers = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;
    private const uint HotkeyVk = 0xC0;                               // VK_OEM_3 = ` / ~

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;

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

        if (!RegisterHotKey(Handle, HotkeyId, HotkeyModifiers, HotkeyVk))
        {
            // 被别的程序占用了同一个组合键：不静默失败，用托盘气泡告知（不打断操作）
            notifyIcon.BalloonTipTitle = "PathNote";
            notifyIcon.BalloonTipText = "全局快捷键 Ctrl+Alt+` 注册失败，可能被其他程序占用。";
            notifyIcon.ShowBalloonTip(4000);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        RemoveClipboardFormatListener(Handle);
        UnregisterHotKey(Handle, HotkeyId);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE)
            OnClipboardChanged();
        else if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            ToggleWindowVisibility();
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

        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count > 0)
                    path = files[0];
            }

            if (path == null && Clipboard.ContainsText())
            {
                // 大量文本（大段文档内容等）不属于本程序的工作内容，直接忽略、整块不读；
                // 只读取很短（≤ MaxPathChars 字符）的文本块，避免内存暴涨/崩溃，
                // 也避免长时间占用剪贴板干扰复制源程序。
                var text = ReadClipboardTextSmall();
                if (!string.IsNullOrEmpty(text))
                    path = text;
            }
        }
        catch
        {
            // 剪贴板正被其他程序占用（如源程序仍在写入）或格式异常时静默忽略，绝不崩溃
            return;
        }

        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                path = fullPath;
            }
            else if (Directory.Exists(fullPath))
            {
                path = Path.TrimEndingDirectorySeparator(fullPath) + Path.DirectorySeparatorChar;
            }
            else
            {
                return;
            }
        }
        catch { return; }

        if (InvokeRequired)
            Invoke(() => AddPath(path));
        else
            AddPath(path);
    }

    /// <summary>
    /// 有界读取剪贴板文本：仅当文本块很小（≤ MaxPathChars 字符）时才读取并截断到首个 '\0'；
    /// 超长内容（大段文本）直接返回 null 忽略。路径字符串不可能超过该上限。
    /// </summary>
    private static string? ReadClipboardTextSmall()
    {
        const int MaxPathChars = 4096;

        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var isUnicode = true;
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero)
            {
                h = GetClipboardData(CF_TEXT);
                isUnicode = false;
            }
            if (h == IntPtr.Zero) return null;

            var byteSize = (int)GlobalSize(h);
            var maxBytes = isUnicode ? MaxPathChars * 2 : MaxPathChars;
            if (byteSize <= 1 || byteSize > maxBytes) return null;

            var ptr = GlobalLock(h);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                string text;
                if (isUnicode)
                {
                    var chars = new char[byteSize / 2];
                    Marshal.Copy(ptr, chars, 0, chars.Length);
                    text = new string(chars);
                }
                else
                {
                    text = Marshal.PtrToStringAnsi(ptr, byteSize) ?? "";
                }

                var nul = text.IndexOf('\0');
                if (nul >= 0) text = text[..nul];
                return text.Trim().Trim('"');
            }
            finally
            {
                GlobalUnlock(h);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(path ?? "");

    private void AddPath(string path)
    {
        var normalized = NormalizePath(path);

        var existing = flowPanel.Controls
            .Cast<Control>()
            .FirstOrDefault(c => NormalizePath(c.Tag?.ToString() ?? "") == normalized);

        if (existing != null)
        {
            if (!pinnedPaths.Contains(normalized))
            {
                var pinnedItemCount = flowPanel.Controls
                    .Cast<Control>().Count(c => pinnedPaths.Contains(NormalizePath(c.Tag?.ToString() ?? "")));
                var currentIndex = flowPanel.Controls.GetChildIndex(existing);
                var targetIndex = pinnedItemCount;
                if (currentIndex != targetIndex)
                {
                    flowPanel.Controls.SetChildIndex(existing, targetIndex);
                }
                existing.BackColor = NormalHover;
            }
            SavePaths();
            RestoreFromMinimized();
            return;
        }

        var item = CreatePathItem(path);
        var pinCount = flowPanel.Controls
            .Cast<Control>().Count(c => pinnedPaths.Contains(NormalizePath(c.Tag?.ToString() ?? "")));
        flowPanel.Controls.Add(item);
        flowPanel.Controls.SetChildIndex(item, pinCount);
        SavePaths();
        RestoreFromMinimized();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// 剪贴板捕获到新路径时"弹出但不打扰"：仅在窗口最小化时恢复显示；
    /// 用户主动关闭到托盘造成的隐藏不打扰。
    /// </summary>
    private void RestoreFromMinimized()
    {
        if (WindowState != FormWindowState.Minimized) return;
        ShowWithoutActivating();
    }

    /// <summary>
    /// 全局快捷键（Ctrl+Alt+`）：最小化 ←→ 前置 之间切换。
    /// 两个方向都不改变用户当前正在使用的程序的焦点。
    /// </summary>
    private void ToggleWindowVisibility()
    {
        if (Visible && WindowState != FormWindowState.Minimized)
            MinimizeKeepingForeground();
        else
            ShowWithoutActivating();
    }

    /// <summary>显示/前置窗口，但把焦点还给按下快捷键时所在的那个程序。</summary>
    private void ShowWithoutActivating()
    {
        var prevForeground = GetForegroundWindow();

        ShowWindow(Handle, SW_SHOWNOACTIVATE);          // 未激活地显示/还原
        if (!Visible) Visible = true;                   // 从托盘隐藏状态恢复时同步 WinForms 内部状态
        if (WindowState != FormWindowState.Normal)
            WindowState = FormWindowState.Normal;       // WinForms 内部走 SW_RESTORE，会激活本窗口
        RestoreForeground(prevForeground);              // …所以最后把焦点还回去
    }

    /// <summary>最小化窗口，并把焦点还给用户原来的程序（最小化本身会激活 Z 序里的下一个窗口）。</summary>
    private void MinimizeKeepingForeground()
    {
        var prevForeground = GetForegroundWindow();
        ShowWindow(Handle, SW_MINIMIZE);
        RestoreForeground(prevForeground);
    }

    private void RestoreForeground(IntPtr prevForeground)
    {
        // prevForeground 等于自己：用户本来就在用 PathNote，交给系统自然激活下一个窗口即可
        if (prevForeground != IntPtr.Zero && prevForeground != Handle)
            SetForegroundWindow(prevForeground);
    }

    private Button CreatePathItem(string path)
    {
        var isPinned = pinnedPaths.Contains(NormalizePath(path));
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

            if (pinnedPaths.Contains(NormalizePath(text)))
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
            btn.BackColor = pinnedPaths.Contains(NormalizePath(path)) ? PinnedHover : NormalHover;
        };
        btn.MouseLeave += (_, _) =>
        {
            btn.BackColor = pinnedPaths.Contains(NormalizePath(path)) ? PinnedBack : NormalBack;
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

        return btn;
    }

    private void CopyPath(string path)
    {
        // 先举旗再写入：Windows 可能在 SetText 内部就同步派发 WM_CLIPBOARDUPDATE，
        // 那时旗子必须已经是 true，否则会被当成"用户复制了新路径"而误处理。
        var previousIgnore = ignoreNextClipboardChange;
        ignoreNextClipboardChange = true;
        try
        {
            Clipboard.SetText(path);
        }
        catch
        {
            // 剪贴板被其他程序占用等原因导致写入失败：必须把旗子放回去，
            // 否则它会留在 true 状态，把用户下一次真实的复制静默吞掉。
            ignoreNextClipboardChange = previousIgnore;
        }
    }

    private void TogglePin(Button btn)
    {
        var display = btn.Tag?.ToString() ?? "";
        var normalized = NormalizePath(display);
        if (string.IsNullOrEmpty(normalized)) return;

        if (pinnedPaths.Contains(normalized))
            pinnedPaths.Remove(normalized);
        else
            pinnedPaths.Add(normalized);

        var isPinned = pinnedPaths.Contains(normalized);
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

        // Determine root prefix (e.g. "C:\" or "\\")
        string root;
        if (path.StartsWith("\\\\"))
            root = "\\\\";
        else if (path.Length >= 2 && path[1] == ':')
            root = path[..3];
        else if (path.StartsWith(sep.ToString()))
            root = sep.ToString();
        else
            root = "";

        // For short paths (<=2 segments), just use original path and truncate from front
        if (parts.Length <= 2)
        {
            var display = path;
            while (display.Length > 0 &&
                   TextRenderer.MeasureText(g, display, font, Size.Empty, TextFormatFlags.Default).Width > maxWidth)
                display = display[1..];
            return display;
        }

        // >=3 segments: show last 2 segments as the tail
        var tail = string.Join(sep, parts[^2..]);
        var baseText = ellipsis + sep + tail;

        if (TextRenderer.MeasureText(g, baseText, font, Size.Empty, TextFormatFlags.Default).Width <= maxWidth)
        {
            // Build the leading part from the root + middle segments
            var rootLen = root.Length;
            var body = path[rootLen..];                     // everything after root
            var bodyParts = body.Split(sep, StringSplitOptions.RemoveEmptyEntries);
            var middleCount = bodyParts.Length - 2;          // exclude last 2 tail segments

            var bestLeading = "";
            for (var i = 0; i < middleCount; i++)
            {
                var leading = root + string.Join(sep, bodyParts[..(i + 1)]) + sep;
                if (TextRenderer.MeasureText(g, leading + baseText, font, Size.Empty, TextFormatFlags.Default).Width <= maxWidth)
                    bestLeading = leading;
                else
                    break;
            }
            return bestLeading + baseText;
        }

        // Even the last 2 segments don't fit, shrink the tail from front
        while (tail.Length > 1 &&
               TextRenderer.MeasureText(g, ellipsis + sep + tail, font, Size.Empty, TextFormatFlags.Default).Width > maxWidth)
        {
            var idx = tail.IndexOf(sep);
            if (idx >= 0 && idx < tail.Length - 1)
                tail = tail[(idx + 1)..];
            else
                tail = tail[1..];
        }

        return ellipsis + sep + tail;
    }

    private void RemovePath(Button btn)
    {
        flowPanel.Controls.Remove(btn);
        pinnedPaths.Remove(NormalizePath(btn.Tag?.ToString() ?? ""));
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
            pinnedPaths.Remove(NormalizePath(c.Tag?.ToString() ?? ""));
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

    private void SavePaths()
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            var entries = flowPanel.Controls
                .Cast<Control>()
                .Select(c => c.Tag?.ToString())
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => new PathEntry { Path = p!, IsPinned = pinnedPaths.Contains(NormalizePath(p!)) })
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
                var display = entry.Path ?? "";
                var normalized = Path.TrimEndingDirectorySeparator(display);
                if (string.IsNullOrEmpty(normalized)) continue;
                if (!File.Exists(normalized) && !Directory.Exists(normalized)) continue;

                if (Directory.Exists(normalized) && !normalized.EndsWith(Path.DirectorySeparatorChar))
                    display = normalized + Path.DirectorySeparatorChar;

                if (entry.IsPinned)
                    pinnedPaths.Add(normalized);
                flowPanel.Controls.Add(CreatePathItem(display));
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
