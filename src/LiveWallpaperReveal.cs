using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopIconFlick
{
    internal sealed class LiveWallpaperReveal : IDisposable
    {
        private readonly IntPtr wallpaperHost, desktopRoot;
        private readonly WindowsDesktop.Rect workArea;
        private readonly Rect desktopArea;
        private readonly Dictionary<Rect, IntPtr> thumbnails = new Dictionary<Rect, IntPtr>();
        private HwndSource window;
        private bool disposed;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int Width, Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ThumbnailProperties
        {
            public uint Flags;
            public WindowsDesktop.Rect Destination, Source;
            public byte Opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool Visible;
            [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr window, out WindowsDesktop.Rect bounds);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);

        [DllImport("dwmapi.dll")]
        private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out NativeSize size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref ThumbnailProperties properties);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);

        public LiveWallpaperReveal(IntPtr wallpaperHost, IntPtr desktopView, WindowsDesktop.Rect workArea)
        {
            const uint rootAncestor = 2;
            this.wallpaperHost = wallpaperHost;
            desktopRoot = GetAncestor(desktopView, rootAncestor);
            this.workArea = workArea;
            desktopArea = workArea.Width > 0 && workArea.Height > 0 ?
                new Rect(workArea.Left, workArea.Top, workArea.Width, workArea.Height) : Rect.Empty;
        }

        internal IntPtr Handle { get { return window == null ? IntPtr.Zero : window.Handle; } }

        internal bool IsShowing(Rect desktopBounds)
        {
            return thumbnails.ContainsKey(desktopBounds);
        }

        public bool Show(Rect desktopBounds)
        {
            const uint rootAncestor = 2;
            if (disposed || wallpaperHost == IntPtr.Zero || desktopRoot == IntPtr.Zero || wallpaperHost == desktopRoot ||
                GetAncestor(wallpaperHost, rootAncestor) != wallpaperHost)
                return false;

            Rect visible = Rect.Intersect(desktopBounds, desktopArea);
            if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0)
                return false;

            var origin = new NativePoint();
            WindowsDesktop.Rect client;
            if (!GetClientRect(wallpaperHost, out client) || !ClientToScreen(wallpaperHost, ref origin))
                return false;
            WindowsDesktop.Rect source = ToNative(visible, origin.X, origin.Y);
            if (source.Left < 0 || source.Top < 0 || source.Right > client.Width || source.Bottom > client.Height)
                return false;

            if (window == null && !CreateWindow())
                return false;
            if (!PlaceAboveDesktop())
            {
                Dispose();
                return false;
            }
            if (thumbnails.ContainsKey(visible))
                return true;

            IntPtr thumbnail;
            if (DwmRegisterThumbnail(window.Handle, wallpaperHost, out thumbnail) != 0)
                return false;
            var properties = new ThumbnailProperties
            {
                Flags = 0x1f, // Destination, source, opacity, visibility and client-area selection.
                Destination = ToNative(visible, workArea.Left, workArea.Top),
                Source = source,
                Opacity = 255,
                Visible = true,
                SourceClientAreaOnly = true
            };
            NativeSize size;
            if (DwmQueryThumbnailSourceSize(thumbnail, out size) != 0 ||
                source.Right > size.Width || source.Bottom > size.Height ||
                DwmUpdateThumbnailProperties(thumbnail, ref properties) != 0)
            {
                DwmUnregisterThumbnail(thumbnail);
                return false;
            }
            thumbnails.Add(visible, thumbnail);
            return true;
        }

        private bool CreateWindow()
        {
            const int popup = unchecked((int)0x80000000), noActivate = 0x08000000, toolWindow = 0x80, transparent = 0x20;
            var parameters = new HwndSourceParameters("DesktopIconFlick.LiveWallpaper", workArea.Width, workArea.Height)
            {
                WindowStyle = popup,
                ExtendedWindowStyle = noActivate | toolWindow | transparent,
                PositionX = workArea.Left,
                PositionY = workArea.Top,
                UsesPerPixelOpacity = true
            };
            try
            {
                window = new HwndSource(parameters);
                window.CompositionTarget.BackgroundColor = Colors.Transparent;
                window.RootVisual = new DrawingVisual();
                return true;
            }
            catch (Win32Exception) { Dispose(); }
            catch (InvalidOperationException) { Dispose(); }
            return false;
        }

        private bool PlaceAboveDesktop()
        {
            const uint previousWindow = 3, nextWindow = 2, noZOrder = 0x4, noActivate = 0x10, show = 0x40;
            IntPtr handle = window.Handle;
            IntPtr preceding = GetWindow(desktopRoot, previousWindow);
            if (preceding == IntPtr.Zero)
                return false;
            if (preceding == handle && IsWindowVisible(handle))
                return true;
            uint flags = noActivate | show;
            if (preceding == handle)
                flags |= noZOrder;
            return SetWindowPos(handle, preceding, workArea.Left, workArea.Top, workArea.Width, workArea.Height, flags) &&
                GetWindow(handle, nextWindow) == desktopRoot;
        }

        private static WindowsDesktop.Rect ToNative(Rect bounds, int originX, int originY)
        {
            return new WindowsDesktop.Rect
            {
                Left = (int)Math.Floor(bounds.Left) - originX,
                Top = (int)Math.Floor(bounds.Top) - originY,
                Right = (int)Math.Ceiling(bounds.Right) - originX,
                Bottom = (int)Math.Ceiling(bounds.Bottom) - originY
            };
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            foreach (IntPtr thumbnail in thumbnails.Values)
                DwmUnregisterThumbnail(thumbnail);
            thumbnails.Clear();
            if (window != null)
            {
                window.Dispose();
                window = null;
            }
        }
    }
}
