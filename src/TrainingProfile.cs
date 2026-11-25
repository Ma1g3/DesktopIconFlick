using System;
using System.Globalization;
using System.IO;
using System.Security;

namespace DesktopIconFlick
{
    internal sealed class TrainingProfile
    {
        private readonly string path;
        public AimSettings Aim { get; private set; }
        public bool UseIcons { get; set; }
        public bool PlayHitSound { get; set; }
        public int Best { get; private set; }
        public string Notice { get; private set; }

        public TrainingProfile(string path)
        {
            this.path = path;
            Aim = AimSettings.Default;
            UseIcons = true;
            PlayHitSound = true;
            Notice = "";
        }

        public static TrainingProfile Load(string path)
        {
            var profile = new TrainingProfile(path);
            try
            {
                string[] fields = File.ReadAllLines(path);
                AimSettings aim;
                bool icons;
                int best;
                if ((fields.Length < 5 || fields.Length > 7) || !AimSettings.TryRead(fields[0], fields[1], fields[2], out aim) ||
                    !bool.TryParse(fields[3], out icons) ||
                    !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out best))
                    throw new InvalidDataException();
                profile.Aim = aim;
                profile.UseIcons = icons;
                profile.Best = best;
                bool sound;
                if (fields.Length >= 6 && bool.TryParse(fields[5], out sound))
                    profile.PlayHitSound = sound;
                if (fields.Length == 7)
                {
                    AimSettings localAim;
                    if (aim.TryReadLocal(fields[6], fields[2], out localAim))
                        profile.Aim = localAim;
                    else
                        profile.Notice = "局内 DPI 读取失败，已按保存的校准值恢复。";
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception error)
            {
                if (!IsStorageError(error))
                    throw;
                profile.Notice = "无法读取本地设置，已使用默认值。";
            }
            return profile;
        }

        public bool SetAim(string dpi, string distance, string fov)
        {
            AimSettings aim;
            if (!AimSettings.TryRead(dpi, distance, fov, out aim))
                return false;
            Aim = aim;
            return true;
        }

        public bool SetLocalAim(string localDpi, string fov)
        {
            if (localDpi == Aim.LocalDpiText)
                localDpi = Aim.LocalDpi.ToString("R", CultureInfo.InvariantCulture);
            AimSettings aim;
            if (!Aim.TryReadLocal(localDpi, fov, out aim))
                return false;
            Aim = aim;
            return true;
        }

        public void RecordCompletedRound(int hits)
        {
            Best = Math.Max(Best, hits);
            Save();
        }

        public void Save()
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(temporary, new[] {
                    Aim.Dpi.ToString(CultureInfo.InvariantCulture),
                    Aim.Distance.ToString("R", CultureInfo.InvariantCulture),
                    Aim.HorizontalFov.ToString("R", CultureInfo.InvariantCulture),
                    UseIcons.ToString(),
                    Best.ToString(CultureInfo.InvariantCulture),
                    PlayHitSound.ToString(),
                    Aim.LocalDpi.ToString("R", CultureInfo.InvariantCulture)
                });
                if (File.Exists(path))
                    File.Replace(temporary, path, null);
                else
                    File.Move(temporary, path);
                Notice = "";
            }
            catch (Exception error)
            {
                if (!IsStorageError(error))
                    throw;
                Notice = "无法保存本地记录，本次成绩仍可查看。";
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception error) { if (!IsStorageError(error)) throw; }
            }
        }

        private static bool IsStorageError(Exception error)
        {
            return error is IOException || error is InvalidDataException ||
                   error is UnauthorizedAccessException || error is SecurityException;
        }
    }
}
