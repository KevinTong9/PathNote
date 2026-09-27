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
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

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

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

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
    private static readonly Color NormalFore = Color.FromArgb(30, 30, 30);
    private static readonly Color OfflineFore = Color.FromArgb(150, 150, 150);   // 目标当前够不着的条目

    private readonly string dataDir;
    private readonly string pathsFile;
    private readonly string windowFile;
    private bool forceClose;
    private bool ignoreNextClipboardChange;
    private readonly HashSet<string> pinnedPaths = new();

    // 目标当前不可达（离线）的路径，只用于把条目文字显示成灰色。
    // 与条目是否保留无关——保留离线条目是 LoadPaths 的刻意行为，见那里的注释。
    private readonly HashSet<string> unavailablePaths = new();

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
                // 位置调好之后要把底色恢复正确：原来固定写 NormalHover，
                // 鼠标不划过就一直是"悬停"色，看着像卡住了。
                // （只有未标定的重复项会走到这里，所以用 Normal* 两种底色即可）
                var pointerInside = existing.ClientRectangle.Contains(existing.PointToClient(Cursor.Position));
                existing.BackColor = pointerInside ? NormalHover : NormalBack;
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
        RestoreForeground(prevForeground);              // …所以先把焦点还给用户原来的程序
        RaiseToTopWithoutActivating();                  // 再把它提到 Z 序最前（只动 Z 序，不动焦点）
    }

    /// <summary>
    /// 把窗口提到 Z 序最前但**不激活**它，焦点仍留在用户原来的程序上。
    ///
    /// 实测结论（本机，前台窗口属于外部进程 msedge 时）：
    ///   · ShowWindow(SW_SHOWNOACTIVATE) 只把窗口还原到它原来那一层，不动 Z 序；
    ///   · SetWindowPos(HWND_TOP, SWP_NOACTIVATE) **完全无效**——被前台窗口压着时
    ///     Z 序不变（前台窗口保护），所以看起来像"在当前层和最底层之间切换"；
    ///   · 先临时置 topmost 再退回非 topmost 才可靠：topmost 层永远在所有普通窗口之上，
    ///     不受前台窗口保护影响；HWND_NOTOPMOST 又会把它放在所有普通窗口的最前，
    ///     且不会长期占据 topmost 层。两次调用紧邻，肉眼看不到闪烁。
    /// 全程 SWP_NOACTIVATE，所以不抢焦点。
    /// </summary>
    private void RaiseToTopWithoutActivating()
    {
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        SetWindowPos(Handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
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
            ForeColor = unavailablePaths.Contains(NormalizePath(path)) ? OfflineFore : NormalFore,
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
                RefreshAvailability(btn, OpenInExplorer(p));
            else if (Control.ModifierKeys == Keys.Shift)
                TogglePin(btn);
            else
                CopyPath(p);
        };

        return btn;
    }

    /// <summary>
    /// 刷新条目的"可用 / 离线"灰显状态。只在 Ctrl+左键 点击时更新一次：
    /// 目标恢复了就自动恢复正常色，够不着就变灰——不做轮询，
    /// 也绝不在 Paint/Resize 里查盘（断开的网络路径会把 UI 冻住）。
    /// </summary>
    private void RefreshAvailability(Button btn, bool available)
    {
        var normalized = NormalizePath(btn.Tag?.ToString() ?? "");
        if (string.IsNullOrEmpty(normalized)) return;

        var changed = available ? unavailablePaths.Remove(normalized) : unavailablePaths.Add(normalized);
        if (!changed) return;

        btn.ForeColor = available ? NormalFore : OfflineFore;
        btn.Invalidate();
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

    /// <summary>在资源管理器中打开路径；返回目标此刻是否真的存在（供灰显状态刷新用）。</summary>
    private static bool OpenInExplorer(string path)
    {
        var exists = false;
        try
        {
            if (Directory.Exists(path))
            {
                exists = true;
                Process.Start("explorer.exe", path);
            }
            else if (File.Exists(path))
            {
                exists = true;
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
        }
        catch { }
        return exists;
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
            WriteFileAtomic(pathsFile, JsonSerializer.Serialize(entries));
        }
        catch { }
    }

    /// <summary>
    /// 原子写配置：先写同目录的 .tmp，再改名顶替。
    /// 直接 File.WriteAllText 覆盖时若进程中途死掉，文件会只剩半截，
    /// 整个路径列表就没了；同卷内 File.Move(overwrite) 是原子替换。
    /// </summary>
    private static void WriteFileAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
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

                // 目标当前不可达（移动硬盘没插、网络盘没连、盘符变了）也要保留这一条。
                // 保存是"整表覆盖"，一旦在这里 continue 把它丢掉，之后任何一次保存
                // （复制/删除/标定）都会把它从 paths.json 里永久删除，插回硬盘也回不来。
                // 能不能打开是点击那一刻现查的（见 OpenInExplorer），所以保留它没有副作用。
                if (Directory.Exists(normalized) && !normalized.EndsWith(Path.DirectorySeparatorChar))
                    display = normalized + Path.DirectorySeparatorChar;
                else if (!File.Exists(normalized))
                    unavailablePaths.Add(normalized);   // 当前够不着 → 灰显，但条目照旧保留

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

            var width = cfg.Width > 0 ? cfg.Width : Width;
            var height = cfg.Height > 0 ? cfg.Height : Height;

            // 保存下来的坐标可能属于一块已经拔掉/改了分辨率的显示器，
            // 那样窗口会恢复到看不见的地方，用户只能删 window.json 才能救回来。
            var bounds = ClampToVisibleScreen(new Rectangle(cfg.X, cfg.Y, width, height));
            Size = new Size(bounds.Width, bounds.Height);
            StartPosition = FormStartPosition.Manual;
            Location = bounds.Location;
        }
        catch { }
    }

    /// <summary>
    /// 把窗口位置夹回"现在仍然存在"的屏幕：只要标题栏在某块屏幕的工作区里
    /// 露出足够宽度就原样保留（含副屏、负坐标等正常多显示器布局），
    /// 否则挪到主屏工作区左上角。
    /// </summary>
    private static Rectangle ClampToVisibleScreen(Rectangle bounds)
    {
        const int MinVisibleWidth = 80;   // 标题栏至少要露出这么宽，才拖得回来
        const int TitleBarHeight = 32;

        foreach (var screen in Screen.AllScreens)
        {
            var wa = screen.WorkingArea;

            var visibleWidth = Math.Min(bounds.Right, wa.Right) - Math.Max(bounds.Left, wa.Left);
            if (visibleWidth < MinVisibleWidth) continue;

            var visibleCaptionHeight = Math.Min(bounds.Top + TitleBarHeight, wa.Bottom) - Math.Max(bounds.Top, wa.Top);
            if (visibleCaptionHeight < TitleBarHeight / 2) continue;

            return bounds;
        }

        var primary = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        return new Rectangle(
            primary.Left + 40,
            primary.Top + 40,
            Math.Min(bounds.Width, primary.Width),
            Math.Min(bounds.Height, primary.Height));
    }

    private void SaveWindowBounds()
    {
        try
        {
            if (WindowState != FormWindowState.Normal) return;
            Directory.CreateDirectory(dataDir);
            var cfg = new WindowConfig { Width = Width, Height = Height, X = Left, Y = Top };
            WriteFileAtomic(windowFile, JsonSerializer.Serialize(cfg));
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
