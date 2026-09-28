using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using InGameCompanion.Native;

namespace InGameCompanion.Core;

internal sealed record GameContext(
    IntPtr Hwnd,
    string ProcessName,
    string WindowTitle,
    string GameName,
    bool IsKnown,
    string? ProfileText,
    string? ProgressNote);

/// <summary>Ön plandaki pencereden hangi oyunun oynandığını bulur.</summary>
internal sealed class GameDetector
{
    private readonly Func<Settings> _settings;
    private readonly ConcurrentDictionary<uint, string> _pidNames = new();
    private readonly ConcurrentDictionary<string, string?> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly uint _selfPid = (uint)Environment.ProcessId;

    public GameDetector(Func<Settings> settings) => _settings = settings;

    public void ClearCache() { _profiles.Clear(); }

    /// <summary>Hızlı yol (hook içinde çağrılır): ön plan penceresi bilinen bir oyuna mı ait?</summary>
    public bool IsKnownGameForeground()
    {
        var name = GetProcessName(Win32.GetForegroundWindow(), out _);
        return name != null && _settings().Games.ContainsKey(name);
    }

    public GameContext? Detect(IntPtr hwnd)
    {
        var proc = GetProcessName(hwnd, out uint pid);
        if (proc == null || pid == _selfPid) return null;

        var title = GetTitle(hwnd);
        var s = _settings();
        if (s.Games.TryGetValue(proc, out var entry) && !string.IsNullOrWhiteSpace(entry.Name))
        {
            return new GameContext(hwnd, proc, title, entry.Name, true, LoadProfile(entry.Profile), entry.ProgressNote);
        }
        // Bilinmeyen oyun: pencere başlığını oyun adı olarak kullan
        var guess = string.IsNullOrWhiteSpace(title) ? proc : title;
        return new GameContext(hwnd, proc, title, guess, false, null, null);
    }

    private string? GetProcessName(IntPtr hwnd, out uint pid)
    {
        pid = 0;
        if (hwnd == IntPtr.Zero) return null;
        Win32.GetWindowThreadProcessId(hwnd, out pid);
        if (pid == 0) return null;
        if (_pidNames.TryGetValue(pid, out var cached)) return cached;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            var n = p.ProcessName;
            _pidNames[pid] = n;
            return n;
        }
        catch { return null; }
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var buf = new char[256];
        int n = Win32.GetWindowTextW(hwnd, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : "";
    }

    private string? LoadProfile(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        return _profiles.GetOrAdd(file, f =>
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "games", f);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch { return null; }
        });
    }
}
