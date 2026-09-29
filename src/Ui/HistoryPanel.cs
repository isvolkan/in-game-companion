using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using InGameCompanion.Core;
using InGameCompanion.Native;

namespace InGameCompanion.Ui;

/// <summary>
/// Sohbet + önceki sorular paneli. HUD'ın aksine tıklanabilir: açılınca odağı alır (oyun imleci bırakır),
/// altındaki kutuya yazıp Enter ile soru sorulur, tıklamayla cevap açılır/kapanır, tekerlekle kaydırılır.
/// Esc, Mouse 5 ya da dışarı tıklama kapatır; kapanınca odak oyuna geri verilir.
/// Tüm üyeler UI (ana) iş parçacığında çalışır; dışarıdan <see cref="OverlayWindow.Invoke"/> ile çağır.
/// </summary>
internal sealed class HistoryPanel : IDisposable
{
    private const string ClassName = "InGameCompanionHistory";

    private static Win32.WndProc? _wndProc; // GC koruması
    private static HistoryPanel? _instance;

    private readonly Func<Settings> _settings;
    private readonly TextRenderer _text = new();
    private readonly List<TextRenderer.Op> _ops = new();
    private readonly List<TextRenderer.Op> _visibleOps = new();
    private IntPtr _hwnd;
    private readonly IntPtr _arrow, _hand;

    // Durum
    private volatile bool _open;
    private string _game = "";
    private List<MemoryExchange> _items = new();
    private readonly HashSet<int> _expanded = new();
    private float _scroll;
    private int _hover = -1;
    private bool _hoverClose;
    private bool _tracking;
    private IntPtr _returnTo;

    // Yazı kutusu
    private string _input = "";
    private int _caret;
    private bool _caretOn = true;
    private bool _pending;          // cevap bekleniyor / akıyor: yeni soru gönderilemez
    private const int MaxInput = 600;
    private const int CaretTimerId = 7;

    /// <summary>Enter ile gönderilen soru (UI iş parçacığında çağrılır).</summary>
    public Action<string>? Submitted { get; set; }
    /// <summary>Panel açılırken ön plandaki pencere (yazılı soruların ekran görüntüsü buradan alınır).</summary>
    public IntPtr ReturnTarget => _returnTo;
    public string Game => _game;

    // Yerleşim (son çizimden)
    private readonly List<(float top, float bottom)> _itemRects = new();
    private float _viewTop, _viewBottom, _contentH;
    private (float x, float y, float w, float h) _closeRect;

    // Yüzey
    private IntPtr _monitor;
    private double _scale = 1;
    private TextRenderer.Fonts? _fonts;
    private IntPtr _screenDc, _memDc, _dib, _oldBmp, _bits, _gBitmap, _g;
    private int _surfW, _surfH, _w, _h;
    private Win32.POINT _pos;

    public HistoryPanel(Func<Settings> settings)
    {
        _settings = settings;
        _instance = this;
        _arrow = Win32.LoadCursorW(IntPtr.Zero, new IntPtr(Win32.IDC_ARROW));
        _hand = Win32.LoadCursorW(IntPtr.Zero, new IntPtr(Win32.IDC_HAND));
        CreateWindow();
    }

    /// <summary>Herhangi bir iş parçacığından okunabilir.</summary>
    public bool IsOpen => _open;

    private void CreateWindow()
    {
        _wndProc = StaticWndProc;
        var hInst = Win32.GetModuleHandleW(null);
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInst,
            hCursor = _arrow,
            lpszClassName = ClassName,
        };
        Win32.RegisterClassExW(ref wc);

