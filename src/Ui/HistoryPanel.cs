using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using InGameCompanion.Core;
using InGameCompanion.Native;

namespace InGameCompanion.Ui;

/// <summary>
/// Sohbet + ayarlar paneli. HUD'ın aksine tıklanabilir: açılınca odağı alır (oyun imleci bırakır).
/// Sohbet: mesajlar yukarıdan aşağı akar (en yeni altta), altındaki kutuya yazıp Enter ile soru sorulur,
/// tıklamayla eski cevaplar açılır/kapanır. Ayarlar: sağ üstteki düğmeyle açılır; satıra tıklayınca değer değişir
/// ve anında settings.json'a kaydedilir. Esc, Mouse 5 ya da dışarı tıklama kapatır; odak oyuna geri verilir.
/// Tüm üyeler UI (ana) iş parçacığında çalışır; dışarıdan <see cref="OverlayWindow.Invoke"/> ile çağır.
/// </summary>
internal sealed class HistoryPanel : IDisposable
{
    private const string ClassName = "InGameCompanionHistory";
    private const int MaxInput = 600;
    private const int CaretTimerId = 7;
    private const int ExpandedByDefault = 3;

    private enum View { Chat, Settings }

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
    private View _view = View.Chat;
    private string _game = "";
    private List<MemoryExchange> _items = new();          // kronolojik: en eski başta, en yeni sonda
    private readonly HashSet<int> _expanded = new();
    private float _scroll;
    private bool _stick = true;                            // sohbet altta kalsın (kullanıcı yukarı kaydırınca kapanır)
    private int _hover = -1;
    private bool _hoverClose, _hoverMode;
    private bool _tracking;
    private IntPtr _returnTo;

    // Yazı kutusu
    private string _input = "";
    private int _caret;
    private bool _caretOn = true;
    private bool _pending;          // cevap bekleniyor / akıyor: yeni soru gönderilemez

    // Ayarlar ekranı
    private sealed class Row
    {
        public string Label = "", Desc = "";
        public Func<string> Value = () => "";
        public Func<bool>? On;                 // toggle ise açık mı (pill rengi için)
        public Action Click = () => { };
    }
    private List<Row> _rows = new();
    private string _status = "";
    private DateTime _statusUntil;

    /// <summary>Enter ile gönderilen soru (UI iş parçacığında çağrılır).</summary>
    public Action<string>? Submitted { get; set; }
    /// <summary>Bir ayar değişip kaydedildi (kısayol tuşu gibi şeyler için kanca yeniden kurulabilir).</summary>
    public Action? SettingsChanged { get; set; }
    /// <summary>Panel açılırken ön plandaki pencere (yazılı soruların ekran görüntüsü buradan alınır).</summary>
    public IntPtr ReturnTarget => _returnTo;
    public string Game => _game;

