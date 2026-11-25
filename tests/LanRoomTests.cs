using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;

namespace DesktopIconFlick
{
    internal static partial class Tests
    {
        private static readonly LanAddress LoopbackLan = new LanAddress(IPAddress.Loopback, IPAddress.Parse("255.0.0.0"));

        private static void LanRoomTracksPlayersAndReadiness()
        {
            var clock = Stopwatch.StartNew();
            Func<double> now = () => clock.Elapsed.TotalSeconds;
            using (LanSession host = LanSession.Host(LoopbackLan, 0, "同名", now))
            {
                host.SetReady(true);
                Require(!host.View.Snapshot.CanStart, "one ready player cannot start");
                var clients = new List<LanSession>();
                try
                {
                    for (int player = 2; player <= LanRoom.Capacity; player++)
                    {
                        LanSession guest = LanSession.Join(host.Endpoint, "同名", now);
                        clients.Add(guest);
                        WaitForLan(() => guest.View.Snapshot != null, "joining LAN room");
                        Require(guest.View.Snapshot.Players.Length == player, "each new client sees current roster");
                    }
                    Require(host.View.Snapshot.Players.Select(player => player.Id).Distinct().Count() == 8,
                        "same nickname players have distinct identities");
                    Require(!host.View.Snapshot.CanStart, "mixed readiness prevents start");
                    using (LanSession extra = LanSession.Join(host.Endpoint, "候补", now))
                    {
                        WaitForLan(() => extra.View.Error != null, "room full rejection");
                        Require(extra.View.Error.Contains("已满"), "ninth player receives room full message");
                    }
                    foreach (LanSession guest in clients)
                    {
                        WaitForLan(() => guest.View.CanReady, "clock synchronized before readiness");
                        guest.SetReady(true);
                    }
                    WaitForLan(() => host.View.Snapshot.CanStart && clients.All(guest => guest.View.Snapshot.CanStart),
                        "all clients see the same ready roster");
                    clients[0].SetReady(false);
                    WaitForLan(() => !host.View.Snapshot.CanStart, "unready blocks start");
                    clients[0].Dispose();
                    WaitForLan(() => host.View.Snapshot.Players.Length == 7 && clients[1].View.Snapshot.Players.Length == 7,
                        "leaving updates host and clients");
                }
                finally
                {
                    foreach (LanSession guest in clients)
                        guest.Dispose();
                }
            }
        }

        private static void LanStartsAtMappedTimeAndLocksRoster()
        {
            var room = new LanRoom("甲", new Random(71));
            int id, newcomer;
            Require(room.Join("乙", out id) == LanError.None, "admit before countdown");
            room.SetReady(LanRoom.HostId, true);
            Require(!room.Start(100), "unready participant prevents countdown");
            room.SetReady(id, true);
            Require(room.Start(100), "host locks an all-ready roster");
            LanSnapshot opening = room.Snapshot();
            Require(opening.Phase == LanPhase.Countdown && opening.StartsAt == 103 && opening.RoundId > 0,
                "three-second countdown announced");
            Require(room.Join("丙", out newcomer) == LanError.RoundInProgress && newcomer == 0, "reject in-progress join");

            var clock = new LanClock();
            Require(clock.Schedule(opening, 6) == null, "cannot map start without a sample");
            clock.Observe(5, 100, 6);
            Require(!clock.IsSynchronized(6), "reject slow round-trip sample");
            clock.Observe(6, 101.02, 6.04);
            clock.Observe(6.1, 101.17, 6.19);
            LanSchedule schedule = clock.Schedule(opening, 6.2);
            Near(schedule.StartsAt, 8, 0.000001, "client clock offset maps common start");
            Near(schedule.Deadline, 38, 0.000001, "client clock offset maps common deadline");
            Require(!schedule.IsLate(8.1) && schedule.IsLate(8.16) && clock.Schedule(opening, 10) == null,
                "late instruction or stale sample cannot start a competition");
            Require(room.Forfeit(id, opening.RoundId, LanPlayerStatus.Unfocused) &&
                room.Snapshot().Players.Single(player => player.Id == id).Status == LanPlayerStatus.Unfocused,
                "loss of readiness after roster lock becomes a forfeit");
            Require(!room.SetReady(id, true) && !room.Advance(102.999) && room.Advance(103), "no mid-countdown rejoin");
            Require(!room.Advance(132.999) && room.Advance(133) && room.Phase == LanPhase.Collecting, "exact collection boundary");

            var elapsed = Stopwatch.StartNew();
            using (LanSession host = LanSession.Host(LoopbackLan, 0, "甲", () => 100 + elapsed.Elapsed.TotalSeconds))
            using (LanSession guest = LanSession.Join(host.Endpoint, "乙", () => 5 + elapsed.Elapsed.TotalSeconds))
            {
                WaitForLan(() => guest.View.CanReady && guest.View.Snapshot != null, "join receives clock samples");
                host.SetReady(true);
                guest.SetReady(true);
                WaitForLan(() => host.View.Snapshot.CanStart, "synchronized room can start");
                host.StartRound();
                WaitForLan(() => guest.View.Schedule != null, "guest receives mapped start");
                Near(host.View.Schedule.StartsAt - guest.View.Schedule.StartsAt, 95, LanClock.TimingTolerance,
                    "connected clients share one monotonic start");
                guest.SetReady(false, LanPlayerStatus.Unfocused);
                WaitForLan(() => host.View.Snapshot.Players.Any(player => player.Status == LanPlayerStatus.Unfocused),
                    "cancelling readiness during countdown forfeits on host");
                using (LanSession late = LanSession.Join(host.Endpoint, "候补", () => elapsed.Elapsed.TotalSeconds))
                {
                    WaitForLan(() => late.View.Error != null, "new client rejected after countdown begins");
                    Require(late.View.Error.Contains("进行中"), "join rejection distinguishes in-progress room");
                }
            }
        }

