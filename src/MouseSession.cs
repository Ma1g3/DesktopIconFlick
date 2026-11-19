using System;

namespace DesktopIconFlick
{
    internal sealed class MouseSession
    {
        internal delegate bool ReadBounds(out WindowsDesktop.Rect bounds);
        internal delegate bool LimitCursor(IntPtr window, out WindowsDesktop.Rect bounds);

        private readonly Func<IntPtr> foreground;
        private readonly Func<IntPtr, bool> registerMouse;
        private readonly ReadBounds readClip;
        private readonly Func<WindowsDesktop.Rect> desktopBounds;
        private readonly LimitCursor confine;
        private readonly Action releaseCursor;
        private IntPtr owner;
        private WindowsDesktop.Rect clip;

        public MouseSession() : this(WindowsDesktop.GetForegroundWindow, WindowsDesktop.RegisterMouse,
            WindowsDesktop.GetClipCursor, WindowsDesktop.VirtualScreen, WindowsDesktop.Confine, WindowsDesktop.ReleaseCursor)
        {
        }

        internal MouseSession(Func<IntPtr> foreground, Func<IntPtr, bool> registerMouse, ReadBounds readClip,
            Func<WindowsDesktop.Rect> desktopBounds, LimitCursor confine, Action releaseCursor)
        {
            this.foreground = foreground;
            this.registerMouse = registerMouse;
            this.readClip = readClip;
            this.desktopBounds = desktopBounds;
            this.confine = confine;
            this.releaseCursor = releaseCursor;
        }

        public bool IsActive { get { return owner != IntPtr.Zero; } }

        public bool TryAcquire(IntPtr window)
        {
            WindowsDesktop.Rect current;
            if (IsActive || window == IntPtr.Zero || foreground() != window ||
                !readClip(out current) || !current.Equals(desktopBounds()) || !registerMouse(window))
                return false;
            if (!confine(window, out clip))
            {
                registerMouse(IntPtr.Zero);
                return false;
            }
            owner = window;
            if (CanRead(window))
                return true;
            Release();
            return false;
        }

        public bool CanRead(IntPtr window)
        {
            return IsActive && owner == window && foreground() == window;
        }

        public bool HasConfinement()
        {
            WindowsDesktop.Rect current;
            return IsActive && readClip(out current) && current.Equals(clip);
        }

        public bool Release()
        {
            if (!IsActive)
                return false;
            owner = IntPtr.Zero;
            WindowsDesktop.Rect current;
            if (readClip(out current) && current.Equals(clip))
                releaseCursor();
            registerMouse(IntPtr.Zero);
            return true;
        }
    }
}
