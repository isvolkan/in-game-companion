using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using InGameCompanion.Core;
using InGameCompanion.Native;

namespace InGameCompanion.Media;

internal sealed record CaptureResult(
    byte[] FullJpeg,
    byte[]? FocusJpeg,
    int SourceWidth,
    int SourceHeight,
    bool FocusFromCursor,
    long ElapsedMs);

/// <summary>
/// Oyun penceresinin anlık görüntüsünü alır (Borderless Windowed'da DWM kompozisyonundan BitBlt).
/// Overlay penceresi WDA_EXCLUDEFROMCAPTURE ile işaretli olduğu için kareye girmez.
/// İki görüntü üretir: küçültülmüş tam kare + odak noktası çevresinden tam çözünürlüklü kırpma (küçük yazılar için).
/// </summary>
internal static class ScreenCapture
{
    public static CaptureResult Capture(IntPtr gameHwnd, CaptureSettings cfg)
    {
        var sw = Stopwatch.StartNew();
        var rect = GetCaptureRect(gameHwnd);
        int w = rect.Width, h = rect.Height;
        if (w < 16 || h < 16) throw new InvalidOperationException($"Geçersiz yakalama alanı {rect}");

        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
        IntPtr dib = Win32.CreateDib32(screenDc, w, h, out IntPtr bits);
        IntPtr old = Win32.SelectObject(memDc, dib);
        IntPtr full = IntPtr.Zero;
        try
        {
            if (!Win32.BitBlt(memDc, 0, 0, w, h, screenDc, rect.Left, rect.Top, Win32.SRCCOPY))
                throw new InvalidOperationException("BitBlt başarısız: " + Marshal.GetLastWin32Error());

            Gdip.Check(Gdip.GdipCreateBitmapFromScan0(w, h, w * 4, Gdip.PixelFormat32bppRGB, bits, out full), "FromScan0");

            // 1) Küçültülmüş tam kare
            double scale = Math.Min(1.0, (double)cfg.MaxLongEdge / Math.Max(w, h));
            int tw = Math.Max(1, (int)Math.Round(w * scale));
            int th = Math.Max(1, (int)Math.Round(h * scale));
            byte[] fullJpeg = ScaleAndEncode(full, tw, th, cfg.JpegQuality);

            // 2) Odak kırpması
            byte[]? focusJpeg = null;
            bool fromCursor = false;
            if (cfg.FocusCropSize > 0 && (w > cfg.FocusCropSize || h > cfg.FocusCropSize))
            {
                var (fx, fy, cursor) = GetFocusPoint(rect);
                fromCursor = cursor;
                int cw = Math.Min(cfg.FocusCropSize, w);
                int ch = Math.Min(cfg.FocusCropSize, h);
                int cx = Math.Clamp(fx - rect.Left - cw / 2, 0, w - cw);
                int cy = Math.Clamp(fy - rect.Top - ch / 2, 0, h - ch);
                Gdip.Check(Gdip.GdipCloneBitmapAreaI(cx, cy, cw, ch, Gdip.PixelFormat24bppRGB, full, out IntPtr crop), "CloneArea");
                try { focusJpeg = Gdip.EncodeJpeg(crop, Math.Min(95, cfg.JpegQuality + 8)); }
                finally { Gdip.GdipDisposeImage(crop); }
            }

            sw.Stop();
            if (cfg.SaveDebugCaptures) SaveDebug(fullJpeg, focusJpeg);
            return new CaptureResult(fullJpeg, focusJpeg, w, h, fromCursor, sw.ElapsedMilliseconds);
        }
        finally
        {
            if (full != IntPtr.Zero) Gdip.GdipDisposeImage(full);
            Win32.SelectObject(memDc, old);
            Win32.DeleteObject(dib);
            Win32.DeleteDC(memDc);
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static byte[] ScaleAndEncode(IntPtr src, int tw, int th, int quality)
    {
        Gdip.Check(Gdip.GdipCreateBitmapFromScan0(tw, th, 0, Gdip.PixelFormat24bppRGB, IntPtr.Zero, out IntPtr dst), "CreateTarget");
        try
        {
            Gdip.Check(Gdip.GdipGetImageGraphicsContext(dst, out IntPtr g), "Graphics");
            try
            {
                Gdip.GdipSetInterpolationMode(g, Gdip.InterpolationModeHighQualityBilinear);
                Gdip.GdipSetPixelOffsetMode(g, Gdip.PixelOffsetModeHighQuality);
                Gdip.GdipSetCompositingQuality(g, Gdip.CompositingQualityHighSpeed);
                Gdip.Check(Gdip.GdipDrawImageRectI(g, src, 0, 0, tw, th), "DrawImage");
            }
            finally { Gdip.GdipDeleteGraphics(g); }
            return Gdip.EncodeJpeg(dst, quality);
        }
        finally { Gdip.GdipDisposeImage(dst); }
    }

    /// <summary>Oyun penceresinin görünen alanı (gölge/kenar hariç), bulunduğu monitörle kesişimi.</summary>
    public static Win32.RECT GetCaptureRect(IntPtr hwnd)
    {
        IntPtr mon = hwnd != IntPtr.Zero
            ? Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST)
            : MonitorUnderCursor();
        var monRect = Win32.GetMonitorRect(mon);
        if (hwnd == IntPtr.Zero) return monRect;

        Win32.RECT r;
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<Win32.RECT>()) != 0)
            Win32.GetWindowRect(hwnd, out r);

        var result = new Win32.RECT
        {
            Left = Math.Max(r.Left, monRect.Left),
            Top = Math.Max(r.Top, monRect.Top),
            Right = Math.Min(r.Right, monRect.Right),
            Bottom = Math.Min(r.Bottom, monRect.Bottom),
        };
        // Pencere küçük / küçültülmüş ise tüm monitörü al
        if (result.Width < 320 || result.Height < 240) return monRect;
        return result;
    }

    private static IntPtr MonitorUnderCursor()
    {
        Win32.GetCursorPos(out var p);
        return Win32.MonitorFromPoint(p, Win32.MONITOR_DEFAULTTONEAREST);
    }

    /// <summary>İmleç görünürse imleç konumu, değilse (3. şahıs kamera / nişangah) ekran merkezi.</summary>
    private static (int x, int y, bool fromCursor) GetFocusPoint(Win32.RECT r)
    {
        var ci = new Win32.CURSORINFO { cbSize = Marshal.SizeOf<Win32.CURSORINFO>() };
        if (Win32.GetCursorInfo(ref ci) && (ci.flags & Win32.CURSOR_SHOWING) != 0)
        {
            var p = ci.ptScreenPos;
            if (p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom)
                return (p.X, p.Y, true);
        }
        return ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2, false);
    }

    private static void SaveDebug(byte[] full, byte[]? focus)
    {
        try
        {
            var dir = Path.Combine(Log.Dir, "captures");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("HHmmss_fff");
            File.WriteAllBytes(Path.Combine(dir, $"{stamp}_full.jpg"), full);
            if (focus != null) File.WriteAllBytes(Path.Combine(dir, $"{stamp}_focus.jpg"), focus);
        }
        catch { }
    }
}
