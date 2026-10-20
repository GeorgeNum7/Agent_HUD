using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace AgentHud
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            var root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
            var url = args.Length > 1 ? args[1] : "http://127.0.0.1:4318";
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != "http" || !uri.IsLoopback) return 2;
            var data = Path.Combine(root, ".data");
            Directory.CreateDirectory(data);
            var hash = BitConverter.ToString(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(root.ToLowerInvariant()))).Replace("-", "").Substring(0, 16);
            bool first;
            using (var mutex = new Mutex(true, "Local\\AgentHUD.Widget." + hash, out first))
            {
                var startup = Path.Combine(data, "widget-startup.log");
                if (!first)
                {
                    try { File.AppendAllText(startup, DateTime.Now.ToString("HH:mm:ss.fff") + " pid " + Process.GetCurrentProcess().Id + ": already running, asking it to show\n"); } catch { }
                    try { using (var signal = EventWaitHandle.OpenExisting("Local\\AgentHUD.Show." + hash)) signal.Set(); } catch { }
                    return 0;
                }
                // The running instance starts a fresh log; a second launch only adds its one line above.
                try { File.WriteAllText(startup, DateTime.Now.ToString("HH:mm:ss.fff") + " pid " + Process.GetCurrentProcess().Id + ": starting\n"); } catch { }
                using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\AgentHUD.Show." + hash))
                using (var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\AgentHUD.Exit." + hash))
                {
                    try
                    {
                        File.AppendAllText(startup, "Creating application\n");
                        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                        app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                        {
                            File.AppendAllText(Path.Combine(data, "widget-error.log"), DateTime.UtcNow.ToString("o") + " " + e.Exception.GetType().Name + ": " + e.Exception.Message + Environment.NewLine);
                        };
                        var window = new HudWindow(data, uri, signal, args.Contains("--preview"), exitSignal);
                        File.AppendAllText(Path.Combine(data, "widget-startup.log"), "Starting dispatcher\n");
                        app.Run(window);
                        return 0;
                    }
                    catch (Exception e)
                    {
                        File.AppendAllText(Path.Combine(data, "widget-error.log"), e.ToString() + Environment.NewLine);
                        return 1;
                    }
                }
            }
        }
    }

    public sealed class HudWindow : Window
    {
        readonly string dataDir;
        readonly Uri server;
        readonly EventWaitHandle showSignal;
        readonly HttpClient http;
        readonly WindowPin pin;
        readonly WindowPlacement placement;
        RegisteredWaitHandle showRegistration, exitRegistration;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
        readonly DispatcherTimer timer = new DispatcherTimer();
        readonly DispatcherTimer saveTimer = new DispatcherTimer();
        readonly QuotaRing codex = new QuotaRing { Platform = "Codex" };
        readonly QuotaRing claude = new QuotaRing { Platform = "Claude Code" };
        readonly QuotaLabel codexLabel = new QuotaLabel("Codex");
        readonly QuotaLabel claudeLabel = new QuotaLabel("Claude");
        readonly Border tint = new Border();
        readonly Border sheen = new Border { IsHitTestVisible = false };
        readonly Border edge = new Border { BorderThickness = new Thickness(1), IsHitTestVisible = false };
        readonly Border rim = new Border { BorderThickness = new Thickness(1), Margin = new Thickness(1), IsHitTestVisible = false };
        readonly System.Windows.Shapes.Path pinShape = new System.Windows.Shapes.Path { Data = Geometry.Parse("M5,2 H11 L10.3,7.3 L12.5,10 H3.5 L5.7,7.3 Z M8,10 V14.5"), StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        readonly System.Windows.Shapes.Path pinSlash = new System.Windows.Shapes.Path { Data = Geometry.Parse("M2,2 L14,14"), StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        Grid capsule;
        Palette palette;
        WidgetState saved = new WidgetState();
        Snapshot snapshot;
        Button periodButton, pinButton;
        Forms.NotifyIcon tray;
        bool busy, online, exiting, degraded;
        double lastServiceStart;
        int failedFetches;

        public HudWindow(string data, Uri url, EventWaitHandle signal, bool preview, EventWaitHandle exitSignal = null)
        {
            dataDir = data; server = url; showSignal = signal;
            http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(4), MaxResponseContentBufferSize = 1024 * 1024 };
            ReadState();
            Title = "Agent HUD Widget";
            Width = 260; Height = 48;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            // Not a layered window: DWM only honours the capsule region for the blur on regular windows.
            // The glass frame spans the whole client area so transparent pixels show the blurred backdrop.
            Background = Brushes.Transparent;
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
            ShowInTaskbar = false; ShowActivated = false;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            Application.Current.Resources.MergedDictionaries.Add(Palette.LoadStyles());
            Content = BuildUi();
            ApplyTheme();
            SetCornerRadius(GlassWindow.SystemCornerRadius);
            SourceInitialized += delegate
            {
                var handle = new WindowInteropHelper(this).Handle;
                HwndSource.FromHwnd(handle).CompositionTarget.BackgroundColor = Colors.Transparent;
                GlassWindow.EnableBlur(handle);
                if (!GlassWindow.RoundCorners(handle)) SetCornerRadius(0);
            };
            pin = new WindowPin(this, saved.pinned, delegate(int error)
            {
                File.AppendAllText(Path.Combine(dataDir, "widget-error.log"), DateTime.UtcNow.ToString("o") + " Window pin: Win32 error " + error + Environment.NewLine);
            });
            placement = new WindowPlacement(this, saved, SaveState);
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            File.AppendAllText(Path.Combine(dataDir, "widget-startup.log"), "UI constructed\n");
            SetupTray();
            File.AppendAllText(Path.Combine(dataDir, "widget-startup.log"), "Tray constructed\n");
            SetPeriod(saved.period);
            UpdatePin();
            RestorePosition();
            saveTimer.Interval = TimeSpan.FromMilliseconds(350);
            saveTimer.Tick += delegate { saveTimer.Stop(); SaveState(); };
            LocationChanged += delegate { if (IsLoaded) { saveTimer.Stop(); saveTimer.Start(); } };
            Loaded += async delegate
            {
                File.AppendAllText(Path.Combine(dataDir, "widget-startup.log"), "Window loaded\n");
                if (showRegistration == null) showRegistration = ThreadPool.RegisterWaitForSingleObject(showSignal, delegate { DispatchSignal(ShowWidget); }, null, Timeout.Infinite, false);
                if (exitSignal != null && exitRegistration == null) exitRegistration = ThreadPool.RegisterWaitForSingleObject(exitSignal, delegate { DispatchSignal(Close); }, null, Timeout.Infinite, false);
                await Fetch();
                if (exiting) return;
                timer.Start();
                if (preview) { await Task.Delay(750); if (!exiting) await Dispatcher.InvokeAsync(new Action(ExportPreview), DispatcherPriority.ApplicationIdle); }
            };
            ContentRendered += delegate { File.AppendAllText(Path.Combine(dataDir, "widget-startup.log"), "Window rendered\n"); };
            timer.Interval = TimeSpan.FromSeconds(5);
            timer.Tick += async delegate { await Fetch(); };
            IsVisibleChanged += delegate
            {
                if (IsVisible) RenderData();
                else { codex.PauseAnimation(); claude.PauseAnimation(); }
            };
            capsule.ToolTipOpening += delegate { RenderData(); };
            Closing += delegate
            {
                exiting = true;
                if (showRegistration != null) showRegistration.Unregister(null);
                if (exitRegistration != null) exitRegistration.Unregister(null);
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                timer.Stop(); saveTimer.Stop(); SaveState(); tray.Visible = false; tray.Dispose(); http.Dispose();
            };
        }

        void DispatchSignal(Action action)
        {
            if (exiting || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(delegate { if (!exiting) action(); }));
        }

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color || e.Category == UserPreferenceCategory.VisualStyle)
                Dispatcher.BeginInvoke(new Action(ApplyTheme));
        }

        void SetCornerRadius(double radius)
        {
            tint.CornerRadius = sheen.CornerRadius = edge.CornerRadius = new CornerRadius(radius);
            rim.CornerRadius = new CornerRadius(Math.Max(0, radius - 1));
        }

        void ApplyTheme()
        {
            palette = Palette.Read();
            palette.ApplyTo(Application.Current.Resources);
            tint.Background = palette.Tint; sheen.Background = palette.Sheen; edge.BorderBrush = palette.Edge;
            rim.BorderBrush = degraded ? palette.RimOffline : palette.Rim;
            codex.Logo = Logos.Codex; codex.ArcBrush = palette.CodexArc; codex.LogoBrush = palette.CodexLogo; codex.Track = palette.CodexTrack;
            claude.Logo = Logos.Claude; claude.ArcBrush = palette.ClaudeArc; claude.LogoBrush = palette.ClaudeLogo; claude.Track = palette.ClaudeTrack;
            codex.InvalidateVisual(); claude.InvalidateVisual();
            if (pinButton != null) UpdatePin();
        }

        // Glass layers: blurred backdrop (window accent) → grey-white tint → hairline edge; the rim turns heat-orange when offline.
        UIElement BuildUi()
        {
            capsule = new Grid { Cursor = Cursors.SizeAll, Background = Brushes.Transparent };
            capsule.Children.Add(tint); capsule.Children.Add(sheen); capsule.Children.Add(edge); capsule.Children.Add(rim);
            var body = new Grid();
            // The two buttons get the strip's full height as hit area (the visible chip stays small).
            foreach (var width in new[] { 7.0, 34, 5, 52, 5, 34, 5, 52, -1, 22, 42, 2 })
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = width < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(width) });
            capsule.Children.Add(body);
            codex.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(codex, 1); body.Children.Add(codex);
            Grid.SetColumn(codexLabel, 3); body.Children.Add(codexLabel);
            claude.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(claude, 5); body.Children.Add(claude);
            Grid.SetColumn(claudeLabel, 7); body.Children.Add(claudeLabel);

            capsule.ContextMenu = BuildMenu();
            var pinIcon = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            pinIcon.Children.Add(pinShape); pinIcon.Children.Add(pinSlash);
            // One click pins, the next unpins; the full menu stays on right-click anywhere on the strip.
            pinButton = new Button { Content = pinIcon, Width = 22, Height = 46, RenderTransform = new ScaleTransform(), RenderTransformOrigin = new Point(.5, .5) };
            pinButton.SetResourceReference(StyleProperty, "IconChip");
            AutomationProperties.SetAutomationId(pinButton, "PinButton");
            pinButton.Click += delegate { SetPinned(!saved.pinned); };
            pinButton.PreviewMouseLeftButtonDown += delegate { Press(pinButton, .9, 100); };
            pinButton.PreviewMouseLeftButtonUp += delegate { Press(pinButton, 1, 220); };
            pinButton.MouseLeave += delegate { Press(pinButton, 1, 220); };
            Grid.SetColumn(pinButton, 9); body.Children.Add(pinButton);

            periodButton = new Button { Content = "5h", Width = 42, Height = 46, FontSize = 11, FontWeight = FontWeights.SemiBold, RenderTransform = new ScaleTransform() };
            periodButton.SetResourceReference(StyleProperty, "GlassChip");
            AutomationProperties.SetName(periodButton, "点击切换 5 小时 / 7 天额度");
            periodButton.Click += delegate { SetPeriod(saved.period == 300 ? 10080 : 300); };
            // Feedback on pointer-down, not release.
            periodButton.PreviewMouseLeftButtonDown += delegate { Press(periodButton, .94, 100); };
            periodButton.PreviewMouseLeftButtonUp += delegate { Press(periodButton, 1, 220); };
            periodButton.MouseLeave += delegate { Press(periodButton, 1, 220); };
            Grid.SetColumn(periodButton, 10); body.Children.Add(periodButton);

            capsule.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                DependencyObject node = e.OriginalSource as DependencyObject;
                while (node != null && node != capsule) { if (node is Button) return; node = VisualTreeHelper.GetParent(node); }
                if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); placement.EnsureVisible(true); SaveState(); } catch (InvalidOperationException) { } }
            };
            return capsule;
        }

        // Starts from the current scale, so a quick tap reverses mid-flight; no overshoot.
        static void Press(UIElement element, double to, int milliseconds)
        {
            var scale = (ScaleTransform)element.RenderTransform;
            if (!SystemParameters.ClientAreaAnimation) { scale.ScaleX = scale.ScaleY = 1; return; }
            var move = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, move); scale.BeginAnimation(ScaleTransform.ScaleYProperty, move);
        }
        ContextMenu BuildMenu()
        {
            var menu = new ContextMenu();
            var open = new MenuItem { Header = "打开完整面板" }; open.Click += delegate { OpenDashboard(); };
            var codexUsage = new MenuItem { Header = "Codex 官方用量 ↗" }; codexUsage.Click += delegate { OpenCodexUsage(); };
            var claudeUsage = new MenuItem { Header = "Claude 官方用量 ↗" }; claudeUsage.Click += delegate { OpenClaudeUsage(); };
            var top = new MenuItem { Header = "始终置顶", IsCheckable = true };
            top.Click += delegate { SetPinned(top.IsChecked); top.IsChecked = saved.pinned; };
            var hide = new MenuItem { Header = "收起到托盘" }; hide.Click += delegate { Hide(); };
            var reset = new MenuItem { Header = "重置小组件位置" }; reset.Click += delegate { placement.Reset(); };
            var quit = new MenuItem { Header = "退出小组件" }; quit.Click += delegate { Close(); };
            menu.Items.Add(open); menu.Items.Add(codexUsage); menu.Items.Add(claudeUsage); menu.Items.Add(new Separator());
            menu.Items.Add(top); menu.Items.Add(hide); menu.Items.Add(reset); menu.Items.Add(new Separator()); menu.Items.Add(quit);
            menu.Opened += delegate { top.IsChecked = saved.pinned; }; return menu;
        }
        void SetPeriod(int period)
        {
            saved.period = period == 10080 ? 10080 : 300;
            if (periodButton == null) return;
            periodButton.Content = saved.period == 300 ? "5h" : "7d";
            periodButton.ToolTip = saved.period == 300 ? "当前 5 小时额度 · 点击查看 7 天" : "当前 7 天额度 · 点击查看 5 小时";
            RenderData(); if (IsLoaded) SaveState();
        }
        // Shared by the pin button and the menu item, so both always agree.
        // If Windows refuses the z-order change, the previous state is kept.
        void SetPinned(bool value)
        {
            if (pin.SetPinned(value)) saved.pinned = value;
            else pin.SetPinned(saved.pinned);
            UpdatePin(); SaveState();
        }
        void UpdatePin()
        {
            var brush = new SolidColorBrush(saved.pinned ? palette.Accent : palette.TextMuted);
            pinShape.Stroke = brush; pinShape.Fill = saved.pinned ? brush : Brushes.Transparent;
            pinSlash.Stroke = brush; pinSlash.Visibility = saved.pinned ? Visibility.Collapsed : Visibility.Visible;
            var action = saved.pinned ? "已置顶 · 点击取消置顶" : "未置顶 · 点击置顶";
            pinButton.ToolTip = action + "\n右键打开更多选项";
            AutomationProperties.SetName(pinButton, saved.pinned ? "已置顶，点击取消置顶" : "未置顶，点击置顶");
        }
        async Task Fetch()
        {
            if (busy || exiting) return;
            busy = true;
            try
            {
                var raw = await http.GetStringAsync(new Uri(server, "/api/quotas"));
                if (exiting) return;
                var next = json.Deserialize<Snapshot>(raw);
                if (next == null || next.providers == null) throw new InvalidDataException();
                snapshot = next; online = true; failedFetches = 0;
            }
            catch { online = false; if (!exiting && ++failedFetches >= 2) StartService(); }
            finally { busy = false; if (!exiting) RenderData(); }
        }
        // The local service can die with whatever launched it (an agent session, a closed terminal).
        // The widget runs on its own, so it brings the service back through the normal launcher.
        void StartService()
        {
            var now = RingValue.Now();
            if (now - lastServiceStart < 60000) return;
            lastServiceStart = now;
            var root = Path.GetDirectoryName(dataDir);
            var script = Path.Combine(root, "start.ps1");
            if (!File.Exists(script)) return;
            try
            {
                var info = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\" -NoBrowser") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
                info.EnvironmentVariables["PORT"] = server.Port.ToString(CultureInfo.InvariantCulture);
                Process.Start(info);
            }
            catch (Exception e) { File.AppendAllText(Path.Combine(dataDir, "widget-error.log"), DateTime.UtcNow.ToString("o") + " Service start: " + e.Message + Environment.NewLine); }
        }
        void RenderData()
        {
            if (!IsVisible || exiting) return;
            var providers = snapshot == null ? null : snapshot.providers;
            var now = RingValue.Now();
            var c = RingValue.From(providers == null ? null : providers.FirstOrDefault(p => p.id == "codex"), saved.period, now, online);
            var a = RingValue.From(providers == null ? null : providers.FirstOrDefault(p => p.id == "claude"), saved.period, now, online);
            codex.Update(c); claude.Update(a); codexLabel.Show(c, !online); claudeLabel.Show(a, !online);
            var scanStale = snapshot != null && (!snapshot.collectedAt.HasValue || now - snapshot.collectedAt.Value > 30000);
            // Offline: orange edge *and* the word "离线" under each ring — colour is never the only signal.
            // A slow session scan doesn't touch the quotas shown here, so it only goes in the tooltip.
            var wasDegraded = degraded;
            degraded = !online;
            if (degraded != wasDegraded) rim.BorderBrush = degraded ? palette.RimOffline : palette.Rim;
            var detail = "拖动可移动 · 靠近屏幕边缘自动贴边 · 右键打开菜单\n" + (!online ? "本地服务离线，正在自动重启；显示的是最后一次快照" : snapshot.loading ? "正在读取额度" : scanStale ? "会话扫描延迟，额度不受影响" : "每 5 秒读取本地快照；平台额度按各自频率更新") + "\n悬停圆环查看额度更新时间和重置倒计时";
            if (!Equals(capsule.ToolTip, detail)) capsule.ToolTip = detail;
        }
        void OpenDashboard() { try { Process.Start(new ProcessStartInfo(server.AbsoluteUri) { UseShellExecute = true }); } catch { } }
        void OpenClaudeUsage() { try { Process.Start(new ProcessStartInfo("https://claude.ai/settings/usage") { UseShellExecute = true }); } catch { } }
        void OpenCodexUsage() { try { Process.Start(new ProcessStartInfo("https://chatgpt.com/codex/settings/usage") { UseShellExecute = true }); } catch { } }
        void ShowWidget() { Show(); WindowState = WindowState.Normal; placement.EnsureVisible(false); pin.SetPinned(saved.pinned); RenderData(); Activate(); }

        // Render our own WPF content for visual QA, without capturing the user's desktop.
        void ExportPreview()
        {
            try
            {
                UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Width * 2, (int)Height * 2, 192, 192, PixelFormats.Pbgra32);
                bitmap.Render((Visual)Content);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(dataDir, "widget-preview.png"))) encoder.Save(stream);
            }
            catch (Exception e) { File.AppendAllText(Path.Combine(dataDir, "widget-error.log"), "Preview: " + e.Message + Environment.NewLine); }
        }

        void SetupTray()
        {
            tray = new Forms.NotifyIcon { Text = "Agent HUD · 额度小组件", Icon = CreateIcon(), Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("显示小组件", null, delegate { Dispatcher.Invoke(new Action(ShowWidget)); });
            menu.Items.Add("重置小组件位置", null, delegate { Dispatcher.Invoke(new Action(delegate { ShowWidget(); placement.Reset(); })); });
            menu.Items.Add("打开完整面板", null, delegate { OpenDashboard(); });
            menu.Items.Add("Codex 官方用量", null, delegate { OpenCodexUsage(); });
            menu.Items.Add("Claude 官方用量", null, delegate { OpenClaudeUsage(); });
            menu.Items.Add("收起小组件", null, delegate { Dispatcher.Invoke(new Action(Hide)); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出小组件", null, delegate { Dispatcher.Invoke(new Action(Close)); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(new Action(ShowWidget)); };
            var iconPath = Path.Combine(dataDir, "widget.ico");
            if (!File.Exists(iconPath)) { using (var file = File.Create(iconPath)) tray.Icon.Save(file); }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);
        static Drawing.Icon CreateIcon()
        {
            using (var bitmap = new Drawing.Bitmap(32, 32))
            using (var g = Drawing.Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                // Same mark as public/icon.svg: graphite tile, white ring gauge.
                using (var background = new Drawing.SolidBrush(Drawing.Color.FromArgb(29, 29, 31))) g.FillEllipse(background, 0, 0, 32, 32);
                using (var track = new Drawing.Pen(Drawing.Color.FromArgb(70, 255, 255, 255), 4)) g.DrawEllipse(track, 6, 6, 20, 20);
                using (var arc = new Drawing.Pen(Drawing.Color.White, 4)) { arc.StartCap = Drawing.Drawing2D.LineCap.Round; arc.EndCap = Drawing.Drawing2D.LineCap.Round; g.DrawArc(arc, 6, 6, 20, 20, -90, 252); }
                var handle = bitmap.GetHicon();
                try { using (var original = Drawing.Icon.FromHandle(handle)) return (Drawing.Icon)original.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
        void ReadState()
        {
            try { saved = json.Deserialize<WidgetState>(File.ReadAllText(Path.Combine(dataDir, "widget-state.json"))) ?? new WidgetState(); } catch { }
        }
        void SaveState()
        {
            try
            {
                saved.left = Left; saved.top = Top;
                if (placement != null) placement.Capture();
                var target = Path.Combine(dataDir, "widget-state.json");
                var tmp = target + ".tmp";
                File.WriteAllText(tmp, json.Serialize(saved));
                if (File.Exists(target)) File.Replace(tmp, target, null); else File.Move(tmp, target);
            }
            catch { }
        }
        void RestorePosition()
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 22; Top = area.Top + 70;
            if (!saved.left.HasValue || !saved.top.HasValue) return;
            var left = saved.left.Value; var top = saved.top.Value;
            if (Double.IsNaN(left) || Double.IsInfinity(left) || Double.IsNaN(top) || Double.IsInfinity(top)) return;
            // Migrate the legacy DIP coordinates; WindowPlacement validates against real monitor work areas after HWND creation.
            Left = left; Top = top;
        }
    }

    // The percentage (or a short status) above the platform name, beside each ring.
    // Text on glass stays in the primary colour (grey on glass measures as low as 2.3 : 1);
    // hierarchy comes from size and weight, and an uncertain value drops to regular weight.
    public sealed class QuotaLabel : StackPanel
    {
        readonly string platform;
        readonly TextBlock value = new TextBlock { FontSize = 13.5, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI") };
        readonly TextBlock name = new TextBlock { FontSize = 9.5, Margin = new Thickness(0, -2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        double? lastPercent;
        string lastCaption, lastBadge;
        bool initialized, lastStale, lastOffline;

        public QuotaLabel(string platform)
        {
            this.platform = platform;
            VerticalAlignment = VerticalAlignment.Center;
            name.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            Children.Add(value); Children.Add(name);
            Show(new RingValue { Caption = "等待数据" }, false);
        }

        public void Show(RingValue ring, bool offline)
        {
            if (!Equals(ToolTip, ring.Detail)) ToolTip = ring.Detail;
            var badge = ring.ResetsAt.HasValue ? RingValue.ResetBadge(ring.ResetsAt.Value, RingValue.Now()) : null;
            if (initialized && lastPercent == ring.Percent && lastCaption == ring.Caption && lastStale == ring.Stale && lastOffline == offline && lastBadge == badge) return;
            initialized = true; lastPercent = ring.Percent; lastCaption = ring.Caption; lastStale = ring.Stale; lastOffline = offline; lastBadge = badge;
            if (ring.Percent.HasValue)
            {
                value.Text = Math.Round(ring.Percent.Value).ToString(CultureInfo.InvariantCulture) + "%";
                value.FontWeight = ring.Stale ? FontWeights.Normal : FontWeights.SemiBold;
                // Red only for a nearly used-up quota, always next to the number itself.
                value.SetResourceReference(TextBlock.ForegroundProperty, ring.Percent.Value < 20 ? "Low" : "Text");
                // The logo in the ring names the platform, so this line carries when the quota resets.
                // Offline keeps its word; without a reset time it falls back to the platform name.
                name.Text = offline ? platform + " 离线" : badge ?? platform + (ring.Stale ? " 快照" : " 剩余");
            }
            else
            {
                value.Text = offline ? "离线" : ring.Caption == "等待连接" ? "待连接" : ring.Caption == "桌面版未接入" ? "未接入" : ring.Caption == "等待额度重置" ? "待更新" : ring.Caption == "等待数据" ? "等待" : "未提供";
                value.FontWeight = FontWeights.Normal;
                value.SetResourceReference(TextBlock.ForegroundProperty, "Text");
                name.Text = platform;
            }
        }
    }
}
