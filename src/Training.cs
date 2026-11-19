using System;
using System.Collections.Generic;
using System.Globalization;

namespace DesktopIconFlick
{
    internal sealed class AimSettings
    {
        public const string Ranges = "DPI 100–32000 · cm/360 5–200 · 水平 FOV 60–120°";
        public static readonly AimSettings Default = new AimSettings(800, 40, 100);
        public readonly int Dpi;
        public readonly double Distance;
        public readonly double HorizontalFov;

        private AimSettings(int dpi, double distance, double horizontalFov)
        {
            Dpi = dpi;
            Distance = distance;
            HorizontalFov = horizontalFov;
        }

        public double DegreesPerCount { get { return 360.0 * 2.54 / (Dpi * Distance); } }

        public static bool TryRead(string dpi, string distance, string fov, out AimSettings settings)
        {
            int dpiValue;
            double distanceValue, fovValue;
            settings = null;
            if (!int.TryParse(dpi, NumberStyles.Integer, CultureInfo.InvariantCulture, out dpiValue) ||
                !double.TryParse(distance, NumberStyles.Float, CultureInfo.InvariantCulture, out distanceValue) ||
                !double.TryParse(fov, NumberStyles.Float, CultureInfo.InvariantCulture, out fovValue) ||
                !(dpiValue >= 100 && dpiValue <= 32000 && distanceValue >= 5 && distanceValue <= 200 &&
                  fovValue >= 60 && fovValue <= 120))
                return false;
            settings = new AimSettings(dpiValue, distanceValue, fovValue);
            return true;
        }
    }

    internal struct Vector
    {
        public readonly double X, Y, Z;
        public Vector(double horizontal, double vertical, double depth)
        {
            X = horizontal;
            Y = vertical;
            Z = depth;
        }

        public double Dot(Vector other) { return X * other.X + Y * other.Y + Z * other.Z; }
        public Vector Scale(double factor) { return new Vector(X * factor, Y * factor, Z * factor); }
        public Vector Add(Vector other) { return new Vector(X + other.X, Y + other.Y, Z + other.Z); }
        public Vector Subtract(Vector other) { return Add(other.Scale(-1)); }
        public Vector Unit() { return Scale(1 / Math.Sqrt(Dot(this))); }
    }

    internal struct Projection
    {
        public double X, Y, RadialRadius, TangentRadius, Rotation, Depth;
    }

    internal sealed class AimCamera
    {
        public double Yaw { get; private set; }
        public double Pitch { get; private set; }
        public readonly AimSettings Settings;
        private Vector right, up, forward;
        public Vector Forward { get { return forward; } }

        public AimCamera(AimSettings settings)
        {
            Settings = settings;
            SetAngles(0, 0);
        }

        public void Move(int horizontal, int vertical)
        {
            SetAngles(Yaw + horizontal * Settings.DegreesPerCount, Pitch - vertical * Settings.DegreesPerCount);
        }

        public void SetAngles(double yaw, double pitch)
        {
            Yaw = Math.IEEERemainder(yaw, 360);
            Pitch = Math.Max(-89, Math.Min(89, pitch));
            double radians = Math.PI / 180;
            double yawAngle = Yaw * radians, pitchAngle = Pitch * radians;
            double sinYaw = Math.Sin(yawAngle), cosYaw = Math.Cos(yawAngle);
            double sinPitch = Math.Sin(pitchAngle), cosPitch = Math.Cos(pitchAngle);
            right = new Vector(cosYaw, 0, -sinYaw);
            up = new Vector(-sinYaw * sinPitch, cosPitch, -cosYaw * sinPitch);
            forward = new Vector(sinYaw * cosPitch, sinPitch, cosYaw * cosPitch);
        }

        public double HalfWidthSlope { get { return Math.Tan(Settings.HorizontalFov * Math.PI / 360); } }

        public Vector Ray(double horizontalSlope, double verticalSlope)
        {
            return forward.Add(right.Scale(horizontalSlope)).Add(up.Scale(verticalSlope)).Unit();
        }

        public bool Project(Vector center, double radius, double width, double height, out Projection projected)
        {
            projected = new Projection();
            double depth = center.Dot(forward);
            if (depth <= radius || width <= 0 || height <= 0)
                return false;
            double horizontal = center.Dot(right), vertical = -center.Dot(up);
            double focal = width / (2 * HalfWidthSlope);
            double denominator = depth * depth - radius * radius;
            projected.X = width / 2 + focal * horizontal * depth / denominator;
            projected.Y = height / 2 + focal * vertical * depth / denominator;
            projected.TangentRadius = focal * radius / Math.Sqrt(denominator);
            projected.RadialRadius = projected.TangentRadius *
                Math.Sqrt(1 + (horizontal * horizontal + vertical * vertical) / denominator);
            projected.Rotation = Math.Atan2(vertical, horizontal) * 180 / Math.PI;
            projected.Depth = depth;
            return projected.X + projected.RadialRadius >= 0 && projected.Y + projected.RadialRadius >= 0 &&
                   projected.X - projected.RadialRadius <= width && projected.Y - projected.RadialRadius <= height;
        }
    }

