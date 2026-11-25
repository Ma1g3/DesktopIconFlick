using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace DesktopIconFlick
{
    internal static partial class Tests
    {
        private static void CompetitionRoomUpdatesOnDispatcher()
        {
            string profile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
            var elapsed = Stopwatch.StartNew();
            int shift = 0;
            Func<double> now = () => elapsed.Elapsed.TotalSeconds + Volatile.Read(ref shift);
            var window = new TrainingWindow(profile, null, now, 11);
            try
            {
                var solo = CompetitionField<StackPanel>(window, "soloContent");
                var competition = CompetitionField<StackPanel>(window, "competitionContent");
                Require(solo.Visibility == Visibility.Visible && competition.Visibility == Visibility.Collapsed &&
                    CompetitionField<LanSession>(window, "competitionSession") == null, "single-player entry remains offline");
                InvokeWindow(window, "OpenCompetition");
                CompetitionField<TextBox>(window, "competitionNickname").Text = "乙";
                CompetitionField<TextBox>(window, "competitionAddress").Text = "123";
                InvokeWindow(window, "JoinCompetition");
                var status = CompetitionField<TextBlock>(window, "competitionStatus");
                Require(status.Text.Contains("IPv4"), "invalid room address can be corrected in form");

                using (var probe = LanSession.Host(LoopbackLan, 0, "已关闭", now))
                {
                    CompetitionField<TextBox>(window, "competitionPort").Text = probe.Endpoint.Port.ToString();
                    probe.Dispose();
                }
                CompetitionField<TextBox>(window, "competitionAddress").Text = "127.0.0.1";
                InvokeWindow(window, "JoinCompetition");
                PumpCompetition(() => CompetitionField<LanSession>(window, "competitionSession") == null &&
                    status.Text.Contains("无法连接"), "background connection failure returns to form");

                using (LanSession host = LanSession.Host(LoopbackLan, 0, "甲", now))
                {
                    CompetitionField<TextBox>(window, "competitionPort").Text = host.Endpoint.Port.ToString();
                    InvokeWindow(window, "JoinCompetition");
                    var rows = CompetitionField<StackPanel>(window, "competitionRows");
                    var heading = CompetitionField<TextBlock>(window, "competitionHeading");
                    PumpCompetition(() => rows.Children.Count == 2, "background roster refresh reaches window");
                    var guest = CompetitionField<LanSession>(window, "competitionSession");
                    WaitForLan(() => guest.View.CanReady, "window client has synchronized time");
                    host.SetReady(true);
                    guest.SetReady(true);
                    WaitForLan(() => host.View.Snapshot.CanStart, "window room ready");
                    host.StartRound();
                    WaitForLan(() => guest.View.Schedule != null, "window room schedule received");
                    Volatile.Write(ref shift, 34);
                    host.SubmitScore(1, 4, 8);
                    guest.SubmitScore(1, 5, 9);
                    WaitForLan(() => guest.View.Snapshot.Phase == LanPhase.Results, "window room results received");
                    PumpCompetition(() => heading.Text == "本轮排名", "ranking reaches WPF dispatcher");
                    Require(((Grid)rows.Children[1]).Children.OfType<TextBlock>().Any(text => text.Text == "5"),
                        "WPF uses the score reported in host ranking");
                }
                PumpCompetition(() => CompetitionField<LanSession>(window, "competitionSession") == null,
                    "host shutdown returns window to form");
                InvokeWindow(window, "LeaveCompetition");
                Require(solo.Visibility == Visibility.Visible, "leaving competition restores single-player screen");
            }
            finally
            {
                window.Close();
                File.Delete(profile);
            }
        }

        private static T CompetitionField<T>(TrainingWindow window, string name)
        {
            return (T)typeof(TrainingWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
        }

        private static void InvokeWindow(TrainingWindow window, string name, params object[] arguments)
        {
            typeof(TrainingWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, arguments);
        }

        private static void PumpCompetition(Func<bool> complete, string message, int timeout = 3500)
        {
            var frame = new DispatcherFrame();
            var time = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (sender, args) =>
            {
                if (complete() || time.ElapsedMilliseconds >= timeout)
                    frame.Continue = false;
            };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            Require(complete(), message);
        }

        private static void CompetitionDesktopInputLifecycle()
        {
            string profile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
            new TrainingProfile(profile) { UseIcons = false, PlayHitSound = false }.Save();
            var stopwatch = Stopwatch.StartNew();
            int shift = 0;
            Func<double> now = () => stopwatch.Elapsed.TotalSeconds + Volatile.Read(ref shift);
            var window = new TrainingWindow(profile, Console.WriteLine, now, 11);
            var focusSink = new Window { Width = 220, Height = 100, Title = "桌面甩枪测试（切换窗口）" };
            try
            {
                window.Show();
                window.Activate();
                PumpCompetition(() => window.IsActive, "test training window must be foreground");
                Require(CursorIsFree(), "training starts with free cursor");
                var address = LanAddress.Available().FirstOrDefault() ?? LoopbackLan;
                using (LanSession host = LanSession.Host(address, 0, "房主", now))
                {
                    InvokeWindow(window, "OpenCompetition");
                    CompetitionField<TextBox>(window, "competitionNickname").Text = "客户端";
                    CompetitionField<TextBox>(window, "competitionAddress").Text = host.Endpoint.Address.ToString();
                    CompetitionField<TextBox>(window, "competitionPort").Text = host.Endpoint.Port.ToString();
                    InvokeWindow(window, "JoinCompetition");
                    LanSession guest = CompetitionField<LanSession>(window, "competitionSession");
                    var round = CompetitionField<TrainingRound>(window, "round");
                    BeginDesktopCompetition(window, host, guest);
                    var schedule = host.View.Schedule;
                    PumpCompetition(() => CapturingCompetition(window), "foreground player starts at scheduled time");
                    Require(CompetitionField<TextBlock>(window, "hudKey").Text == "Esc 放弃本轮" && !CursorIsFree(),
                        "active LAN round captures cursor and labels Escape");
                    Face(round.Camera, round.Targets[0].Center);
                    InvokeWindow(window, "ApplyRoundInput", 0, 0, true);
                    Require(round.Hits == 1, "competition shares the gameplay shot entry point");
                    PumpCompetition(() => round.State == RoundState.Finished, "30-second LAN round completes", 35000);
                    Require(now() + LanClock.TimingTolerance >= schedule.Deadline && CursorIsFree(),
                        "scheduled deadline releases native cursor");
                    PumpCompetition(() => now() >= schedule.Deadline, "wait for host deadline");
                    host.SubmitScore(schedule.RoundId, 0, 0);
                    PumpCompetition(() => guest.View.Snapshot.Phase == LanPhase.Results, "30-second round publishes rank");
                    Require(guest.View.Snapshot.Players[0].Hits == 1 && TrainingProfile.Load(profile).Best == 0,
                        "competition score ranks without updating single-player best");

                    host.ReturnToLobby();
                    PumpCompetition(() => guest.View.Snapshot.Phase == LanPhase.Lobby, "lobby before countdown cancellation");
                    BeginDesktopCompetition(window, host, guest);
                    var cancelReady = CompetitionField<Button>(window, "competitionReady");
                    PumpCompetition(() => cancelReady.Visibility == Visibility.Visible && cancelReady.IsEnabled,
                        "countdown exposes an explicit cancellation control");
                    InvokeWindow(window, "ToggleCompetitionReady");
                    PumpCompetition(() => host.View.Snapshot.Players.Any(player => player.Status == LanPlayerStatus.Withdrew),
                        "countdown cancellation reaches the host");
                    Require(CursorIsFree() && !CapturingCompetition(window), "countdown cancellation never captures the cursor");
                    Interlocked.Add(ref shift, 39);
                    PumpCompetition(() => guest.View.Snapshot.Phase == LanPhase.Results, "cancelled countdown reaches results");

                    foreach (LanPlayerStatus reason in new[] { LanPlayerStatus.Withdrew, LanPlayerStatus.Unfocused,
                        LanPlayerStatus.DisplayChanged, LanPlayerStatus.CaptureLost })
                    {
                        host.ReturnToLobby();
                        PumpCompetition(() => guest.View.Snapshot.Phase == LanPhase.Lobby, "return to same room");
                        BeginDesktopCompetition(window, host, guest);
                        Interlocked.Add(ref shift, 3);
                        PumpCompetition(() => CapturingCompetition(window), "next round captures input");
                        switch (reason)
                        {
                            case LanPlayerStatus.Withdrew:
                                EscapeTraining(window);
                                break;
                            case LanPlayerStatus.Unfocused:
                                if (!focusSink.IsVisible)
                                    focusSink.Show();
                                focusSink.Activate();
                                PumpCompetition(() => !window.IsActive, "foreground moved to another window");
                                window.Activate();
                                PumpCompetition(() => window.IsActive, "focus returned to training window");
                                break;
                            case LanPlayerStatus.DisplayChanged:
                                InvokeWindow(window, "ProcessWindowMessage", IntPtr.Zero, WindowsDesktop.DisplayChange,
                                    IntPtr.Zero, IntPtr.Zero, false);
                                break;
                            case LanPlayerStatus.CaptureLost:
                                WindowsDesktop.ReleaseCursor();
                                break;
                        }
                        PumpCompetition(() => !CapturingCompetition(window) &&
                            host.View.Snapshot.Players.Any(player => player.Status == reason), "input releases and reports interruption " + reason);
                        Require(CursorIsFree() && round.State == RoundState.Ready &&
                            !CompetitionField<RangeSurface>(window, "surface").Flash, "no capture or hit feedback after interruption");
                        Interlocked.Add(ref shift, 36);
                        PumpCompetition(() => guest.View.Snapshot.Phase == LanPhase.Results, "forfeit round resolves");
                    }

                    host.ReturnToLobby();
                    PumpCompetition(() => guest.View.Snapshot.Phase == LanPhase.Lobby, "ready room after last result");
                    BeginDesktopCompetition(window, host, guest);
                    Interlocked.Add(ref shift, 3);
                    PumpCompetition(() => CapturingCompetition(window), "input active before room shutdown");
                    host.Dispose();
                    PumpCompetition(() => CompetitionField<LanSession>(window, "competitionSession") == null,
                        "window detects host shutdown");
                    Require(CursorIsFree() && CompetitionField<TextBlock>(window, "competitionStatus").Text.Contains("比赛已取消"),
                        "room shutdown releases native cursor and cancels match");
                }
                InvokeWindow(window, "LeaveCompetition");
                var soloRound = CompetitionField<TrainingRound>(window, "round");
                InvokeWindow(window, "StartOrResume");
                Require(soloRound.State == RoundState.Active && !CursorIsFree(), "solo capture after LAN exit");
                EscapeTraining(window);
                Require(soloRound.State == RoundState.Paused && CursorIsFree(), "solo Escape still pauses");
                InvokeWindow(window, "StartOrResume");
                Require(soloRound.State == RoundState.Active, "solo resumes with explicit action");
                Interlocked.Add(ref shift, 30);
                PumpCompetition(() => soloRound.State == RoundState.Finished && CursorIsFree(), "solo settlement releases input");
                Console.WriteLine("Passed: foreground TCP competition, real 30-second settlement, Escape, focus loss, display invalidation, capture loss, host closure and solo recovery.");
            }
            finally
            {
                focusSink.Close();
                window.Close();
                File.Delete(profile);
            }
        }

        private static void BeginDesktopCompetition(TrainingWindow window, LanSession host, LanSession guest)
        {
            var ready = CompetitionField<Button>(window, "competitionReady");
            PumpCompetition(() => ready.IsEnabled && guest.View.CanReady, "desktop guest can prepare");
            InvokeWindow(window, "ToggleCompetitionReady");
            host.SetReady(true);
            PumpCompetition(() => host.View.Snapshot.CanStart, "both desktop participants are ready");
            host.StartRound();
            PumpCompetition(() => guest.View.Schedule != null, "desktop guest receives countdown");
            Require(CursorIsFree() && !CapturingCompetition(window), "countdown keeps native cursor free");
        }

        private static bool CapturingCompetition(TrainingWindow window)
        {
            return CompetitionField<LanSchedule>(window, "competitionRun") != null;
        }

        private static bool CursorIsFree()
        {
            WindowsDesktop.Rect clip;
            return WindowsDesktop.GetClipCursor(out clip) && clip.Equals(WindowsDesktop.VirtualScreen());
        }

        private static void EscapeTraining(TrainingWindow window)
        {
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            window.RaiseEvent(key);
        }
    }
}
