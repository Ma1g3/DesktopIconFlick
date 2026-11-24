using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace DesktopIconFlick
{
    internal enum LanPhase { Lobby, Countdown, Playing, Collecting, Results }
    internal enum LanPlayerStatus { Pending, Finished, Withdrew, Unfocused, MissedStart, CaptureLost, DisplayChanged, Disconnected, MissingScore }

    [DataContract]
    internal sealed class LanPlayer
    {
        [DataMember] public int Id { get; set; }
        [DataMember] public string Nickname { get; set; }
        [DataMember] public bool Ready { get; set; }
        [DataMember] public bool Connected { get; set; }
        [DataMember] public LanPlayerStatus Status { get; set; }
        [DataMember] public int Rank { get; set; }
        [DataMember] public int Hits { get; set; }
        [DataMember] public int Shots { get; set; }

        public double Accuracy { get { return Shots == 0 ? 0 : 100.0 * Hits / Shots; } }

        public LanPlayer Copy()
        {
            return (LanPlayer)MemberwiseClone();
        }
    }

    [DataContract]
    internal sealed class LanSnapshot
    {
        [DataMember] public LanPhase Phase { get; set; }
        [DataMember] public LanPlayer[] Players { get; set; }
        [DataMember] public int RoundId { get; set; }
        [DataMember] public int Seed { get; set; }
        [DataMember] public double StartsAt { get; set; }

        public bool CanStart
        {
            get { return Phase == LanPhase.Lobby && Players.Length >= 2 && Players.All(player => player.Ready); }
        }

        public bool IsValid()
        {
            return Enum.IsDefined(typeof(LanPhase), Phase) && Players != null &&
                (Phase == LanPhase.Lobby ? RoundId == 0 : RoundId > 0 && StartsAt >= 0 && LanClock.IsFinite(StartsAt)) &&
                Players.Length >= 1 && Players.Length <= LanRoom.Capacity &&
                Players.All(player => player != null && player.Id > 0 && LanProtocol.IsNickname(player.Nickname) &&
                    Enum.IsDefined(typeof(LanPlayerStatus), player.Status) && player.Hits >= 0 && player.Shots >= player.Hits &&
                    player.Rank >= 0 && player.Rank <= LanRoom.Capacity) &&
                Players.Select(player => player.Id).Distinct().Count() == Players.Length &&
                Players.Any(player => player.Id == LanRoom.HostId);
        }
    }

    internal sealed partial class LanMessage
    {
        [DataMember(EmitDefaultValue = false)] public int PlayerId { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool Ready { get; set; }
        [DataMember(EmitDefaultValue = false)] public LanSnapshot Snapshot { get; set; }
        [DataMember(EmitDefaultValue = false)] public double SentAt { get; set; }
        [DataMember(EmitDefaultValue = false)] public double HostTime { get; set; }
        [DataMember(EmitDefaultValue = false)] public int RoundId { get; set; }
        [DataMember(EmitDefaultValue = false)] public LanPlayerStatus Status { get; set; }
        [DataMember(EmitDefaultValue = false)] public int Hits { get; set; }
        [DataMember(EmitDefaultValue = false)] public int Shots { get; set; }
    }

    internal sealed class LanRoom
    {
        public const int HostId = 1;
        public const int Capacity = 8;
        private const double CountdownSeconds = 3;
        private const double ScoreWindow = 5;
        private readonly List<LanPlayer> players = new List<LanPlayer>();
        private readonly Random random;
        private int lastPlayerId = HostId;
        private int roundId, seed;
        private double startsAt;

        public LanRoom(string nickname, Random random = null)
        {
            this.random = random ?? new Random();
            players.Add(new LanPlayer { Id = HostId, Nickname = nickname, Connected = true });
        }

        public LanPhase Phase { get; private set; }
        private double Deadline { get { return startsAt + TrainingRound.Duration; } }

        public LanError Join(string nickname, out int id)
        {
            id = 0;
            if (Phase != LanPhase.Lobby)
                return LanError.RoundInProgress;
            if (players.Count == Capacity)
                return LanError.Full;
            id = ++lastPlayerId;
            players.Add(new LanPlayer { Id = id, Nickname = nickname, Connected = true });
            return LanError.None;
        }

        public bool SetReady(int id, bool ready)
        {
            LanPlayer player = FindPlayer(id);
            if (Phase != LanPhase.Lobby || player == null || player.Ready == ready)
                return false;
            player.Ready = ready;
            return true;
        }

        public bool Disconnect(int id)
        {
            LanPlayer player = FindPlayer(id);
            if (player == null || player.Id == HostId || !player.Connected)
                return false;
            if (Phase == LanPhase.Lobby)
                players.Remove(player);
            else
            {
                player.Connected = false;
                if (player.Status == LanPlayerStatus.Pending)
                    player.Status = LanPlayerStatus.Disconnected;
            }
            return true;
        }

        public bool Start(double now)
        {
            if (Phase != LanPhase.Lobby || players.Count < 2 || players.Any(player => !player.Ready))
                return false;
            roundId++;
            int newSeed = random.Next();
            seed = newSeed == seed ? newSeed + 1 : newSeed;
            startsAt = now + CountdownSeconds;
            Phase = LanPhase.Countdown;
            return true;
        }

        public bool Advance(double now)
        {
            LanPhase previous = Phase;
            if (Phase == LanPhase.Countdown && now >= startsAt)
                Phase = LanPhase.Playing;
            if (Phase == LanPhase.Playing && now >= Deadline)
                Phase = LanPhase.Collecting;
            if (Phase == LanPhase.Collecting &&
                (now >= Deadline + ScoreWindow || players.All(player => player.Status != LanPlayerStatus.Pending)))
                Settle();
            return Phase != previous;
        }

        public bool Submit(int id, int round, int hits, int shots, double now)
        {
            if (round != roundId || (Phase != LanPhase.Playing && Phase != LanPhase.Collecting) ||
                now < Deadline - LanClock.TimingTolerance || now >= Deadline + ScoreWindow || hits < 0 || shots < hits)
                return false;
            LanPlayer player = FindPlayer(id);
            if (player == null || player.Status != LanPlayerStatus.Pending)
                return false;
            player.Hits = hits;
            player.Shots = shots;
            player.Status = LanPlayerStatus.Finished;
            return true;
        }

        public bool Forfeit(int id, int round, LanPlayerStatus reason)
        {
            if (reason != LanPlayerStatus.Withdrew && reason != LanPlayerStatus.Unfocused &&
                reason != LanPlayerStatus.MissedStart && reason != LanPlayerStatus.CaptureLost &&
                reason != LanPlayerStatus.DisplayChanged)
                return false;
            LanPlayer player = FindPlayer(id);
            if (roundId != round || Phase == LanPhase.Lobby || Phase == LanPhase.Results ||
                player == null || player.Status != LanPlayerStatus.Pending)
                return false;
            player.Status = reason;
            return true;
        }

        public LanSnapshot Snapshot()
        {
            LanPlayer[] copies = players.Select(player => player.Copy()).ToArray();
            if (Phase == LanPhase.Results)
                copies = copies.OrderBy(player => player.Rank == 0 ? int.MaxValue : player.Rank).ThenBy(player => player.Id).ToArray();
            else
                foreach (LanPlayer player in copies)
                    player.Hits = player.Shots = 0;
            return new LanSnapshot
            {
                Phase = Phase, RoundId = Phase == LanPhase.Lobby ? 0 : roundId,
                StartsAt = startsAt, Seed = seed, Players = copies
            };
        }

        public bool ReturnToLobby()
        {
            if (Phase != LanPhase.Results)
                return false;
            players.RemoveAll(player => !player.Connected);
            foreach (LanPlayer player in players)
            {
                player.Ready = false;
                player.Status = LanPlayerStatus.Pending;
                player.Rank = player.Hits = player.Shots = 0;
            }
            Phase = LanPhase.Lobby;
            return true;
        }

        private void Settle()
        {
            foreach (LanPlayer player in players)
                if (player.Status == LanPlayerStatus.Pending)
                    player.Status = LanPlayerStatus.MissingScore;
            int placed = 0, rank = 0, previousHits = -1;
            foreach (LanPlayer player in players.Where(player => player.Status == LanPlayerStatus.Finished)
                .OrderByDescending(player => player.Hits))
            {
                placed++;
                if (player.Hits != previousHits)
                    rank = placed;
                player.Rank = rank;
                previousHits = player.Hits;
            }
            Phase = LanPhase.Results;
        }

        private LanPlayer FindPlayer(int id)
        {
            return players.Find(player => player.Id == id);
        }
    }
}
