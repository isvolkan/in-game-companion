using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using InGameCompanion.Native;

namespace InGameCompanion.Ui;

/// <summary>Sistem tepsisi ikonu + sağ tık menüsü (gizli yardımcı pencere üzerinden).</summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint WM_TRAY = Win32.WM_APP + 10;
    private const string ClassName = "InGameCompanionTray";

    private static Win32.WndProc? _wndProc;
    private static TrayIcon? _instance;

    private readonly IntPtr _hwnd;
    private readonly uint _taskbarCreated;
    private readonly List<(string? text, Action? action)> _items = new();
    private string _tooltip;
    private readonly IntPtr _icon;

    public event Action? DoubleClicked;

    public TrayIcon(string tooltip)
    {
        _instance = this;
        _tooltip = tooltip;
        _wndProc = StaticWndProc;
        var hInst = Win32.GetModuleHandleW(null);
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInst,
            lpszClassName = ClassName,
        };
        Win32.RegisterClassExW(ref wc);
        _hwnd = Win32.CreateWindowExW(Win32.WS_EX_TOOLWINDOW, ClassName, "InGameCompanionTray", Win32.WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        _taskbarCreated = Win32.RegisterWindowMessageW("TaskbarCreated");
        _icon = Win32.LoadIconW(IntPtr.Zero, Win32.IDI_INFORMATION);
        Add();
    }

    public void AddMenuItem(string text, Action action) => _items.Add((text, action));
    public void AddSeparator() => _items.Add((null, null));

    public void SetTooltip(string text)
    {
        _tooltip = text;
        var d = Data(Win32.NIF_TIP);
        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref d);
    }

    private Win32.NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_TRAY,
        hIcon = _icon,
        szTip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private void Add()
    {
        var d = Data(Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP);
        Win32.Shell_NotifyIconW(Win32.NIM_ADD, ref d);
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = _instance;
        if (self != null && hwnd == self._hwnd)
        {
            if (msg == WM_TRAY)
            {
                int ev = Win32.LoWord(lParam);
                if (ev == Win32.WM_RBUTTONUP || ev == Win32.WM_CONTEXTMENU || ev == Win32.WM_LBUTTONUP) self.ShowMenu();
                else if (ev == Win32.WM_LBUTTONDBLCLK) self.DoubleClicked?.Invoke();
                return IntPtr.Zero;
            }
            if (msg == self._taskbarCreated && msg != 0)
            {
                self.Add(); // Explorer yeniden başladı
                return IntPtr.Zero;
            }
        }
        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = Win32.CreatePopupMenu();
        for (int i = 0; i < _items.Count; i++)
        {
            var (text, _) = _items[i];
            if (text == null) Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, UIntPtr.Zero, null);
            else Win32.AppendMenuW(menu, Win32.MF_STRING, (UIntPtr)(uint)(i + 1), text);
        }
        Win32.GetCursorPos(out var pt);
        Win32.SetForegroundWindow(_hwnd); // menü dışına tıklanınca kapanması için gerekli
        int cmd = Win32.TrackPopupMenu(menu, Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD | Win32.TPM_NONOTIFY,
            pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        Win32.PostMessageW(_hwnd, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        Win32.DestroyMenu(menu);
        if (cmd > 0 && cmd <= _items.Count) _items[cmd - 1].action?.Invoke();
    }

    public void Dispose()
    {
        var d = Data(0);
        Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref d);
        Win32.DestroyWindow(_hwnd);
    }
}
