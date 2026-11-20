using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace DesktopIconFlick
{
    internal sealed class RangeSurface : FrameworkElement
    {
        private static readonly Brush BallFill = Paint(255, 163, 247, 217);
        private static readonly Pen Outline = Stroke(Paint(255, 8, 27, 29), 5);
        private static readonly Pen Ring = Stroke(BallFill, 2);
        private static readonly Pen CrosshairEdge = Stroke(Brushes.Black, 4);
        private static readonly Pen Crosshair = Stroke(Brushes.White, 1.5);
        private static readonly Pen HitMark = Stroke(BallFill, 2);
        private readonly TrainingRound round;
        private readonly TargetIcons icons;
        private readonly List<DrawTarget> visible = new List<DrawTarget>(3);
        public bool Flash { get; set; }

        private struct DrawTarget
        {
            public Target Target;
            public Projection Projection;
        }

        public RangeSurface(TrainingRound round, TargetIcons icons)
        {
            this.round = round;
            this.icons = icons;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawing)
        {
            if (round.State != RoundState.Active)
                return;
            double width = ActualWidth, height = ActualHeight;
            drawing.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
            visible.Clear();
            foreach (Target target in round.Targets)
            {
                Projection projection;
                if (round.Camera.Project(target.Center, Target.Radius, width, height, out projection))
                    visible.Add(new DrawTarget { Target = target, Projection = projection });
            }
            visible.Sort((first, second) => second.Projection.Depth.CompareTo(first.Projection.Depth));
            foreach (DrawTarget entry in visible)
            {
                Projection projected = entry.Projection;
                var center = new Point(projected.X, projected.Y);
                DesktopIcon icon = icons.For(entry.Target);
                if (icon != null)
                {
                    ImageSource art = icon.Image;
                    // Fit the image diagonal within the smaller diameter of the projected hit sphere.
                    double diagonal = Math.Sqrt(art.Width * art.Width + art.Height * art.Height);
                    double scale = 2 * projected.TangentRadius / diagonal;
                    double iconWidth = art.Width * scale, iconHeight = art.Height * scale;
                    drawing.DrawImage(art, new Rect(center.X - iconWidth / 2, center.Y - iconHeight / 2, iconWidth, iconHeight));
                    continue;
                }

                drawing.PushTransform(new RotateTransform(projected.Rotation, center.X, center.Y));
                drawing.DrawEllipse(BallFill, Outline, center, projected.RadialRadius, projected.TangentRadius);
                drawing.DrawEllipse(null, Ring, center, projected.RadialRadius, projected.TangentRadius);
                drawing.Pop();
            }
            double middleX = width / 2, middleY = height / 2;
            DrawCross(drawing, CrosshairEdge, middleX, middleY);
            DrawCross(drawing, Crosshair, middleX, middleY);
            if (Flash)
            {
                for (int vertical = -1; vertical <= 1; vertical += 2)
                    for (int horizontal = -1; horizontal <= 1; horizontal += 2)
                        drawing.DrawLine(HitMark, new Point(middleX + horizontal * 8, middleY + vertical * 8),
                            new Point(middleX + horizontal * 14, middleY + vertical * 14));
            }
            drawing.Pop();
        }

        private static void DrawCross(DrawingContext drawing, Pen pen, double centerX, double centerY)
        {
            drawing.DrawLine(pen, new Point(centerX - 10, centerY), new Point(centerX - 3, centerY));
            drawing.DrawLine(pen, new Point(centerX + 3, centerY), new Point(centerX + 10, centerY));
            drawing.DrawLine(pen, new Point(centerX, centerY - 10), new Point(centerX, centerY - 3));
            drawing.DrawLine(pen, new Point(centerX, centerY + 3), new Point(centerX, centerY + 10));
        }

        internal static Brush Paint(byte alpha, byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
            brush.Freeze();
            return brush;
        }

        private static Pen Stroke(Brush color, double width)
        {
            var pen = new Pen(color, width);
            pen.Freeze();
            return pen;
        }
    }
}
