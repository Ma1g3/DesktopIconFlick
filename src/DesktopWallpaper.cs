using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopIconFlick
{
    internal sealed class DesktopWallpaper
    {
        private enum Position { Center, Tile, Stretch, Fit, Fill, Span }

        [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDesktopWallpaper
        {
            // The native slot order includes setters. Wallpaper loading uses only the getters.
            [PreserveSig] int SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitor, [MarshalAs(UnmanagedType.LPWStr)] string path);
            [PreserveSig] int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitor, [MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int GetMonitorDevicePathAt(uint index, [MarshalAs(UnmanagedType.LPWStr)] out string monitor);
            [PreserveSig] int GetMonitorDevicePathCount(out uint count);
            [PreserveSig] int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitor, out WindowsDesktop.Rect bounds);
            [PreserveSig] int SetBackgroundColor(uint color);
            [PreserveSig] int GetBackgroundColor(out uint color);
            [PreserveSig] int SetPosition(Position position);
            [PreserveSig] int GetPosition(out Position position);
            [PreserveSig] int SetSlideshow(IntPtr items);
            [PreserveSig] int GetSlideshow(out IntPtr items);
            [PreserveSig] int SetSlideshowOptions(uint options, uint interval);
            [PreserveSig] int GetSlideshowOptions(out uint options, out uint interval);
            [PreserveSig] int AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string monitor, uint direction);
            [PreserveSig] int GetStatus(out uint state);
        }

        private static readonly Guid WallpaperClass = new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD");
        private const uint SlideshowConfigured = 0x2;
        private const string WallpaperEngineRenderer = "WPEDesktopDX11Window";
        private readonly DrawingGroup background;

        private delegate bool EnumWindowCallback(IntPtr window, IntPtr data);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        internal DesktopWallpaper(Color color, Rect monitor) : this(color, monitor, monitor, Position.Center, null) { }

        private DesktopWallpaper(Color color, Rect monitor, Rect desktop, Position position, BitmapSource image)
        {
            background = new DrawingGroup();
            using (DrawingContext drawing = background.Open())
            {
                drawing.PushClip(new RectangleGeometry(monitor));
                drawing.DrawRectangle(new SolidColorBrush(color), null, monitor);
                if (image != null)
                {
                    if (position == Position.Tile)
                    {
                        var tiles = new ImageBrush(image)
                        {
                            ViewportUnits = BrushMappingMode.Absolute,
                            Viewport = new Rect(desktop.X, desktop.Y, image.PixelWidth, image.PixelHeight),
                            TileMode = TileMode.Tile,
                            Stretch = Stretch.Fill
                        };
                        drawing.DrawRectangle(tiles, null, monitor);
                    }
                    else
                    {
                        Rect canvas = position == Position.Span ? desktop : monitor;
                        drawing.DrawImage(image, ImageBounds(canvas, position, image.PixelWidth, image.PixelHeight));
                    }
                }
                drawing.Pop();
            }
            background.Freeze();
        }

        public static DesktopWallpaper Load()
        {
            object instance = null;
            try
            {
                instance = Activator.CreateInstance(Type.GetTypeFromCLSID(WallpaperClass, true));
                var wallpaper = (IDesktopWallpaper)instance;
                uint state, count, colorRef;
                Position position;
                if (wallpaper.GetStatus(out state) != 0 || (state & SlideshowConfigured) != 0 ||
                    wallpaper.GetMonitorDevicePathCount(out count) != 0 || wallpaper.GetPosition(out position) != 0 ||
                    position < Position.Center || position > Position.Span || wallpaper.GetBackgroundColor(out colorRef) != 0)
                    return null;

                for (uint index = 0; index < count; index++)
                {
                    string id;
                    WindowsDesktop.Rect bounds;
                    if (wallpaper.GetMonitorDevicePathAt(index, out id) != 0 || wallpaper.GetMonitorRECT(id, out bounds) != 0 ||
                        bounds.Left != 0 || bounds.Top != 0 || bounds.Width <= 0 || bounds.Height <= 0)
                        continue;

                    string path;
                    if (FindWallpaperEngine(bounds, false) != IntPtr.Zero || wallpaper.GetWallpaper(id, out path) != 0)
                        return null;
                    Color color = Color.FromRgb((byte)colorRef, (byte)(colorRef >> 8), (byte)(colorRef >> 16));
                    Rect monitor = ToRect(bounds);
                    if (string.IsNullOrEmpty(path))
                        return new DesktopWallpaper(color, monitor);

                    BitmapSource image = LoadImage(path);
                    return image == null ? null : new DesktopWallpaper(color, monitor,
                        ToRect(WindowsDesktop.VirtualScreen()), position, image);
                }
                return null;
            }
            catch (Exception error)
            {
                if (!(error is COMException || error is InvalidCastException || error is IOException ||
                      error is UnauthorizedAccessException || error is SecurityException || error is ArgumentException ||
                      error is FormatException || error is NotSupportedException))
                    throw;
                return null;
            }
            finally
            {
                if (instance != null)
                    Marshal.ReleaseComObject(instance);
            }
        }

        public void Draw(DrawingContext drawing, Rect desktopBounds, Rect localBounds)
        {
            if (desktopBounds.IsEmpty || localBounds.IsEmpty || desktopBounds.Width <= 0 || desktopBounds.Height <= 0 ||
                localBounds.Width <= 0 || localBounds.Height <= 0)
                return;
            double scaleX = localBounds.Width / desktopBounds.Width;
            double scaleY = localBounds.Height / desktopBounds.Height;
            var physicalToLocal = new Matrix(scaleX, 0, 0, scaleY, localBounds.X - desktopBounds.X * scaleX,
                localBounds.Y - desktopBounds.Y * scaleY);
            drawing.PushClip(new RectangleGeometry(localBounds));
            drawing.PushTransform(new MatrixTransform(physicalToLocal));
            drawing.DrawDrawing(background);
            drawing.Pop();
            drawing.Pop();
        }

        internal static IntPtr FindLiveHost()
        {
            return FindWallpaperEngine(WindowsDesktop.PrimaryWorkArea(), true);
        }

        private static IntPtr FindWallpaperEngine(WindowsDesktop.Rect monitor, bool requireExclusiveHost)
        {
            IntPtr shell = GetShellWindow();
            uint shellProcess;
            if (GetWindowThreadProcessId(shell, out shellProcess) == 0)
                return IntPtr.Zero;
            if (!requireExclusiveHost && ContainsWallpaperEngine(shell, monitor))
                return shell;

            IntPtr worker = IntPtr.Zero;
            while ((worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
            {
                uint process;
                if (GetWindowThreadProcessId(worker, out process) == 0 || process != shellProcess)
                    continue;

                if (!requireExclusiveHost)
                {
                    if (ContainsWallpaperEngine(worker, monitor))
                        return worker;
                    continue;
                }

                IntPtr renderer = FindWindowEx(worker, IntPtr.Zero, WallpaperEngineRenderer, null);
                if (renderer != IntPtr.Zero && IsWindowVisible(renderer) && Covers(renderer, monitor) &&
                    Covers(worker, monitor) && IsExclusiveWallpaperHost(worker, renderer))
                    return worker;
            }
            return IntPtr.Zero;
        }

        private static bool ContainsWallpaperEngine(IntPtr host, WindowsDesktop.Rect monitor)
        {
            bool found = false;
            var name = new StringBuilder(64);
            EnumChildWindows(host, (window, data) =>
            {
                WindowsDesktop.Rect bounds;
                found = GetClassName(window, name, name.Capacity) != 0 && name.ToString() == WallpaperEngineRenderer &&
                    IsWindowVisible(window) && WindowsDesktop.GetWindowRect(window, out bounds) &&
                    bounds.Left < monitor.Right && bounds.Right > monitor.Left &&
                    bounds.Top < monitor.Bottom && bounds.Bottom > monitor.Top;
                return !found;
            }, IntPtr.Zero);
            return found;
        }

        private static bool Covers(IntPtr window, WindowsDesktop.Rect required)
        {
            WindowsDesktop.Rect bounds;
            return WindowsDesktop.GetWindowRect(window, out bounds) &&
                bounds.Left <= required.Left && bounds.Top <= required.Top &&
                bounds.Right >= required.Right && bounds.Bottom >= required.Bottom;
        }

        private static bool IsExclusiveWallpaperHost(IntPtr worker, IntPtr renderer)
        {
            return FindWindowEx(worker, IntPtr.Zero, null, null) == renderer &&
                FindWindowEx(worker, renderer, null, null) == IntPtr.Zero &&
                FindWindowEx(renderer, IntPtr.Zero, null, null) == IntPtr.Zero;
        }

        private static BitmapSource LoadImage(string path)
        {
            Uri file;
            if (!Uri.TryCreate(path, UriKind.Absolute, out file) || !file.IsFile || file.IsUnc)
                return null;
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0)
                    return null;
                BitmapSource image = decoder.Frames[0];
                image.Freeze();
                return image;
            }
        }

        private static Rect ImageBounds(Rect canvas, Position position, double width, double height)
        {
            if (position == Position.Stretch)
                return canvas;
            if (position != Position.Center)
            {
                double horizontal = canvas.Width / width, vertical = canvas.Height / height;
                double scale = position == Position.Fit ? Math.Min(horizontal, vertical) : Math.Max(horizontal, vertical);
                width = Math.Max(1, Math.Round(width * scale));
                height = Math.Max(1, Math.Round(height * scale));
            }
            return new Rect(canvas.X + Math.Truncate((canvas.Width - width) / 2),
                canvas.Y + Math.Truncate((canvas.Height - height) / 2), width, height);
        }

        private static Rect ToRect(WindowsDesktop.Rect bounds)
        {
            return new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }
    }
}
