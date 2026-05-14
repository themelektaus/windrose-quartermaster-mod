using System;
using System.IO;

namespace Windrose.Quartermaster.Core;

#if NET
// Cross-platform trash/delete helper. Uses the Windows recycle bin on
// Windows; falls back to the freedesktop.org XDG trash spec on Linux/macOS.
public static class CrossPlatformTrash
{
    public static void DeleteToTrash(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        if (OperatingSystem.IsWindows())
        {
            // Windows: use the proven VB runtime path.
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
        }
        else
        {
            // Linux/macOS: use XDG Trash spec.
            // Files go to $XDG_DATA_HOME/Trash or $HOME/.local/share/Trash
            var trashDir = GetTrashDir();
            var baseName = Path.GetFileName(path);

            // Deduplicate: if a file with the same name already exists in
            // trash, append a counter before the extension.
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

            // Ensure the 'files' subdir exists.
            Directory.CreateDirectory(Path.Combine(trashDir, "files"));

            // Move to trash.
            File.Move(path, destPath);

            // Write a .trashinfo entry (freedesktop.org spec).
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