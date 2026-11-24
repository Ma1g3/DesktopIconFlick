using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopIconFlick
{
    internal sealed class LanClock
    {
        public const double TimingTolerance = 0.05;
        private const double SampleLifetime = 3;
        private readonly List<Sample> samples = new List<Sample>();

        private sealed class Sample
        {
            public double ObservedAt, Delay, Offset;
        }

        public bool IsSynchronized(double now)
        {
            return samples.Count > 0 && now - samples[samples.Count - 1].ObservedAt <= SampleLifetime;
        }

        public void Observe(double sent, double hostTime, double received)
        {
            double delay = received - sent;
            if (!(delay >= 0 && delay <= TimingTolerance * 2) || !IsFinite(hostTime))
                return;
            samples.RemoveAll(sample => received - sample.ObservedAt > SampleLifetime);
            samples.Add(new Sample { ObservedAt = received, Delay = delay, Offset = hostTime - (sent + delay / 2) });
        }

        public LanSchedule Schedule(LanSnapshot snapshot, double now)
        {
            if (!IsSynchronized(now))
                return null;
            Sample best = samples.Where(sample => now - sample.ObservedAt <= SampleLifetime)
                .OrderBy(sample => sample.Delay).ThenByDescending(sample => sample.ObservedAt).First();
            return new LanSchedule(snapshot.RoundId, snapshot.Seed, snapshot.StartsAt - best.Offset, best.Delay / 2);
        }

        public static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    internal sealed class LanSchedule
    {
        public const double StartGrace = 0.15;
        public readonly int RoundId;
        public readonly int Seed;
        public readonly double StartsAt;
        public readonly double ClockUncertainty;
        public double Deadline { get { return StartsAt + TrainingRound.Duration; } }

        public LanSchedule(int roundId, int seed, double startsAt, double clockUncertainty = 0)
        {
            RoundId = roundId;
            Seed = seed;
            StartsAt = startsAt;
            ClockUncertainty = clockUncertainty;
        }

        public bool IsLate(double now)
        {
            return now > StartsAt + StartGrace;
        }
    }
}
