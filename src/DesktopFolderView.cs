using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DesktopIconFlick
{
    internal static class DesktopFolderView
    {
        private const uint DesktopParsingName = 0x80028000;
        private static readonly Guid ShellWindowsClass = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
        private static readonly Guid TopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProvider
        {
            [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
        }

        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellBrowser
        {
            // Keep the native vtable order through QueryActiveShellView.
            [PreserveSig] int GetWindow(out IntPtr window);
            [PreserveSig] int ContextSensitiveHelp(int enterMode);
            [PreserveSig] int InsertMenusSB(IntPtr menu, IntPtr groupWidths);
            [PreserveSig] int SetMenuSB(IntPtr menu, IntPtr holeMenu, IntPtr activeWindow);
            [PreserveSig] int RemoveMenusSB(IntPtr menu);
            [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string text);
            [PreserveSig] int EnableModelessSB(int enable);
            [PreserveSig] int TranslateAcceleratorSB(IntPtr message, ushort command);
            [PreserveSig] int BrowseObject(IntPtr item, uint flags);
            [PreserveSig] int GetViewStateStream(uint mode, out IntPtr stream);
            [PreserveSig] int GetControlWindow(uint id, out IntPtr window);
            [PreserveSig] int SendControlMsg(uint id, uint message, UIntPtr wParam, IntPtr lParam, out IntPtr result);
            [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object view);
        }

        [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFolderView
        {
            [PreserveSig] int GetCurrentViewMode(out uint mode);
            [PreserveSig] int SetCurrentViewMode(uint mode);
            [PreserveSig] int GetFolder(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object folder);
            [PreserveSig] int Item(int index, out IntPtr item);
            [PreserveSig] int ItemCount(uint flags, out int count);
        }

        [ComImport, Guid("1AC3D9F0-175C-11D1-95BE-00609797EA4F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFolder2
        {
            [PreserveSig] int GetClassID(out Guid classId);
            [PreserveSig] int Initialize(IntPtr folder);
            [PreserveSig] int GetCurFolder(out IntPtr folder);
        }

        [DllImport("shell32.dll")]
        private static extern IntPtr ILCombine(IntPtr parent, IntPtr child);

        [DllImport("shell32.dll")]
        private static extern int SHGetNameFromIDList(IntPtr item, uint format, out IntPtr name);

        internal static string ParsingName(IntPtr item)
        {
            if (item == IntPtr.Zero)
                return null;
            IntPtr name = IntPtr.Zero;
            try { return SHGetNameFromIDList(item, DesktopParsingName, out name) == 0 ? Marshal.PtrToStringUni(name) : null; }
            finally { Marshal.FreeCoTaskMem(name); }
        }

        internal static ISet<string> ReadParsingNames()
        {
            const int desktopLocation = 0, desktopClass = 8, requireDispatch = 1;
            const uint allViewItems = 2;
            object windows = null, desktopWindow = null, browser = null, view = null, folder = null;
            IntPtr parent = IntPtr.Zero;
            try
            {
                windows = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClass, true));
                object[] arguments = { desktopLocation, null, desktopClass, 0, requireDispatch };
                var byReference = new ParameterModifier(arguments.Length);
                byReference[0] = byReference[1] = byReference[3] = true;
                desktopWindow = windows.GetType().InvokeMember("FindWindowSW", BindingFlags.InvokeMethod, null,
                    windows, arguments, new[] { byReference }, null, null);
                var services = desktopWindow as IServiceProvider;
                Guid service = TopLevelBrowser, browserInterface = typeof(IShellBrowser).GUID;
                if (services == null || services.QueryService(ref service, ref browserInterface, out browser) != 0 || browser == null ||
                    ((IShellBrowser)browser).QueryActiveShellView(out view) != 0 || view == null)
                    return null;

                var folderView = (IFolderView)view;
                Guid folderInterface = typeof(IPersistFolder2).GUID;
                int count;
                if (folderView.GetFolder(ref folderInterface, out folder) != 0 || folder == null ||
                    ((IPersistFolder2)folder).GetCurFolder(out parent) != 0 || parent == IntPtr.Zero ||
                    folderView.ItemCount(allViewItems, out count) != 0)
                    return null;

                var names = new HashSet<string>(StringComparer.Ordinal);
                for (int index = 0; index < count; index++)
                {
                    IntPtr item = IntPtr.Zero, fullItem = IntPtr.Zero;
                    try
                    {
                        if (folderView.Item(index, out item) != 0 || item == IntPtr.Zero)
                            return null;
                        fullItem = ILCombine(parent, item);
                        string name = ParsingName(fullItem);
                        if (name != null)
                            names.Add(name);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(fullItem);
                        Marshal.FreeCoTaskMem(item);
                    }
                }
                return names;
            }
            catch (COMException) { return null; }
            catch (TargetInvocationException) { return null; }
            catch (InvalidCastException) { return null; }
            catch (MissingMethodException) { return null; }
            finally
            {
                Marshal.FreeCoTaskMem(parent);
                Release(folder);
                Release(view);
                Release(browser);
                Release(desktopWindow);
                Release(windows);
            }
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value))
                Marshal.ReleaseComObject(value);
        }
    }
}
