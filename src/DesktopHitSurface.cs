using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace DesktopIconFlick
{
    internal sealed class DesktopHitSurface : FrameworkElement
    {
        private const double PulseDuration = 0.22;
        private static readonly Pen PulseEdge = RangeSurface.Stroke(RangeSurface.Paint(255, 8, 27, 29), 4);
        private static readonly Pen PulseColor = RangeSurface.Stroke(RangeSurface.Paint(255, 163, 247, 217), 2);
        private readonly TrainingRound round;
        private readonly TargetIcons icons;
        private readonly Brush shade;
        private readonly DrawingVisual feedback = new DrawingVisual();
        private readonly List<Pulse> pulses = new List<Pulse>();
        public DesktopWallpaper Wallpaper { get; set; }
        public Rect DesktopArea { get; set; }
        public IDictionary<string, Rect> VerifiedBounds { get; set; }
        public Func<IList<Rect>> ReadObstructions { get; set; }
        public Func<Rect, bool> HasLiveBackdrop { get; set; }

        private struct Pulse
        {
            public DesktopIcon Icon;
            public double StartedAt;
        }

        public DesktopHitSurface(TrainingRound round, TargetIcons icons, Brush shade)
        {
            this.round = round;
            this.icons = icons;
            this.shade = shade;
            IsHitTestVisible = false;
            AddVisualChild(feedback);
        }

        protected override int VisualChildrenCount { get { return 1; } }

        protected override Visual GetVisualChild(int index)
        {
            if (index != 0)
                throw new ArgumentOutOfRangeException("index");
            return feedback;
        }

        private bool CanDraw
        {
            get
            {
                return round.State == RoundState.Active && !DesktopArea.IsEmpty &&
                    DesktopArea.Width > 0 && DesktopArea.Height > 0 && ActualWidth > 0 && ActualHeight > 0;
            }
        }

        private Matrix DesktopToLocal
        {
            get
            {
                double scaleX = ActualWidth / DesktopArea.Width, scaleY = ActualHeight / DesktopArea.Height;
                return new Matrix(scaleX, 0, 0, scaleY, -DesktopArea.X * scaleX, -DesktopArea.Y * scaleY);
            }
        }

        protected override void OnRender(DrawingContext drawing)
        {
            if (!CanDraw || Wallpaper == null)
                return;

            IList<Rect> obstructions = null;
            Matrix toLocal = DesktopToLocal;
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
                Geometry clip = VisibleClip(desktop, local, toLocal, obstructions);

                if (clip != null)
                    drawing.PushClip(clip);
                Wallpaper.Draw(drawing, desktop, local);
                drawing.DrawRectangle(shade, null, local);
                if (clip != null)
                    drawing.Pop();
            }
        }

        internal void StartPulse(DesktopIcon icon, double now)
        {
            Rect desktop;
            if (!CanDraw || !TryGetDesktopBounds(icon, out desktop) || !HasBackdrop(desktop))
                return;

            IList<Rect> obstructions = ReadObstructions == null ? null : ReadObstructions();
            if (ReadObstructions != null && obstructions == null)
                return;
            Matrix toLocal = DesktopToLocal;
            Geometry clip = VisibleClip(desktop, Rect.Transform(desktop, toLocal), toLocal, obstructions);
            if (clip == null || clip.GetArea() > 0)
                pulses.Add(new Pulse { Icon = icon, StartedAt = now });
        }

        internal void AdvancePulses(double now)
        {
            if (pulses.Count == 0)
                return;

            pulses.RemoveAll(pulse => now - pulse.StartedAt >= PulseDuration);
            using (DrawingContext drawing = feedback.RenderOpen())
            {
                if (pulses.Count == 0 || !CanDraw)
                    return;

                IList<Rect> obstructions = ReadObstructions == null ? null : ReadObstructions();
                if (ReadObstructions != null && obstructions == null)
                    return;
                Matrix toLocal = DesktopToLocal;
                foreach (Pulse pulse in pulses)
                {
                    Rect desktop;
                    if (!TryGetDesktopBounds(pulse.Icon, out desktop) || !HasBackdrop(desktop))
                        continue;

                    Rect local = Rect.Transform(desktop, toLocal);
                    Geometry clip = VisibleClip(desktop, local, toLocal, obstructions);
                    if (clip != null)
                        drawing.PushClip(clip);
                    double opacity = 1 - (now - pulse.StartedAt) / PulseDuration;
                    drawing.PushOpacity(opacity * opacity);
                    DrawCorners(drawing, local);
                    drawing.Pop();
                    if (clip != null)
                        drawing.Pop();
                }
            }
        }

        internal void ClearPulses()
        {
            if (pulses.Count == 0)
                return;
            pulses.Clear();
            feedback.RenderOpen().Close();
        }

        private bool HasBackdrop(Rect desktop)
        {
            return Wallpaper != null || (HasLiveBackdrop != null && HasLiveBackdrop(desktop));
        }

        private static Geometry VisibleClip(Rect desktop, Rect local, Matrix toLocal, IList<Rect> obstructions)
        {
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
            return clip;
        }

        private static void DrawCorners(DrawingContext drawing, Rect local)
        {
            const double inset = 2;
            double span = Math.Min(11, Math.Min(local.Width, local.Height) / 2 - inset);
            if (span <= 0)
                return;

            for (int row = 0; row < 2; row++)
            {
                double y = row == 0 ? local.Top + inset : local.Bottom - inset;
                double endY = y + (row == 0 ? span : -span);
                for (int column = 0; column < 2; column++)
                {
                    double x = column == 0 ? local.Left + inset : local.Right - inset;
                    double endX = x + (column == 0 ? span : -span);
                    var corner = new Point(x, y);
                    var horizontal = new Point(endX, y);
                    var vertical = new Point(x, endY);
                    drawing.DrawLine(PulseEdge, corner, horizontal);
                    drawing.DrawLine(PulseEdge, corner, vertical);
                    drawing.DrawLine(PulseColor, corner, horizontal);
                    drawing.DrawLine(PulseColor, corner, vertical);
                }
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

        internal static IList<Rect> GetObstructions(IntPtr overlay, IntPtr desktopView, IntPtr liveBackdrop = default(IntPtr))
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
                if (window == liveBackdrop || !IsWindowVisible(window) || IsIconic(window))
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
