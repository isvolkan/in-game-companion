using System;
using System.IO;

namespace InGameCompanion.Core;

internal static class Log
{
    private static readonly object Gate = new();
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "logs");
    private static string FilePath => Path.Combine(Dir, $"companion-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERR ", ex == null ? msg : $"{msg}: {ex}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {level} {msg}{Environment.NewLine}");
            }
        }
        catch { /* log hatası uygulamayı durdurmasın */ }
    }
}
