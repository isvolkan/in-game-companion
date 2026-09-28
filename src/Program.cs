using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using InGameCompanion.Ai;
using InGameCompanion.Core;
using InGameCompanion.Input;
using InGameCompanion.Native;
using InGameCompanion.Ui;

namespace InGameCompanion;

internal static class Program
{
    private static Settings _settings = new();

    [STAThread]
    private static int Main()
    {
        using var mutex = new Mutex(true, @"Local\InGameCompanion.Single", out bool first);
        if (!first)
        {
            Win32.MessageBoxW(IntPtr.Zero, "Oyun Asistanı zaten çalışıyor (sistem tepsisine bak).", "Oyun Asistanı", 0x40);
            return 0;
        }

        try { Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Yakalanmamış hata", e.ExceptionObject as Exception);

        try
        {
            _settings = Settings.Load();
        }
        catch (Exception ex)
        {
            Win32.MessageBoxW(IntPtr.Zero, "settings.json okunamadı:\n" + ex.Message, "Oyun Asistanı", 0x10);
            return 1;
        }

        Gdip.Startup();
        Log.Info($"Başlatıldı. Model={_settings.Model}, Hotkey={_settings.Hotkey}, Arama={_settings.UseWebSearch}");

        Settings Current() => Volatile.Read(ref _settings);

        var overlay = new OverlayWindow(Current);
        var panel = new HistoryPanel(Current);
        var detector = new GameDetector(Current);
        var gemini = new GeminiClient(Current);
        var memory = new GameMemoryStore();
        var companion = new Companion(Current, overlay, panel, detector, gemini, memory);

        MouseHook? hook = null;
        void InstallHook()
        {
            hook?.Dispose();
            var s = Current();
            hook = new MouseHook(s.Hotkey, s.SwallowHotkey);
            hook.ShouldHandle = companion.ShouldHandle;
            hook.Pressed += companion.OnPressed;
            hook.Released += companion.OnReleased;
            hook.Start();
        }

        try { InstallHook(); }
        catch (Exception ex)
        {
            Log.Error("Hook", ex);
            Win32.MessageBoxW(IntPtr.Zero, ex.Message, "Oyun Asistanı", 0x10);
            return 1;
        }

        using var tray = new TrayIcon($"Oyun Asistanı — {hook!.ButtonLabel} basılı tut");
        tray.AddMenuItem("Ayarları aç (settings.json)", () => OpenSettings());
        tray.AddMenuItem("Ayarları yeniden yükle", () =>
        {
            try
            {
                Volatile.Write(ref _settings, Settings.Load());
                detector.ClearCache();
                InstallHook();
                tray.SetTooltip($"Oyun Asistanı — {hook!.ButtonLabel} basılı tut");
                overlay.ShowInfo("Ayarlar yüklendi", $"Model: **{Current().Model}** · Tuş: **{hook.ButtonLabel}**", 4);
            }
            catch (Exception ex) { overlay.ShowError("Ayarlar yüklenemedi: " + ex.Message); }
        });
        tray.AddMenuItem("Önceki sorular…", () => companion.OpenHistory());
        tray.AddMenuItem("Hafıza klasörünü aç", () => { Directory.CreateDirectory(GameMemoryStore.Dir); OpenFile(GameMemoryStore.Dir); });
        tray.AddMenuItem("Son oyunun hafızasını sil…", () =>
        {
            var game = companion.LastGameName;
            if (string.IsNullOrEmpty(game))
            {
                overlay.ShowInfo("Hafıza", "Bu oturumda henüz soru sorulmadı. Silmek için hafıza klasöründeki dosyayı da silebilirsin.", 5);
                return;
            }
            const uint MB_YESNO = 0x4, MB_ICONQUESTION = 0x20; const int IDYES = 6;
            if (Win32.MessageBoxW(IntPtr.Zero,
                    $"\"{game}\" için tüm hafıza (notlar, ilerleme, özet, geçmiş) silinsin mi?",
                    "Oyun Asistanı", MB_YESNO | MB_ICONQUESTION) == IDYES)
            {
                memory.Clear(game);
                overlay.ShowInfo("Hafıza silindi", $"**{game}** için hafıza sıfırlandı.", 4);
            }
        });
        tray.AddMenuItem("Log klasörünü aç", () => { Directory.CreateDirectory(Log.Dir); OpenFile(Log.Dir); });
        tray.AddMenuItem("Test: overlay göster", () => overlay.ShowInfo("Test",
            "- Bu kutu tıklamaları **arkaya geçirir**\n- Odağı oyundan **çalmaz**\n- Birkaç saniye sonra kendiliğinden kaybolur", 6));
        tray.AddSeparator();
        tray.AddMenuItem("Çıkış", () => Win32.PostQuitMessage(0));
        tray.DoubleClicked += () => OpenSettings();

        // Başlangıç bilgisi
        if (string.IsNullOrEmpty(Current().ResolvedApiKey))
        {
            overlay.ShowError("Gemini API anahtarı yok. settings.json içine ApiKey yaz, sonra tepsi menüsünden 'Ayarları yeniden yükle'.");
            OpenSettings();
        }
        else
        {
            overlay.ShowInfo("Oyun Asistanı hazır",
                $"Oyunda **{hook.ButtonLabel}** tuşunu basılı tut, sorunu söyle, bırak. Kısa dokunuş kutuyu kapatır, çift dokunuş önceki soruları açar.", 6);
            _ = gemini.WarmUpAsync();
        }

        // Ana mesaj döngüsü (overlay + tepsi)
        while (Win32.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessageW(ref msg);
        }

        hook?.Dispose();
        panel.Dispose();
        overlay.Dispose();
        Gdip.Shutdown();
        Log.Info("Kapatıldı.");
        return 0;
    }

    private static void OpenSettings()
    {
        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Settings.FilePath}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Ayarlar açılamadı", ex); }
    }

    private static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Açılamadı: " + path, ex); }
    }
}