        // HUD'dan farkı: WS_EX_TRANSPARENT ve WS_EX_NOACTIVATE yok → tıklanabilir, odak alabilir
        int ex = Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST;
        _hwnd = Win32.CreateWindowExW(ex, ClassName, "Oyun Asistanı — Önceki sorular", Win32.WS_POPUP,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("Geçmiş paneli oluşturulamadı: " + Marshal.GetLastWin32Error());
        if (!Win32.SetWindowDisplayAffinity(_hwnd, Win32.WDA_EXCLUDEFROMCAPTURE))
            Win32.SetWindowDisplayAffinity(_hwnd, Win32.WDA_MONITOR);
        _screenDc = Win32.GetDC(IntPtr.Zero);
        _memDc = Win32.CreateCompatibleDC(_screenDc);
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = _instance;
        if (self != null && hwnd == self._hwnd)
        {
            try
            {
                switch (msg)
                {
                    case Win32.WM_ACTIVATE:
                        // Dışarı (oyuna) tıklandı → kapan, odağı zaten oyun aldı
                        if (((long)wParam & 0xFFFF) == Win32.WA_INACTIVE && self._open)
                        {
                            Log.Info("Geçmiş paneli odağı kaybetti, kapanıyor");
                            self.Close(restoreFocus: false);
                        }
                        return IntPtr.Zero;
                    case Win32.WM_MOUSEWHEEL:
                        self.ScrollBy(-Win32.WheelDelta(wParam) / 120f * 3 * (self._fonts?.BodyLine ?? 20));
                        return IntPtr.Zero;
                    case Win32.WM_MOUSEMOVE:
                        self.OnMouseMove(Win32.GetXParam(lParam), Win32.GetYParam(lParam));
                        return IntPtr.Zero;
                    case Win32.WM_MOUSELEAVE:
                        self._tracking = false;
                        self.OnMouseMove(-1, -1);
                        return IntPtr.Zero;
                    case Win32.WM_LBUTTONDOWN:
                        self.OnClick(Win32.GetXParam(lParam), Win32.GetYParam(lParam));
                        return IntPtr.Zero;
                    case Win32.WM_KEYDOWN:
                        self.OnKey((int)wParam);
                        return IntPtr.Zero;
                    case Win32.WM_CHAR:
                        self.OnChar((char)(int)wParam);
                        return IntPtr.Zero;
                    case Win32.WM_TIMER:
                        self._caretOn = !self._caretOn;
                        self.Render();
                        return IntPtr.Zero;
                    case Win32.WM_SETCURSOR:
                        Win32.SetCursor(self._hover >= 0 || self._hoverClose ? self._hand : self._arrow);
                        return new IntPtr(1);
                }
            }
            catch (Exception ex) { Log.Error("Geçmiş paneli", ex); }
        }
        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // ------------------------------------------------------------------ Aç / kapat

    public void Open(string game, IReadOnlyList<MemoryExchange> items, IntPtr monitor, IntPtr returnTo)
    {
        _game = game;
        _items = new List<MemoryExchange>(items);
        _pending = false;
        _input = "";
        _caret = 0;
        _caretOn = true;
        _expanded.Clear();
        if (items.Count > 0) _expanded.Add(0); // en son cevap açık gelsin
        _scroll = 0;
        _hover = -1;
        _hoverClose = false;
        _returnTo = returnTo;
        SetMonitor(monitor);
        _open = true;
        Render();

        Win32.ShowWindow(_hwnd, Win32.SW_SHOW);
        Win32.SetTimer(_hwnd, (UIntPtr)CaretTimerId, 530, IntPtr.Zero);
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE);
        TakeFocus();
        // Oyun imleci pencereye kilitlemiş olabilir; serbest bırak ve imleci panelin ortasına getir
        Win32.ClipCursor(IntPtr.Zero);
        Win32.SetCursorPos(_pos.X + _w / 2, _pos.Y + Math.Min(_h / 2, (int)(_viewTop + 60 * _scale)));
        Log.Info($"Geçmiş paneli açıldı: {game}, {items.Count} kayıt");
    }

    public void Close(bool restoreFocus)
    {
        if (!_open) return;
        _open = false;
        Win32.KillTimer(_hwnd, (UIntPtr)CaretTimerId);
        Win32.ShowWindow(_hwnd, Win32.SW_HIDE);
        if (restoreFocus && _returnTo != IntPtr.Zero && Win32.IsWindow(_returnTo))
            Win32.SetForegroundWindow(_returnTo);
        _items = new List<MemoryExchange>();
    }

    // ------------------------------------------------------------------ Sohbet: bekleyen soru

