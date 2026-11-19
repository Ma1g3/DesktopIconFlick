using System;
using System.Runtime.InteropServices;

namespace DesktopIconFlick
{
    internal static class DesktopProbe
    {
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(long point);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr window);

        [DllImport("user32.dll")]
        public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowStyle(IntPtr window, int index);

        public static IntPtr HitWindow(int horizontal, int vertical)
        {
            return WindowFromPoint(((long)vertical << 32) | (uint)horizontal);
        }

        public static bool IsNonClickThroughLayer(IntPtr window)
        {
            const long layered = 0x00080000, transparent = 0x00000020;
            return (GetWindowStyle(window, -20).ToInt64() & (layered | transparent)) == layered;
        }
    }
}
