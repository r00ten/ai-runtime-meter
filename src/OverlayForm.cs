using DerivityMeter.Models;
using DerivityMeter.Store;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DerivityMeter;

public class OverlayForm : Form
{
    private readonly RuntimeStore _store;
    private readonly MeterSettings _settings;
    private readonly bool _otelRunning;
    private readonly int _otelPort;
    private readonly int _mcpPort;
    private readonly string _mcpError;
    private readonly string _otelError;
    private readonly Action _onExit;

    private enum Tab { Live, Session, Launchers, Settings, About }

    private bool _expanded = false;
    private bool _hasReceivedMetric = false;
    private Tab _activeTab = Tab.Live;
    private bool _overlayEverything = true;
    private string _compactText = "Ready · waiting for agent…";
    private Color _compactColor = Color.FromArgb(140, 200, 140);

    // launcher state
    private string _workspacePath = "";
    private string _launcherStatus = "";
    private string _expandedText = "";
    private string _sessionText = "";

    // keyboard / confirm state for Launchers tab
    private int _launcherFocus = 1;          // index into LauncherRows (first actionable row)
    private int _launcherPending = -1;        // row awaiting confirmation; -1 = none
    private CancellationTokenSource? _pendingCts;

    private Point _dragStart;
    private bool _dragging = false;
    private bool _exiting = false;

    private static readonly Color BG      = Color.FromArgb(18, 18, 20);
    private static readonly Font  FONT    = new(FontFamily.GenericMonospace, 9f,  FontStyle.Regular, GraphicsUnit.Point);
    private static readonly Font  FONT_SM = new(FontFamily.GenericMonospace, 8f,  FontStyle.Regular, GraphicsUnit.Point);
    private static readonly Font  FONT_TAB= new(FontFamily.GenericMonospace, 7f,  FontStyle.Regular, GraphicsUnit.Point);

    private static readonly Image? _logo = LoadLogo();

