using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;

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

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string name, uint style,
            int left, int top, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr window, uint key, byte alpha, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int left, int top, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr window);

        public static IntPtr HitWindow(int horizontal, int vertical)
        {
            return WindowFromPoint(((long)vertical << 32) | (uint)horizontal);
        }

        public static bool IsNonClickThroughLayer(IntPtr window)
        {
            const long layered = 0x00080000, transparent = 0x00000020;
            return (GetWindowStyle(window, -20).ToInt64() & (layered | transparent)) == layered;
        }

        public static void TransparentWindowsDoNotObstructDesktop()
        {
            const uint noActivateTool = 0x08000080, popupVisible = 0x90000000, layered = 0x00080000;
            const uint alphaAttribute = 0x2, keepBoundsAndActivation = 0x13;
            const int overlay = 0, invisible = 1, translucent = 2, opaque = 3, backdrop = 4;
            WindowsDesktop.Rect desktop = WindowsDesktop.VirtualScreen();
            var windows = new IntPtr[5];
            var bounds = new Rect[windows.Length];
            try
            {
                for (int index = 0; index < windows.Length; index++)
                {
                    uint attributes = noActivateTool;
                    if (index == invisible || index == translucent)
                        attributes |= layered;
                    windows[index] = CreateWindowEx(attributes, "STATIC", null, popupVisible,
                        desktop.Right + 40 * (index + 1), desktop.Top, 24, 24,
                        IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (windows[index] == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create a desktop obstruction test window.");
                    WindowsDesktop.Rect windowBounds;
                    if (!WindowsDesktop.GetWindowRect(windows[index], out windowBounds))
                        throw new InvalidOperationException("Unable to read a desktop obstruction test window's bounds.");
                    bounds[index] = new Rect(windowBounds.Left, windowBounds.Top, windowBounds.Width, windowBounds.Height);
                }

                if (!SetLayeredWindowAttributes(windows[invisible], 0, 0, alphaAttribute) ||
                    !SetLayeredWindowAttributes(windows[translucent], 0, 128, alphaAttribute))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to set test window opacity.");

                for (int index = 1; index < windows.Length; index++)
                    if (!SetWindowPos(windows[index], windows[index - 1], 0, 0, 0, 0, keepBoundsAndActivation))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to order the desktop obstruction test windows.");

                var obstructions = DesktopHitSurface.GetObstructions(windows[overlay], windows[backdrop]);
                if (obstructions == null || obstructions.Contains(bounds[invisible]) ||
                    !obstructions.Contains(bounds[translucent]) || !obstructions.Contains(bounds[opaque]))
                    throw new InvalidOperationException("Only the visible desktop obstruction test windows should obstruct icons.");
                obstructions = DesktopHitSurface.GetObstructions(windows[overlay], windows[backdrop], windows[opaque]);
                if (obstructions == null || obstructions.Contains(bounds[opaque]) || !obstructions.Contains(bounds[translucent]))
                    throw new InvalidOperationException("The known live wallpaper compositor should not obstruct icon effects.");
            }
            finally
            {
                foreach (IntPtr window in windows)
                    if (window != IntPtr.Zero)
                        DestroyWindow(window);
            }
        }
    }
}
