using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using InGameCompanion.Core;
using InGameCompanion.Native;

namespace InGameCompanion.Media;

internal sealed record AudioClip(byte[] Wav, double Seconds, int Peak);

/// <summary>
/// Tuş basılıyken mikrofonu RAM'e kaydeder (16 kHz, 16-bit, mono PCM → WAV).
/// winmm waveIn API'si doğrudan kullanılır; mikrofon yalnızca tuş basılıyken açıktır.
/// </summary>
internal sealed class MicRecorder
{
    public const int SampleRate = 16000;
    private const int BufferMs = 40;
    private const int BufferCount = 6;
    private static readonly int HdrSize = Marshal.SizeOf<Win32.WAVEHDR>();

    private readonly int _device;
    private readonly MemoryStream _pcm = new();
    private readonly TaskCompletionSource<AudioClip?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _opened = new();
    private volatile bool _stop;
    private volatile bool _cancel;
    private string? _openError;

    public MicRecorder(int device) => _device = device;

    /// <summary>Her dolan tampon için mikrofon iş parçacığından çağrılır (canlı yazı için). Hızlı dönmeli.</summary>
    public Action<byte[]>? ChunkReady { get; set; }

    /// <summary>Kaydı başlatır; aygıt açılana kadar (tipik ~10-40 ms) bekler.</summary>
    public void Start()
    {
        var t = new Thread(Run) { IsBackground = true, Name = "MicRecorder", Priority = ThreadPriority.AboveNormal };
        t.Start();
        _opened.Wait(1500);
        if (_openError != null) throw new InvalidOperationException(_openError);
    }

    public Task<AudioClip?> StopAsync()
    {
        _stop = true;
        return _done.Task;
    }

    public void Cancel()
    {
        _cancel = true;
        _stop = true;
    }

    private void Run()
    {
        var fmt = new Win32.WAVEFORMATEX
        {
            wFormatTag = 1, // PCM
            nChannels = 1,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = 16,
            nBlockAlign = 2,
            nAvgBytesPerSec = SampleRate * 2,
            cbSize = 0,
        };
        using var evt = new AutoResetEvent(false);
        uint dev = _device < 0 ? Win32.WAVE_MAPPER : (uint)_device;
        int rc = Win32.waveInOpen(out IntPtr hwi, dev, ref fmt, evt.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, Win32.CALLBACK_EVENT);
        if (rc != 0)
        {
            _openError = rc == 2 /*MMSYSERR_BADDEVICEID*/ || rc == 6 /*NODRIVER*/
                ? "Mikrofon bulunamadı"
                : $"Mikrofon açılamadı (waveInOpen={rc}). Windows gizlilik ayarlarında masaüstü uygulamalarına mikrofon izni verili mi?";
            _opened.Set();
            _done.TrySetResult(null);
            return;
        }

        int bufBytes = SampleRate * 2 * BufferMs / 1000;
        var headers = new IntPtr[BufferCount];
        var datas = new IntPtr[BufferCount];
        try
        {
            for (int i = 0; i < BufferCount; i++)
            {
                datas[i] = Marshal.AllocHGlobal(bufBytes);
                headers[i] = Marshal.AllocHGlobal(HdrSize);
                var hdr = new Win32.WAVEHDR { lpData = datas[i], dwBufferLength = (uint)bufBytes };
                Marshal.StructureToPtr(hdr, headers[i], false);
                Win32.waveInPrepareHeader(hwi, headers[i], HdrSize);
                Win32.waveInAddBuffer(hwi, headers[i], HdrSize);
            }
            Win32.waveInStart(hwi);
            _opened.Set();

            while (!_stop)
            {
                evt.WaitOne(25);
                Drain(hwi, headers, requeue: true);
            }

            // Kalan tamponları topla
            Win32.waveInStop(hwi);
            Win32.waveInReset(hwi);
            Drain(hwi, headers, requeue: false);

            for (int i = 0; i < BufferCount; i++) Win32.waveInUnprepareHeader(hwi, headers[i], HdrSize);
        }
        catch (Exception ex)
        {
            Log.Error("Mikrofon kaydı hatası", ex);
        }
        finally
        {
            Win32.waveInClose(hwi);
            for (int i = 0; i < BufferCount; i++)
            {
                if (headers[i] != IntPtr.Zero) Marshal.FreeHGlobal(headers[i]);
                if (datas[i] != IntPtr.Zero) Marshal.FreeHGlobal(datas[i]);
            }
            _opened.Set();
        }

        if (_cancel) { _done.TrySetResult(null); return; }
        var pcm = _pcm.ToArray();
        _done.TrySetResult(new AudioClip(ToWav(pcm), pcm.Length / (SampleRate * 2.0), Peak(pcm)));
    }

    private int _next; // tamponlar sırayla dolar; sırayı korumak için halka indeksi

    private void Drain(IntPtr hwi, IntPtr[] headers, bool requeue)
    {
        for (int n = 0; n < headers.Length; n++)
        {
            IntPtr h = headers[_next];
            var hdr = Marshal.PtrToStructure<Win32.WAVEHDR>(h);
            if ((hdr.dwFlags & Win32.WHDR_DONE) == 0) break; // sıradaki henüz dolmadı
            if (hdr.dwBytesRecorded > 0)
            {
                var tmp = new byte[hdr.dwBytesRecorded];
                Marshal.Copy(hdr.lpData, tmp, 0, tmp.Length);
                _pcm.Write(tmp, 0, tmp.Length);
                try { ChunkReady?.Invoke(tmp); } catch { }
            }
            // DONE bayrağını temizle ki aynı tampon iki kez okunmasın
            hdr.dwFlags &= ~Win32.WHDR_DONE;
            hdr.dwBytesRecorded = 0;
            Marshal.StructureToPtr(hdr, h, false);
            if (requeue) Win32.waveInAddBuffer(hwi, h, HdrSize);
            _next = (_next + 1) % headers.Length;
        }
    }

    private static int Peak(byte[] pcm)
    {
        int peak = 0;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            int v = Math.Abs((int)BitConverter.ToInt16(pcm, i));
            if (v > peak) peak = v;
        }
        return peak;
    }

    public static byte[] ToWav(byte[] pcm)
    {
        using var ms = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);          // PCM
        w.Write((short)1);          // mono
        w.Write(SampleRate);
        w.Write(SampleRate * 2);    // byte rate
        w.Write((short)2);          // block align
        w.Write((short)16);         // bits
        w.Write("data"u8.ToArray());
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }
}
