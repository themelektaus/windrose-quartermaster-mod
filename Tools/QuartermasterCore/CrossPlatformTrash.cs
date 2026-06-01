using System;
using System.IO;

namespace Windrose.Quartermaster.Core;

#if NET
public static class CrossPlatformTrash
{
    public static void DeleteToTrash(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        if (OperatingSystem.IsWindows())
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
        }
        else
        {
            var trashDir = GetTrashDir();
            var baseName = Path.GetFileName(path);

            var destPath = Path.Combine(trashDir, "files", baseName);
            if (File.Exists(destPath))
            {
                var nameWithoutExt = Path.GetFileNameWithoutExtension(baseName);
                var ext = Path.GetExtension(baseName);
                int counter = 1;
                string candidate;
                do
                {
                    candidate = $"{nameWithoutExt}_{counter}{ext}";
                    destPath = Path.Combine(trashDir, "files", candidate);
                    counter++;
                }
                while (File.Exists(destPath));
            }

            Directory.CreateDirectory(Path.Combine(trashDir, "files"));

            File.Move(path, destPath);

            var infoPath = Path.Combine(trashDir, "info", Path.GetFileName(destPath) + ".trashinfo");
            Directory.CreateDirectory(Path.Combine(trashDir, "info"));

            var deletionDate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.000Z");
            var infoContent = $@"[Trash Info]
Path={Uri.EscapeDataString(path.Replace("\\", "/"))}
DeletionDate={deletionDate}
";
            File.WriteAllText(infoPath, infoContent);
        }
    }

    private static string GetTrashDir()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(dataHome))
            return Path.Combine(dataHome, "Trash");
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", "Trash");
    }
}
#endif