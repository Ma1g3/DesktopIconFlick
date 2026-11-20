using System;
using System.IO;
using System.Linq;
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
            TrackCoveredDesktopIcons();
            ClockAndProfile();
            PauseAtDeadline();
            DecodePackets();
            MouseLifecycle();
            DrawSkins();
            DrawDesktopHits();
            SameNamedDesktopItemsUseVisibleIdentity();
            DesktopProbe.TransparentWindowsDoNotObstructDesktop();
            Console.WriteLine("Passed: aim math, projection, 18000 deterministic replacements, icon source tracking, clock, profile, raw packets, mouse lifecycle, offscreen skins, desktop masks, Shell identity matching, and native window occlusion.");
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
            var sphereProfile = new TrainingProfile("unused") { UseIcons = false };
            var iconProfile = new TrainingProfile("unused") { UseIcons = true };
            var viewports = new[] { new Size(1080, 1920), new Size(1920, 1440), new Size(1920, 1080),
                new Size(1920, 810), new Size(1920, 540) };
            foreach (string fov in new[] { "60", "100", "120" })
            {
                Require(sphereProfile.SetAim("800", "40", fov) && iconProfile.SetAim("800", "40", fov), "replay FOV");
                foreach (Size viewport in viewports)
                {
                    for (int seed = 0; seed < 12; seed++)
                    {
                        var spheres = new TrainingRound(() => 0, seed);
                        var icons = new TrainingRound(() => 0, seed);
                        spheres.Start(sphereProfile.Aim, viewport.Width, viewport.Height);
                        icons.Start(iconProfile.Aim, viewport.Width, viewport.Height);
                        Require(!spheres.Input(0, 0, true) && !icons.Input(0, 0, true), "empty center initially");
                        Require(spheres.Shots == 1 && spheres.Hits == 0 && spheres.Targets[0].Id == 0, "miss doesn't replace");
                        CheckCluster(spheres, viewport);
                        for (int step = 0; step < 100; step++)
                        {
                            int index = step % 3, other = (index + 1) % 3, third = (index + 2) % 3;
                            Target old = spheres.Targets[index], unchanged = spheres.Targets[other], last = spheres.Targets[third];
                            double pitchOffset = step % 2 == 0 ? 1.5 : -1.5;
                            Face(spheres.Camera, old.Center, pitchOffset);
                            Face(icons.Camera, icons.Targets[index].Center, pitchOffset);
                            Require(spheres.Input(0, 0, true) && icons.Input(0, 0, true), "same hit in both skins");
                            Require(spheres.Targets.Count == 3 && spheres.Targets[other] == unchanged && spheres.Targets[third] == last, "one replacement");
                            Target fresh = spheres.Targets[index];
                            Vector replayDelta = fresh.Center.Subtract(icons.Targets[index].Center);
                            Near(replayDelta.Dot(replayDelta), 0, 0.000001, "same spawn trajectory");
                            Vector oldDelta = fresh.Center.Subtract(old.Center);
                            Require(oldDelta.Dot(oldDelta) > 4 * Target.Radius * Target.Radius, "separate from hit location");
                            Require(double.IsPositiveInfinity(fresh.HitDistance(spheres.Camera.Forward)), "fresh target requires another aim");
                            Require(FullyVisible(spheres.Camera, fresh, viewport), "replacement visible after a near-edge hit");
                            CheckCluster(spheres, viewport);
                        }
                        Require(spheres.Hits == 100 && icons.Shots == spheres.Shots, "same scoring");
                        spheres.Input(1, 1, false);
                        Require(spheres.Shots == 101, "motion without new press doesn't fire");
                    }
                }
            }
        }

        private static void CheckCluster(TrainingRound round, Size viewport)
        {
            double roundCenterLimit = Math.Cos(Math.PI / 12);
            double separationLimit = Math.Cos(Math.PI / 6);
            foreach (Target target in round.Targets)
            {
                Vector direction = target.Center.Unit();
                Require(direction.Z >= roundCenterLimit, "target stays near opening direction");
                Face(round.Camera, target.Center);
                foreach (Target other in round.Targets)
                {
                    Require(FullyVisible(round.Camera, other, viewport), "all targets visible when aimed at one");
                    if (other != target)
                    {
                        Require(direction.Dot(other.Center.Unit()) >= separationLimit, "target centers within 30 degrees");
                        Vector delta = target.Center.Subtract(other.Center);
                        Require(delta.Dot(delta) > 4 * Target.Radius * Target.Radius, "visible targets have separate hit volumes");
                    }
                }
            }
        }

        private static bool FullyVisible(AimCamera camera, Target target, Size viewport)
        {
            Projection placement;
            return camera.Project(target.Center, Target.Radius, viewport.Width, viewport.Height, out placement) &&
                placement.X >= placement.RadialRadius && placement.X + placement.RadialRadius <= viewport.Width &&
                placement.Y >= placement.RadialRadius && placement.Y + placement.RadialRadius <= viewport.Height;
        }

        private static void TrackCoveredDesktopIcons()
        {
            double time = 0;
            var round = new TrainingRound(() => time, 31);
            var artwork = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, new byte[] { 0, 255, 0, 255 }, 4);
            var first = new DesktopIcon(artwork, new Rect(10, 10, 30, 40));
            var second = new DesktopIcon(artwork, new Rect(50, 10, 30, 40));
            var third = new DesktopIcon(artwork, new Rect(90, 10, 30, 40));
            var unpositioned = new DesktopIcon(artwork, Rect.Empty);
            var icons = new TargetIcons();
            round.Start(AimSettings.Default, 1920, 1080);
            icons.Start(round.Targets, new[] { first, second, third, unpositioned });
            Require(!icons.Covered.Any(), "original icons visible before hits");
            Require(icons.For(round.Targets[0]) == first && icons.For(round.Targets[1]) == second &&
                icons.For(round.Targets[2]) == third, "three distinct sources despite shared art");

            Require(HitIcon(round, icons, 0), "first positioned hit changes cover");
            Require(icons.Covered.Single() == first, "same art does not cover other desktop items");
            Require(icons.For(round.Targets[0]) == unpositioned, "next unused source assigned");
            Target removed, replacement;
            Require(!round.Input(0, 0, true, out removed, out replacement) && removed == null && replacement == null &&
                icons.Covered.Single() == first, "miss yields no identity or cover change");
            Require(!HitIcon(round, icons, 0) && icons.For(round.Targets[0]) == first, "unpositioned source never covered");
            Require(!HitIcon(round, icons, 0) && icons.For(round.Targets[0]) == unpositioned,
                "repeated source already covered and cycle skips live survivors");
            Require(HitIcon(round, icons, 1) && icons.Covered.Count() == 2 && icons.Covered.Contains(second) &&
                !icons.Covered.Contains(third), "second item independently covered");

            time = 4;
            round.Pause();
            Require(!round.Input(0, 0, true, out removed, out replacement) && removed == null && replacement == null,
                "paused input reports no hit identity");
            time = 12;
            round.Resume();
            Require(icons.Covered.Count() == 2, "same round retains cover sources after pause");
            time = 38;
            Require(!round.Input(0, 0, true, out removed, out replacement) && round.State == RoundState.Finished &&
                removed == null && replacement == null, "expired input reports no hit identity");
            icons.Clear();
            Require(!icons.Covered.Any() && icons.For(round.Targets[0]) == null, "clear discards round state");

            round.Start(AimSettings.Default, 1920, 1080);
            icons.Start(round.Targets, new[] { first });
            Require(round.Targets.All(target => icons.For(target) == first), "one source can fill all target slots");
            Require(HitIcon(round, icons, 0) && !HitIcon(round, icons, 1), "reused source covered once");
            round.Start(AimSettings.Default, 1920, 1080);
            icons.Start(round.Targets, new DesktopIcon[0]);
            Require(!icons.Covered.Any() && icons.For(round.Targets[0]) == null && !HitIcon(round, icons, 0) &&
                round.Hits == 1, "new round clears covers and no-source mode still scores hits");
        }

        private static bool HitIcon(TrainingRound round, TargetIcons icons, int index)
        {
            Target old = round.Targets[index], removed, replacement;
            Face(round.Camera, old.Center);
            Require(round.Input(0, 0, true, out removed, out replacement) && removed == old &&
                replacement == round.Targets[index] && replacement != removed, "hit yields exact old and new targets");
            return icons.Replace(removed, replacement);
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

        private static void Face(AimCamera camera, Vector center, double pitchOffset = 0)
        {
            Vector direction = center.Unit();
            camera.SetAngles(Math.Atan2(direction.X, direction.Z) * 180 / Math.PI, Math.Asin(direction.Y) * 180 / Math.PI + pitchOffset);
        }

        private static void DrawSkins()
        {
            const int width = 960, height = 540;
            var round = new TrainingRound(() => 0, 3);
            round.Start(AimSettings.Default, width, height);
            Target target = round.Targets[0];
            Face(round.Camera, target.Center);
            round.Camera.SetAngles(round.Camera.Yaw, round.Camera.Pitch + 12);
            Projection projection;
            Require(round.Camera.Project(target.Center, Target.Radius, width, height, out projection), "test target is visible off the crosshair");

            var art = ShellIcons.ReadIcon(Path.GetTempPath());
            Require(art != null && art.IsFrozen, "Shell returns a transferable icon bitmap");
            var targetIcons = new TargetIcons();
            var surface = new RangeSurface(round, targetIcons);
            byte[] balls = Pixels(surface, width, height);
            targetIcons.Start(round.Targets, new[] { new DesktopIcon(art, Rect.Empty) });
            byte[] icons = Pixels(surface, width, height);
            Require(!System.Linq.Enumerable.SequenceEqual(balls, icons), "actual icon and ball frames differ");

            // A rectangular icon has a translucent center stripe and transparent sides.
            const int artWidth = 16, artHeight = 8, stride = artWidth * 4;
            var artPixels = new byte[stride * artHeight];
            for (int y = 0; y < artHeight; y++)
            {
                for (int x = 6; x < 10; x++)
                {
                    int pixel = y * stride + x * 4;
                    artPixels[pixel] = 16;
                    artPixels[pixel + 1] = 40;
                    artPixels[pixel + 2] = 120;
                    artPixels[pixel + 3] = 128;
                }
            }
            var testArt = BitmapSource.Create(artWidth, artHeight, 96, 96, PixelFormats.Pbgra32, null, artPixels, stride);
            targetIcons.Start(round.Targets, new[] { new DesktopIcon(testArt, Rect.Empty) });
            byte[] transparentIcon = Pixels(surface, width, height);
            int center = ((int)projection.Y * width + (int)projection.X) * 4;
            int insideTransparentSide = ((int)projection.Y * width + (int)(projection.X + projection.TangentRadius * 0.55)) * 4;
            int belowIcon = ((int)(projection.Y + projection.TangentRadius * 0.7) * width + (int)projection.X) * 4;
            Require(transparentIcon[center] == 16 && transparentIcon[center + 1] == 40 &&
                    transparentIcon[center + 2] == 120 && transparentIcon[center + 3] == 128,
                "icon center preserves color and opacity");
            Require(transparentIcon[insideTransparentSide + 3] == 0 && balls[insideTransparentSide + 3] == 255,
                "icon's transparent pixels have no ball backing");
            Require(transparentIcon[belowIcon + 3] == 0 && balls[belowIcon + 3] == 255,
                "rectangular icon retains its proportions");

            targetIcons.Clear();
            byte[] fallback = Pixels(surface, width, height);
            Require(System.Linq.Enumerable.SequenceEqual(balls, fallback), "missing icon assets render the ordinary balls");
            int crosshairPixel = (height / 2 * width + width / 2 + 6) * 4;
            Require(balls[crosshairPixel + 3] > 0 && icons[crosshairPixel + 3] == balls[crosshairPixel + 3], "crosshair at viewport center");
        }

        private static void DrawDesktopHits()
        {
            const int width = 200, height = 100;
            var viewport = new Rect(100, 50, 400, 200);
            var round = new TrainingRound(() => 0, 8);
            round.Start(AimSettings.Default, width, height);
            var art = new DrawingImage(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
            var first = new DesktopIcon(art, new Rect(90, 70, 40, 40), "first");
            var second = new DesktopIcon(art, new Rect(150, 70, 30, 40), "second");
            var targetIcons = new TargetIcons();
            targetIcons.Start(round.Targets, new[] { first, second });
            var wallpaper = new DesktopWallpaper(Colors.Orange, new Rect(0, 0, 600, 300));
            var surface = new DesktopHitSurface(round, targetIcons, Brushes.Transparent)
            {
                Wallpaper = wallpaper,
                DesktopArea = viewport,
                VerifiedBounds = new System.Collections.Generic.Dictionary<string, Rect>
                {
                    { "first", first.DesktopBounds }, { "second", second.DesktopBounds }
                }
            };
            int hitPixel = (20 * width + 5) * 4, boundsEdge = (20 * width + 17) * 4;
            int otherPixel = (20 * width + 30) * 4;
            Require(Pixels(surface, width, height)[hitPixel + 3] == 0, "desktop is visible before the first hit");
            Require(HitIcon(round, targetIcons, 0), "first desktop source covered");
            byte[] active = Pixels(surface, width, height);
            Require(active[hitPixel] == Colors.Orange.B && active[hitPixel + 1] == Colors.Orange.G &&
                    active[hitPixel + 2] == Colors.Orange.R && active[hitPixel + 3] == 255, "matching wallpaper drawn at the hit icon position");
            Require(active[otherPixel + 3] == 0 && active[boundsEdge + 3] == 0, "same artwork and adjacent desktop pixels remain visible");

            round.Pause();
            Require(Pixels(surface, width, height)[hitPixel + 3] == 0, "pausing removes the wallpaper patch");
            round.Resume();
            Require(Pixels(surface, width, height)[hitPixel + 3] == 255, "resuming restores the wallpaper patch");

            surface.ReadObstructions = () => new[] { first.DesktopBounds };
            Require(Pixels(surface, width, height)[hitPixel + 3] == 0, "an app in front of the desktop is never painted over");
            int exposedPixel = (20 * width + 11) * 4;
            surface.ReadObstructions = () => new[] { new Rect(100, 70, 14, 40), new Rect(130, 70, 10, 40) };
            byte[] partlyCovered = Pixels(surface, width, height);
            Require(partlyCovered[hitPixel + 3] == 0 && partlyCovered[exposedPixel + 3] == 255,
                "a partially obstructed icon is masked only in its exposed area");
            surface.ReadObstructions = null;
            surface.VerifiedBounds["first"] = new Rect(100, 50, 40, 40);
            Require(Pixels(surface, width, height)[hitPixel + 3] == 0, "changed desktop coordinates invalidate the old patch");
            surface.VerifiedBounds["first"] = first.DesktopBounds;
            surface.Wallpaper = null;
            byte[] fallback = Pixels(surface, width, height);
            Require(fallback[hitPixel + 3] == 0 && fallback[otherPixel + 3] == 0, "missing wallpaper leaves the original desktop visible");
        }

        private static void SameNamedDesktopItemsUseVisibleIdentity()
        {
            const string hiddenControlPanel = "::{26EE0668-A00A-44D7-9371-BEB064C98683}";
            const string desktopControlPanel = "::{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}";
            var names = new[] { hiddenControlPanel, desktopControlPanel, desktopControlPanel };
            var visible = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal) { desktopControlPanel };
            Require(ShellIcons.FindUniqueViewItem(new[] { 0, 1, 2 }, visible, index => names[index]) == 1,
                "a unique visible Control Panel identity wins and duplicate sources can be dropped");
            visible.Add(hiddenControlPanel);
            Require(ShellIcons.FindUniqueViewItem(new[] { 0, 1 }, visible, index => names[index]) == -1,
                "two different visible identities remain ambiguous");
            Require(ShellIcons.FindUniqueViewItem(new[] { 0, 1 }, null, index => names[index]) == -1,
                "unavailable Explorer view does not guess an identity");
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

        private static byte[] Pixels(FrameworkElement surface, int width, int height)
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