    // Yerleşim (son çizimden)
    private readonly List<(float top, float bottom)> _itemRects = new();
    private readonly List<(float top, float bottom, uint color)> _backgrounds = new();
    private readonly List<(float x, float y, float w, float h, uint color)> _pills = new();
    private float _viewTop, _viewBottom, _contentH;
    private (float x, float y, float w, float h) _closeRect, _modeRect;

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
        _hwnd = Win32.CreateWindowExW(ex, ClassName, "Oyun Asistanı — Sohbet", Win32.WS_POPUP,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("Sohbet paneli oluşturulamadı: " + Marshal.GetLastWin32Error());
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
                            Log.Info("Sohbet paneli odağı kaybetti, kapanıyor");
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
                        Win32.SetCursor(self._hover >= 0 || self._hoverClose || self._hoverMode ? self._hand : self._arrow);
                        return new IntPtr(1);
                }
            }
            catch (Exception ex) { Log.Error("Sohbet paneli", ex); }
        }
        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // ------------------------------------------------------------------ Aç / kapat

    /// <param name="items">Geçmiş, EN YENİ BAŞTA (GameMemoryStore.History sırası); panel bunu kronolojik gösterir.</param>
    public void Open(string game, IReadOnlyList<MemoryExchange> items, IntPtr monitor, IntPtr returnTo, bool settingsView = false)
    {
        _game = game;
        _items = new List<MemoryExchange>(items);
        _items.Reverse();
        _pending = false;
        _input = "";
        _caret = 0;
        _caretOn = true;
        _view = settingsView ? View.Settings : View.Chat;
        _expanded.Clear();
        for (int i = Math.Max(0, _items.Count - ExpandedByDefault); i < _items.Count; i++) _expanded.Add(i);
        _scroll = 0;
        _stick = true;
        _hover = -1;
        _hoverClose = _hoverMode = false;
        _status = "";
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
        Log.Info($"Sohbet paneli açıldı ({_view}): {game}, {items.Count} kayıt");
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

    /// <summary>Gönderilen soruyu sohbetin sonuna ekler; cevap gelene kadar "Düşünüyor…" gösterilir.</summary>
    public void BeginPending(string question)
    {
        if (!_open) return;
        _pending = true;
        _view = View.Chat;
        _items.Add(new MemoryExchange { Q = question, A = "", At = DateTime.Now });
        _expanded.Add(_items.Count - 1);
        _stick = true;
        Render();
    }

    /// <summary>Yeniden deneme: gelen kısmi cevabı at.</summary>
    public void ResetPending()
    {
        if (!_open || !_pending || _items.Count == 0) return;
        _items[^1].A = "";
        Render();
    }

    public void AppendPending(string delta)
    {
        if (!_open || !_pending || _items.Count == 0) return;
        _items[^1].A += delta;
        Render();
    }

    /// <summary>Akış bitti. <paramref name="error"/> doluysa cevap yerine hata metni gösterilir.</summary>
    public void EndPending(string? error)
    {
        if (!_open || !_pending) return;
        _pending = false;
        if (error != null && _items.Count > 0) _items[^1].A = "⚠ " + error;
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
        if (Win32.GetForegroundWindow() != _hwnd) Log.Warn("Sohbet paneli odak alamadı; tıklama ve tekerlek çalışmayabilir");
    }

    // ------------------------------------------------------------------ Girdi

    private void OnKey(int vk)
    {
        float line = _fonts?.BodyLine ?? 20;
        float page = Math.Max(line, _viewBottom - _viewTop - line);
        bool chat = _view == View.Chat;
        switch (vk)
        {
            case Win32.VK_ESCAPE:
                if (_view == View.Settings) { SwitchView(View.Chat); break; }
                Close(restoreFocus: true);
                break;
            case Win32.VK_LEFT when chat: _caret = Math.Max(0, _caret - 1); CaretMoved(); break;
            case Win32.VK_RIGHT when chat: _caret = Math.Min(_input.Length, _caret + 1); CaretMoved(); break;
            case Win32.VK_HOME when chat: _caret = 0; CaretMoved(); break;
            case Win32.VK_END when chat: _caret = _input.Length; CaretMoved(); break;
            case Win32.VK_DELETE when chat:
                if (_caret < _input.Length) { _input = _input.Remove(_caret, 1); CaretMoved(); }
                break;
            case 'V' when chat && (Win32.GetKeyState(Win32.VK_CONTROL) & 0x8000) != 0:
                Insert(Win32.GetClipboardText(_hwnd));
                break;
            case Win32.VK_UP: ScrollBy(-line * 2); break;
            case Win32.VK_DOWN: ScrollBy(line * 2); break;
            case Win32.VK_PRIOR: ScrollBy(-page); break;
            case Win32.VK_NEXT: ScrollBy(page); break;
        }
    }

    private void SwitchView(View v)
    {
        _view = v;
        _scroll = 0;
        _stick = v == View.Chat;
        _hover = -1;
        _status = "";
        Render();
    }

    private void CaretMoved()
    {
        _caretOn = true;
        Render();
    }

    private void OnChar(char c)
    {
        if (_view != View.Chat) return;
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
        if (Math.Abs(next - _scroll) < 0.5f && !(dy > 0 && next >= max - 2)) return;
        _scroll = next;
        _stick = _view == View.Chat && next >= max - 2;   // en alttaysa yeni mesajlar akmaya devam etsin
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
        bool hoverClose = x >= 0 && In(_closeRect, x, y);
        bool hoverMode = x >= 0 && In(_modeRect, x, y);
        if (hover != _hover || hoverClose != _hoverClose || hoverMode != _hoverMode)
        {
            _hover = hover;
            _hoverClose = hoverClose;
            _hoverMode = hoverMode;
            Render();
        }
    }

    private void OnClick(int x, int y)
    {
        if (In(_closeRect, x, y)) { Close(restoreFocus: true); return; }
        if (In(_modeRect, x, y)) { SwitchView(_view == View.Chat ? View.Settings : View.Chat); return; }
        int i = HitItem(y);
        if (i < 0) return;

        if (_view == View.Settings)
        {
            if (i < _rows.Count)
            {
                try { _rows[i].Click(); }
                catch (Exception ex) { Log.Error("Ayar değiştirilemedi", ex); SetStatus("Hata: " + ex.Message); }
                Render();
            }
            return;
        }

        if (!_expanded.Add(i)) _expanded.Remove(i);
        _stick = false;
        Render();
        // Açılan cevap görünür alanın altına taşıyorsa kaydır
        if (_expanded.Contains(i) && i < _itemRects.Count)
        {
            var (top, bottom) = _itemRects[i];
            float viewH = _viewBottom - _viewTop;
            if (bottom - _scroll > viewH) ScrollBy(Math.Min(bottom - _scroll - viewH, top - _scroll));
        }
    }

    private static bool In((float x, float y, float w, float h) r, int x, int y) =>
        x >= r.x && x <= r.x + r.w && y >= r.y && y <= r.y + r.h;

    private int HitItem(int y)
    {
        if (y < _viewTop || y > _viewBottom) return -1;
        float cy = y - _viewTop + _scroll;
        for (int i = 0; i < _itemRects.Count; i++)
            if (cy >= _itemRects[i].top && cy < _itemRects[i].bottom) return i;
        return -1;
    }

    // ------------------------------------------------------------------ Ayarlar

    private void SetStatus(string s)
    {
        _status = s;
        _statusUntil = DateTime.UtcNow.AddSeconds(3.5);
    }

    /// <summary>Ayarı değiştirir, settings.json'a yazar ve kancayı bilgilendirir.</summary>
    private void Apply(Action<Settings> change, string? doneMessage = null)
    {
        var s = _settings();
        change(s);
        try
        {
            s.Save();
            SetStatus(doneMessage ?? "Kaydedildi");
        }
        catch (Exception ex) { SetStatus("Kaydedilemedi: " + ex.Message); }
        try { SettingsChanged?.Invoke(); } catch (Exception ex) { Log.Error("Ayar değişikliği uygulanamadı", ex); }
    }

    private static string Next(string current, string[] values)
    {
        int i = Array.FindIndex(values, v => string.Equals(v, current, StringComparison.OrdinalIgnoreCase));
        return values[(i + 1) % values.Length];
    }

    private static string Name(string value, (string v, string name)[] map)
    {
        foreach (var (v, name) in map)
            if (string.Equals(v, value, StringComparison.OrdinalIgnoreCase)) return name;
        return string.IsNullOrEmpty(value) ? "Kapalı" : value;
    }

    private static readonly (string, string)[] LengthNames = { ("Short", "Kısa"), ("Normal", "Orta"), ("Detailed", "Ayrıntılı") };
    private static readonly (string, string)[] HotkeyNames = { ("XButton2", "Mouse 5"), ("XButton1", "Mouse 4"), ("Middle", "Orta tuş") };
    private static readonly (string, string)[] SpoilerNames = { ("Strict", "Sıkı"), ("Mild", "Hafif") };

    private Row Toggle(string label, string desc, Func<Settings, bool> get, Action<Settings, bool> set) => new()
    {
        Label = label, Desc = desc,
        Value = () => get(_settings()) ? "Açık" : "Kapalı",
        On = () => get(_settings()),
        Click = () => Apply(s => set(s, !get(s))),
    };

    private Row Choice(string label, string desc, string[] values, (string, string)[] names,
                       Func<Settings, string> get, Action<Settings, string> set) => new()
    {
        Label = label, Desc = desc,
        Value = () => Name(get(_settings()), names),
        Click = () => Apply(s => set(s, Next(get(s), values))),
    };

    private void BuildRows()
    {
        var models = new[] { "gemini-3.8-flash", "gemini-3.5-flash-lite" };
        var s0 = _settings();
        // Elle yazılmış farklı bir model varsa döngüye eklenir
        string[] WithCurrent(string cur, string[] baseList) =>
            string.IsNullOrWhiteSpace(cur) || Array.Exists(baseList, m => string.Equals(m, cur, StringComparison.OrdinalIgnoreCase))
                ? baseList : new List<string>(baseList) { cur }.ToArray();

        _rows = new List<Row>
        {
            Choice("Ana model", "3.8 daha zeki ama sık \"yoğun\" olabilir; lite hızlı",
                   WithCurrent(s0.Model, models), Array.Empty<(string, string)>(), s => s.Model, (s, v) => s.Model = v),
            Choice("Yedek model", "Ana model yoğunken bununla cevaplanır",
                   WithCurrent(s0.FallbackModel, new[] { "gemini-3.5-flash-lite", "" }), Array.Empty<(string, string)>(), s => s.FallbackModel ?? "", (s, v) => s.FallbackModel = v),
            Choice("Sesli cevap uzunluğu", "Mouse 5 ile sorduğunda", new[] { "Short", "Normal", "Detailed" }, LengthNames,
                   s => s.AnswerLength, (s, v) => s.AnswerLength = v),
            Choice("Yazılı cevap uzunluğu", "Bu panelden sorduğunda", new[] { "Short", "Normal", "Detailed" }, LengthNames,
                   s => s.TypedAnswerLength, (s, v) => s.TypedAnswerLength = v),
            Toggle("Ekranda işaret", "\"Şuna bas\" dediğinde hedefin üstüne halka çizer", s => s.PointerMarkers, (s, v) => s.PointerMarkers = v),
            Toggle("İşareti hassaslaştır", "Hedefi bir kez daha yakından kontrol eder (+1 istek, ~2-4 sn)", s => s.PointerRefine, (s, v) => s.PointerRefine = v),
            new Row
            {
                Label = "İşaret süresi", Desc = "Halkalar ekranda ne kadar kalsın",
                Value = () => _settings().MarkerSeconds.ToString("0", CultureInfo.InvariantCulture) + " sn",
                Click = () => Apply(s =>
                {
                    double[] opts = { 8, 14, 20, 30 };
                    int i = Array.FindIndex(opts, o => Math.Abs(o - s.MarkerSeconds) < 0.5);
                    s.MarkerSeconds = opts[(i + 1) % opts.Length];
                }),
            },
            Toggle("Konuşurken canlı yazı", "Söylediklerin kutuda anında görünür", s => s.LiveTranscription, (s, v) => s.LiveTranscription = v),
            Toggle("Web araması", "Ücretsiz Gemini katmanında çalışmaz (429)", s => s.UseWebSearch, (s, v) => s.UseWebSearch = v),
            Choice("Spoiler koruması", "Sıkı: hikâye bilgisi vermez", new[] { "Strict", "Mild" }, SpoilerNames,
                   s => s.SpoilerLevel, (s, v) => s.SpoilerLevel = v),
            Toggle("Otomatik oyun profili", "Yeni oyunu tanıyıp o oyunu anlatan profili kendiliğinden oluşturur (oyun başına +1 istek)", s => s.AutoGameProfile, (s, v) => s.AutoGameProfile = v),
            Toggle("Yedek model uyarısı", "Yedeğe geçilince kutuda not göster", s => s.ShowModelNotice, (s, v) => s.ShowModelNotice = v),
            Choice("Kısayol tuşu", "Basılı tut = sor · çift dokun = bu panel", new[] { "XButton2", "XButton1", "Middle" }, HotkeyNames,
                   s => s.Hotkey, (s, v) => s.Hotkey = v),
            Toggle("Tuş oyuna gitmesin", "Kısayol tuşu oyunda başka işe atanmışsa çakışmaz", s => s.SwallowHotkey, (s, v) => s.SwallowHotkey = v),
            new Row
            {
                Label = "Gemini API anahtarı", Desc = "Tıkla: panodaki anahtarı yapıştır",
                Value = () => MaskKey(_settings().ApiKey),
                Click = PasteKey,
            },
            new Row { Label = "settings.json dosyasını aç", Desc = "Gelişmiş ayarlar (yazı boyutu, renkler, oyun listesi…)", Value = () => "Aç", Click = () => OpenPath(Settings.FilePath) },
            new Row { Label = "Log klasörünü aç", Desc = "Sorun olursa buradaki dosyayı gönder", Value = () => "Aç", Click = () => OpenPath(Log.Dir) },
        };
    }

    private static string MaskKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "Girilmemiş";
        var k = key.Trim();
        return k.Length <= 8 ? "••••" : k[..3] + "••••" + k[^4..];
    }

    private void PasteKey()
    {
        var t = Win32.GetClipboardText(_hwnd)?.Trim();
        if (string.IsNullOrEmpty(t) || t.Length < 20 || t.Length > 200 || t.IndexOfAny(new[] { ' ', '\r', '\n', '\t' }) >= 0)
        {
            SetStatus("Panoda geçerli bir anahtar yok. Önce anahtarı kopyala.");
            return;
        }
        Apply(s => s.ApiKey = t, "Anahtar kaydedildi");
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Açılamadı: " + path, ex); }
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
        if (_status.Length > 0 && DateTime.UtcNow > _statusUntil) _status = "";
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

        // ---- Başlık (kaymaz)
        _ops.Clear();
        float y = pad;
        bool chat = _view == View.Chat;
        _ops.Add(new TextRenderer.Op { Text = chat ? "Sohbet · " + _game : "Ayarlar", X = x0, Y = y, Font = f.SmallBold, Color = Muted, LineHeight = f.SmallLine });

        float btn = f.SmallLine + 6 * s;
        _closeRect = (W - pad - btn, y - 3 * s, btn, btn);
        if (_hoverClose) FillRoundRect(_g, 0x33FFFFFF, _closeRect.x, _closeRect.y, _closeRect.w, _closeRect.h, 4 * s);
        float xw = _text.Measure(f.SmallBold, "✕");
        _ops.Add(new TextRenderer.Op { Text = "✕", X = _closeRect.x + (btn - xw) / 2, Y = y, Font = f.SmallBold, Color = _hoverClose ? Bold : Muted, LineHeight = f.SmallLine });

        string modeText = chat ? "Ayarlar" : "‹ Sohbet";
        float mw = _text.Measure(f.SmallBold, modeText) + 20 * s;
        _modeRect = (_closeRect.x - 8 * s - mw, y - 3 * s, mw, btn);
        FillRoundRect(_g, _hoverMode ? 0x40FFFFFFu : 0x22FFFFFFu, _modeRect.x, _modeRect.y, _modeRect.w, _modeRect.h, btn / 2);
        _ops.Add(new TextRenderer.Op { Text = modeText, X = _modeRect.x + 10 * s, Y = y, Font = f.SmallBold, Color = _hoverMode ? Bold : White, LineHeight = f.SmallLine });

        y += f.SmallLine + 2 * s;
        _text.LayoutPlain(chat ? "Aşağıya yaz + Enter: sor · Tıkla: eski cevabı aç/kapat · Esc / Mouse 5: kapat"
                               : "Satıra tıkla: değeri değiştir · Anında kaydedilir · Esc: sohbete dön",
            f.Small, f.SmallLine, Faint, x0, contentW - btn * 2, ref y, _ops);
        y += 6 * s;
        FillRect(_g, 0x22FFFFFF, x0, y, W - x0 - pad, Math.Max(1, s));
        y += 6 * s;
        _viewTop = y;

        float footerH = chat ? f.BodyLine + 12 * s : f.SmallLine + 8 * s;
        float footerTop = H - pad - footerH;
        _viewBottom = footerTop - 10 * s;
        _text.Draw(_g, _ops, float.MaxValue);

        // ---- Kayan içerik (içerik koordinatları: 0 = görünür alanın tepesi, kaydırmadan önce)
        _ops.Clear();
        _itemRects.Clear();
        _backgrounds.Clear();
        _pills.Clear();
        float cy = 0;
        if (chat) BuildChat(f, s, x0, contentW, accent, White, Bold, Muted, Faint, ref cy);
        else BuildSettings(f, s, x0, contentW, accent, White, Bold, Muted, Faint, ref cy);

        _contentH = cy;
        float viewH = _viewBottom - _viewTop;
        float maxScroll = Math.Max(0, _contentH - viewH);
        _scroll = _stick && chat ? maxScroll : Math.Clamp(_scroll, 0, maxScroll);

        float off = _viewTop - _scroll;
        Gdip.GdipSetClipRect(_g, 0, _viewTop, W, viewH, 0);
        foreach (var (top, bottom, color) in _backgrounds)
            FillRoundRect(_g, color, x0 - 6 * s, top + off, contentW + 12 * s, bottom - top, 6 * s);
        for (int i = 1; i < _itemRects.Count; i++)
            FillRect(_g, 0x14FFFFFF, x0, _itemRects[i].top + off, contentW, Math.Max(1, s));
        foreach (var (px, py, pw, ph, pc) in _pills)
            FillRoundRect(_g, pc, px, py + off, pw, ph, ph / 2);
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

        // ---- Alt bölüm
        _ops.Clear();
        if (chat) DrawInput(f, s, x0, W, pad, footerTop, footerH, White, Faint);
        else if (_status.Length > 0)
            _ops.Add(new TextRenderer.Op { Text = _status, X = x0, Y = footerTop + 2 * s, Font = f.SmallBold, Color = accent, LineHeight = f.SmallLine });
        _text.Draw(_g, _ops, float.MaxValue);

        // ---- Kaydırma çubuğu
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

    /// <summary>Sohbet içeriği: en eski başta, en yeni sonda.</summary>
    private void BuildChat(TextRenderer.Fonts f, float s, float x0, float contentW, uint accent,
                           uint White, uint Bold, uint Muted, uint Faint, ref float cy)
    {
        if (_items.Count == 0)
        {
            _text.LayoutPlain("Bu oyun için henüz kayıtlı soru yok. Aşağıya yazarak ya da Mouse 5'i basılı tutup konuşarak soru sor; burada görünecek.",
                f.Body, f.BodyLine, Muted, x0, contentW, ref cy, _ops);
            return;
        }
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            bool open = _expanded.Contains(i);
            bool isPending = _pending && i == _items.Count - 1;
            float top = cy;
            cy += 7 * s;
            string when = isPending ? "şimdi" : it.At.ToString("dd.MM · HH:mm", CultureInfo.InvariantCulture);
            _ops.Add(new TextRenderer.Op { Text = (open ? "▾ " : "▸ ") + when, X = x0, Y = cy, Font = f.Small, Color = Faint, LineHeight = f.SmallLine });
            cy += f.SmallLine;
            var q = string.IsNullOrWhiteSpace(it.Q) ? "(soru metni yok)" : it.Q;
            _text.LayoutPlain(q, open ? f.Bold : f.Body, f.BodyLine, open ? Bold : White, x0, contentW, ref cy, _ops);
            if (open && !string.IsNullOrWhiteSpace(it.A))
            {
                cy += 4 * s;
                _text.LayoutRich(it.A, f, White, accent, Bold, x0 + 6 * s, contentW - 6 * s, ref cy, _ops);
            }
            else if (open && isPending)
            {
                cy += 4 * s;
                _text.LayoutPlain("Düşünüyor…", f.SmallItalic, f.SmallLine, Faint, x0 + 6 * s, contentW - 6 * s, ref cy, _ops);
            }
            cy += 7 * s;
            _itemRects.Add((top, cy));
            if (i == _hover) _backgrounds.Add((top, cy, 0x18FFFFFF));
            else if (open) _backgrounds.Add((top, cy, 0x0CFFFFFF));
        }
    }

    /// <summary>Ayarlar içeriği: her satırda etiket + açıklama solda, değer hapı sağda.</summary>
    private void BuildSettings(TextRenderer.Fonts f, float s, float x0, float contentW, uint accent,
                               uint White, uint Bold, uint Muted, uint Faint, ref float cy)
    {
        BuildRows();
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            string val = r.Value();
            float pw = _text.Measure(f.SmallBold, val) + 22 * s;
            float ph = f.SmallLine + 8 * s;
            float top = cy;
            cy += 8 * s;
            float labelW = Math.Max(60 * s, contentW - pw - 14 * s);
            float rowTop = cy;
            _text.LayoutPlain(r.Label, f.Body, f.BodyLine, White, x0, labelW, ref cy, _ops);
            if (!string.IsNullOrEmpty(r.Desc))
                _text.LayoutPlain(r.Desc, f.Small, f.SmallLine, Faint, x0, labelW, ref cy, _ops);
            float rowH = cy - rowTop;
            float py = rowTop + (rowH - ph) / 2;
            bool on = r.On?.Invoke() ?? false;
            uint pillColor = r.On != null ? (on ? (accent & 0x00FFFFFF) | 0x99000000 : 0x26FFFFFF) : 0x26FFFFFF;
            _pills.Add((x0 + contentW - pw, py, pw, ph, pillColor));
            _ops.Add(new TextRenderer.Op { Text = val, X = x0 + contentW - pw + 11 * s, Y = py + 4 * s, Font = f.SmallBold, Color = Bold, LineHeight = f.SmallLine });
            cy += 8 * s;
            _itemRects.Add((top, cy));
            if (i == _hover) _backgrounds.Add((top, cy, 0x18FFFFFF));
        }
    }

    private void DrawInput(TextRenderer.Fonts f, float s, float x0, int W, float pad, float inputTop, float inputH,
                           uint White, uint Faint)
    {
        float bx = x0 - 6 * s, bw = W - bx - pad;
        FillRoundRect(_g, 0x26FFFFFF, bx, inputTop, bw, inputH, 8 * s);
        FillRoundRect(_g, 0xF20D1015, bx + Math.Max(1, s), inputTop + Math.Max(1, s), bw - 2 * Math.Max(1, s), inputH - 2 * Math.Max(1, s), 7 * s);
        float tx = bx + 10 * s, ty = inputTop + (inputH - f.BodyLine) / 2;
        float availW = bw - 20 * s;
        float caretH = f.BodyLine - 4 * s, caretW = Math.Max(1.5f, 1.5f * s);
        if (_pending)
            _ops.Add(new TextRenderer.Op { Text = "Cevap yazılıyor…", X = tx, Y = ty, Font = f.SmallItalic, Color = Faint, LineHeight = f.BodyLine });
        else if (_input.Length == 0)
        {
            _ops.Add(new TextRenderer.Op { Text = "Bir şey sor…  (Enter: gönder)", X = tx, Y = ty, Font = f.Body, Color = Faint, LineHeight = f.BodyLine });
            if (_caretOn) FillRect(_g, 0xFFFFFFFF, tx - 1 * s, ty + 2 * s, caretW, caretH);
        }
        else
        {
            // İmleç görünür kalsın: soldan kırp, sağdan sığdığı kadar göster
            int a = 0;
            while (a < _caret && _text.Measure(f.Body, _input.Substring(a, _caret - a)) > availW - 4 * s) a++;
            int e = _input.Length;
            while (e > _caret && _text.Measure(f.Body, _input.Substring(a, e - a)) > availW) e--;
            _ops.Add(new TextRenderer.Op { Text = _input.Substring(a, e - a), X = tx, Y = ty, Font = f.Body, Color = White, LineHeight = f.BodyLine });
            if (_caretOn)
                FillRect(_g, 0xFFFFFFFF, tx + _text.Measure(f.Body, _input.Substring(a, _caret - a)), ty + 2 * s, caretW, caretH);
        }
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
