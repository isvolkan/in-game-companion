using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using InGameCompanion.Core;
using InGameCompanion.Native;

namespace InGameCompanion.Input;

/// <summary>
/// Global low-level fare kancası. Kendi iş parçacığında ve kendi mesaj döngüsünde çalışır;
/// böylece arayüz ya da ağ işleri kancayı asla yavaşlatmaz (Windows yavaş kancaları sessizce söker).
/// Olaylar ThreadPool'a aktarılır, kanca geri çağrısı mikro saniyeler içinde döner.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    public enum Button { XButton1, XButton2, Middle }

    public event Action? Pressed;
    public event Action? Released;

    /// <summary>Basış anında çağrılır; false dönerse bu basış yok sayılır ve oyuna iletilir.</summary>
    public Func<bool>? ShouldHandle { get; set; }

    private readonly Button _button;
    private readonly bool _swallow;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private Win32.HookProc? _proc; // GC'den korumak için alan olarak tutulur
    private bool _down;
    private bool _handlingCurrent;
    // Basış/bırakış sırası korunmalı: tek tüketicili kuyruk
    private readonly BlockingCollection<bool> _events = new();
    private Thread? _dispatcher;

    public MouseHook(string hotkey, bool swallow)
    {
        _button = hotkey.Trim().ToLowerInvariant() switch
        {
            "xbutton1" or "mouse4" => Button.XButton1,
            "middle" or "mbutton" or "mouse3" => Button.Middle,
            _ => Button.XButton2,
        };
        _swallow = swallow;
    }

    public string ButtonLabel => _button switch
    {
        Button.XButton1 => "Mouse 4",
        Button.Middle => "Orta tuş",
        _ => "Mouse 5",
    };

    public void Start()
    {
        _dispatcher = new Thread(() =>
        {
            foreach (var down in _events.GetConsumingEnumerable())
                SafeRaise(down ? Pressed : Released);
        })
        { IsBackground = true, Name = "HotkeyDispatch" };
        _dispatcher.Start();

        var ready = new ManualResetEventSlim();
        Exception? error = null;
        _thread = new Thread(() =>
        {
            _threadId = Win32.GetCurrentThreadId();
            _proc = HookCallback;
            _hook = Win32.SetWindowsHookExW(Win32.WH_MOUSE_LL, _proc, Win32.GetModuleHandleW(null), 0);
            if (_hook == IntPtr.Zero) error = new InvalidOperationException("Fare kancası kurulamadı, hata " + Marshal.GetLastWin32Error());
            ready.Set();
            if (_hook == IntPtr.Zero) return;

            while (Win32.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessageW(ref msg);
            }
            Win32.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        })
        {
            IsBackground = true,
            Name = "MouseHook",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        ready.Wait();
        if (error != null) throw error;
        Log.Info($"Fare kancası kuruldu ({ButtonLabel}, swallow={_swallow})");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            bool? isDown = null;
            if (_button == Button.Middle)
            {
                if (msg == Win32.WM_MBUTTONDOWN) isDown = true;
                else if (msg == Win32.WM_MBUTTONUP) isDown = false;
            }
            else if (msg == Win32.WM_XBUTTONDOWN || msg == Win32.WM_XBUTTONUP)
            {
                var data = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                int which = Win32.HiWord(data.mouseData);
                int wanted = _button == Button.XButton1 ? Win32.XBUTTON1 : Win32.XBUTTON2;
                if (which == wanted) isDown = msg == Win32.WM_XBUTTONDOWN;
            }

            if (isDown == true)
            {
                if (!_down)
                {
                    _down = true;
                    bool handle;
                    try { handle = ShouldHandle?.Invoke() ?? true; } catch { handle = true; }
                    _handlingCurrent = handle;
                    if (handle) _events.Add(true);
                }
                if (_handlingCurrent && _swallow) return new IntPtr(1);
            }
            else if (isDown == false)
            {
                bool wasHandling = _handlingCurrent;
                if (_down)
                {
                    _down = false;
                    _handlingCurrent = false;
                    if (wasHandling) _events.Add(false);
                }
                if (wasHandling && _swallow) return new IntPtr(1);
            }
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static void SafeRaise(Action? a)
    {
        try { a?.Invoke(); }
        catch (Exception ex) { Log.Error("Tuş olayı işlenemedi", ex); }
    }

    public void Dispose()
    {
        if (_threadId != 0) Win32.PostThreadMessageW(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
        _events.CompleteAdding();
    }
}
