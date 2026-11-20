using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace DesktopIconFlick
{
    internal sealed class DesktopHitSurface : FrameworkElement
    {
        private readonly TrainingRound round;
        private readonly TargetIcons icons;
        private readonly Brush shade;
        public DesktopWallpaper Wallpaper { get; set; }
        public Rect DesktopArea { get; set; }
        public IDictionary<string, Rect> VerifiedBounds { get; set; }
        public Func<IList<Rect>> ReadObstructions { get; set; }

        public DesktopHitSurface(TrainingRound round, TargetIcons icons, Brush shade)
        {
            this.round = round;
            this.icons = icons;
            this.shade = shade;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawing)
        {
            if (round.State != RoundState.Active || Wallpaper == null || DesktopArea.IsEmpty ||
                DesktopArea.Width <= 0 || DesktopArea.Height <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
                return;

            IList<Rect> obstructions = null;
            double scaleX = ActualWidth / DesktopArea.Width, scaleY = ActualHeight / DesktopArea.Height;
            var toLocal = new Matrix(scaleX, 0, 0, scaleY, -DesktopArea.X * scaleX, -DesktopArea.Y * scaleY);
            foreach (DesktopIcon icon in icons.Covered)
            {
                Rect desktop;
                if (!TryGetDesktopBounds(icon, out desktop))
                    continue;
                if (ReadObstructions != null && obstructions == null)
                {
                    obstructions = ReadObstructions();
                    if (obstructions == null)
                        return;
                }

                Rect local = Rect.Transform(desktop, toLocal);
                Geometry clip = null;
                if (obstructions != null)
                {
                    foreach (Rect obstruction in obstructions)
                    {
                        Rect overlap = Rect.Intersect(desktop, obstruction);
                        if (overlap.IsEmpty || overlap.Width <= 0 || overlap.Height <= 0)
                            continue;
                        clip = new CombinedGeometry(GeometryCombineMode.Exclude, clip ?? new RectangleGeometry(local),
                            new RectangleGeometry(Rect.Transform(overlap, toLocal)));
                    }
                }

                if (clip != null)
                    drawing.PushClip(clip);
                Wallpaper.Draw(drawing, desktop, local);
                drawing.DrawRectangle(shade, null, local);
                if (clip != null)
                    drawing.Pop();
            }
        }

        internal bool TryGetDesktopBounds(DesktopIcon icon, out Rect bounds)
        {
            Rect verified;
            if (VerifiedBounds != null && (icon.DesktopName == null ||
                !VerifiedBounds.TryGetValue(icon.DesktopName, out verified) || verified != icon.DesktopBounds))
            {
                bounds = Rect.Empty;
                return false;
            }

            bounds = Rect.Intersect(DesktopArea, icon.DesktopBounds);
            return !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool GetLayeredWindowAttributes(IntPtr window, IntPtr colorKey, out byte alpha, out uint flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out WindowsDesktop.Rect value, int size);

        internal static IList<Rect> GetObstructions(IntPtr overlay, IntPtr desktopView)
        {
            const uint rootAncestor = 2, nextWindow = 2, constantAlpha = 2;
            const int frameBoundsAttribute = 9, cloakedAttribute = 14;
            if (desktopView == IntPtr.Zero || !IsWindowVisible(desktopView))
                return null;

            IntPtr desktop = GetAncestor(desktopView, rootAncestor);
            int boundsSize = Marshal.SizeOf(typeof(WindowsDesktop.Rect));
            var obstructions = new List<Rect>();
            for (IntPtr window = GetWindow(overlay, nextWindow); window != IntPtr.Zero; window = GetWindow(window, nextWindow))
            {
                if (window == desktop)
                    return obstructions;
                if (!IsWindowVisible(window) || IsIconic(window))
                    continue;
                int cloaked;
                if (DwmGetWindowAttribute(window, cloakedAttribute, out cloaked, sizeof(int)) == 0 && cloaked != 0)
                    continue;
                byte alpha;
                uint flags;
                if (GetLayeredWindowAttributes(window, IntPtr.Zero, out alpha, out flags) &&
                    (flags & constantAlpha) != 0 && alpha == 0)
                    continue;

                WindowsDesktop.Rect bounds;
                if (DwmGetWindowAttribute(window, frameBoundsAttribute, out bounds, boundsSize) != 0 &&
                    !WindowsDesktop.GetWindowRect(window, out bounds))
                    continue;
                if (bounds.Width > 0 && bounds.Height > 0)
                    obstructions.Add(new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
            }

            return null;
        }
    }
}