        private static void WaitForLan(Func<bool> condition, string message)
        {
            var wait = Stopwatch.StartNew();
            while (!condition() && wait.ElapsedMilliseconds < 3500)
                Thread.Sleep(10);
            Require(condition(), message);
        }

        private static void LanCollectsAndPublishesFinalRankings()
        {
            var room = new LanRoom("房主", new Random(37));
            var ids = new List<int> { LanRoom.HostId };
            for (int i = 0; i < 6; i++)
            {
                int id;
                Require(room.Join("玩家", out id) == LanError.None, "player joins before ranking");
                ids.Add(id);
            }
            foreach (int id in ids)
                room.SetReady(id, true);
            Require(room.Start(0) && room.Advance(3), "ranking room starts");
            LanSnapshot playing = room.Snapshot();
            int round = playing.RoundId;
            Require(!room.Submit(ids[0], round, 12, 12, 32) && !room.Submit(ids[0], round - 1, 12, 12, 33) &&
                !room.Submit(ids[0], round, -1, 12, 33) && !room.Submit(ids[0], round, 13, 12, 33), "invalid score is rejected");
            Require(room.Submit(ids[0], round, 12, 15, 32.99) && room.Submit(ids[1], round, 12, 20, 33) &&
                room.Submit(ids[2], round, 8, 8, 33) && room.Submit(ids[3], round, 0, 0, 33), "valid scores are collected");
            Require(!room.Submit(ids[0], round, 13, 13, 33) && room.Disconnect(ids[1]) && room.Disconnect(ids[4]) &&
                !room.Submit(ids[4], round, 1, 2, 33), "duplicate and post-disconnect scores are ignored");
            Require(room.Forfeit(ids[5], round, LanPlayerStatus.Withdrew), "one player forfeits");
            Require(room.Advance(33) && room.Snapshot().Players.All(player => player.Shots == 0),
                "unsettled scores stay hidden");
            Require(!room.Advance(37.999) && !room.Submit(ids[6], round, 1, 2, 38) && room.Advance(38),
                "late submission cannot change the bounded ranking");
            LanSnapshot result = room.Snapshot();
            Require(result.Players.Select(player => player.Rank).SequenceEqual(new[] { 1, 1, 3, 4, 0, 0, 0 }),
                "ranking uses competition ranks and excludes unfinished players");
            Require(result.Players[0].Accuracy == 80 && result.Players[1].Status == LanPlayerStatus.Finished &&
                result.Players[3].Accuracy == 0 && result.Players[4].Status == LanPlayerStatus.Disconnected &&
                result.Players[6].Status == LanPlayerStatus.MissingScore, "accuracy and unfinished reasons");
            Require(room.ReturnToLobby() && room.Snapshot().Players.All(player => !player.Ready && player.Hits == 0)
                && room.Snapshot().Players.Length == 5, "rematch resets scores and drops disconnected players");
            foreach (LanPlayer player in room.Snapshot().Players)
                room.SetReady(player.Id, true);
            Require(room.Start(50) && room.Snapshot().RoundId != round && room.Snapshot().Seed != playing.Seed &&
                !room.Submit(ids[0], round, 1, 1, 83), "rematch has independent identity and seed");
            foreach (LanPlayer player in room.Snapshot().Players)
                room.Forfeit(player.Id, room.Snapshot().RoundId, LanPlayerStatus.Withdrew);
            Require(room.Advance(83) && room.Snapshot().Players.All(player => player.Rank == 0),
                "all-forfeit round has no winner");

            var stopwatch = Stopwatch.StartNew();
            int shift = 0;
            Func<double> now = () => stopwatch.Elapsed.TotalSeconds + Volatile.Read(ref shift);
            using (LanSession host = LanSession.Host(LoopbackLan, 0, "甲", now))
            using (LanSession guest = LanSession.Join(host.Endpoint, "乙", now))
            {
                WaitForLan(() => guest.View.CanReady && guest.View.Snapshot != null, "ranking guest joins");
                host.SetReady(true);
                guest.SetReady(true);
                WaitForLan(() => host.View.Snapshot.CanStart, "ranking contestants ready");
                host.StartRound();
                WaitForLan(() => guest.View.Schedule != null, "ranking guest knows its deadline");
                Volatile.Write(ref shift, 34);
                host.SubmitScore(1, 4, 8);
                guest.SubmitScore(1, 5, 9);
                WaitForLan(() => guest.View.Snapshot.Phase == LanPhase.Results, "ranking reaches the guest");
                Require(host.View.Snapshot.Players.Select(player => player.Id).SequenceEqual(
                    guest.View.Snapshot.Players.Select(player => player.Id)) &&
                    guest.View.Snapshot.Players[0].Hits == 5 && guest.View.Snapshot.Players[0].Rank == 1,
                    "rank and score match over the wire");
                host.ReturnToLobby();
                WaitForLan(() => guest.View.Snapshot.Phase == LanPhase.Lobby, "rematch returns both to the room");
                host.Dispose();
                WaitForLan(() => guest.View.Error != null, "guest detects host closure");
            }
        }
    }
}
