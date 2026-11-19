using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopIconFlick
{
    internal static class Tests
    {
        [STAThread]
        public static int Main()
        {
            CameraAndSettings();
            ReplaysAndTargetPlacement();
            ClockAndProfile();
            PauseAtDeadline();
            DecodePackets();
            MouseLifecycle();
            DrawSkins();
            Console.WriteLine("Passed: aim math, projection, 4800 deterministic replacements, clock, profile, raw packets, mouse lifecycle and offscreen skins.");
            return 0;
        }

        private static void CameraAndSettings()
        {
            AimSettings narrow;
            Require(AimSettings.TryRead("800", "40", "60", out narrow), "valid settings");
            var wideCamera = new AimCamera(AimSettings.Default);
            var narrowCamera = new AimCamera(narrow);
            wideCamera.Move(3150, -200);
            narrowCamera.Move(3150, -200);
            Near(wideCamera.Yaw, 90, 0.02, "cm/360");
            Near(wideCamera.Yaw, narrowCamera.Yaw, 0.000001, "FOV independent yaw");
            Near(wideCamera.Pitch, 5.715, 0.000001, "mouse up looks up");
            Projection wide, zoomed;
            Vector center = wideCamera.Forward.Scale(12);
            Require(wideCamera.Project(center, Target.Radius, 1920, 1080, out wide), "front projection");
            Require(narrowCamera.Project(center, Target.Radius, 1920, 1080, out zoomed), "zoom projection");
            Near(zoomed.X, 960, 0.000001, "crosshair X");
            Near(zoomed.Y, 540, 0.000001, "crosshair Y");
            Require(zoomed.RadialRadius > wide.RadialRadius, "smaller FOV enlarges target");
            Require(!wideCamera.Project(center.Scale(-1), Target.Radius, 1920, 1080, out wide), "behind camera culled");
            var profile = new TrainingProfile("unused");
            foreach (string invalid in new[] { "NaN", "Infinity", "-Infinity", "1e999", "0", "201", "not-a-number" })
                Require(!profile.SetAim("800", invalid, "60") && profile.Aim == AimSettings.Default, "preserve valid settings: " + invalid);
            Require(!profile.SetAim("0", "40", "100") && !profile.SetAim("800", "40", "180"), "reject out-of-range DPI and FOV");
        }

        private static void ReplaysAndTargetPlacement()
        {
            AimSettings settings;
            AimSettings.TryRead("800", "40", "60", out settings);
            var sphereProfile = new TrainingProfile("unused") { UseIcons = false };
            var iconProfile = new TrainingProfile("unused") { UseIcons = true };
            foreach (int height in new[] { 540, 810, 1080, 1440 })
            {
                for (int seed = 0; seed < 12; seed++)
                {
                    var spheres = new TrainingRound(() => 0, seed);
                    var icons = new TrainingRound(() => 0, seed);
                    sphereProfile.SetAim("800", "40", settings.HorizontalFov.ToString());
                    iconProfile.SetAim("800", "40", settings.HorizontalFov.ToString());
                    spheres.Start(sphereProfile.Aim, 1920, height);
                    icons.Start(iconProfile.Aim, 1920, height);
                    Require(!spheres.Input(0, 0, true) && !icons.Input(0, 0, true), "empty center initially");
                    Require(spheres.Shots == 1 && spheres.Hits == 0 && spheres.Targets[0].Id == 0, "miss doesn't replace");
                    for (int step = 0; step < 100; step++)
                    {
                        int index = step % 3, other = (index + 1) % 3, third = (index + 2) % 3;
                        Target old = spheres.Targets[index], unchanged = spheres.Targets[other], last = spheres.Targets[third];
                        Face(spheres.Camera, old.Center);
                        Face(icons.Camera, icons.Targets[index].Center);
                        Require(spheres.Input(0, 0, true) && icons.Input(0, 0, true), "same hit in both skins");
                        Require(spheres.Targets.Count == 3 && spheres.Targets[other] == unchanged && spheres.Targets[third] == last, "one replacement");
                        Target fresh = spheres.Targets[index];
                        Near(fresh.Center.Subtract(icons.Targets[index].Center).Dot(fresh.Center.Subtract(icons.Targets[index].Center)), 0, 0.000001, "same spawn trajectory");
                        foreach (Target blocked in new[] { old, unchanged, last })
                        {
                            Vector delta = fresh.Center.Subtract(blocked.Center);
                            Require(delta.Dot(delta) > 4 * Target.Radius * Target.Radius, "separate spawn");
                        }
                        Projection placement;
                        Require(spheres.Camera.Project(fresh.Center, Target.Radius, 1920, height, out placement) &&
                                placement.X >= placement.RadialRadius && placement.X + placement.RadialRadius <= 1920 &&
                                placement.Y >= placement.RadialRadius && placement.Y + placement.RadialRadius <= height, "replacement inside viewport");
                    }
                    Require(spheres.Hits == 100 && icons.Shots == spheres.Shots, "same scoring");
                    spheres.Input(1, 1, false);
                    Require(spheres.Shots == 101, "motion without new press doesn't fire");
                }
            }
        }

        private static void ClockAndProfile()
        {
            double time = 0;
            var round = new TrainingRound(() => time, 17);
            round.Start(AimSettings.Default, 1920, 1080);
            time = 10;
            round.Pause();
            time = 72;
            round.Input(5, 5, true);
            Near(round.Remaining, 20, 0.000001, "paused time");
            Require(round.Shots == 0 && round.Camera.Yaw == 0, "paused input ignored");
            round.Resume();
            Face(round.Camera, round.Targets[0].Center);
            time = 91.999;
            Require(round.Input(0, 0, true), "hit before deadline");
            time = 92;
            Require(!round.Input(0, 0, true) && round.State == RoundState.Finished && round.Hits == 1 && round.Shots == 1, "deadline");
            string folder = Path.Combine(Path.GetTempPath(), "DesktopIconFlick-test-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(folder, "profile.txt");
            try
            {
                var profile = TrainingProfile.Load(path);
                profile.RecordCompletedRound(round.Hits);
                round.Start(profile.Aim, 1920, 1080);
                Require(round.Hits == 0 && round.Shots == 0 && round.Remaining == 30 && round.Accuracy == 0, "restart / zero shots");
                profile.RecordCompletedRound(0);
                Require(TrainingProfile.Load(path).Best == 1, "best persists when reopened");
                File.WriteAllText(path, "broken");
                profile = TrainingProfile.Load(path);
                Require(profile.Aim == AimSettings.Default && profile.Notice.Length > 0, "bad profile fallback");
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
                if (Directory.Exists(folder))
                    Directory.Delete(folder);
            }
        }

        private static void PauseAtDeadline()
        {
            double time = 0;
            int reads = 0;
            bool advancing = false;
            var round = new TrainingRound(() => {
                reads++;
                if (!advancing)
                    return time;
                double sample = time;
                time += 0.001;
                return sample;
            }, 9);
            round.Start(AimSettings.Default, 1920, 1080);
            time = 29.9995;
            reads = 0;
            advancing = true;
            round.Pause();
            Require(reads == 1 && round.State == RoundState.Paused && round.Remaining > 0, "pause samples time once before expiry");
            advancing = false;
            time = 90;
            round.Resume();
            time = 90.001;
            round.Pause();
            Require(round.State == RoundState.Finished && round.Remaining == 0 && round.Shots == 0,
                "pause after expiry settles instead of leaving a zero-time paused round");
        }

        private static void Face(AimCamera camera, Vector center)
        {
            Vector direction = center.Unit();
            camera.SetAngles(Math.Atan2(direction.X, direction.Z) * 180 / Math.PI, Math.Asin(direction.Y) * 180 / Math.PI);
        }

        private static void DrawSkins()
        {
            const int width = 960, height = 540;
            var round = new TrainingRound(() => 0, 3);
            round.Start(AimSettings.Default, width, height);
            var art = ShellIcons.ReadIcon(Path.GetTempPath());
            Require(art != null && art.IsFrozen, "Shell returns a transferable icon bitmap");
            var surface = new RangeSurface(round) { Icons = new[] { art } };
            byte[] balls = Pixels(surface, width, height);
            surface.UseIcons = true;
            byte[] icons = Pixels(surface, width, height);
            Require(!System.Linq.Enumerable.SequenceEqual(balls, icons), "actual icon and ball frames differ");
            surface.Icons = new ImageSource[0];
            byte[] fallback = Pixels(surface, width, height);
            Require(System.Linq.Enumerable.SequenceEqual(balls, fallback), "missing icon assets render the ordinary balls");
            int crosshairPixel = (height / 2 * width + width / 2 + 6) * 4;
            Require(balls[crosshairPixel + 3] > 0 && icons[crosshairPixel + 3] == balls[crosshairPixel + 3], "crosshair at viewport center");
        }

        private static void DecodePackets()
        {
            int horizontal, vertical;
            bool pressed;
            WindowsDesktop.DecodeMouse(3150, -200, 0, 1, false, out horizontal, out vertical, out pressed);
            Require(horizontal == 3150 && vertical == -200 && pressed, "relative packet and press");
            WindowsDesktop.DecodeMouse(65, 22, 0, 0, false, out horizontal, out vertical, out pressed);
            Require(horizontal == 65 && vertical == 22 && !pressed, "held button does not fire from motion");
            WindowsDesktop.DecodeMouse(0, 0, 0, 2, false, out horizontal, out vertical, out pressed);
            Require(!pressed, "release does not fire");
            WindowsDesktop.DecodeMouse(65400, 120, 1, 4, true, out horizontal, out vertical, out pressed);
            Require(horizontal == 0 && vertical == 0 && pressed, "absolute coordinates ignored; swapped primary button works");
        }

        private static void MouseLifecycle()
        {
            var window = new IntPtr(1);
            var otherWindow = new IntPtr(2);
            IntPtr foreground = otherWindow;
            IntPtr registered = IntPtr.Zero;
            var desktop = new WindowsDesktop.Rect { Left = -2560, Top = -120, Right = 1920, Bottom = 1440 };
            var locked = new WindowsDesktop.Rect { Left = 960, Top = 500, Right = 961, Bottom = 501 };
            var external = new WindowsDesktop.Rect { Left = -400, Top = 20, Right = -200, Bottom = 200 };
            WindowsDesktop.Rect clip = desktop;
            bool canRegister = true, canConfine = false, loseFocusOnConfine = false;
            int registrationChanges = 0, releases = 0;
            var mouse = new MouseSession(() => foreground,
                value =>
                {
                    registrationChanges++;
                    if (!canRegister)
                        return false;
                    registered = value;
                    return true;
                },
                delegate(out WindowsDesktop.Rect value) { value = clip; return true; },
                () => desktop,
                delegate(IntPtr value, out WindowsDesktop.Rect bounds)
                {
                    bounds = locked;
                    if (!canConfine)
                        return false;
                    clip = locked;
                    if (loseFocusOnConfine)
                        foreground = otherWindow;
                    return true;
                },
                () => { releases++; clip = desktop; });

            Require(!mouse.TryAcquire(window) && registrationChanges == 0, "no background acquisition");
            foreground = window;
            clip = external;
            Require(!mouse.TryAcquire(window) && clip.Equals(external) && registrationChanges == 0, "do not override another cursor constraint");
            clip = desktop;
            canRegister = false;
            Require(!mouse.TryAcquire(window) && clip.Equals(desktop) && !mouse.IsActive, "registration failure leaves cursor alone");
            canRegister = true;
            Require(!mouse.TryAcquire(window) && registered == IntPtr.Zero && !mouse.IsActive, "clip failure unregisters raw input");
            canConfine = loseFocusOnConfine = true;
            Require(!mouse.TryAcquire(window) && registered == IntPtr.Zero && clip.Equals(desktop) && releases == 1, "focus loss during capture rolls back");
            foreground = window;
            loseFocusOnConfine = false;
            Require(mouse.TryAcquire(window) && mouse.CanRead(window) && !mouse.CanRead(otherWindow) && mouse.HasConfinement(), "own foreground input only");
            foreground = otherWindow;
            Require(!mouse.CanRead(window) && mouse.Release() && !mouse.Release() && releases == 2, "release is idempotent");
            foreground = window;
            Require(!mouse.CanRead(window) && registered == IntPtr.Zero, "refocus does not resume");
            Require(mouse.TryAcquire(window), "explicit reacquisition");
            clip = external;
            Require(!mouse.HasConfinement() && mouse.Release() && registered == IntPtr.Zero &&
                    clip.Equals(external) && releases == 2, "do not clear replacement clip");
        }

        private static byte[] Pixels(RangeSurface surface, int width, int height)
        {
            surface.Measure(new Size(width, height));
            surface.Arrange(new Rect(0, 0, width, height));
            surface.InvalidateVisual();
            surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            return pixels;
        }

        private static void Near(double value, double expected, double epsilon, string message)
        {
            Require(Math.Abs(value - expected) < epsilon, message + ": " + value);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
