using System.Windows;

namespace DesktopIconFlick
{
    internal static partial class Tests
    {
        private static void CompetitionSharesTargetGeometry()
        {
            AimSettings narrow, wide;
            Require(AimSettings.Default.TryReadLocal("800", "60", out narrow), "narrow competition aim settings");
            Require(AimSettings.Default.TryReadLocal("1600", "120", out wide), "wide competition aim settings");
            var portrait = new Size(1080, 1920);
            var ultrawide = new Size(1920, 540);
            var balls = new TrainingRound(() => 0, 17);
            var icons = new TrainingRound(() => 0, 35);
            for (int seed = 0; seed < 12; seed++)
            {
                balls.StartCompetition(narrow, seed, 0);
                icons.StartCompetition(wide, seed, 0);
                for (int step = 0; step < 100; step++)
                {
                    CheckCluster(balls, portrait);
                    CheckCluster(icons, ultrawide);
                    for (int index = 0; index < 3; index++)
                    {
                        Vector difference = balls.Targets[index].Center.Subtract(icons.Targets[index].Center);
                        Near(difference.Dot(difference), 0, 0.000001, "competition target geometry matches across viewports");
                    }
                    int hit = step % 3;
                    Face(balls.Camera, balls.Targets[hit].Center);
                    Face(icons.Camera, icons.Targets[hit].Center);
                    Target untouched = icons.Targets[hit];
                    Require(balls.Input(0, 0, true) && icons.Targets[hit] == untouched && icons.Hits == step,
                        "one competition player cannot replace another player's target");
                    Require(icons.Input(0, 0, true), "same competition target can be hit with different aim settings");
                }
            }
        }

        private static void CompetitionUsesScheduledShotWindow()
        {
            double time = 4;
            var round = new TrainingRound(() => time, 17);
            round.StartCompetition(AimSettings.Default, 9, 5);
            Require(!round.Input(0, 0, true) && round.Shots == 0 && round.Remaining == 30, "competition before start");
            time = 5;
            Face(round.Camera, round.Targets[0].Center);
            Require(round.Input(0, 0, true), "competition scores at scheduled start");
            time = 34.999;
            round.Pause();
            Face(round.Camera, round.Targets[1].Center);
            Require(round.State == RoundState.Active && round.Input(0, 0, true), "competition does not pause before cutoff");
            time = 35;
            Require(!round.Input(0, 0, true) && round.State == RoundState.Finished, "competition rejects shot at cutoff");
            time = 36;
            Require(!round.Input(0, 0, true) && round.Shots == 2 && round.Hits == 2, "competition rejects late input");
            round.StartCompetition(AimSettings.Default, 9, 35.9);
            Near(round.Remaining, 29.9, 0.000001, "late starts retain the scheduled cutoff");
        }
    }
}