    /// <summary>Gönderilen soruyu listenin başına ekler; cevap gelene kadar "Düşünüyor…" gösterilir.</summary>
    public void BeginPending(string question)
    {
        if (!_open) return;
        _pending = true;
        _items.Insert(0, new MemoryExchange { Q = question, A = "", At = DateTime.Now });
        _expanded.Clear();
        _expanded.Add(0);
        _scroll = 0;
        Render();
    }

    /// <summary>Yeniden deneme: gelen kısmi cevabı at.</summary>
    public void ResetPending()
    {
        if (!_open || !_pending || _items.Count == 0) return;
        _items[0].A = "";
        Render();
    }

    public void AppendPending(string delta)
    {
        if (!_open || !_pending || _items.Count == 0) return;
        _items[0].A += delta;
        Render();
    }

    /// <summary>Akış bitti. <paramref name="error"/> doluysa cevap yerine hata metni gösterilir.</summary>
    public void EndPending(string? error)
    {
        if (!_open || !_pending) return;
        _pending = false;
        if (error != null && _items.Count > 0) _items[0].A = "⚠ " + error;
        Render();
    }

    /// <summary>
    /// Windows arka plandaki işlemlerin odak almasını kısıtlar. Ön plandaki iş parçacığının girdisine
    /// geçici olarak bağlanmak (ve gerekirse sahte Alt basışı) bu kısıtı aşmanın bilinen yolu.
    /// </summary>
    private void TakeFocus()
    {
        var fg = Win32.GetForegroundWindow();
        uint fgThread = fg != IntPtr.Zero ? Win32.GetWindowThreadProcessId(fg, out _) : 0;
        uint me = Win32.GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && Win32.AttachThreadInput(me, fgThread, true);
        try
        {
            Win32.BringWindowToTop(_hwnd);
            Win32.SetForegroundWindow(_hwnd);
            Win32.SetFocus(_hwnd);
        }
        finally
        {
            if (attached) Win32.AttachThreadInput(me, fgThread, false);
        }
        if (Win32.GetForegroundWindow() != _hwnd)
        {
            Win32.keybd_event((byte)Win32.VK_MENU, 0, 0, UIntPtr.Zero);
            Win32.keybd_event((byte)Win32.VK_MENU, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Win32.SetForegroundWindow(_hwnd);
            Win32.SetFocus(_hwnd);
        }
        if (Win32.GetForegroundWindow() != _hwnd) Log.Warn("Geçmiş paneli odak alamadı; tıklama ve tekerlek çalışmayabilir");
    }

    // ------------------------------------------------------------------ Girdi

    private void OnKey(int vk)
    {
        float line = _fonts?.BodyLine ?? 20;
        float page = Math.Max(line, _viewBottom - _viewTop - line);
        switch (vk)
        {
            case Win32.VK_ESCAPE: Close(restoreFocus: true); break;
            case Win32.VK_LEFT: _caret = Math.Max(0, _caret - 1); CaretMoved(); break;
            case Win32.VK_RIGHT: _caret = Math.Min(_input.Length, _caret + 1); CaretMoved(); break;
            case Win32.VK_HOME: _caret = 0; CaretMoved(); break;
            case Win32.VK_END: _caret = _input.Length; CaretMoved(); break;
            case Win32.VK_DELETE:
                if (_caret < _input.Length) { _input = _input.Remove(_caret, 1); CaretMoved(); }
                break;
            case 'V' when (Win32.GetKeyState(Win32.VK_CONTROL) & 0x8000) != 0:
                Insert(Win32.GetClipboardText(_hwnd));
                break;
            case Win32.VK_UP: ScrollBy(-line * 2); break;
            case Win32.VK_DOWN: ScrollBy(line * 2); break;
            case Win32.VK_PRIOR: ScrollBy(-page); break;
            case Win32.VK_NEXT: ScrollBy(page); break;
        }
    }

    private void CaretMoved()
    {
        _caretOn = true;
        Render();
    }

    private void OnChar(char c)
    {
        if (c == '\r') { Submit(); return; }
        if (c == '\b')
        {
            if (_caret > 0) { _input = _input.Remove(_caret - 1, 1); _caret--; CaretMoved(); }
            return;
        }
        if (c < 32 || c == 127) return; // Esc, Ctrl+harf vb.
        Insert(c.ToString());
    }

    private void Insert(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        // Yapıştırılan çok satırlı metin tek satıra indirilir
        text = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        text = string.Concat(System.Linq.Enumerable.Where(text, ch => !char.IsControl(ch)));
        int room = MaxInput - _input.Length;
        if (room <= 0) return;
        if (text.Length > room) text = text[..room];
        _input = _input.Insert(_caret, text);
        _caret += text.Length;
        CaretMoved();
    }

    private void Submit()
    {
        var q = _input.Trim();
        if (q.Length == 0 || _pending) return;
        _input = "";
        _caret = 0;
        BeginPending(q);
        try { Submitted?.Invoke(q); }
        catch (Exception ex)
        {
            Log.Error("Yazılı soru gönderilemedi", ex);
            EndPending(ex.Message);
        }
    }

    private void ScrollBy(float dy)
    {
        float max = Math.Max(0, _contentH - (_viewBottom - _viewTop));
        float next = Math.Clamp(_scroll + dy, 0, max);
        if (Math.Abs(next - _scroll) < 0.5f) return;
        _scroll = next;
        Render();
    }

    private void OnMouseMove(int x, int y)
    {
        if (x >= 0 && !_tracking)
        {
            var tme = new Win32.TRACKMOUSEEVENT
            {
                cbSize = (uint)Marshal.SizeOf<Win32.TRACKMOUSEEVENT>(),
                dwFlags = Win32.TME_LEAVE,
                hwndTrack = _hwnd,
            };
            _tracking = Win32.TrackMouseEvent(ref tme);
        }
        int hover = x < 0 ? -1 : HitItem(y);
        bool hoverClose = x >= 0 && InClose(x, y);
        if (hover != _hover || hoverClose != _hoverClose)
        {
            _hover = hover;
            _hoverClose = hoverClose;
            Render();
        }
    }

    private void OnClick(int x, int y)
    {
        if (InClose(x, y)) { Close(restoreFocus: true); return; }
        int i = HitItem(y);
        if (i < 0) return;
        if (!_expanded.Add(i)) _expanded.Remove(i);
        Render();
        // Açılan cevap görünür alanın altına taşıyorsa kaydır
        if (_expanded.Contains(i) && i < _itemRects.Count)
        {
            var (top, bottom) = _itemRects[i];
            float viewH = _viewBottom - _viewTop;
            if (bottom - _scroll > viewH) ScrollBy(Math.Min(bottom - _scroll - viewH, top - _scroll));
        }
    }

    private bool InClose(int x, int y) =>
        x >= _closeRect.x && x <= _closeRect.x + _closeRect.w && y >= _closeRect.y && y <= _closeRect.y + _closeRect.h;

    private int HitItem(int y)
    {
        if (y < _viewTop || y > _viewBottom) return -1;
        float cy = y - _viewTop + _scroll;
        for (int i = 0; i < _itemRects.Count; i++)
            if (cy >= _itemRects[i].top && cy < _itemRects[i].bottom) return i;
        return -1;
    }

    // ------------------------------------------------------------------ Çizim

    private void SetMonitor(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) monitor = Win32.MonitorFromPoint(new Win32.POINT(0, 0), Win32.MONITOR_DEFAULTTONEAREST);
        double scale = Win32.GetMonitorScale(monitor);
        if (_fonts == null || Math.Abs(scale - _scale) > 0.001)
        {
            _fonts?.Dispose();
            var o = _settings().Overlay;
            _fonts = _text.CreateFonts(o.FontFamily, o.FontSize, scale);
            _scale = scale;
        }
        _monitor = monitor;
    }