    private static Image? LoadLogo()
    {
        try
        {
            var asm  = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("derivity_logo.png"));
            if (name is null) return null;
            using var s = asm.GetManifestResourceStream(name)!;
            return Image.FromStream(s);
        }
        catch { return null; }
    }

    private const int COMPACT_H  = 32;
    private const int TAB_H      = 20;
    private const int EXPANDED_H = 240;
    private const int W          = 236;

    // × button: right-aligned in the compact bar
    private const int CLOSE_W = 20;
    private const int CLOSE_H = COMPACT_H;
    private Rectangle CloseRect => new(W - CLOSE_W, 0, CLOSE_W, CLOSE_H);

    // Named event so a second instance can ask us to come to the front
    private readonly EventWaitHandle _bringToFrontEvent =
        new EventWaitHandle(false, EventResetMode.AutoReset, "Global\\DerivityMeter_BringToFront");

    public OverlayForm(RuntimeStore store, MeterSettings settings,
        bool otelRunning = false, int otelPort = 0,
        int mcpPort = 0, string mcpError = "", string otelError = "",
        Action? onExit = null)
    {
        _store = store;
        _settings = settings;
        _otelRunning = otelRunning;
        _otelPort = otelPort;
        _mcpPort = mcpPort;
        _mcpError = mcpError;
        _otelError = otelError;
        _onExit = onExit ?? (() => Environment.Exit(0));

        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer,
            true);

        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = BG;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(W, COMPACT_H);

        FormClosing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true;
            DoExit();
        };

        KeyPreview = true;
        KeyDown += OnKeyDown;

        Cursor = Cursors.Hand;
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp   += OnMouseUp;
        MouseClick += OnClick;

        // workspace: restore last used path; empty means not yet set — user will be prompted
        _workspacePath = _settings.LastWorkspacePath ?? "";

        _expandedText = BuildStartupText();
        _store.OnMetricReceived += m => this.Invoke(() => OnMetric(m));

        // Listen for bring-to-front signal from a second instance
        Task.Run(() =>
        {
            while (!_exiting)
            {
                if (_bringToFrontEvent.WaitOne(500))
                    this.Invoke(() => { TopMost = true; Activate(); });
            }
        });

        // Open expanded on startup so otel/mcp status is immediately visible
        _expanded = true;
        ClientSize = new Size(W, COMPACT_H + TAB_H + EXPANDED_H);
        PositionBottomRight();
    }

    private void DoExit()
    {
        if (_exiting) return;
        _exiting = true;
        _bringToFrontEvent.Dispose();
        _onExit();
    }

    private static readonly string MeterJsonPath =
        Path.Combine(AppContext.BaseDirectory, "meter.json");

    private void OnMetric(RuntimeUsageMetric m)
    {
        if (!_hasReceivedMetric)
        {
            _hasReceivedMetric = true;
            _expanded = false;
            ClientSize = new Size(W, COMPACT_H);
            PositionBottomRight();
        }

        var derived = RuntimeDerivedMetric.From(m, _settings);

        _compactColor = derived.PressureLevel switch
        {
            PressureLevel.Critical => Color.FromArgb(255, 80, 80),
            PressureLevel.Warning  => Color.FromArgb(255, 180, 50),
            PressureLevel.Watch    => Color.FromArgb(220, 200, 80),
            _                      => Color.FromArgb(140, 200, 140)
        };

        string costShort = RuntimeCost.FormatUsdShort(m.EstimatedCostUsd, m.CostIsReported);
        string costLine  = RuntimeCost.FormatUsd(m.EstimatedCostUsd, m.CostIsReported);
        var row = ModelPricingCatalog.Resolve(m.Model);
        string estBreakdown = m.CostIsReported ? "" :
            $"\n  (est. {row.Family}: in×${row.InputPerMTok} wr×${row.CacheWrite5mPerMTok} rd×${row.CacheReadPerMTok} out×${row.OutputPerMTok}/M)";

        _compactText = derived.PressureLevel switch
        {
            PressureLevel.Critical => $"{costShort} · cache {FormatK(m.CacheReadInputTokens)} · CRIT",
            PressureLevel.Warning  => $"{costShort} · cache {FormatK(m.CacheReadInputTokens)} · WARN",
            _ => $"{costShort} · cache {FormatK(m.CacheReadInputTokens)} · out {FormatK(m.OutputTokens)}"
        };

        string otelLine = _otelRunning ? $"otel: 127.0.0.1:{_otelPort}" : "otel: offline";
        string mcpLine  = _mcpPort > 0 ? $"mcp:  127.0.0.1:{_mcpPort}"  : "mcp:  offline";
        string failLine = _store.LastOtelParseFailure is not null ? "otel: protobuf — use http/json" : "";

        _expandedText =
            $"provider:    {m.Provider.ToString().ToLower()}\n" +
            $"model:       {m.Model ?? "unknown"}\n" +
            $"session:     {Trunc(m.SessionId ?? m.ProjectPath ?? "—", 22)}\n" +
            $"────────────────────\n" +
            $"input:       {FormatK(m.InputTokens)}\n" +
            $"cache write: {FormatK(m.CacheCreationInputTokens)}\n" +
            $"cache read:  {FormatK(m.CacheReadInputTokens)}\n" +
            $"tokens in:   {FormatK(m.TotalInputSideTokens)}\n" +
            $"output:      {FormatK(m.OutputTokens)}\n" +
            $"────────────────────\n" +
            $"cost:        {costLine}{estBreakdown}\n" +
            $"status:      {RuntimeDerivedMetric.ComputePressure(m, _settings).ToString().ToLower()} (cache volume)\n" +
            $"source:      {m.Source.ToString().ToLower()}\n" +
            $"────────────────────\n" +
            $"{otelLine}\n" +
            $"{mcpLine}" +
            (failLine.Length > 0 ? $"\n{failLine}" : "");

        BuildSessionText();
        Invalidate();
    }

    private void BuildSessionText()
    {
        var sessions = _store.Sessions?.Sessions;
        if (sessions is null || sessions.Count == 0)
        {
            _sessionText = "no session data yet.";
            return;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var kv in sessions.OrderByDescending(x => x.Value.LastSeen).Take(5))
        {
            var s = kv.Value;
            var label = Trunc(s.SessionId ?? s.ProjectPath ?? s.SessionKey, 22);
            sb.AppendLine($"session: {label}");
            if (!string.IsNullOrEmpty(s.LastModel)) sb.AppendLine($"model:    {Trunc(s.LastModel, 22)}");
            sb.AppendLine($"requests: {s.Requests}");
            sb.AppendLine($"input:    {FormatK(s.InputTokens)}");
            sb.AppendLine($"cache wr: {FormatK(s.CacheCreationInputTokens)}");
            sb.AppendLine($"cache rd: {FormatK(s.CacheReadInputTokens)}");
            sb.AppendLine($"tokens in:{FormatK(s.InputTokens + s.CacheCreationInputTokens + s.CacheReadInputTokens)}");
            sb.AppendLine($"output:   {FormatK(s.OutputTokens)}");
            if (s.CostReportedUsd > 0 && s.CostEstimatedUsd > 0)
                sb.AppendLine($"cost:     ${s.EstimatedCostUsd:F2} (${s.CostReportedUsd:F2} rep + ${s.CostEstimatedUsd:F2} est)");
            else if (s.CostReportedUsd > 0)
                sb.AppendLine($"cost:     ${s.CostReportedUsd:F2} (reported)");
            else
                sb.AppendLine($"cost:     ${s.EstimatedCostUsd:F2} (est.)");
            sb.AppendLine($"────────────────────");
        }
        _sessionText = sb.ToString().TrimEnd();
    }

    // ── Tab geometry ─────────────────────────────────────────────────────────
    // Five tabs evenly divided across the width, left-anchored at TAB_MARGIN.
    // Each tab owns an equal-width click slot; labels are centered within their slot.
    private static readonly string[] Tabs = ["Live", "Session", "Launch", "Settings", "About"];

    private const float TAB_MARGIN = 4f;

    // Equal-width slot for a tab index: (start, end) in client X coordinates.
    private static (float Start, float End) TabSlot(int index)
    {
        float slot = (W - TAB_MARGIN * 2f) / Tabs.Length;
        float start = TAB_MARGIN + slot * index;
        return (start, start + slot);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.FillRectangle(new SolidBrush(BG), ClientRectangle);

        // compact row
        using var compactBrush = new SolidBrush(_compactColor);
        g.DrawString(_compactText, FONT, compactBrush, new PointF(8, 7));

        // × close button
        var cr = CloseRect;
        using var closeBrush = new SolidBrush(Color.FromArgb(160, 80, 80));
        using var closeFont  = new Font(FontFamily.GenericMonospace, 9f, FontStyle.Bold, GraphicsUnit.Point);
        var closeStr = "×";
        var sz = g.MeasureString(closeStr, closeFont);
        g.DrawString(closeStr, closeFont, closeBrush,
            new PointF(cr.X + (cr.Width - sz.Width) / 2f, cr.Y + (cr.Height - sz.Height) / 2f));

        // border
        using var pen = new Pen(Color.FromArgb(45, 45, 55), 1);
        g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        if (!_expanded) return;

        // tab row
        int tabY = COMPACT_H;
        using var tabActiveBrush = new SolidBrush(Color.FromArgb(200, 200, 220));
        using var tabDimBrush    = new SolidBrush(Color.FromArgb(90, 90, 110));

        var activeTab = _activeTab;
        float ulY = tabY + TAB_H - 2;
        using var ulPen = new Pen(Color.FromArgb(140, 160, 220), 1);

        for (int t = 0; t < Tabs.Length; t++)
        {
            var label = Tabs[t];
            var (start, end) = TabSlot(t);
            var brush = (Tab)t == activeTab ? tabActiveBrush : tabDimBrush;

            // center the label within its slot
            float lw = g.MeasureString(label, FONT_TAB).Width;
            float lx = start + (end - start - lw) / 2f;
            g.DrawString(label, FONT_TAB, brush, new PointF(lx, tabY + 4));

            // underline the active label (centered, matching its width)
            if ((Tab)t == activeTab)
                g.DrawLine(ulPen, lx + 1, ulY, lx + lw - 1, ulY);
        }

        // separator
        int sepY = COMPACT_H + TAB_H;
        using var sepPen = new Pen(Color.FromArgb(40, 40, 50), 1);
        g.DrawLine(sepPen, 0, sepY, Width, sepY);

        // body
        const int LOGO_H = 28;
        using var expandBrush = new SolidBrush(Color.FromArgb(190, 190, 200));

        switch (activeTab)
        {
            case Tab.Settings:  DrawSettingsBody(g, sepY, LOGO_H, expandBrush); break;
            case Tab.Launchers: DrawLaunchersBody(g, sepY, LOGO_H); break;
            case Tab.About:     DrawAboutBody(g, sepY, LOGO_H); break;
            default:
                var bodyText = activeTab == Tab.Live ? _expandedText : _sessionText;
                g.DrawString(bodyText, FONT_SM, expandBrush,
                    new RectangleF(8, sepY + 6, W - 16, EXPANDED_H - TAB_H - LOGO_H - 10));
                break;
        }

        // logo footer
        if (_logo is not null)
        {
            int logoY    = COMPACT_H + TAB_H + EXPANDED_H - LOGO_H - 2;
            int maxLogoW = W - 32;
            float aspect = _logo.Width / (float)_logo.Height;
            int logoW    = Math.Min(maxLogoW, (int)(LOGO_H * aspect));
            int logoH2   = (int)(logoW / aspect);
            int logoX    = (W - logoW) / 2;
            int logoYc   = logoY + (LOGO_H - logoH2) / 2;

            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            using var logoAttrs = new ImageAttributes();
            var cm2 = new ColorMatrix(); cm2.Matrix33 = 0.45f;
            logoAttrs.SetColorMatrix(cm2);
            g.DrawImage(_logo, new Rectangle(logoX, logoYc, logoW, logoH2),
                0, 0, _logo.Width, _logo.Height, GraphicsUnit.Pixel, logoAttrs);
        }
    }

    private void OnClick(object? s, MouseEventArgs e)
    {
        if (_dragging) return;
        if (e.Button != MouseButtons.Left) return;
        if (CloseRect.Contains(e.Location)) { DoExit(); return; }

        // Tab row
        if (_expanded && e.Y >= COMPACT_H && e.Y < COMPACT_H + TAB_H)
        {
            for (int t = 0; t < Tabs.Length; t++)
            {
                var (x0, x1) = TabSlot(t);
                if (e.X >= x0 && e.X < x1) { _activeTab = (Tab)t; break; }
            }
            ClearPending();
            Invalidate();
            return;
        }

        // Settings clicks
        if (_expanded && _activeTab == Tab.Settings)
        {
            int sepY = COMPACT_H + TAB_H;

            // Always on Top toggle
            var toggleHitRect = new Rectangle(8, sepY + 10, W - 16, 28);
            if (toggleHitRect.Contains(e.Location))
            {
                _overlayEverything = !_overlayEverything;
                TopMost = _overlayEverything;
                Invalidate();
                return;
            }

            // IDE preset rows
            for (int i = 0; i < IdeOptions.Length; i++)
            {
                if (SettingsIdeRowRect(i, sepY).Contains(e.Location))
                {
                    _settings.PreferredIde = IdeOptions[i].Cmd;
                    _settings.Save();
                    Invalidate();
                    return;
                }
            }

            // Custom row
            if (SettingsIdeRowRect(IdeOptions.Length, sepY).Contains(e.Location))
            {
                PickCustomIde();
                return;
            }
        }

        // Launchers clicks
        if (_expanded && _activeTab == Tab.Launchers)
        {
            HandleLauncherMouseClick(e.Location);
            return;
        }

        _expanded = !_expanded;
        ClientSize = new Size(W, _expanded ? COMPACT_H + TAB_H + EXPANDED_H : COMPACT_H);
        PositionBottomRight();
        Invalidate();
    }

    // ── Keyboard handling ─────────────────────────────────────────────────────
    private void OnKeyDown(object? s, KeyEventArgs e)
    {
        if (!_expanded || _activeTab != Tab.Launchers) return;

        if (e.KeyCode == Keys.Up)
        {
            MoveLauncherFocus(-1);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Down)
        {
            MoveLauncherFocus(+1);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            ActivateLauncherRow(_launcherFocus, keyboard: true);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            ClearPending();
            Invalidate();
            e.Handled = true;
        }
    }

    private void MoveLauncherFocus(int delta)
    {
        ClearPending();
        var actionable = ActionableLauncherRows();
        int cur = actionable.IndexOf(_launcherFocus);
        if (cur < 0) cur = 0;
        cur = (cur + delta + actionable.Count) % actionable.Count;
        _launcherFocus = actionable[cur];
        Invalidate();
    }

    // ── Launcher row layout ────────────────────────────────────────────────
    private static readonly string[] LauncherRows =
    [
        "workspace:",             // 0 — display only
        "Change workspace",       // 1
        "────────────────────",   // 2 — separator
        "Launch Claude in PowerShell", // 3
        "Launch Claude in IDE",        // 4
        "Copy env for workspace",      // 5
        "────────────────────",   // 6
        "Add Meter MCP to Claude", // 7
        "Check status",           // 8
    ];

    // rows where confirm is skipped (safe/instant)
    private static readonly HashSet<int> DirectRows = [1, 8];

    private static List<int> ActionableLauncherRows() =>
        Enumerable.Range(0, LauncherRows.Length)
            .Where(i => !LauncherRows[i].StartsWith("───") && i != 0)
            .ToList();

    private const int ROW_H = 16;

    private Rectangle LauncherRowRect(int row, int sepY) =>
        new(8, sepY + 8 + row * ROW_H, W - 16, ROW_H);

    private void DrawLaunchersBody(Graphics g, int sepY, int logoH)
    {
        using var labelBrush  = new SolidBrush(Color.FromArgb(190, 190, 200));
        using var dimBrush    = new SolidBrush(Color.FromArgb(90, 90, 110));
        using var actionBrush = new SolidBrush(Color.FromArgb(130, 170, 230));
        using var focusBrush  = new SolidBrush(Color.FromArgb(180, 210, 255));
        using var statusBrush = new SolidBrush(Color.FromArgb(100, 210, 120));
        using var pendBrush   = new SolidBrush(Color.FromArgb(220, 180, 80));

        for (int i = 0; i < LauncherRows.Length; i++)
        {
            var rect  = LauncherRowRect(i, sepY);
            string label = LauncherRows[i];

            if (label.StartsWith("───"))
            {
                using var sp = new Pen(Color.FromArgb(45, 45, 55), 1);
                g.DrawLine(sp, rect.X, rect.Y + ROW_H / 2, rect.Right, rect.Y + ROW_H / 2);
                continue;
            }

            if (i == 0)
            {
                string ws  = Trunc(_workspacePath.Length > 0 ? _workspacePath : "(not set)", 20);
                g.DrawString("ws: ", FONT_SM, dimBrush, new PointF(rect.X, rect.Y));
                var wSz = g.MeasureString("ws: ", FONT_SM);
                g.DrawString(ws, FONT_SM, labelBrush, new PointF(rect.X + wSz.Width - 4, rect.Y));
                continue;
            }

            bool isFocus   = i == _launcherFocus;
            bool isPending = i == _launcherPending;
            var textBrush  = isFocus ? focusBrush : actionBrush;

            // focus highlight bar
            if (isFocus)
            {
                using var hlBrush = new SolidBrush(Color.FromArgb(30, 130, 170, 255));
                g.FillRectangle(hlBrush, new Rectangle(4, rect.Y, W - 8, ROW_H - 1));
            }

            g.DrawString(label, FONT_SM, textBrush, new PointF(rect.X + 4, rect.Y));

            // confirm badge on the right
            if (isPending)
            {
                string badge = "ENTER";
                var bSz = g.MeasureString(badge, FONT_SM);
                g.DrawString(badge, FONT_SM, pendBrush,
                    new PointF(W - bSz.Width - 6, rect.Y));
            }
        }

        // status line or helper hint
        int bottomY = sepY + 8 + LauncherRows.Length * ROW_H + 4;
        if (_launcherStatus.Length > 0)
        {
            g.DrawString(_launcherStatus, FONT_SM, statusBrush,
                new RectangleF(8, bottomY, W - 16, 36));
        }
        else
        {
            using var hintBrush = new SolidBrush(Color.FromArgb(60, 60, 75));
            g.DrawString(
                "Claude Code must be launched with\nthe meter telemetry environment.",
                FONT_SM, hintBrush, new RectangleF(8, bottomY, W - 16, 36));
        }
    }

    private void HandleLauncherMouseClick(Point loc)
    {
        int sepY = COMPACT_H + TAB_H;
        for (int i = 0; i < LauncherRows.Length; i++)
        {
            if (LauncherRows[i].StartsWith("───") || i == 0) continue;
            if (!LauncherRowRect(i, sepY).Contains(loc)) continue;

            _launcherFocus = i;
            ActivateLauncherRow(i, keyboard: false);
            return;
        }
    }

    private void ActivateLauncherRow(int row, bool keyboard)
    {
        // Direct rows execute immediately (no confirm needed)
        if (DirectRows.Contains(row))
        {
            ClearPending();
            ExecuteLauncherRow(row);
            return;
        }

        // First activation: arm/confirm
        if (_launcherPending != row)
        {
            _launcherPending = row;
            Invalidate();
            _pendingCts?.Cancel();
            _pendingCts = new CancellationTokenSource();
            var token = _pendingCts.Token;
            Task.Delay(2500, token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                    this.Invoke(() => { if (_launcherPending == row) { ClearPending(); Invalidate(); } });
            }, TaskScheduler.Default);
            return;
        }

        // Second activation: execute
        ClearPending();
        ExecuteLauncherRow(row);
    }

    private void ExecuteLauncherRow(int row)
    {
        switch (row)
        {
            case 1: PickWorkspace(); break;
            case 3: LaunchClaude(); break;
            case 4: LaunchIde(); break;
            case 5: CopyEnvVars(); break;
            case 7: RunInstallMcp(); break;
            case 8: RunCheckStatus(); break;
        }
    }

    private void ClearPending()
    {
        _pendingCts?.Cancel();
        _pendingCts = null;
        _launcherPending = -1;
    }

    private void PickWorkspace()
    {
        // FolderBrowserDialog crashes in this overlay's COM context.
        // Delegate to a hidden PowerShell -STA process that hosts the dialog,
        // writes the result to a temp file, then we apply it on the UI thread.
        SetLauncherStatus("Opening folder picker…");
        try
        {
            string outFile = Path.Combine(Path.GetTempPath(), $"derivity_ws_{Guid.NewGuid():N}.txt");
            string initial = (_workspacePath.Length > 0 ? _workspacePath : Environment.CurrentDirectory)
                .Replace("'", "''");

            string ps = $$"""
                Add-Type -AssemblyName System.Windows.Forms
                Add-Type -AssemblyName Microsoft.VisualBasic
                $d = New-Object System.Windows.Forms.FolderBrowserDialog
                $d.Description = 'Select workspace directory'
                $d.SelectedPath = '{{initial}}'
                if ($d.ShowDialog() -eq 'OK') {
                    $d.SelectedPath | Out-File -FilePath '{{outFile}}' -Encoding utf8 -NoNewline
                } else {
                    $typed = [Microsoft.VisualBasic.Interaction]::InputBox('Paste or type a workspace path:', 'Workspace path', '{{initial}}')
                    if ($typed -ne '' -and (Test-Path $typed -PathType Container)) {
                        $typed | Out-File -FilePath '{{outFile}}' -Encoding utf8 -NoNewline
                    }
                }
                """;

            string tmp = Path.Combine(Path.GetTempPath(), $"derivity_pick_{Guid.NewGuid():N}.ps1");
            File.WriteAllText(tmp, ps);

            var psi = new ProcessStartInfo
            {
                FileName        = "powershell.exe",
                Arguments       = $"-ExecutionPolicy Bypass -STA -WindowStyle Hidden -File \"{tmp}\"",
                UseShellExecute = false,
                CreateNoWindow  = true,
            };

            var proc = Process.Start(psi)!;

            // Wait async so the overlay UI thread stays alive during picking
            Task.Run(() =>
            {
                proc.WaitForExit(30_000);
                proc.Dispose();
                this.Invoke(() =>
                {
                    try
                    {
                        if (File.Exists(outFile))
                        {
                            string chosen = File.ReadAllText(outFile).Trim();
                            try { File.Delete(outFile); } catch { }
                            if (chosen.Length > 0 && Directory.Exists(chosen))
                            {
                                _workspacePath = chosen;
                                _settings.LastWorkspacePath = chosen;
                                _settings.Save();
                                _launcherStatus = "";
                                Invalidate();
                                return;
                            }
                        }
                        // Picker was cancelled or produced no output
                        _launcherStatus = "";
                        Invalidate();
                    }
                    catch (Exception ex)
                    {
                        SetLauncherStatus($"Picker error:\n{ex.Message[..Math.Min(ex.Message.Length, 50)]}");
                    }
                    finally
                    {
                        try { File.Delete(tmp); } catch { }
                    }
                });
            });
        }
        catch (Exception ex)
        {
            SetLauncherStatus($"Picker error:\n{ex.Message[..Math.Min(ex.Message.Length, 50)]}");
        }
    }

    private string BuildOtelEnvBlock() =>
        $"$env:CLAUDE_CODE_ENABLE_TELEMETRY=\"1\"\n" +
        $"$env:OTEL_LOGS_EXPORTER=\"otlp\"\n" +
        $"$env:OTEL_METRICS_EXPORTER=\"otlp\"\n" +
        $"$env:OTEL_EXPORTER_OTLP_PROTOCOL=\"http/json\"\n" +
        $"$env:OTEL_EXPORTER_OTLP_ENDPOINT=\"http://127.0.0.1:{_otelPort}\"";

    private void LaunchClaude()
    {
        if (!EnsureWorkspace()) return;
        if (!EnsureOtel()) return;
        string script = $"{BuildOtelEnvBlock()}\ncd \"{_workspacePath}\"\nclaude";
        LaunchPowerShell(script, keepOpen: true);
        SetLauncherStatus("Launching Claude…");
    }

    private void LaunchIde()
    {
        if (!EnsureWorkspace()) return;
        string cmd   = _settings.PreferredIde;
        string label = IdeLabel(cmd);
        try
        {
            // Full-path exes launch directly; short shims (code, cursor, windsurf, zed)
            // are .cmd files on PATH — cmd /c start resolves them without a visible window.
            if (cmd.Contains('\\') || cmd.Contains('/'))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = cmd,
                    Arguments       = $"\"{_workspacePath}\"",
                    UseShellExecute = true,
                });
            }
            else
            {
                // Resolve the shim to its full path first (where.exe works for .cmd shims)
                string? resolved = ResolveShim(cmd);
                if (resolved is null)
                {
                    SetLauncherStatus($"{cmd} not found in PATH.\nInstall it or pick exe manually.");
                    return;
                }
                Process.Start(new ProcessStartInfo
                {
                    FileName        = resolved,
                    Arguments       = $"\"{_workspacePath}\"",
                    UseShellExecute = true,
                });
            }
            SetLauncherStatus($"Launching {label}…");
        }
        catch (Exception ex)
        {
            SetLauncherStatus($"Launch failed:\n{ex.Message[..Math.Min(ex.Message.Length, 50)]}");
        }
    }

    private static string IdeLabel(string cmd)
    {
        if (IdeOptions.FirstOrDefault(o => o.Cmd == cmd).Label is { Length: > 0 } lbl)
            return lbl;
        // Full path — show just the filename without extension
        if (cmd.Contains('\\') || cmd.Contains('/'))
            return Path.GetFileNameWithoutExtension(cmd);
        return cmd;
    }

    private void PickCustomIde()
    {
        // Resolve a sensible initial directory for the file picker
        string currentExe = IsCustomIde() ? _settings.PreferredIde : "";
        string initDir = "";
        if (File.Exists(currentExe))
            initDir = Path.GetDirectoryName(currentExe) ?? "";
        if (initDir.Length == 0)
            initDir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        try
        {
            string outFile = Path.Combine(Path.GetTempPath(), $"derivity_ide_{Guid.NewGuid():N}.txt");
            string ps = $$"""
                Add-Type -AssemblyName System.Windows.Forms
                $d = New-Object System.Windows.Forms.OpenFileDialog
                $d.Title = 'Select your IDE executable'
                $d.Filter = 'Executable|*.exe'
                $d.InitialDirectory = '{{initDir.Replace("'", "''")}}'
                if ($d.ShowDialog() -eq 'OK') {
                    $d.FileName | Out-File -FilePath '{{outFile}}' -Encoding utf8 -NoNewline
                }
                """;

            string tmp = Path.Combine(Path.GetTempPath(), $"derivity_idepick_{Guid.NewGuid():N}.ps1");
            File.WriteAllText(tmp, ps);

            var proc = Process.Start(new ProcessStartInfo
            {
                FileName        = "powershell.exe",
                Arguments       = $"-ExecutionPolicy Bypass -STA -WindowStyle Hidden -File \"{tmp}\"",
                UseShellExecute = false,
                CreateNoWindow  = true,
            })!;

            Task.Run(() =>
            {
                proc.WaitForExit(60_000);
                proc.Dispose();
                this.Invoke(() =>
                {
                    try
                    {
                        if (File.Exists(outFile))
                        {
                            string chosen = File.ReadAllText(outFile).Trim();
                            try { File.Delete(outFile); } catch { }
                            if (chosen.Length > 0 && File.Exists(chosen))
                            {
                                _settings.PreferredIde = chosen;
                                _settings.Save();
                                Invalidate();
                            }
                        }
                    }
                    finally { try { File.Delete(tmp); } catch { } }
                });
            });
        }
        catch { }
    }

    private void CopyEnvVars()
    {
        if (InvokeRequired) { BeginInvoke(new Action(CopyEnvVars)); return; }
        if (!EnsureOtel()) return;
        string ws = _workspacePath.Length > 0 ? _workspacePath : ".";
        string text = BuildOtelEnvBlock() + $"\n# then run:\ncd \"{ws}\"; claude";
        try
        {
            Clipboard.SetDataObject(text, copy: true, retryTimes: 10, retryDelay: 100);
            SetLauncherStatus("Copied env for workspace");
        }
        catch (ExternalException)
        {
            SetLauncherStatus("Clipboard busy — try again");
        }
    }

    private void RunInstallMcp()
    {
        string script = Path.Combine(AppContext.BaseDirectory, "..", "scripts", "Install-MeterMcp.ps1");
        if (!File.Exists(script)) { SetLauncherStatus("Install-MeterMcp.ps1\nnot found."); return; }
        LaunchPowerShell($"powershell -File \"{Path.GetFullPath(script)}\"", keepOpen: true);
        SetLauncherStatus("MCP install launched.");
    }

    private void RunCheckStatus()
    {
        string lastMetric = _hasReceivedMetric
            ? $"agent: connected\nsource: {_store.Current?.Source.ToString().ToLower() ?? "?"}"
            : "agent: no data yet";
        SetLauncherStatus(
            $"OTEL: {(_otelRunning ? $"ok :{_otelPort}" : "offline")}\n" +
            $"MCP:  {(_mcpPort > 0 ? $"ok :{_mcpPort}" : "offline")}\n" +
            lastMetric);
    }

    private static void LaunchPowerShell(string script, bool keepOpen)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"derivity_launch_{Guid.NewGuid():N}.ps1");
        File.WriteAllText(tmp, script);
        string noExit = keepOpen ? "-NoExit " : "";
        Process.Start(new ProcessStartInfo
        {
            FileName  = "powershell.exe",
            Arguments = $"{noExit}-ExecutionPolicy Bypass -File \"{tmp}\"",
            UseShellExecute = true,
        });
    }

    private bool EnsureWorkspace()
    {
        if (_workspacePath.Length > 0 && Directory.Exists(_workspacePath)) return true;
        PickWorkspace();
        return _workspacePath.Length > 0 && Directory.Exists(_workspacePath);
    }

    private bool EnsureOtel()
    {
        if (_otelRunning && _otelPort > 0) return true;
        SetLauncherStatus("OTEL not running.\nStart Meter first.");
        return false;
    }

    private void SetLauncherStatus(string msg)
    {
        _launcherStatus = msg;
        Invalidate();
        Task.Delay(4000).ContinueWith(_ => this.Invoke(() =>
        {
            if (_launcherStatus == msg) { _launcherStatus = ""; Invalidate(); }
        }));
    }

    private void PositionBottomRight()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1050);
        Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
    }

    private void OnMouseDown(object? s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _dragging = false; _dragStart = e.Location; }
    }
    private void OnMouseMove(object? s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && !_dragging)
        {
            if (Math.Abs(e.X - _dragStart.X) > 3 || Math.Abs(e.Y - _dragStart.Y) > 3)
                _dragging = true;
        }
        if (_dragging)
            Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y);
    }
    private void OnMouseUp(object? s, MouseEventArgs e) { }

    protected override void WndProc(ref Message m)
    {
        const int WM_RBUTTONUP = 0x0205;
        if (m.Msg == WM_RBUTTONUP)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Clear data", null, (_, _) => ClearPersistedData());
            menu.Items.Add("Exit", null, (_, _) => DoExit());
            menu.Show(this, PointToClient(Cursor.Position));
        }
        base.WndProc(ref m);
    }

    private static void ClearPersistedData()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".derivity", "runtime-meter");
        try
        {
            var events   = Path.Combine(dir, "runtime-events.jsonl");
            var warnings = Path.Combine(dir, "warnings.jsonl");
            var sessions = Path.Combine(dir, "sessions.json");
            if (File.Exists(events))   File.WriteAllText(events, "");
            if (File.Exists(warnings)) File.WriteAllText(warnings, "");
            if (File.Exists(sessions)) File.WriteAllText(sessions, "{}");
        }
        catch { }
    }

    private string BuildStartupText()
    {
        string otelLine = _otelRunning
            ? $"otel: 127.0.0.1:{_otelPort} — listening"
            : (_otelError.Length > 0 ? "otel: no free port" : "otel: offline");
        string mcpLine = _mcpPort > 0
            ? $"mcp:  127.0.0.1:{_mcpPort} — listening"
            : (_mcpError.Length > 0 ? "mcp:  no free port" : "mcp:  offline");
        string jsonLine = File.Exists(MeterJsonPath) ? "json: polling meter.json\n" : "";

        return
            $"Derivity Meter\n" +
            $"────────────────────\n" +
            $"{otelLine}\n" +
            $"{mcpLine}\n" +
            jsonLine +
            $"────────────────────\n" +
            (_otelRunning
                ? $"OTEL_EXPORTER_OTLP_ENDPOINT=\nhttp://127.0.0.1:{_otelPort}\n" +
                  "OTEL_EXPORTER_OTLP_PROTOCOL=\nhttp/json\n"
                : "") +
            "waiting for data...";
    }

    private void DrawAboutBody(Graphics g, int sepY, int logoH)
    {
        using var titleBrush = new SolidBrush(Color.FromArgb(210, 210, 230));
        using var bodyBrush  = new SolidBrush(Color.FromArgb(150, 150, 165));
        using var FONT_BOLD  = new Font(FontFamily.GenericMonospace, 8.5f, FontStyle.Bold, GraphicsUnit.Point);

        int y    = sepY + 8;
        int maxY = COMPACT_H + TAB_H + EXPANDED_H - logoH - 6;

        g.DrawString("Derivity Meter", FONT_BOLD, titleBrush, new PointF(8, y));
        y += 16;

        string body =
            "Live runtime visibility for\n" +
            "Claude Code.\n" +
            "\n" +
            "Shows tokens, cache, cost,\n" +
            "model, source, and session\n" +
            "totals per request.\n" +
            "\n" +
            "Claude Code can run from any\n" +
            "terminal or IDE — it must be\n" +
            "started with the meter\n" +
            "telemetry environment.";

        g.DrawString(body, FONT_SM, bodyBrush,
            new RectangleF(8, y, W - 16, maxY - y));
    }

    // IDE options shown in Settings — (label, cli command)
    private static readonly (string Label, string Cmd)[] IdeOptions =
    [
        ("VS Code",  "code"),
        ("Cursor",   "cursor"),
        ("Windsurf", "windsurf"),
        ("Zed",      "zed"),
    ];

    private bool IsCustomIde() =>
        !IdeOptions.Any(o => o.Cmd == _settings.PreferredIde);

    private void DrawSettingsBody(Graphics g, int sepY, int logoH, SolidBrush textBrush)
    {
        int y = sepY + 10;
        using var labelBrush  = new SolidBrush(Color.FromArgb(190, 190, 200));
        using var onBrush     = new SolidBrush(Color.FromArgb(100, 210, 120));
        using var offBrush    = new SolidBrush(Color.FromArgb(160, 80, 80));
        using var hintBrush   = new SolidBrush(Color.FromArgb(90, 90, 110));
        using var activeBrush = new SolidBrush(Color.FromArgb(180, 210, 255));
        using var dimBrush    = new SolidBrush(Color.FromArgb(90, 90, 110));

        // ── Always on Top ────────────────────────────────────────────────
        bool on = _overlayEverything;
        g.DrawString("Always on Top", FONT_SM, labelBrush, new PointF(8, y));
        string badge   = on ? "ON" : "OFF";
        var badgeBrush = on ? onBrush : offBrush;
        var badgeSz    = g.MeasureString(badge, FONT_SM);
        g.DrawString(badge, FONT_SM, badgeBrush, new PointF(W - badgeSz.Width - 10, y));
        y += 14;
        string hint = on ? "Stays visible over all windows."
                         : "Can fall behind other windows.";
        g.DrawString(hint, FONT_SM, hintBrush, new RectangleF(8, y, W - 16, 14));
        y += 18;

        // ── separator ────────────────────────────────────────────────────
        using var sepPen = new Pen(Color.FromArgb(45, 45, 55), 1);
        g.DrawLine(sepPen, 8, y, W - 8, y);
        y += 6;

        // ── IDE selector ─────────────────────────────────────────────────
        g.DrawString("IDE for Launch", FONT_SM, labelBrush, new PointF(8, y));
        y += 14;

        string current  = _settings.PreferredIde;
        bool   isCustom = IsCustomIde();

        foreach (var (label, cmd) in IdeOptions)
        {
            bool isSelected = cmd == current;
            using var rowBrush = new SolidBrush(isSelected
                ? Color.FromArgb(180, 210, 255)
                : Color.FromArgb(90, 90, 110));

            if (isSelected)
            {
                using var hlBrush = new SolidBrush(Color.FromArgb(30, 130, 170, 255));
                g.FillRectangle(hlBrush, new Rectangle(4, y - 1, W - 8, ROW_H - 1));
            }

            g.DrawString(label, FONT_SM, rowBrush, new PointF(12, y));

            if (isSelected)
            {
                var selSz = g.MeasureString("●", FONT_SM);
                g.DrawString("●", FONT_SM, onBrush, new PointF(W - selSz.Width - 10, y));
            }

            y += ROW_H;
        }

        // Custom row — shows current custom cmd or "Custom…" prompt
        {
            using var rowBrush = new SolidBrush(isCustom
                ? Color.FromArgb(180, 210, 255)
                : Color.FromArgb(90, 90, 110));

            if (isCustom)
            {
                using var hlBrush = new SolidBrush(Color.FromArgb(30, 130, 170, 255));
                g.FillRectangle(hlBrush, new Rectangle(4, y - 1, W - 8, ROW_H - 1));
            }

            string customLabel = isCustom
                ? $"Other: {Path.GetFileName(current)}"
                : "Other (browse for exe)…";
            g.DrawString(customLabel, FONT_SM, rowBrush, new PointF(12, y));

            if (isCustom)
            {
                var selSz = g.MeasureString("●", FONT_SM);
                g.DrawString("●", FONT_SM, onBrush, new PointF(W - selSz.Width - 10, y));
            }
        }
    }

    // ideIndex: 0..IdeOptions.Length-1 = presets, IdeOptions.Length = Custom row
    private Rectangle SettingsIdeRowRect(int ideIndex, int sepY)
    {
        // Always on Top (14) + hint (14) + gap (18) + sep (6) + "IDE for Launch" label (14) = 66
        int y = sepY + 10 + 66 + ideIndex * ROW_H;
        return new Rectangle(4, y - 1, W - 8, ROW_H);
    }

    private static readonly Dictionary<string, string[]> KnownIdeSubPaths = new()
    {
        ["code"]     = [@"AppData\Local\Programs\Microsoft VS Code\bin\code.cmd",
                         @"AppData\Local\Programs\Microsoft VS Code\Code.exe"],
        ["cursor"]   = [@"AppData\Local\Programs\cursor\Cursor.exe",
                         @"AppData\Local\Programs\Cursor\resources\app\bin\cursor.cmd"],
        ["windsurf"] = [@"AppData\Local\Programs\Windsurf\Windsurf.exe"],
        ["zed"]      = [@"AppData\Local\Programs\Zed\zed.exe"],
    };

    private static readonly Dictionary<string, string> KnownProcessNames = new()
    {
        ["code"]     = "Code",
        ["cursor"]   = "Cursor",
        ["windsurf"] = "Windsurf",
        ["zed"]      = "zed",
    };

    private static string? ResolveShim(string cmd)
    {
        // 1. where.exe — works if the IDE added itself to PATH
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName               = "where.exe",
                Arguments              = cmd,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                CreateNoWindow         = true,
            })!;
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(3000);
            foreach (var line in output.Split('\n'))
            {
                string path = line.Trim();
                if (path.Length > 0) return path;
            }
        }
        catch { }

        string key = cmd.ToLowerInvariant();

        // 2. Known sub-paths under the actual user profile root.
        //    Read USERPROFILE from the registry (HKCU volatile) to handle cases
        //    where %LOCALAPPDATA% / %USERPROFILE% env vars point to a different
        //    drive than where the profile actually lives.
        if (KnownIdeSubPaths.TryGetValue(key, out var subPaths))
        {
            var roots = new List<string>();
            // Env var first (works on normal setups)
            string envProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (envProfile.Length > 0) roots.Add(envProfile);
            // Registry ProfileImagePath — authoritative on redirected-profile machines
            try
            {
                string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
                using var key2 = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
                string? regPath = key2?.GetValue("ProfileImagePath") as string;
                if (regPath is { Length: > 0 } && !roots.Contains(regPath, StringComparer.OrdinalIgnoreCase))
                    roots.Add(regPath);
            }
            catch { }

            foreach (var root in roots)
            {
                foreach (var sub in subPaths)
                {
                    string full = Path.Combine(root, sub);
                    if (File.Exists(full)) return full;
                }
            }
        }

        // 3. If the IDE is already running, grab its exe path directly
        if (KnownProcessNames.TryGetValue(key, out string? procName))
        {
            try
            {
                string? exePath = Process.GetProcessesByName(procName)
                    .FirstOrDefault()?.MainModule?.FileName;
                if (exePath is { Length: > 0 } && File.Exists(exePath)) return exePath;
            }
            catch { }
        }

        return null;
    }

    private static string FormatK(long v) =>
        v >= 1_000_000 ? $"{v / 1_000_000.0:F1}M" :
        v >= 1_000     ? $"{v / 1000}k" : v.ToString();

    private static string Trunc(string s, int max) =>
        s.Length > max ? s[..max] + "…" : s;
}
