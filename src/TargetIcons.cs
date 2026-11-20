using System.Collections.Generic;

namespace DesktopIconFlick
{
    internal sealed class TargetIcons
    {
        private readonly List<DesktopIcon> sources = new List<DesktopIcon>();
        private readonly Dictionary<Target, DesktopIcon> assignments = new Dictionary<Target, DesktopIcon>(3);
        private readonly HashSet<DesktopIcon> covered = new HashSet<DesktopIcon>();
        private int nextSource;

        public IEnumerable<DesktopIcon> Covered { get { return covered; } }

        public void Start(IList<Target> targets, IList<DesktopIcon> icons)
        {
            Clear();
            sources.AddRange(icons);
            foreach (Target target in targets)
                Assign(target);
        }

        public DesktopIcon For(Target target)
        {
            DesktopIcon icon;
            return assignments.TryGetValue(target, out icon) ? icon : null;
        }

        public bool Replace(Target removed, Target replacement)
        {
            DesktopIcon original = For(removed);
            assignments.Remove(removed);
            Assign(replacement);
            return original != null && !original.DesktopBounds.IsEmpty && covered.Add(original);
        }

        public void Clear()
        {
            sources.Clear();
            assignments.Clear();
            covered.Clear();
            nextSource = 0;
        }

        private void Assign(Target target)
        {
            if (sources.Count == 0)
                return;
            for (int tried = 0; tried < sources.Count; tried++)
            {
                DesktopIcon icon = NextSource();
                if (!assignments.ContainsValue(icon))
                {
                    assignments.Add(target, icon);
                    return;
                }
            }
            // A small source pool can still fill all three target slots.
            assignments.Add(target, NextSource());
        }

        private DesktopIcon NextSource()
        {
            DesktopIcon icon = sources[nextSource];
            nextSource = (nextSource + 1) % sources.Count;
            return icon;
        }
    }
}