    private void EnsureSurface(int w, int h)
    {
        if (w <= _surfW && h <= _surfH && _g != IntPtr.Zero) return;
        FreeSurface();
        _surfW = Math.Max(w, _surfW);
        _surfH = Math.Max(h, _surfH);
        _dib = Win32.CreateDib32(_screenDc, _surfW, _surfH, out _bits);
        _oldBmp = Win32.SelectObject(_memDc, _dib);
        Gdip.Check(Gdip.GdipCreateBitmapFromScan0(_surfW, _surfH, _surfW * 4, Gdip.PixelFormat32bppPARGB, _bits, out _gBitmap), "PanelSurface");
        Gdip.Check(Gdip.GdipGetImageGraphicsContext(_gBitmap, out _g), "PanelSurfaceG");
        Gdip.GdipSetSmoothingMode(_g, Gdip.SmoothingModeAntiAlias);
        Gdip.GdipSetTextRenderingHint(_g, Gdip.TextRenderingHintAntiAlias);
        Gdip.GdipSetPixelOffsetMode(_g, Gdip.PixelOffsetModeHighQuality);
    }

    private void FreeSurface()
    {
        if (_g != IntPtr.Zero) { Gdip.GdipDeleteGraphics(_g); _g = IntPtr.Zero; }
        if (_gBitmap != IntPtr.Zero) { Gdip.GdipDisposeImage(_gBitmap); _gBitmap = IntPtr.Zero; }
        if (_dib != IntPtr.Zero)
        {
            Win32.SelectObject(_memDc, _oldBmp);
            Win32.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }
    }

