using System;
using System.IO;
using System.Security;
using System.Windows;

namespace DesktopIconFlick
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            string storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopIconFlick");
            Action<string> report = null;
            if (Array.IndexOf(args, "--measure") >= 0)
                report = line => AppendMeasurement(storage, line);
            var app = new Application();
            app.Run(new TrainingWindow(Path.Combine(storage, "profile.txt"), report));
        }

        private static void AppendMeasurement(string storage, string line)
        {
            try
            {
                Directory.CreateDirectory(storage);
                File.AppendAllText(Path.Combine(storage, "measurements.log"), line + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
        }
    }
}