    internal enum RoundState { Ready, Active, Paused, Finished }

    internal sealed class Target
    {
        public const double Radius = 0.33;
        public readonly Vector Center;
        public readonly int Id;

        public Target(Vector center, int id)
        {
            Center = center;
            Id = id;
        }

        public double HitDistance(Vector direction)
        {
            double along = Center.Dot(direction);
            double discriminant = along * along - Center.Dot(Center) + Radius * Radius;
            return along > 0 && discriminant >= 0 ? along - Math.Sqrt(discriminant) : double.PositiveInfinity;
        }
    }

    internal sealed class TrainingRound
    {
        public const double Duration = 30;
        private const double TargetDistance = 12;
        private readonly Func<double> now;
        private readonly Random random;
        private readonly List<Target> targets = new List<Target>(3);
        private readonly IList<Target> readOnlyTargets;
        private int nextId;
        private double used, startedAt, viewportWidth, viewportHeight;

        public TrainingRound(Func<double> now, int seed)
        {
            this.now = now;
            random = new Random(seed);
            readOnlyTargets = targets.AsReadOnly();
        }

        public AimCamera Camera { get; private set; }
        public IList<Target> Targets { get { return readOnlyTargets; } }
        public RoundState State { get; private set; }
        public int Hits { get; private set; }
        public int Shots { get; private set; }
        public double Accuracy { get { return Shots == 0 ? 0 : 100.0 * Hits / Shots; } }
        public double Remaining { get { return Math.Max(0, Duration - (State == RoundState.Active ? ActiveTime(now()) : used)); } }

        public void Start(AimSettings settings, double width, double height)
        {
            Camera = new AimCamera(settings);
            viewportWidth = width;
            viewportHeight = height;
            used = 0;
            Hits = 0;
            Shots = 0;
            nextId = 0;
            targets.Clear();
            while (targets.Count < 3)
                targets.Add(Spawn(null));
            startedAt = now();
            State = RoundState.Active;
        }

        public void Tick()
        {
            if (State == RoundState.Active)
                FinishIfExpired(ActiveTime(now()));
        }

        public void Pause()
        {
            if (State != RoundState.Active)
                return;
            double elapsed = ActiveTime(now());
            if (FinishIfExpired(elapsed))
                return;
            used = elapsed;
            State = RoundState.Paused;
        }

        private double ActiveTime(double timestamp)
        {
            return used + (timestamp - startedAt);
        }

        private bool FinishIfExpired(double elapsed)
        {
            if (elapsed < Duration)
                return false;
            used = Duration;
            State = RoundState.Finished;
            return true;
        }

        public void Resume()
        {
            if (State != RoundState.Paused)
                return;
            startedAt = now();
            State = RoundState.Active;
        }

        public void Cancel()
        {
            targets.Clear();
            State = RoundState.Ready;
        }

        public bool Input(int horizontal, int vertical, bool pressed)
        {
            Tick();
            if (State != RoundState.Active)
                return false;
            Camera.Move(horizontal, vertical);
            if (!pressed)
                return false;

            Shots++;
            int hit = -1;
            double nearest = double.PositiveInfinity;
            for (int index = 0; index < targets.Count; index++)
            {
                double distance = targets[index].HitDistance(Camera.Forward);
                if (distance < nearest)
                {
                    hit = index;
                    nearest = distance;
                }
            }
            if (hit < 0)
                return false;

            Target old = targets[hit];
            targets[hit] = Spawn(old);
            Hits++;
            return true;
        }

        private Target Spawn(Target removed)
        {
            double horizontal = Math.Min(0.65, Camera.HalfWidthSlope * 0.68);
            double vertical = Math.Min(0.55, Camera.HalfWidthSlope * viewportHeight / viewportWidth * 0.68);
            int start = random.Next(35);
            for (int offset = 0; offset < 35; offset++)
            {
                int cell = (start + offset * 12) % 35;
                Vector direction = Camera.Ray((cell % 7 - 3) * horizontal / 3, (cell / 7 - 2) * vertical / 2);
                if (direction.Dot(Camera.Forward) > 0.9985)
                    continue;
                Vector center = direction.Scale(TargetDistance);
                double spacing = Target.Radius * 2.5;
                bool clear = true;
                foreach (Target target in targets)
                {
                    Vector delta = center.Subtract(target.Center);
                    if (delta.Dot(delta) < spacing * spacing)
                    {
                        clear = false;
                        break;
                    }
                }
                if (clear && removed != null)
                {
                    Vector delta = center.Subtract(removed.Center);
                    clear = delta.Dot(delta) >= spacing * spacing;
                }
                if (clear)
                    return new Target(center, nextId++);
            }
            throw new InvalidOperationException("无法生成不重叠的靶位。");
        }
    }
}
