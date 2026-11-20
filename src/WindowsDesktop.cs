using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopIconFlick
{
    internal static class WindowsDesktop
    {
        public const int InputMessage = 0x00FF, DisplayChange = 0x007E, DpiChange = 0x02E0, SettingChange = 0x001A;
        public const int WorkAreaChange = 0x002F;
        private const uint RemoveDevice = 1, RawInputData = 0x10000003;
        private const ushort Absolute = 1, LeftDown = 1, RightDown = 4;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect : IEquatable<Rect>
        {
            public int Left, Top, Right, Bottom;
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
            public bool Equals(Rect other)
            {
                return Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public Rect Monitor, WorkArea;
            public int Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawDevice
        {
            public ushort Page, Usage;
            public uint Flags;
            public IntPtr Window;
        }

        [StructLayout(LayoutKind.Explicit, Size = 48)]
        private struct RawMouse
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(24)] public ushort MoveFlags;
            [FieldOffset(28)] public ushort ButtonFlags;
            [FieldOffset(36)] public int X;
            [FieldOffset(40)] public int Y;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(long point, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int left, int top, int width, int height, uint flags);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr device);

        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr device, int index);

        [DllImport("user32.dll")]
        private static extern bool RegisterRawInputDevices(ref RawDevice device, uint count, uint size);

        [DllImport("user32.dll")]
        private static extern uint GetRawInputData(IntPtr input, uint command, out RawMouse mouse, ref uint size, uint header);

        [DllImport("user32.dll", EntryPoint = "ClipCursor")]
        private static extern bool ClipRect(ref Rect rect);

        [DllImport("user32.dll", EntryPoint = "ClipCursor")]
        private static extern bool ClipCursor(IntPtr rect);

        [DllImport("user32.dll")]
        internal static extern bool GetClipCursor(out Rect rect);

        public static Rect PrimaryWorkArea()
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (!GetMonitorInfo(MonitorFromPoint(0, 1), ref info))
                throw new InvalidOperationException("无法读取主显示器工作区。");
            return info.WorkArea;
        }

        public static Rect VirtualScreen()
        {
            int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
            return new Rect { Left = left, Top = top, Right = left + GetSystemMetrics(78), Bottom = top + GetSystemMetrics(79) };
        }

        public static int MonitorCount()
        {
            const int monitorCountMetric = 80;
            return GetSystemMetrics(monitorCountMetric);
        }

        public static int PrimaryRefreshRate()
        {
            const int refreshCapability = 116;
            IntPtr primaryDevice = GetDC(IntPtr.Zero);
            if (primaryDevice == IntPtr.Zero)
                return 0;
            try
            {
                int refresh = GetDeviceCaps(primaryDevice, refreshCapability);
                return refresh > 1 ? refresh : 0;
            }
            finally { ReleaseDC(IntPtr.Zero, primaryDevice); }
        }

        public static void Place(IntPtr window, Rect bounds)
        {
            if (!SetWindowPos(window, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x14))
                throw new InvalidOperationException("无法设置训练窗口位置。");
        }

        public static bool RegisterMouse(IntPtr window)
        {
            var device = new RawDevice { Page = 1, Usage = 2, Flags = window == IntPtr.Zero ? RemoveDevice : 0, Window = window };
            return RegisterRawInputDevices(ref device, 1, (uint)Marshal.SizeOf(typeof(RawDevice)));
        }

        public static bool ReadMouse(IntPtr input, out int horizontal, out int vertical, out bool pressed)
        {
            uint size = 48;
            RawMouse mouse;
            bool valid = GetRawInputData(input, RawInputData, out mouse, ref size, 24) == 48 && mouse.Type == 0;
            if (valid)
                DecodeMouse(mouse.X, mouse.Y, mouse.MoveFlags, mouse.ButtonFlags, GetSystemMetrics(23) != 0,
                    out horizontal, out vertical, out pressed);
            else
            {
                horizontal = vertical = 0;
                pressed = false;
            }
            return valid;
        }

        internal static void DecodeMouse(int rawX, int rawY, ushort moveFlags, ushort buttonFlags, bool swapped,
            out int horizontal, out int vertical, out bool pressed)
        {
            bool relative = (moveFlags & Absolute) == 0;
            horizontal = relative ? rawX : 0;
            vertical = relative ? rawY : 0;
            pressed = (buttonFlags & (swapped ? RightDown : LeftDown)) != 0;
        }

        public static bool Confine(IntPtr window, out Rect clip)
        {
            Rect bounds;
            clip = new Rect();
            if (!GetWindowRect(window, out bounds) || bounds.Width < 2 || bounds.Height < 2)
                return false;
            clip.Left = bounds.Left + bounds.Width / 2;
            clip.Top = bounds.Top + bounds.Height / 2;
            clip.Right = clip.Left + 1;
            clip.Bottom = clip.Top + 1;
            return ClipRect(ref clip);
        }

        public static void ReleaseCursor()
        {
            ClipCursor(IntPtr.Zero);
        }

    }

    internal sealed class DesktopIcon
    {
        public readonly ImageSource Image;
        public readonly Rect DesktopBounds;
        public readonly string DesktopName;

        public DesktopIcon(ImageSource image, Rect desktopBounds, string desktopName = null)
        {
            Image = image;
            DesktopBounds = desktopBounds;
            DesktopName = desktopName;
        }
    }

    internal static class ShellIcons
    {
        private const int IconLimit = 64;

        private delegate bool EnumWindowCallback(IntPtr window, IntPtr data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfo
        {
            public IntPtr Icon;
            public int Index;
            public uint Attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string Type;
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetIDListFromObject([MarshalAs(UnmanagedType.IUnknown)] object shellItem, out IntPtr item);

        [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(IntPtr item, uint attributes, out FileInfo info, uint size, uint flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        public static void LoadAsync(Action<IList<DesktopIcon>> done)
        {
            var worker = new Thread(() => done(Load())) { IsBackground = true };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        internal static IList<DesktopIcon> Load()
        {
            var icons = new List<DesktopIcon>();
            ReadDesktopNamespace(icons, ReadDesktopItemBounds());
            if (icons.Count == 0)
            {
                ReadDesktopDirectory(Environment.SpecialFolder.DesktopDirectory, icons);
                ReadDesktopDirectory(Environment.SpecialFolder.CommonDesktopDirectory, icons);
            }
            return icons;
        }

        internal static IntPtr DesktopViewWindow()
        {
            IntPtr listView = FindDesktopList(GetShellWindow());
            if (listView != IntPtr.Zero)
                return listView;
            var className = new StringBuilder(64);
            EnumWindows((window, data) =>
            {
                if (GetClassName(window, className, className.Capacity) != 0 && className.ToString() == "WorkerW")
                    listView = FindDesktopList(window);
                return listView == IntPtr.Zero;
            }, IntPtr.Zero);
            return listView;
        }

        private static IntPtr FindDesktopList(IntPtr host)
        {
            IntPtr view = FindVisibleChild(host, "SHELLDLL_DefView");
            return FindVisibleChild(view, "SysListView32");
        }

        private static IntPtr FindVisibleChild(IntPtr parent, string requiredClass)
        {
            if (parent == IntPtr.Zero)
                return IntPtr.Zero;
            IntPtr match = IntPtr.Zero;
            var className = new StringBuilder(64);
            EnumChildWindows(parent, (window, data) =>
            {
                if (GetClassName(window, className, className.Capacity) != 0 &&
                    className.ToString() == requiredClass && IsWindowVisible(window))
                    match = window;
                return match == IntPtr.Zero;
            }, IntPtr.Zero);
            return match;
        }

        internal static Dictionary<string, Rect> ReadDesktopItemBounds()
        {
            var bounds = new Dictionary<string, Rect>(StringComparer.Ordinal);
            IntPtr listView = DesktopViewWindow();
            if (listView == IntPtr.Zero)
                return bounds;
            try
            {
                AutomationElement desktop = AutomationElement.FromHandle(listView);
                var cache = new CacheRequest { AutomationElementMode = AutomationElementMode.None };
                cache.Add(AutomationElement.NameProperty);
                cache.Add(AutomationElement.BoundingRectangleProperty);
                cache.Add(AutomationElement.IsOffscreenProperty);
                var visibleItems = new List<KeyValuePair<string, Rect>>();
                using (cache.Activate())
                {
                    var items = desktop.FindAll(TreeScope.Children,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                    foreach (AutomationElement item in items)
                    {
                        var properties = item.Cached;
                        string name = properties.Name;
                        Rect rectangle = properties.BoundingRectangle;
                        bool visible = !properties.IsOffscreen && rectangle.Width > 0 && rectangle.Height > 0;
                        if (visible)
                            visibleItems.Add(new KeyValuePair<string, Rect>(name, rectangle));
                        if (!string.IsNullOrEmpty(name))
                            bounds[name] = visible && !bounds.ContainsKey(name) ? rectangle : Rect.Empty;
                    }
                }
                ExcludeOverlappingBounds(bounds, visibleItems);
            }
            catch (ElementNotAvailableException) { bounds.Clear(); }
            catch (COMException) { bounds.Clear(); }
            catch (InvalidOperationException) { bounds.Clear(); }
            catch (ArgumentException) { bounds.Clear(); }
            return bounds;
        }

        private static void ExcludeOverlappingBounds(Dictionary<string, Rect> bounds, List<KeyValuePair<string, Rect>> visibleItems)
        {
            for (int index = 0; index < visibleItems.Count; index++)
            {
                var item = visibleItems[index];
                for (int next = index + 1; next < visibleItems.Count; next++)
                {
                    var other = visibleItems[next];
                    Rect overlap = Rect.Intersect(item.Value, other.Value);
                    if (overlap.Width <= 0 || overlap.Height <= 0)
                        continue;
                    if (!string.IsNullOrEmpty(item.Key))
                        bounds[item.Key] = Rect.Empty;
                    if (!string.IsNullOrEmpty(other.Key))
                        bounds[other.Key] = Rect.Empty;
                }
            }
        }

        private static void ReadDesktopNamespace(List<DesktopIcon> icons, IDictionary<string, Rect> desktopBounds)
        {
            object shell = null, desktop = null, items = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null)
                    return;
                shell = Activator.CreateInstance(shellType);
                desktop = Invoke(shell, "NameSpace", BindingFlags.InvokeMethod, 0);
                if (desktop == null)
                    return;
                items = Invoke(desktop, "Items", BindingFlags.InvokeMethod);
                if (items == null)
                    return;
                int count = (int)Invoke(items, "Count", BindingFlags.GetProperty);
                var excludedAliases = new HashSet<int>();
                var matches = MatchDesktopItems(items, count, desktopBounds, excludedAliases);
                for (int index = 0; index < count && icons.Count < IconLimit; index++)
                {
                    string name;
                    if (matches.TryGetValue(index, out name))
                        ReadDesktopItem(icons, items, index, desktopBounds[name], name);
                }
                for (int index = 0; index < count && icons.Count < IconLimit; index++)
                {
                    if (!matches.ContainsKey(index) && !excludedAliases.Contains(index))
                        ReadDesktopItem(icons, items, index, Rect.Empty, null);
                }
            }
            catch (COMException) { }
            catch (TargetInvocationException) { }
            finally
            {
                Release(items);
                Release(desktop);
                Release(shell);
            }
        }

        private static Dictionary<int, string> MatchDesktopItems(object items, int count, IDictionary<string, Rect> desktopBounds,
            ISet<int> excludedAliases)
        {
            var matches = new Dictionary<int, string>();
            if (desktopBounds.Count == 0)
                return matches;

            var indices = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int index = 0; index < count; index++)
            {
                object item = null;
                try
                {
                    item = Invoke(items, "Item", BindingFlags.InvokeMethod, index);
                    string name = item == null ? null : Invoke(item, "Name", BindingFlags.GetProperty) as string;
                    Rect bounds;
                    if (string.IsNullOrEmpty(name) || !desktopBounds.TryGetValue(name, out bounds) || bounds.IsEmpty)
                        continue;
                    List<int> candidates;
                    if (!indices.TryGetValue(name, out candidates))
                    {
                        candidates = new List<int>();
                        indices.Add(name, candidates);
                    }
                    candidates.Add(index);
                }
                catch (COMException) { }
                catch (TargetInvocationException) { }
                finally { Release(item); }
            }
            var visibleItems = new Lazy<ISet<string>>(DesktopFolderView.ReadParsingNames);
            foreach (var group in indices)
            {
                List<int> candidates = group.Value;
                int selected = candidates[0];
                if (candidates.Count > 1)
                {
                    selected = FindUniqueViewItem(candidates, visibleItems.Value, index => ReadParsingName(items, index));
                    if (selected < 0)
                        continue;
                    foreach (int alias in candidates)
                        if (alias != selected)
                            excludedAliases.Add(alias);
                }
                matches.Add(selected, group.Key);
            }
            return matches;
        }

        internal static int FindUniqueViewItem(IList<int> candidates, ISet<string> visibleItems, Func<int, string> readParsingName)
        {
            if (visibleItems == null)
                return -1;
            int selected = -1;
            string selectedIdentity = null;
            foreach (int index in candidates)
            {
                string identity = readParsingName(index);
                if (identity == null || !visibleItems.Contains(identity))
                    continue;
                if (selected >= 0)
                {
                    if (!string.Equals(selectedIdentity, identity, StringComparison.Ordinal))
                        return -1;
                    continue;
                }
                selected = index;
                selectedIdentity = identity;
            }
            return selected;
        }

        private static string ReadParsingName(object items, int index)
        {
            object item = null;
            IntPtr pidl = IntPtr.Zero;
            try
            {
                item = Invoke(items, "Item", BindingFlags.InvokeMethod, index);
                return item != null && SHGetIDListFromObject(item, out pidl) >= 0 ? DesktopFolderView.ParsingName(pidl) : null;
            }
            catch (COMException) { return null; }
            catch (TargetInvocationException) { return null; }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
                Release(item);
            }
        }

        private static void ReadDesktopItem(List<DesktopIcon> icons, object items, int index, Rect bounds, string name)
        {
            object item = null;
            IntPtr id = IntPtr.Zero;
            try
            {
                item = Invoke(items, "Item", BindingFlags.InvokeMethod, index);
                if (item == null)
                    return;
                if (name != null && !string.Equals(name, Invoke(item, "Name", BindingFlags.GetProperty) as string, StringComparison.Ordinal))
                {
                    name = null;
                    bounds = Rect.Empty;
                }
                ImageSource image = null;
                if (SHGetIDListFromObject(item, out id) >= 0 && id != IntPtr.Zero)
                    image = ReadIcon(id);
                if (image == null)
                {
                    string path = Invoke(item, "Path", BindingFlags.GetProperty) as string;
                    if (!string.IsNullOrEmpty(path))
                        image = ReadIcon(path);
                }
                if (image != null)
                    icons.Add(new DesktopIcon(image, bounds, name));
            }
            catch (COMException) { }
            catch (TargetInvocationException) { }
            finally
            {
                if (id != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(id);
                Release(item);
            }
        }

        private static void ReadDesktopDirectory(Environment.SpecialFolder folder, List<DesktopIcon> icons)
        {
            try
            {
                string directory = Environment.GetFolderPath(folder);
                if (directory.Length == 0)
                    return;
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (icons.Count >= IconLimit)
                        break;
                    try
                    {
                        if ((File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                            continue;
                        ImageSource image = ReadIcon(path);
                        if (image != null)
                            icons.Add(new DesktopIcon(image, Rect.Empty));
                    }
                    catch (Exception error)
                    {
                        if (!CanSkipItem(error))
                            throw;
                    }
                }
            }
            catch (Exception error)
            {
                if (!CanSkipItem(error))
                    throw;
            }
        }

        private static bool CanSkipItem(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException ||
                   error is SecurityException || error is COMException;
        }

        private static ImageSource ReadIcon(IntPtr item)
        {
            FileInfo info;
            if (SHGetFileInfo(item, 0, out info, (uint)Marshal.SizeOf(typeof(FileInfo)), 0x108) == IntPtr.Zero)
                return null;
            return CopyIcon(info.Icon);
        }

        internal static ImageSource ReadIcon(string path)
        {
            IntPtr buffer = Marshal.StringToCoTaskMemUni(path);
            try
            {
                FileInfo info;
                return SHGetFileInfo(buffer, 0, out info, (uint)Marshal.SizeOf(typeof(FileInfo)), 0x100) != IntPtr.Zero ?
                    CopyIcon(info.Icon) : null;
            }
            finally { Marshal.FreeCoTaskMem(buffer); }
        }

        private static ImageSource CopyIcon(IntPtr icon)
        {
            if (icon == IntPtr.Zero)
                return null;
            try
            {
                BitmapSource image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            finally { DestroyIcon(icon); }
        }

        private static object Invoke(object target, string name, BindingFlags flags, params object[] arguments)
        {
            return target.GetType().InvokeMember(name, flags, null, target, arguments);
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value))
                Marshal.ReleaseComObject(value);
        }
    }
}
