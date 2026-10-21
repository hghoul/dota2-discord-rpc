using System.Diagnostics;
using System.Text;

namespace Dota2RPC.Core;

/// <summary>Простой потокобезопасный файловый журнал с ротацией (app.log, app.1.log, app.2.log).</summary>
internal static class Log
{
    const long MaxSize = 1024 * 1024;
    static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppPaths.LogDir, "app.log");

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{Environment.CurrentManagedThreadId,3}] {message}";
        Debug.WriteLine(line);
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                var path = FilePath;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxSize)
                    Rotate(path);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Журнал не должен ронять программу.
            }
        }
    }

    static void Rotate(string path)
    {
        var p2 = Path.ChangeExtension(path, ".2.log");
        var p1 = Path.ChangeExtension(path, ".1.log");
        if (File.Exists(p2)) File.Delete(p2);
        if (File.Exists(p1)) File.Move(p1, p2);
        File.Move(path, p1);
    }
}
