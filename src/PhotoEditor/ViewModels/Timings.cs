using System;
using System.IO;

namespace PhotoEditor.ViewModels;

/// <summary>
/// Appends timing lines to %LOCALAPPDATA%\PhotoEditor\timings.log (kept under ~1 MB), so slow spots on a real PC
/// can be found. Never throws.
/// </summary>
public static class Timings
{
    private static readonly object Lock = new();

    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoEditor", "timings.log");

    public static void Log(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Timing is only a diagnostic.
        }
    }
}