    private void Render()
    {
        if (!_open || _fonts == null) return;
        var o = _settings().Overlay;
        var f = _fonts;
        float s = (float)_scale;
        var mon = Win32.GetMonitorRect(_monitor);

        int W = (int)Math.Round(o.Width * 1.15 * s);
        int H = (int)(mon.Height * 0.72);
        float pad = 14 * s;
        float x0 = pad + 8 * s;
        float contentW = W - x0 - pad - 8 * s; // sağda kaydırma çubuğu payı

        uint accent = ParseHex(o.AccentColor, 0xD2463C) | 0xFF000000;
        const uint White = 0xFFECEFF3, Bold = 0xFFFFFFFF, Muted = 0xFF9AA4AF, Faint = 0xFF6E7884;

        EnsureSurface(W, H);
        Gdip.GdipGraphicsClear(_g, 0);
        FillRoundRect(_g, 0xF20D1015, 0.5f, 0.5f, W - 1, H - 1, 10 * s);
        FillRect(_g, accent, pad * 0.65f, pad, 3 * s, H - pad * 2);

        // Başlık (kaymaz)
        _ops.Clear();
        float y = pad;
        _ops.Add(new TextRenderer.Op { Text = "Sohbet · " + _game, X = x0, Y = y, Font = f.SmallBold, Color = Muted, LineHeight = f.SmallLine });
        float closeSize = f.SmallLine + 6 * s;
        _closeRect = (W - pad - closeSize, y - 3 * s, closeSize, closeSize);
        if (_hoverClose) FillRoundRect(_g, 0x33FFFFFF, _closeRect.x, _closeRect.y, _closeRect.w, _closeRect.h, 4 * s);
        float xw = _text.Measure(f.SmallBold, "✕");
        _ops.Add(new TextRenderer.Op { Text = "✕", X = _closeRect.x + (closeSize - xw) / 2, Y = y, Font = f.SmallBold, Color = _hoverClose ? Bold : Muted, LineHeight = f.SmallLine });
        y += f.SmallLine + 2 * s;
        _text.LayoutPlain("Aşağıya yaz + Enter: sor · Tıkla: cevabı aç/kapat · Tekerlek: kaydır · Esc / Mouse 5: kapat",
            f.Small, f.SmallLine, Faint, x0, contentW, ref y, _ops);
        y += 6 * s;
        FillRect(_g, 0x22FFFFFF, x0, y, W - x0 - pad, Math.Max(1, s));
        y += 6 * s;
        _viewTop = y;
        float inputH = f.BodyLine + 12 * s;
        float inputTop = H - pad - inputH;
        _viewBottom = inputTop - 10 * s;
        _text.Draw(_g, _ops, float.MaxValue);

        // Kayan içerik (içerik koordinatları: 0 = görünür alanın tepesi, kaydırmadan önce)
        _ops.Clear();
        _itemRects.Clear();
        var backgrounds = new List<(float top, float bottom, uint color)>();
        float cy = 0;
        if (_items.Count == 0)
        {
            _text.LayoutPlain("Bu oyun için henüz kayıtlı soru yok. Aşağıya yazarak ya da Mouse 5'i basılı tutup konuşarak soru sor; burada görünecek.",
                f.Body, f.BodyLine, Muted, x0, contentW, ref cy, _ops);
        }
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            bool open = _expanded.Contains(i);
            float top = cy;
            cy += 7 * s;
            string when = _pending && i == 0 ? "şimdi" : it.At.ToString("dd.MM · HH:mm", CultureInfo.InvariantCulture);
            _ops.Add(new TextRenderer.Op { Text = (open ? "▾ " : "▸ ") + when, X = x0, Y = cy, Font = f.Small, Color = Faint, LineHeight = f.SmallLine });
            cy += f.SmallLine;
            var q = string.IsNullOrWhiteSpace(it.Q) ? "(soru metni yok)" : it.Q;
            _text.LayoutPlain(q, open ? f.Bold : f.Body, f.BodyLine, open ? Bold : White, x0, contentW, ref cy, _ops);
            if (open && !string.IsNullOrWhiteSpace(it.A))
            {
                cy += 4 * s;
                _text.LayoutRich(it.A, f, White, accent, Bold, x0 + 6 * s, contentW - 6 * s, ref cy, _ops);
            }
            else if (open && _pending && i == 0)
            {
                cy += 4 * s;
                _text.LayoutPlain("Düşünüyor…", f.SmallItalic, f.SmallLine, Faint, x0 + 6 * s, contentW - 6 * s, ref cy, _ops);
            }
            cy += 7 * s;
            _itemRects.Add((top, cy));
            if (i == _hover) backgrounds.Add((top, cy, 0x18FFFFFF));
            else if (open) backgrounds.Add((top, cy, 0x0CFFFFFF));
        }
        _contentH = cy;
        float viewH = _viewBottom - _viewTop;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _contentH - viewH));

        float off = _viewTop - _scroll;
        Gdip.GdipSetClipRect(_g, 0, _viewTop, W, viewH, 0);
        foreach (var (top, bottom, color) in backgrounds)
            FillRoundRect(_g, color, x0 - 6 * s, top + off, contentW + 12 * s, bottom - top, 6 * s);
        for (int i = 1; i < _itemRects.Count; i++)
            FillRect(_g, 0x14FFFFFF, x0, _itemRects[i].top + off, contentW, Math.Max(1, s));
        _visibleOps.Clear();
        foreach (var op in _ops)
        {
            float oy = op.Y + off;
            if (oy + op.LineHeight < _viewTop || oy > _viewBottom) continue;
            var shifted = op;
            shifted.Y = oy;
            _visibleOps.Add(shifted);
        }
        _text.Draw(_g, _visibleOps, float.MaxValue);
        Gdip.GdipResetClip(_g);

        // Yazı kutusu
        {
            float bx = x0 - 6 * s, bw = W - bx - pad;
            FillRoundRect(_g, 0x26FFFFFF, bx, inputTop, bw, inputH, 8 * s);
            FillRoundRect(_g, 0xF20D1015, bx + Math.Max(1, s), inputTop + Math.Max(1, s), bw - 2 * Math.Max(1, s), inputH - 2 * Math.Max(1, s), 7 * s);
            float tx = bx + 10 * s, ty = inputTop + (inputH - f.BodyLine) / 2;
            float availW = bw - 20 * s;
            _ops.Clear();
            if (_pending)
                _ops.Add(new TextRenderer.Op { Text = "Cevap yazılıyor…", X = tx, Y = ty, Font = f.SmallItalic, Color = Faint, LineHeight = f.BodyLine });
            else if (_input.Length == 0)
                _ops.Add(new TextRenderer.Op { Text = "Bir şey sor…  (Enter: gönder)", X = tx, Y = ty, Font = f.Body, Color = Faint, LineHeight = f.BodyLine });
            else
            {
                // İmleç görünür kalsın: soldan kırp, sağdan sığdığı kadar göster
                int a = 0;
                while (a < _caret && _text.Measure(f.Body, _input.Substring(a, _caret - a)) > availW - 4 * s) a++;
                int e = _input.Length;
                while (e > _caret && _text.Measure(f.Body, _input.Substring(a, e - a)) > availW) e--;
                _ops.Add(new TextRenderer.Op { Text = _input.Substring(a, e - a), X = tx, Y = ty, Font = f.Body, Color = White, LineHeight = f.BodyLine });
                if (_caretOn)
                {
                    float cxp = tx + _text.Measure(f.Body, _input.Substring(a, _caret - a));
                    FillRect(_g, 0xFFFFFFFF, cxp, ty + 2 * s, Math.Max(1.5f, 1.5f * s), f.BodyLine - 4 * s);
                }
            }
            if (!_pending && _input.Length == 0 && _caretOn)
                FillRect(_g, 0xFFFFFFFF, tx - 1 * s, ty + 2 * s, Math.Max(1.5f, 1.5f * s), f.BodyLine - 4 * s);
            _text.Draw(_g, _ops, float.MaxValue);
        }

        // Kaydırma çubuğu
        if (_contentH > viewH)
        {
            float trackX = W - pad * 0.5f - 4 * s;
            float thumbH = Math.Max(24 * s, viewH * viewH / _contentH);
            float thumbY = _viewTop + (viewH - thumbH) * (_scroll / (_contentH - viewH));
            FillRoundRect(_g, 0x1AFFFFFF, trackX, _viewTop, 4 * s, viewH, 2 * s);
            FillRoundRect(_g, 0x66FFFFFF, trackX, thumbY, 4 * s, thumbH, 2 * s);
        }

        _w = W;
        _h = H;
        _pos = new Win32.POINT(mon.Right - (int)Math.Round(o.MarginRight * s) - W, mon.Top + (int)Math.Round(o.MarginTop * s));
        var size = new Win32.SIZE(W, H);
        var srcPt = new Win32.POINT(0, 0);
        var blend = new Win32.BLENDFUNCTION { BlendOp = Win32.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Win32.AC_SRC_ALPHA };
        if (!Win32.UpdateLayeredWindow(_hwnd, _screenDc, ref _pos, ref size, _memDc, ref srcPt, 0, ref blend, Win32.ULW_ALPHA))
            Log.Warn("Panel UpdateLayeredWindow başarısız: " + Marshal.GetLastWin32Error());
    }

    private static uint ParseHex(string? hex, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var h = hex.Trim().TrimStart('#');
        return uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? (v & 0xFFFFFF) : fallback;
    }

    private static void FillRect(IntPtr g, uint color, float x, float y, float w, float h)
    {
        Gdip.GdipCreateSolidFill(color, out var b);
        Gdip.GdipFillRectangle(g, b, x, y, w, h);
        Gdip.GdipDeleteBrush(b);
    }

    private static void FillRoundRect(IntPtr g, uint color, float x, float y, float w, float h, float r)
    {
        r = Math.Min(r, Math.Min(w, h) / 2);
        float d = r * 2;
        if (d <= 0) { FillRect(g, color, x, y, w, h); return; }
        Gdip.GdipCreatePath(0, out var path);
        Gdip.GdipAddPathArc(path, x, y, d, d, 180, 90);
        Gdip.GdipAddPathArc(path, x + w - d, y, d, d, 270, 90);
        Gdip.GdipAddPathArc(path, x + w - d, y + h - d, d, d, 0, 90);
        Gdip.GdipAddPathArc(path, x, y + h - d, d, d, 90, 90);
        Gdip.GdipClosePathFigure(path);
        Gdip.GdipCreateSolidFill(color, out var b);
        Gdip.GdipFillPath(g, b, path);
        Gdip.GdipDeleteBrush(b);
        Gdip.GdipDeletePath(path);
    }

    public void Dispose()
    {
        _open = false;
        FreeSurface();
        _fonts?.Dispose();
        _text.Dispose();
        if (_memDc != IntPtr.Zero) Win32.DeleteDC(_memDc);
        if (_screenDc != IntPtr.Zero) Win32.ReleaseDC(IntPtr.Zero, _screenDc);
        if (_hwnd != IntPtr.Zero) Win32.DestroyWindow(_hwnd);
    }
}
