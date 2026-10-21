using System;
using System.IO;

namespace DanmuFree.App.Services;

/// <summary>
/// Append-only file logger writing to <c>%AppData%/DanmuFree/log.txt</c>.
/// All writes are serialized through an internal lock so the logger is safe
/// to call from multiple threads.
/// </summary>
public sealed class FileLogger
{
    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DanmuFree",
        "log.txt");

    private static readonly object Gate = new();

    public void Info(string message) => Write("INFO ", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message} | {exception}");

    private void Write(string level, string message)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= 2 * 1024 * 1024)
                File.Move(LogPath, LogPath + ".1", overwrite: true);
            File.AppendAllText(
                LogPath,
                $"{DateTime.Now:HH:mm:ss} [{level}] {message}{Environment.NewLine}");
        }
    }
}
