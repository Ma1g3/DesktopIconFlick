using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopIconFlick
{
    internal sealed partial class TrainingWindow
    {
        private readonly StackPanel soloContent;
        private StackPanel competitionContent, competitionForm, competitionRows;
        private TextBlock competitionHeading, competitionStatus, competitionEndpoint;
        private TextBox competitionNickname, competitionPort, competitionAddress;
        private ComboBox competitionAdapter;
        private Button competitionHost, competitionReady, competitionStart, competitionBack;
        private DispatcherTimer competitionTimer;
        private LanSession competitionSession;
        private LanSnapshot displayedCompetition;
        private LanSchedule competitionRun;
        private int handledCompetitionRound;
        private double competitionStartedAt;
        private bool competitionOpen, competitionPrepared;
        private string competitionMessage = "";

        private StackPanel BuildCompetitionView()
        {
            competitionHeading = Text("局域网竞赛", 36, Brushes.White);
            competitionHeading.FontWeight = FontWeights.SemiBold;
            competitionStatus = Text("", 14, Muted);
            competitionStatus.Margin = new Thickness(0, 8, 0, 12);
            competitionEndpoint = Text("", 15, Accent);
            competitionEndpoint.Margin = new Thickness(0, 0, 0, 10);
            competitionRows = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            competitionNickname = Field("昵称", "");
            competitionNickname.MaxLength = LanProtocol.NicknameLimit;
            competitionPort = Field("端口", LanProtocol.DefaultPort.ToString(CultureInfo.InvariantCulture));
            competitionPort.MaxLength = 5;
            competitionAddress = Field("房主 IPv4 地址", "");
            competitionAddress.MaxLength = 15;
            competitionAdapter = new ComboBox { FontSize = 14, Padding = new Thickness(6) };
            AutomationProperties.SetName(competitionAdapter, "建房网卡");
            competitionHost = ControlButton("创建房间", Accent, Ink, HostCompetition);
            competitionHost.Margin = new Thickness(0, 7, 0, 16);
            competitionHost.HorizontalAlignment = HorizontalAlignment.Left;
            var join = ControlButton("加入房间", Accent, Ink, JoinCompetition);
            join.Margin = new Thickness(0, 7, 0, 16);
            join.HorizontalAlignment = HorizontalAlignment.Left;
            var adapterLabel = Text("建房网卡", 12, Muted);
            adapterLabel.Margin = new Thickness(0, 12, 0, 5);

            competitionForm = new StackPanel();
            competitionForm.Children.Add(AimFields(competitionNickname, competitionPort));
            competitionForm.Children.Add(adapterLabel);
            competitionForm.Children.Add(competitionAdapter);
            competitionForm.Children.Add(competitionHost);
            competitionForm.Children.Add(LabelField(competitionAddress, 0));
            competitionForm.Children.Add(join);

            competitionReady = ControlButton("准备", Accent, Ink, ToggleCompetitionReady);
            competitionStart = ControlButton("开始竞赛", Accent, Ink, AdvanceCompetitionRoom);
            competitionBack = ControlButton("返回训练", Brushes.Transparent, Brushes.White, LeaveCompetition);
            var actions = new WrapPanel();
            actions.Children.Add(competitionReady);
            actions.Children.Add(competitionStart);
            actions.Children.Add(competitionBack);
            competitionContent = new StackPanel { Visibility = Visibility.Collapsed };
            competitionContent.Children.Add(competitionHeading);
            competitionContent.Children.Add(competitionStatus);
            competitionContent.Children.Add(competitionEndpoint);
            competitionContent.Children.Add(competitionForm);
            competitionContent.Children.Add(competitionRows);
            competitionContent.Children.Add(actions);
            competitionTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(20) };
            competitionTimer.Tick += RefreshCompetition;
            return competitionContent;
        }

        private void OpenCompetition()
        {
            if (round.State == RoundState.Paused || round.State == RoundState.Active)
                return;
            if (!profile.SetLocalAim(localDpiInput.Text, fovInput.Text))
            {
                message = "请检查输入范围。上一组有效设置未更改。";
                RefreshNotice();
                return;
            }
            profile.UseIcons = iconInput.IsChecked == true;
            profile.Save();
            message = "";
            round.Cancel();
            competitionOpen = true;
            competitionMessage = "";
            try
            {
                competitionAdapter.ItemsSource = LanAddress.Available();
                competitionAdapter.SelectedIndex = competitionAdapter.Items.Count > 0 ? 0 : -1;
                competitionHost.IsEnabled = competitionAdapter.Items.Count > 0;
                if (!competitionHost.IsEnabled)
                    competitionMessage = "当前没有可用于建房的局域网 IPv4 地址。";
            }
            catch (NetworkInformationException)
            {
                competitionHost.IsEnabled = false;
                competitionMessage = "无法读取本机网卡地址。";
            }
            ShowState();
        }

        private bool TryCompetitionIdentity(out string nickname, out int port)
        {
            nickname = competitionNickname.Text.Trim();
            port = 0;
            if (!LanProtocol.IsNickname(nickname))
                competitionMessage = "请输入 1 至 20 个字符的昵称。";
            else if (!int.TryParse(competitionPort.Text, NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
                port < 1 || port > 65535)
                competitionMessage = "请输入 1 至 65535 之间的端口。";
            else
                return true;
            RefreshCompetition(null, EventArgs.Empty);
            return false;
        }

        private void HostCompetition()
        {
            string nickname;
            int port;
            var address = competitionAdapter.SelectedItem as LanAddress;
            if (competitionSession != null || address == null || !TryCompetitionIdentity(out nickname, out port))
                return;
            try
            {
                SetCompetitionSession(LanSession.Host(address, port, nickname, now));
            }
            catch (SocketException)
            {
                competitionMessage = "无法在此地址和端口建房。请检查网卡或更换端口。";
                RefreshCompetition(null, EventArgs.Empty);
            }
        }

        private void JoinCompetition()
        {
            string nickname;
            int port;
            if (competitionSession != null || !TryCompetitionIdentity(out nickname, out port))
                return;
            IPAddress address;
            string input = competitionAddress.Text.Trim();
            if (!IPAddress.TryParse(input, out address) || address.AddressFamily != AddressFamily.InterNetwork ||
                address.ToString() != input || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast))
            {
                competitionMessage = "请输入房主显示的 IPv4 地址。";
                RefreshCompetition(null, EventArgs.Empty);
                return;
            }
            SetCompetitionSession(LanSession.Join(new IPEndPoint(address, port), nickname, now));
        }

        private void SetCompetitionSession(LanSession session)
        {
            competitionSession = session;
            competitionMessage = "";
            competitionTimer.Start();
            RefreshCompetition(null, EventArgs.Empty);
        }

        private void DisposeCompetitionSession()
        {
            competitionTimer.Stop();
            if (competitionRun != null)
                ClearCompetitionRound(true);
            if (competitionSession != null)
                competitionSession.Dispose();
            competitionSession = null;
            competitionPrepared = false;
            handledCompetitionRound = 0;
            displayedCompetition = null;
            competitionRows.Children.Clear();
        }

        private void LeaveCompetition()
        {
            if (competitionSession != null)
            {
                DisposeCompetitionSession();
                competitionMessage = "";
            }
            else
                competitionOpen = false;
            ShowState();
        }

        private void ToggleCompetitionReady()
        {
            if (competitionSession == null)
                return;
            LanView view = competitionSession.View;
            if (view.Snapshot == null)
                return;
            if (view.Snapshot.Phase != LanPhase.Lobby)
            {
                if (competitionPrepared)
                    InterruptCompetition(LanPlayerStatus.Withdrew);
                return;
            }
            if (!competitionPrepared && (!view.CanReady || !IsForegroundTraining() || displayQueued || CompetitionIconsLoading()))
                return;
            competitionPrepared = !competitionPrepared;
            competitionSession.SetReady(competitionPrepared);
            RefreshCompetition(null, EventArgs.Empty);
        }

        private void AdvanceCompetitionRoom()
        {
            if (competitionSession == null)
                return;
            LanSnapshot snapshot = competitionSession.View.Snapshot;
            if (snapshot == null)
                return;
            if (snapshot.Phase == LanPhase.Results)
                competitionSession.ReturnToLobby();
            else
                competitionSession.StartRound();
            RefreshCompetition(null, EventArgs.Empty);
        }

        private void RefreshCompetition(object sender, EventArgs args)
        {
            LanView view = competitionSession == null ? null : competitionSession.View;
            if (view != null && view.Error != null)
            {
                LanSnapshot last = view.Snapshot;
                competitionMessage = view.Error;
                if (last != null && last.Phase != LanPhase.Lobby && last.Phase != LanPhase.Results)
                    competitionMessage += " 本轮比赛已取消。";
                DisposeCompetitionSession();
                view = null;
            }
            LanSnapshot snapshot = view == null ? null : view.Snapshot;
            bool host = competitionSession != null && competitionSession.IsHost;
            bool lobby = snapshot != null && snapshot.Phase == LanPhase.Lobby;
            bool results = snapshot != null && snapshot.Phase == LanPhase.Results;
            bool pending = competitionSession != null && snapshot == null;
            if (lobby && displayedCompetition != null && displayedCompetition.Phase != LanPhase.Lobby)
            {
                competitionPrepared = false;
                competitionMessage = "";
            }
            if (snapshot != null)
                AdvanceCompetitionPlay(view, now());
            shownState = round.State;
            if (competitionRun != null)
            {
                displayedCompetition = snapshot;
                panel.Visibility = Visibility.Collapsed;
                hud.Visibility = Visibility.Visible;
                hudKey.Text = "Esc 放弃本轮";
                UpdateHud();
                return;
            }
            panel.Visibility = Visibility.Visible;
            hud.Visibility = Visibility.Collapsed;
            competitionRows.Visibility = lobby || results ? Visibility.Visible : Visibility.Collapsed;
            competitionForm.Visibility = competitionSession == null ? Visibility.Visible : Visibility.Collapsed;
            bool canCancelCountdown = snapshot != null && snapshot.Phase == LanPhase.Countdown && competitionPrepared;
            competitionReady.Visibility = lobby || canCancelCountdown ? Visibility.Visible : Visibility.Collapsed;
            competitionReady.Content = competitionPrepared ? "取消准备" : "准备";
            bool loadingIcons = CompetitionIconsLoading();
            competitionReady.IsEnabled = canCancelCountdown || (lobby &&
                (competitionPrepared || (view.CanReady && !loadingIcons && !displayQueued)));
            competitionStart.Visibility = host && (lobby || results) ? Visibility.Visible : Visibility.Collapsed;
            competitionBack.Content = host ? "关闭房间" : pending ? "取消连接" : snapshot != null ? "离开房间" : "返回训练";
            competitionHeading.Text = results ? "本轮排名" : lobby ? "局域网房间" : "局域网竞赛";
            competitionEndpoint.Text = host ? "房主地址：" + competitionSession.Endpoint : "";
            competitionEndpoint.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
            if (snapshot != displayedCompetition)
            {
                DrawCompetitionRows(snapshot, view == null ? 0 : view.PlayerId);
                displayedCompetition = snapshot;
            }

            double time = now();
            if (lobby)
            {
                competitionStart.Content = "开始竞赛";
                competitionStart.IsEnabled = snapshot.CanStart;
                competitionStatus.Text = loadingIcons ? "正在读取桌面图标…" : !view.CanReady ? "正在同步房主时间…" : snapshot.CanStart
                    ? host ? "全员已准备。" : "等待房主开始。" : "至少两人，全部准备后由房主开始。";
            }
            else if (results)
            {
                competitionStart.Content = "返回房间";
                competitionStart.IsEnabled = true;
                competitionStatus.Text = snapshot.Players.Any(player => player.Rank == 1) ? "" : "本轮无人完赛。";
            }
            else if (view != null && snapshot != null)
                competitionStatus.Text = competitionMessage.Length > 0 ? competitionMessage :
                    view.Schedule != null && time < view.Schedule.StartsAt
                        ? string.Format("{0:0} 秒后开始", Math.Ceiling(view.Schedule.StartsAt - time)) : "等待本轮结算…";
            else
                competitionStatus.Text = pending ? "正在连接房主…" : competitionMessage;
            competitionStatus.Visibility = competitionStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool CompetitionIconsLoading()
        {
            return profile.UseIcons && !iconsDone;
        }

        private void AdvanceCompetitionPlay(LanView view, double time)
        {
            LanSnapshot snapshot = view.Snapshot;
            if (competitionRun != null)
            {
                round.Tick();
                if (round.State == RoundState.Finished)
                    CompleteCompetitionRound();
                else if (snapshot.RoundId != competitionRun.RoundId || snapshot.Phase == LanPhase.Results)
                    ClearCompetitionRound(true);
                else if (!IsForegroundTraining())
                    StopCompetitionPlay(LanPlayerStatus.Unfocused);
                else if (!mouse.HasConfinement())
                    StopCompetitionPlay(LanPlayerStatus.CaptureLost);
                return;
            }
            if (snapshot.Phase == LanPhase.Lobby)
            {
                if (competitionPrepared && !view.CanReady)
                {
                    competitionPrepared = false;
                    competitionSession.SetReady(false, LanPlayerStatus.MissedStart);
                }
                return;
            }
            if (snapshot.Phase == LanPhase.Results || snapshot.RoundId == handledCompetitionRound)
                return;
            LanPlayer local = snapshot.Players.First(player => player.Id == view.PlayerId);
            LanSchedule schedule = view.Schedule;
            if (local.Status != LanPlayerStatus.Pending)
            {
                competitionPrepared = false;
                handledCompetitionRound = snapshot.RoundId;
                competitionMessage = CompetitionStatus(local.Status) + "，等待本轮结算。";
            }
            else if (!competitionPrepared || !local.Ready || schedule == null || schedule.IsLate(time))
                WithdrawCompetitionRound(snapshot.RoundId, LanPlayerStatus.MissedStart);
            else if (time >= schedule.StartsAt)
                StartCompetitionPlay(schedule);
        }

        private void StartCompetitionPlay(LanSchedule schedule)
        {
            if (displayQueued || CompetitionIconsLoading())
            {
                WithdrawCompetitionRound(schedule.RoundId, LanPlayerStatus.DisplayChanged);
                return;
            }
            if (!IsForegroundTraining())
            {
                WithdrawCompetitionRound(schedule.RoundId, LanPlayerStatus.Unfocused);
                return;
            }
            if (!AcquireInput())
            {
                WithdrawCompetitionRound(schedule.RoundId, LanPlayerStatus.CaptureLost);
                return;
            }
            frameSampleStart = now();
            if (schedule.IsLate(frameSampleStart))
            {
                ReleaseInput();
                WithdrawCompetitionRound(schedule.RoundId, LanPlayerStatus.MissedStart);
                return;
            }
            competitionPrepared = false;
            handledCompetitionRound = schedule.RoundId;
            competitionRun = schedule;
            competitionStartedAt = frameSampleStart;
            competitionMessage = "";
            round.StartCompetition(profile.Aim, schedule.Seed, schedule.StartsAt);
            PrepareRoundPresentation();
            shownSecond = shownHits = shownShots = -1;
            Focus();
            UpdateDesktopHits();
            surface.InvalidateVisual();
        }

        private void InterruptCompetition(LanPlayerStatus reason)
        {
            if (competitionSession == null)
                return;
            if (competitionRun != null)
            {
                round.Tick();
                if (round.State == RoundState.Finished)
                    CompleteCompetitionRound();
                else
                    StopCompetitionPlay(reason);
            }
            else if (competitionPrepared)
            {
                competitionPrepared = false;
                LanSnapshot snapshot = competitionSession.View.Snapshot;
                if (snapshot != null && snapshot.RoundId != 0 && snapshot.Phase != LanPhase.Results)
                    WithdrawCompetitionRound(snapshot.RoundId, reason);
                else
                    competitionSession.SetReady(false, reason);
            }
            RefreshCompetition(null, EventArgs.Empty);
        }

        private void StopCompetitionPlay(LanPlayerStatus reason)
        {
            int roundId = competitionRun.RoundId;
            ClearCompetitionRound(true);
            WithdrawCompetitionRound(roundId, reason);
        }

        private void WithdrawCompetitionRound(int roundId, LanPlayerStatus reason)
        {
            competitionPrepared = false;
            handledCompetitionRound = roundId;
            competitionMessage = CompetitionStatus(reason) + "，等待本轮结算。";
            competitionSession.Forfeit(roundId, reason);
        }

        private void CompleteCompetitionRound()
        {
            LanSchedule played = competitionRun;
            ClearCompetitionRound(false);
            competitionMessage = "已完赛，等待本轮结算…";
            competitionSession.SubmitScore(played.RoundId, round.Hits, round.Shots);
            if (report != null)
                report(string.Format(CultureInfo.InvariantCulture,
                    "{0:O} mode=lan round={1} seed={2} appearance={3} work_area={4}x{5} hits={6} shots={7} start_lateness_ms={8:0.0} clock_uncertainty_ms={9:0.0}",
                    DateTimeOffset.Now, played.RoundId, played.Seed, roundUsesIcons ? "icons" : "balls",
                    workArea.Width, workArea.Height, round.Hits, round.Shots, (competitionStartedAt - played.StartsAt) * 1000,
                    played.ClockUncertainty * 1000));
        }

        private void ClearCompetitionRound(bool cancel)
        {
            ReleaseInput();
            if (cancel)
                round.Cancel();
            competitionRun = null;
            targetIcons.Clear();
            UpdateDesktopHits();
            surface.InvalidateVisual();
        }

        private void DrawCompetitionRows(LanSnapshot snapshot, int localPlayer)
        {
            competitionRows.Children.Clear();
            if (snapshot == null)
                return;
            if (snapshot.Phase == LanPhase.Lobby)
            {
                foreach (LanPlayer player in snapshot.Players)
                    AddCompetitionRow(new[] { 3.0, 1.2 }, PlayerLabel(player, localPlayer, snapshot.Players), player.Ready ? "已准备" : "未准备");
            }
            else if (snapshot.Phase == LanPhase.Results)
            {
                var widths = new[] { 0.9, 3.2, 1, 1, 1.5 };
                AddCompetitionRow(widths, "名次", "玩家", "命中", "射击", "命中率");
                foreach (LanPlayer player in snapshot.Players)
                {
                    string label = PlayerLabel(player, localPlayer, snapshot.Players);
                    if (player.Rank == 0)
                        AddCompetitionRow(widths, "—", label + "\n" + CompetitionStatus(player.Status), "", "", "");
                    else
                        AddCompetitionRow(widths, player.Rank.ToString(), label, player.Hits.ToString(),
                            player.Shots.ToString(), string.Format("{0:0.#}%", player.Accuracy));
                }
            }
        }

        private void AddCompetitionRow(double[] widths, params string[] values)
        {
            var row = new Grid();
            for (int i = 0; i < values.Length; i++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[i], GridUnitType.Star) });
                var text = Text(values[i], 13, Brushes.White);
                text.Margin = new Thickness(0, 4, 4, 4);
                Grid.SetColumn(text, i);
                row.Children.Add(text);
            }
            competitionRows.Children.Add(row);
        }

        private static string PlayerLabel(LanPlayer player, int localPlayer, LanPlayer[] roster)
        {
            string name = roster.Count(entry => entry.Nickname == player.Nickname) > 1
                ? string.Format("{0}（玩家 {1}）", player.Nickname, player.Id) : player.Nickname;
            return string.Format("{0}{1}{2}", name,
                player.Id == LanRoom.HostId ? "（房主）" : "", player.Id == localPlayer ? "（你）" : "");
        }

        private static string CompetitionStatus(LanPlayerStatus status)
        {
            switch (status)
            {
                case LanPlayerStatus.Withdrew: return "已放弃";
                case LanPlayerStatus.Unfocused: return "切出窗口";
                case LanPlayerStatus.MissedStart: return "错过开局";
                case LanPlayerStatus.CaptureLost: return "鼠标控制已释放";
                case LanPlayerStatus.DisplayChanged: return "显示设置已变化";
                case LanPlayerStatus.Disconnected: return "连接中断";
                case LanPlayerStatus.MissingScore: return "未收到成绩";
                default: return "未完赛";
            }
        }
    }
}
