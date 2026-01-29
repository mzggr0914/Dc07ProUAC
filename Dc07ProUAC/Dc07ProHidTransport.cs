using HidSharp;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Dc07ProUAC;

public sealed class Dc07ProHidTransport : IDisposable
{
    private HidStream _stream;
    private CancellationTokenSource _cts;
    private Task _readLoop;

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _lastWriteTick;

    public event Action<byte[]> FrameReceived;

    public async Task OpenAsync(int vid, int pid, string preferPathContains = null)
    {
        var list = DeviceList.Local.GetHidDevices(vid, pid).ToList();
        if (list.Count == 0) throw new InvalidOperationException("DC07 Pro HID device not found.");

        var dev = Pick();

        if (!dev.TryOpen(out _stream) || _stream is null)
            throw new InvalidOperationException("Found device but failed to open stream.");

        _stream.ReadTimeout = Timeout.Infinite;
        _stream.WriteTimeout = 500;

        _cts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoop(_cts.Token));
        await Task.CompletedTask;
        return;

        HidDevice Pick()
        {
            if (!string.IsNullOrWhiteSpace(preferPathContains))
            {
                var hit = list.FirstOrDefault(d =>
                    d.DevicePath.Contains(preferPathContains, StringComparison.OrdinalIgnoreCase));
                if (hit is not null) return hit;
            }

            var best = list
                .OrderByDescending(d => d.GetMaxOutputReportLength())
                .ThenByDescending(d => d.GetMaxInputReportLength())
                .First();

            return best;
        }
    }

    private void ReadLoop(CancellationToken ct)
    {
        var s = _stream;
        if (s is null) return;

        var inLen = s.Device.GetMaxInputReportLength();
        if (inLen < 9) inLen = 9;

        var buf = new byte[inLen];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var n = s.Read(buf, 0, buf.Length);
                if (n <= 1) continue;

                var payload = new byte[8];
                Buffer.BlockCopy(buf, 1, payload, 0, 8);
                FrameReceived?.Invoke(payload);
            }
            catch
            {
                break;
            }
        }
    }

    public async Task SendAsync(byte[] payload8, int retry = 3)
    {
        if (_stream is null) throw new InvalidOperationException("Not opened.");
        if (payload8 is null || payload8.Length != 8) throw new ArgumentException("payload must be 8 bytes.");

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var s = _stream ?? throw new InvalidOperationException("Not opened.");

            var outLen = s.Device.GetMaxOutputReportLength();
            if (outLen < 9) outLen = 9;

            var report = new byte[outLen];
            report[0] = 0x00;
            Buffer.BlockCopy(payload8, 0, report, 1, 8);

            for (var i = 0; i < retry; i++)
            {
                var nowTick = Environment.TickCount64;
                var delta = nowTick - Interlocked.Read(ref _lastWriteTick);
                if (delta < 12) await Task.Delay((int)(12 - delta)).ConfigureAwait(false);

                try
                {
                    await s.WriteAsync(report.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                    Interlocked.Exchange(ref _lastWriteTick, Environment.TickCount64);
                    break;
                }
                catch
                {
                    if (i == retry - 1) throw;
                    await Task.Delay(2).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); }
        catch
        {
            // ignored
        }

        try { _readLoop.Wait(200); }
        catch
        {
            // ignored
        }

        _stream.Dispose();
        _cts.Dispose();
        _writeGate.Dispose();
        _stream = null;
        _cts = null;
    }
}