using System;
using System.Runtime.InteropServices;
using ComIStream = System.Runtime.InteropServices.ComTypes.IStream;
using ComSTATSTG = System.Runtime.InteropServices.ComTypes.STATSTG;

namespace InGameCompanion.Native;

/// <summary>GDI+ flat API — overlay çizimi ve JPEG kodlama için.</summary>
internal static class Gdip
{
    public const int PixelFormat24bppRGB = 0x00021808;
    public const int PixelFormat32bppRGB = 0x00022009;
    public const int PixelFormat32bppPARGB = 0x000E200B;

    public const int SmoothingModeAntiAlias = 4;
    public const int TextRenderingHintAntiAlias = 4;
    public const int TextRenderingHintAntiAliasGridFit = 3;
    public const int InterpolationModeHighQualityBilinear = 6;
    public const int PixelOffsetModeHighQuality = 2;
    public const int CompositingQualityHighSpeed = 1;

    public const int FontStyleRegular = 0, FontStyleBold = 1, FontStyleItalic = 2;
    public const int UnitPixel = 2;

    public const int StringFormatFlagsMeasureTrailingSpaces = 0x0800;
    public const int StringFormatFlagsNoWrap = 0x1000;
    public const int StringFormatFlagsNoClip = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    public struct RectF
    {
        public float X, Y, Width, Height;
        public RectF(float x, float y, float w, float h) { X = x; Y = y; Width = w; Height = h; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInput
    {
        public uint GdiplusVersion;
        public IntPtr DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    private const string Dll = "gdiplus.dll";

    [DllImport(Dll)] private static extern int GdiplusStartup(out IntPtr token, ref StartupInput input, IntPtr output);
    [DllImport(Dll)] private static extern void GdiplusShutdown(IntPtr token);

    [DllImport(Dll)] public static extern int GdipCreateBitmapFromScan0(int w, int h, int stride, int format, IntPtr scan0, out IntPtr bitmap);
    [DllImport(Dll)] public static extern int GdipGetImageGraphicsContext(IntPtr image, out IntPtr graphics);
    [DllImport(Dll)] public static extern int GdipDeleteGraphics(IntPtr graphics);
    [DllImport(Dll)] public static extern int GdipDisposeImage(IntPtr image);
    [DllImport(Dll)] public static extern int GdipGraphicsClear(IntPtr graphics, uint argb);
    [DllImport(Dll)] public static extern int GdipSetSmoothingMode(IntPtr graphics, int mode);
    [DllImport(Dll)] public static extern int GdipSetTextRenderingHint(IntPtr graphics, int mode);
    [DllImport(Dll)] public static extern int GdipSetInterpolationMode(IntPtr graphics, int mode);
    [DllImport(Dll)] public static extern int GdipSetPixelOffsetMode(IntPtr graphics, int mode);
    [DllImport(Dll)] public static extern int GdipSetCompositingQuality(IntPtr graphics, int mode);

    [DllImport(Dll)] public static extern int GdipCreateSolidFill(uint argb, out IntPtr brush);
    [DllImport(Dll)] public static extern int GdipDeleteBrush(IntPtr brush);
    [DllImport(Dll)] public static extern int GdipFillRectangle(IntPtr g, IntPtr brush, float x, float y, float w, float h);
    [DllImport(Dll)] public static extern int GdipSetClipRect(IntPtr g, float x, float y, float w, float h, int combineMode);
    [DllImport(Dll)] public static extern int GdipResetClip(IntPtr g);
    [DllImport(Dll)] public static extern int GdipFillEllipse(IntPtr g, IntPtr brush, float x, float y, float w, float h);

    [DllImport(Dll)] public static extern int GdipCreatePath(int fillMode, out IntPtr path);
    [DllImport(Dll)] public static extern int GdipAddPathArc(IntPtr path, float x, float y, float w, float h, float start, float sweep);
    [DllImport(Dll)] public static extern int GdipClosePathFigure(IntPtr path);
    [DllImport(Dll)] public static extern int GdipFillPath(IntPtr g, IntPtr brush, IntPtr path);
    [DllImport(Dll)] public static extern int GdipDeletePath(IntPtr path);

    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern int GdipCreateFontFamilyFromName(string name, IntPtr collection, out IntPtr family);
    [DllImport(Dll)] public static extern int GdipDeleteFontFamily(IntPtr family);
    [DllImport(Dll)] public static extern int GdipCreateFont(IntPtr family, float emSize, int style, int unit, out IntPtr font);
    [DllImport(Dll)] public static extern int GdipDeleteFont(IntPtr font);
    [DllImport(Dll)] public static extern int GdipGetFontHeight(IntPtr font, IntPtr graphics, out float height);

    [DllImport(Dll)] public static extern int GdipStringFormatGetGenericTypographic(out IntPtr format);
    [DllImport(Dll)] public static extern int GdipCloneStringFormat(IntPtr format, out IntPtr clone);
    [DllImport(Dll)] public static extern int GdipSetStringFormatFlags(IntPtr format, int flags);
    [DllImport(Dll)] public static extern int GdipDeleteStringFormat(IntPtr format);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int GdipDrawString(IntPtr g, string text, int length, IntPtr font, ref RectF layout, IntPtr format, IntPtr brush);
    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int GdipMeasureString(IntPtr g, string text, int length, IntPtr font, ref RectF layout, IntPtr format,
        out RectF bounds, out int codepointsFitted, out int linesFilled);

    [DllImport(Dll)] public static extern int GdipDrawImageRectI(IntPtr g, IntPtr image, int x, int y, int w, int h);
    [DllImport(Dll)] public static extern int GdipCloneBitmapAreaI(int x, int y, int w, int h, int format, IntPtr src, out IntPtr dst);
    [DllImport(Dll)] public static extern int GdipSaveImageToStream(IntPtr image, IntPtr stream, ref Guid clsidEncoder, IntPtr encoderParams);

    private static IntPtr _token;

    public static void Startup()
    {
        if (_token != IntPtr.Zero) return;
        var input = new StartupInput { GdiplusVersion = 1 };
        int st = GdiplusStartup(out _token, ref input, IntPtr.Zero);
        if (st != 0) throw new InvalidOperationException("GDI+ başlatılamadı: " + st);
    }

    public static void Shutdown()
    {
        if (_token == IntPtr.Zero) return;
        GdiplusShutdown(_token);
        _token = IntPtr.Zero;
    }

    public static void Check(int status, string what)
    {
        if (status != 0) throw new InvalidOperationException($"GDI+ hatası {status}: {what}");
    }

    // ---------- JPEG ----------
    private static Guid JpegEncoderClsid = new("557CF401-1A04-11D3-9A73-0000F81EF32E");
    private static readonly Guid EncoderQuality = new("1D5BE4B5-FA4A-452D-9CDD-5DB35105E7EB");

    /// <summary>GDI+ görüntüsünü bellekte JPEG'e kodlar.</summary>
    public static byte[] EncodeJpeg(IntPtr image, int quality)
    {
        // EncoderParameters { UINT Count; EncoderParameter[1] } — x64'te parametre 8. bayttan başlar.
        // EncoderParameter { GUID(16), ULONG NumberOfValues(4), ULONG Type(4), void* Value(8) }
        int paramOffset = IntPtr.Size == 8 ? 8 : 4;
        int total = paramOffset + 16 + 4 + 4 + IntPtr.Size;
        IntPtr p = Marshal.AllocHGlobal(total);
        IntPtr qVal = Marshal.AllocHGlobal(4);
        IntPtr stream = IntPtr.Zero;
        try
        {
            Marshal.WriteInt32(qVal, Math.Clamp(quality, 1, 100));
            for (int i = 0; i < total; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt32(p, 0, 1);
            Marshal.Copy(EncoderQuality.ToByteArray(), 0, p + paramOffset, 16);
            Marshal.WriteInt32(p, paramOffset + 16, 1);  // NumberOfValues
            Marshal.WriteInt32(p, paramOffset + 20, 4);  // EncoderParameterValueTypeLong
            Marshal.WriteIntPtr(p, paramOffset + 24, qVal);

            stream = Win32.SHCreateMemStream(IntPtr.Zero, 0);
            if (stream == IntPtr.Zero) throw new InvalidOperationException("SHCreateMemStream başarısız");
            Check(GdipSaveImageToStream(image, stream, ref JpegEncoderClsid, p), "SaveImageToStream");

            var com = (ComIStream)Marshal.GetObjectForIUnknown(stream);
            try
            {
                com.Stat(out ComSTATSTG stat, 1 /* STATFLAG_NONAME */);
                long size = stat.cbSize;
                com.Seek(0, 0, IntPtr.Zero);
                var buf = new byte[size];
                com.Read(buf, (int)size, IntPtr.Zero);
                return buf;
            }
            finally
            {
                Marshal.ReleaseComObject(com);
            }
        }
        finally
        {
            if (stream != IntPtr.Zero) Marshal.Release(stream);
            Marshal.FreeHGlobal(qVal);
            Marshal.FreeHGlobal(p);
        }
    }
}
