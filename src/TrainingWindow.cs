using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopIconFlick
{
    internal sealed class TrainingWindow : Window
    {
        private static readonly Brush Shade = RangeSurface.Paint(70, 8, 23, 27);
        private static readonly Brush Ink = RangeSurface.Paint(255, 17, 38, 41);
        private static readonly Brush Muted = RangeSurface.Paint(255, 192, 219, 216);
        private static readonly Brush Accent = RangeSurface.Paint(255, 163, 247, 217);
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly TrainingRound round;
        private readonly TrainingProfile profile;
        private readonly RangeSurface surface;
        private readonly Border panel, hud;
        private readonly TextBlock heading, details, notice, hudStats;
        private readonly TextBox dpiInput, distanceInput, fovInput;
        private readonly CheckBox iconInput;
        private readonly FrameworkElement settings;
        private readonly Button action, abandon;
        private HwndSource source;
        private WindowsDesktop.Rect workArea;
        private readonly MouseSession mouse = new MouseSession();
        private RoundState shownState;
        private bool displayQueued, iconsDone, closed;
        private string message = "";
        private double hitUntil;
        private int shownSecond = -1, shownHits = -1, shownShots = -1;
        private readonly Action<string> report;
        private double frameSampleStart, sampledSeconds;
        private long frameCount, motionPacketCount;
        private TimeSpan lastRenderTime = TimeSpan.MinValue;

        public TrainingWindow(string profilePath, Action<string> report)
        {
            this.report = report;
            profile = TrainingProfile.Load(profilePath);
            round = new TrainingRound(() => clock.Elapsed.TotalSeconds, Environment.TickCount);
            Title = "桌面甩枪 · Desktop Icon Flick";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            Focusable = true;
            UseLayoutRounding = true;

            var root = new Grid { Background = Shade, ClipToBounds = true };
            Content = root;
            surface = new RangeSurface(round);
            root.Children.Add(surface);

            hudStats = Text("", 16, Brushes.White);
            hud = new Border
            {
                Background = Ink, Padding = new Thickness(18, 10, 18, 10), CornerRadius = new CornerRadius(10),
                Child = hudStats, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 16, 0, 0), IsHitTestVisible = false
            };
            root.Children.Add(hud);

            heading = Text("", 36, Brushes.White);
            heading.FontWeight = FontWeights.SemiBold;
            details = Text("", 15, Muted);
            details.Margin = new Thickness(0, 8, 0, 22);
            notice = Text("", 13, Muted);
            notice.Margin = new Thickness(0, 14, 0, 0);

            dpiInput = Field("鼠标 DPI", profile.Aim.Dpi.ToString(CultureInfo.InvariantCulture));
            distanceInput = Field("cm/360", profile.Aim.Distance.ToString(CultureInfo.InvariantCulture));
            fovInput = Field("水平 FOV", profile.Aim.HorizontalFov.ToString(CultureInfo.InvariantCulture));
            var fields = new Grid();
            foreach (TextBox field in new[] { dpiInput, distanceInput, fovInput })
            {
                int column = fields.ColumnDefinitions.Count;
                fields.ColumnDefinitions.Add(new ColumnDefinition());
                FrameworkElement labeled = LabelField(field, column == 2 ? 0 : 12);
                Grid.SetColumn(labeled, column);
                fields.Children.Add(labeled);
            }
            var ranges = Text(AimSettings.Ranges, 11, Muted);
            ranges.Margin = new Thickness(0, 6, 0, 12);
            iconInput = new CheckBox { Content = "使用我的桌面图标", IsChecked = profile.UseIcons, Foreground = Brushes.White, FontSize = 14 };
            var settingGroup = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
            settingGroup.Children.Add(fields);
            settingGroup.Children.Add(ranges);
            settingGroup.Children.Add(iconInput);
            settings = settingGroup;

            action = ControlButton("开始训练", Accent, Ink, StartOrResume);
            abandon = ControlButton("结束本局", Muted, Ink, () => { round.Cancel(); ShowState(); });
            var exit = ControlButton("退出", Brushes.Transparent, Brushes.White, Close);
            var controls = new WrapPanel();
            controls.Children.Add(action);
            controls.Children.Add(abandon);
            controls.Children.Add(exit);
            var content = new StackPanel();
            content.Children.Add(heading);
            content.Children.Add(details);
            content.Children.Add(settings);
            content.Children.Add(controls);
            content.Children.Add(notice);
            var scroller = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel = new Border
            {
                Child = scroller, Padding = new Thickness(26), Background = Ink, CornerRadius = new CornerRadius(14),
                Width = 448, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14)
            };
            root.Children.Add(panel);
            root.SizeChanged += (sender, args) =>
            {
                panel.Width = Math.Min(448, Math.Max(0, root.ActualWidth - 28));
                panel.MaxHeight = Math.Max(0, root.ActualHeight - 28);
            };
            iconInput.Checked += (sender, args) => RefreshNotice();
            iconInput.Unchecked += (sender, args) => RefreshNotice();
            SourceInitialized += InitializeSource;
            Deactivated += (sender, args) => PauseRound("切出窗口，对局已暂停。");
            Closing += (sender, args) => ReleaseInput();
            Closed += CloseSource;
            PreviewKeyDown += HandleKey;
            ShowState();
        }

        private void InitializeSource(object sender, EventArgs args)
        {
            source = (HwndSource)PresentationSource.FromVisual(this);
            source.AddHook(ProcessWindowMessage);
            workArea = WindowsDesktop.PrimaryWorkArea();
            WindowsDesktop.Place(source.Handle, workArea);
            CompositionTarget.Rendering += Advance;
            ShellIcons.LoadAsync(icons => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (closed)
                    return;
                surface.Icons = icons;
                iconsDone = true;
                RefreshNotice();
            })));
        }

        private void CloseSource(object sender, EventArgs args)
        {
            closed = true;
            ReleaseInput();
            CompositionTarget.Rendering -= Advance;
            if (source != null && !source.IsDisposed)
                source.RemoveHook(ProcessWindowMessage);
        }

        private IntPtr ProcessWindowMessage(IntPtr window, int messageId, IntPtr param, IntPtr data, ref bool handled)
        {
            if (messageId == WindowsDesktop.InputMessage && IsActive && mouse.CanRead(window))
            {
                int horizontal, vertical;
                bool pressed;
                if (WindowsDesktop.ReadMouse(data, out horizontal, out vertical, out pressed))
                {
                    if (report != null && (horizontal != 0 || vertical != 0) && round.State == RoundState.Active && round.Remaining > 0)
                        motionPacketCount++;
                    if (round.Input(horizontal, vertical, pressed))
                        hitUntil = clock.Elapsed.TotalSeconds + 0.08;
                    TransitionAfterTick();
                }
            }
            else if (messageId == WindowsDesktop.DisplayChange || messageId == WindowsDesktop.DpiChange ||
                     (messageId == WindowsDesktop.SettingChange && param.ToInt64() == WindowsDesktop.WorkAreaChange))
            {
                if (!displayQueued && !closed)
                {
                    displayQueued = true;
                    bool active = round.State == RoundState.Active || round.State == RoundState.Paused;
                    if (active)
                    {
                        ReleaseInput();
                        round.Cancel();
                        message = "显示设置已变化，请重新开始。";
                    }
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                    {
                        displayQueued = false;
                        if (closed)
                            return;
                        workArea = WindowsDesktop.PrimaryWorkArea();
                        WindowsDesktop.Place(source.Handle, workArea);
                        ShowState();
                    }));
                }
            }
            return IntPtr.Zero;
        }

        private void HandleKey(object sender, KeyEventArgs args)
        {
            if (args.Key != Key.Escape)
                return;
            args.Handled = true;
            if (args.IsRepeat)
                return;
            if (round.State == RoundState.Active)
                PauseRound("");
            else
                Close();
        }

        private void StartOrResume()
        {
            if (displayQueued || round.State == RoundState.Active)
                return;
            bool resume = round.State == RoundState.Paused;
            if (!resume && !profile.SetAim(dpiInput.Text, distanceInput.Text, fovInput.Text))
            {
                message = "请检查输入范围。上一组有效设置未更改。";
                RefreshNotice();
                return;
            }
            if (!resume)
            {
                profile.UseIcons = iconInput.IsChecked == true;
                profile.Save();
            }
            if (!AcquireInput())
            {
                message = "未能取得鼠标控制。请让训练窗口保持前台后重试。";
                RefreshNotice();
                return;
            }
            if (resume)
                round.Resume();
            else
            {
                frameCount = 0;
                motionPacketCount = 0;
                sampledSeconds = 0;
                lastRenderTime = TimeSpan.MinValue;
                surface.UseIcons = profile.UseIcons && surface.Icons.Count > 0;
                round.Start(profile.Aim, surface.ActualWidth, surface.ActualHeight);
            }
            frameSampleStart = clock.Elapsed.TotalSeconds;
            hitUntil = 0;
            message = "";
            Focus();
            ShowState();
        }

        private bool AcquireInput()
        {
            if (source == null || !IsActive || WindowsDesktop.GetForegroundWindow() != source.Handle ||
                surface.ActualWidth <= 1 || surface.ActualHeight <= 1 ||
                !mouse.TryAcquire(source.Handle))
                return false;
            Cursor = Cursors.None;
            ForceCursor = true;
            Mouse.UpdateCursor();
            return true;
        }

        private void ReleaseInput()
        {
            if (!mouse.Release())
                return;
            sampledSeconds += clock.Elapsed.TotalSeconds - frameSampleStart;
            ForceCursor = false;
            ClearValue(CursorProperty);
            Mouse.UpdateCursor();
        }

        private void PauseRound(string reason)
        {
            if (round.State != RoundState.Active)
                return;
            message = reason;
            round.Pause();
            ReleaseInput();
            if (round.State == RoundState.Finished)
                FinishRound();
            else
                ShowState();
        }

        private void Advance(object sender, EventArgs args)
        {
            if (round.State != RoundState.Active)
                return;
            if (!IsActive || WindowsDesktop.GetForegroundWindow() != source.Handle)
            {
                PauseRound("切出窗口，对局已暂停。");
                return;
            }
            if (!mouse.HasConfinement())
            {
                PauseRound("鼠标控制已释放，请手动继续。");
                return;
            }
            round.Tick();
            TransitionAfterTick();
            if (round.State != RoundState.Active)
                return;
            var frame = args as RenderingEventArgs;
            if (frame != null && frame.RenderingTime != lastRenderTime)
            {
                lastRenderTime = frame.RenderingTime;
                frameCount++;
            }
            surface.Flash = hitUntil > clock.Elapsed.TotalSeconds;
            surface.InvalidateVisual();
            UpdateHud();
        }

        private void TransitionAfterTick()
        {
            if (round.State == RoundState.Finished && shownState != RoundState.Finished)
                FinishRound();
        }

        private void FinishRound()
        {
            ReleaseInput();
            profile.RecordCompletedRound(round.Hits);
            if (report != null)
            {
                Matrix scale = source.CompositionTarget.TransformToDevice;
                WindowsDesktop.Rect desktop = WindowsDesktop.VirtualScreen();
                report(string.Format(CultureInfo.InvariantCulture,
                    "{0:O} appearance={1} work_area={2}x{3} dpi={4:0}x{5:0} shots={6} hits={7} render_callbacks={8} active_seconds={9:0.000} render_callbacks_per_second={10:0.0} move_packets={11} desktop_bounds={12},{13},{14},{15} monitors={16} primary_hz={17} os_build={18}",
                    DateTimeOffset.Now, surface.UseIcons && surface.Icons.Count > 0 ? "icons" : "balls",
                    workArea.Width, workArea.Height, scale.M11 * 96, scale.M22 * 96,
                    round.Shots, round.Hits, frameCount, sampledSeconds, sampledSeconds > 0 ? frameCount / sampledSeconds : 0,
                    motionPacketCount, desktop.Left, desktop.Top, desktop.Right, desktop.Bottom,
                    WindowsDesktop.MonitorCount(), WindowsDesktop.PrimaryRefreshRate(), Environment.OSVersion.Version.Build));
            }
            ShowState();
        }

        private void ShowState()
        {
            shownState = round.State;
            bool active = shownState == RoundState.Active, paused = shownState == RoundState.Paused;
            panel.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            hud.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            settings.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
            abandon.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
            switch (shownState)
            {
                case RoundState.Paused:
                    heading.Text = "对局暂停";
                    details.Text = string.Format("剩余 {0:0.0} 秒 · 命中 {1} / {2}", round.Remaining, round.Hits, round.Shots);
                    action.Content = "继续训练";
                    break;
                case RoundState.Finished:
                    heading.Text = "训练结束";
                    details.Text = string.Format("命中 {0} · 射击 {1} · 命中率 {2:0.#}%\n个人最佳 {3} 命中",
                        round.Hits, round.Shots, round.Accuracy, profile.Best);
                    action.Content = "再来一局";
                    break;
                default:
                    heading.Text = "桌面甩枪";
                    details.Text = "固定准星 · 三个目标 · 30 秒\n甩动鼠标转向，左键击发。Esc 暂停。";
                    if (profile.Best > 0)
                        details.Text += string.Format("\n个人最佳 {0} 命中", profile.Best);
                    action.Content = "开始训练";
                    break;
            }
            shownSecond = shownHits = shownShots = -1;
            surface.InvalidateVisual();
            if (active)
                UpdateHud();
            else
                RefreshNotice();
        }

        private void UpdateHud()
        {
            int seconds = (int)Math.Ceiling(round.Remaining);
            if (shownSecond == seconds && shownHits == round.Hits && shownShots == round.Shots)
                return;
            shownSecond = seconds;
            shownHits = round.Hits;
            shownShots = round.Shots;
            hudStats.Text = string.Format("{0:00} 秒  /  命中 {1}/{2}  /  {3}  ·  Esc 暂停", seconds, round.Hits, round.Shots,
                surface.UseIcons ? "图标" : profile.UseIcons ? "小球（本局回退）" : "小球");
        }

        private void RefreshNotice()
        {
            string helper = message.Length > 0 ? message : profile.Notice;
            if (helper.Length == 0 && iconInput.IsChecked == true)
                helper = !iconsDone ? "正在读取桌面图标，可先用小球开局。" :
                    surface.Icons.Count == 0 ? "未读取到桌面图标，将使用普通小球。" : "只读使用你的桌面图标，不会移动或打开原项目。";
            notice.Text = helper;
            notice.Visibility = helper.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private static TextBlock Text(string value, double size, Brush color)
        {
            return new TextBlock { Text = value, FontSize = size, Foreground = color, TextWrapping = TextWrapping.Wrap };
        }

        private static TextBox Field(string label, string value)
        {
            var field = new TextBox { Text = value, Padding = new Thickness(8, 5, 8, 5), FontSize = 16, MaxLength = 20 };
            AutomationProperties.SetName(field, label);
            return field;
        }

        private static FrameworkElement LabelField(TextBox field, double right)
        {
            var group = new StackPanel { Margin = new Thickness(0, 0, right, 0) };
            var label = Text(AutomationProperties.GetName(field), 12, Muted);
            label.Margin = new Thickness(0, 0, 0, 5);
            group.Children.Add(label);
            group.Children.Add(field);
            return group;
        }

        private static Button ControlButton(string label, Brush background, Brush foreground, Action perform)
        {
            var button = new Button
            {
                Content = label, Background = background, Foreground = foreground, BorderBrush = Muted,
                Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 0), FontSize = 14,
                MinWidth = 76
            };
            button.Click += (sender, args) => perform();
            return button;
        }
    }
}
