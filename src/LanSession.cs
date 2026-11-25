using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopIconFlick
{
    internal sealed class LanView
    {
        public readonly LanSnapshot Snapshot;
        public readonly int PlayerId;
        public readonly string Error;
        public readonly bool CanReady;
        public readonly LanSchedule Schedule;

        public LanView(LanSnapshot snapshot, int playerId, string error, bool canReady, LanSchedule schedule)
        {
            Snapshot = snapshot;
            PlayerId = playerId;
            Error = error;
            CanReady = canReady;
            Schedule = schedule;
        }
    }

    internal sealed class LanSession : IDisposable
    {
        private const int ConnectionTimeout = 5000;
        private const int PulseMilliseconds = 200;
        private readonly object gate = new object();
        private readonly Func<double> now;
        private readonly Dictionary<LanPeer, int> clients = new Dictionary<LanPeer, int>();
        private readonly LanRoom room;
        private readonly TcpListener listener;
        private readonly Timer timer;
        private readonly LanClock clock = new LanClock();
        private TcpClient connecting;
        private LanPeer server;
        private int playerId;
        private LanSnapshot snapshot;
        private string error;
        private bool closed;
        private double nextPing;
        private double? pendingPing;
        private LanSchedule schedule;
        private bool wantsReady;

        private LanSession(Func<double> now, LanRoom room, TcpListener listener)
        {
            this.now = now;
            this.room = room;
            this.listener = listener;
            if (room != null)
            {
                playerId = LanRoom.HostId;
                snapshot = room.Snapshot();
                Endpoint = (IPEndPoint)listener.LocalEndpoint;
            }
            timer = new Timer(Pulse, null, Timeout.Infinite, Timeout.Infinite);
        }

        public IPEndPoint Endpoint { get; private set; }
        public bool IsHost { get { return room != null; } }

        public LanView View
        {
            get
            {
                lock (gate)
                    return new LanView(snapshot, playerId, error,
                        !closed && (IsHost || clock.IsSynchronized(now())), schedule);
            }
        }

        public static LanSession Host(LanAddress address, int port, string nickname, Func<double> now)
        {
            var listener = new TcpListener(address.Address, port);
            listener.Start(LanRoom.Capacity);
            var session = new LanSession(now, new LanRoom(nickname), listener);
            session.timer.Change(PulseMilliseconds, PulseMilliseconds);
            Task.Run(() => session.Accept(address));
            return session;
        }

        public static LanSession Join(IPEndPoint endpoint, string nickname, Func<double> now)
        {
            var tcp = new TcpClient(AddressFamily.InterNetwork);
            var session = new LanSession(now, null, null) { connecting = tcp, Endpoint = endpoint };
            session.timer.Change(PulseMilliseconds, PulseMilliseconds);
            Task.Run(() => session.Connect(tcp, nickname));
            return session;
        }

        public void SetReady(bool ready, LanPlayerStatus reason = LanPlayerStatus.Withdrew)
        {
            lock (gate)
            {
                if (closed || snapshot == null || (ready &&
                    (snapshot.Phase != LanPhase.Lobby || (!IsHost && !clock.IsSynchronized(now())))))
                    return;
                if (IsHost)
                {
                    if (ApplyReady(playerId, ready, reason))
                        Publish();
                }
                else
                {
                    wantsReady = ready;
                    server.Send(new LanMessage { Kind = LanMessageKind.Ready, Ready = ready, Status = reason });
                }
            }
        }

        public void StartRound()
        {
            lock (gate)
                if (!closed && IsHost && room.Start(now()))
                    Publish();
        }

        public void Forfeit(int roundId, LanPlayerStatus reason)
        {
            lock (gate)
            {
                if (closed || snapshot == null || snapshot.RoundId != roundId)
                    return;
                if (IsHost)
                {
                    double time = now();
                    AdvanceRoom(time);
                    AdvanceRoom(time, room.Forfeit(playerId, roundId, reason));
                }
                else
                    server.Send(new LanMessage { Kind = LanMessageKind.Forfeit, RoundId = roundId, Status = reason });
            }
        }

        public void SubmitScore(int roundId, int hits, int shots)
        {
            lock (gate)
            {
                if (closed || snapshot == null || snapshot.RoundId != roundId)
                    return;
                if (IsHost)
                {
                    double time = now();
                    AdvanceRoom(time);
                    AdvanceRoom(time, room.Submit(playerId, roundId, hits, shots, time));
                }
                else
                    server.Send(new LanMessage { Kind = LanMessageKind.Score, RoundId = roundId, Hits = hits, Shots = shots });
            }
        }

        public void ReturnToLobby()
        {
            lock (gate)
                if (!closed && IsHost && room.ReturnToLobby())
                    Publish();
        }

        private void AdvanceRoom(double time, bool changed = false)
        {
            if (room.Advance(time) || changed)
                Publish();
        }

        private bool ApplyReady(int id, bool ready, LanPlayerStatus reason)
        {
            if (room.Phase == LanPhase.Lobby)
                return room.SetReady(id, ready);
            return !ready && room.Phase == LanPhase.Countdown && room.Forfeit(id, snapshot.RoundId, reason);
        }

        private async Task Accept(LanAddress address)
        {
            try
            {
                while (true)
                {
                    TcpClient tcp = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    lock (gate)
                    {
                        if (closed || clients.Count >= LanRoom.Capacity * 2 ||
                            !address.Contains(((IPEndPoint)tcp.Client.RemoteEndPoint).Address))
                        {
                            tcp.Close();
                            if (closed)
                                return;
                            continue;
                        }
                        try
                        {
                            var peer = new LanPeer(tcp);
                            clients.Add(peer, 0);
                            peer.Start(message => ReceiveClient(peer, message), () => DropClient(peer));
                        }
                        catch (SocketException) { tcp.Close(); }
                        catch (InvalidOperationException) { tcp.Close(); }
                    }
                }
            }
            catch (InvalidOperationException)
            {
                lock (gate)
                    if (!closed)
                        Fail("房间监听已中断。");
            }
            catch (SocketException)
            {
                lock (gate)
                    if (!closed)
                        Fail("房间监听已中断。");
            }
        }

        private async Task Connect(TcpClient tcp, string nickname)
        {
            try
            {
                await LanProtocol.Connect(tcp, Endpoint, ConnectionTimeout).ConfigureAwait(false);
                lock (gate)
                {
                    if (closed)
                        return;
                    server = new LanPeer(tcp);
                    connecting = null;
                    server.Start(ReceiveServer, DropServer);
                    server.Send(new LanMessage { Kind = LanMessageKind.Hello, Version = LanProtocol.Version, Name = nickname });
                }
            }
            catch (TimeoutException)
            {
                lock (gate)
                    if (!closed)
                        Fail("连接超时。请检查地址、端口和房主的防火墙。");
            }
            catch (Exception failure)
            {
                if (!(failure is SocketException || failure is IOException || failure is InvalidOperationException))
                    throw;
                lock (gate)
                    if (!closed)
                        Fail("无法连接房主。请检查地址、端口和局域网连接。");
            }
        }

        private void ReceiveClient(LanPeer peer, LanMessage message)
        {
            lock (gate)
            {
                int id;
                if (closed || !clients.TryGetValue(peer, out id))
                    return;
                double time = now();
                AdvanceRoom(time);
                if (id == 0)
                {
                    LanError rejection = LanProtocol.CheckHello(message);
                    if (rejection == LanError.None)
                        rejection = room.Join(message.Name, out id);
                    if (rejection != LanError.None)
                    {
                        clients[peer] = -1;
                        peer.Send(new LanMessage { Kind = LanMessageKind.Error, Error = rejection }, true);
                        return;
                    }
                    clients[peer] = id;
                    peer.Send(new LanMessage { Kind = LanMessageKind.Welcome, Version = LanProtocol.Version, PlayerId = id });
                    Publish();
                    return;
                }
                if (id < 0)
                    return;
                switch (message.Kind)
                {
                    case LanMessageKind.Ping:
                        if (LanClock.IsFinite(message.SentAt))
                            peer.Send(new LanMessage { Kind = LanMessageKind.Pong, SentAt = message.SentAt, HostTime = now() });
                        break;
                    case LanMessageKind.Ready:
                        if (ApplyReady(id, message.Ready, message.Status))
                            Publish();
                        break;
                    case LanMessageKind.Forfeit:
                        AdvanceRoom(time, room.Forfeit(id, message.RoundId, message.Status));
                        break;
                    case LanMessageKind.Score:
                        AdvanceRoom(time, room.Submit(id, message.RoundId, message.Hits, message.Shots, time));
                        break;
                    default:
                        peer.Dispose();
                        break;
                }
            }
        }

        private void ReceiveServer(LanMessage message)
        {
            lock (gate)
            {
                if (closed)
                    return;
                switch (message.Kind)
                {
                    case LanMessageKind.Error:
                        Fail(Describe(message.Error));
                        return;
                    case LanMessageKind.Welcome:
                        if (playerId == 0 && message.PlayerId > LanRoom.HostId && message.Version == LanProtocol.Version)
                        {
                            playerId = message.PlayerId;
                            return;
                        }
                        break;
                    case LanMessageKind.Snapshot:
                        if (playerId != 0 && message.Snapshot != null && message.Snapshot.IsValid() &&
                            message.Snapshot.Players.Any(player => player.Id == playerId))
                        {
                            ReceiveSnapshot(message.Snapshot);
                            return;
                        }
                        break;
                    case LanMessageKind.Pong:
                        if (playerId != 0)
                        {
                            if (message.SentAt == pendingPing)
                            {
                                clock.Observe(message.SentAt, message.HostTime, now());
                                pendingPing = null;
                            }
                            return;
                        }
                        break;
                }
                Fail("房主发送了无效的房间消息。");
            }
        }

        private void ReceiveSnapshot(LanSnapshot incoming)
        {
            if (incoming.RoundId == 0)
            {
                if (snapshot != null && snapshot.RoundId != 0)
                    wantsReady = false;
                schedule = null;
            }
            else if (snapshot == null || incoming.RoundId != snapshot.RoundId)
                schedule = clock.Schedule(incoming, now());
            snapshot = incoming;
        }

        private void Publish()
        {
            snapshot = room.Snapshot();
            schedule = snapshot.RoundId == 0 ? null : new LanSchedule(snapshot.RoundId, snapshot.Seed, snapshot.StartsAt);
            var message = new LanMessage { Kind = LanMessageKind.Snapshot, Snapshot = snapshot };
            foreach (var client in clients.ToArray())
                if (client.Value > 0)
                    client.Key.Send(message);
        }

        private void DropClient(LanPeer peer)
        {
            lock (gate)
            {
                int id;
                if (closed || !clients.TryGetValue(peer, out id))
                    return;
                clients.Remove(peer);
                double time = now();
                AdvanceRoom(time);
                if (id > 0)
                    AdvanceRoom(time, room.Disconnect(id));
            }
        }

        private void DropServer()
        {
            lock (gate)
                if (!closed)
                    Fail(snapshot == null ? "连接在加入房间前中断。" : "与房主的连接已中断。");
        }

        private void Pulse(object unused)
        {
            lock (gate)
            {
                if (closed)
                    return;
                double silence = ConnectionTimeout / 1000.0;
                if (IsHost)
                {
                    AdvanceRoom(now());
                    foreach (LanPeer peer in clients.Keys.ToArray())
                        if (peer.IsSilent(silence))
                            peer.Dispose();
                }
                else if (server != null)
                {
                    double time = now();
                    if (server.IsSilent(silence))
                        Fail(snapshot == null ? "等待房主应答超时。" : "与房主的连接已超时。");
                    else if (playerId != 0 && time >= nextPing)
                    {
                        nextPing = time + 0.6;
                        pendingPing = time;
                        server.Send(new LanMessage { Kind = LanMessageKind.Ping, SentAt = time });
                        if (wantsReady && snapshot != null && snapshot.Phase == LanPhase.Lobby && !clock.IsSynchronized(time))
                            SetReady(false, LanPlayerStatus.MissedStart);
                    }
                }
            }
        }

        private static string Describe(LanError reason)
        {
            switch (reason)
            {
                case LanError.Version: return "两端的局域网协议版本不兼容，请使用相同版本。";
                case LanError.Name: return "昵称无效。";
                case LanError.Full: return "房间已满。";
                case LanError.RoundInProgress: return "本轮进行中，请等待房主返回大厅。";
                default: return "房主拒绝了这次连接。";
            }
        }

        private void Fail(string reason)
        {
            error = reason;
            Dispose();
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (closed)
                    return;
                closed = true;
                timer.Dispose();
                if (listener != null)
                    listener.Stop();
                if (connecting != null)
                    connecting.Close();
                if (server != null)
                    server.Dispose();
                foreach (LanPeer peer in clients.Keys)
                    peer.Dispose();
                clients.Clear();
            }
        }
    }
}
