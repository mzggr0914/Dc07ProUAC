using HidSharp;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Dc07ProUAC.Core.Device;
using Dc07ProUAC.Core.Protocol;

namespace Dc07ProUAC.Infrastructure.Hid;

public sealed class Dc07ProHidTransport : IDc07Transport
{
    private HidStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _lastWriteTick;
    private int _disconnectRaised;
    private bool _disposing;

    public event Action<byte[]>? FrameReceived;
    public event Action<Exception?>? Disconnected;

    public Task OpenAsync(int vid, int pid, string? preferredDevicePath = null)
    {
        if (_stream is not null) throw new InvalidOperationException("Transport is already open.");

        var list = DeviceList.Local.GetHidDevices(vid, pid).ToList();
        if (list.Count == 0) throw new InvalidOperationException("DC07 Pro HID device not found.");

        var device = !string.IsNullOrWhiteSpace(preferredDevicePath)
            ? list.FirstOrDefault(d => string.Equals(d.DevicePath, preferredDevicePath, StringComparison.OrdinalIgnoreCase))
            : null;

        device ??= list
            .OrderByDescending(d => d.GetMaxOutputReportLength())
            .ThenByDescending(d => d.GetMaxInputReportLength())
            .First();

        if (!device.TryOpen(out var stream) || stream is null)
            throw new InvalidOperationException("Found device but failed to open stream.");

        stream.ReadTimeout = Timeout.Infinite;
        stream.WriteTimeout = 500;
        _stream = stream;
        _cts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoop(stream, _cts.Token));
        return Task.CompletedTask;
    }

    private void ReadLoop(HidStream stream, CancellationToken ct)
    {
        var inLen = Math.Max(stream.Device.GetMaxInputReportLength(), Dc07ProtocolConstants.ReportLength);
        var buf = new byte[inLen];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var n = stream.Read(buf, 0, buf.Length);
                if (n < Dc07ProtocolConstants.ReportLength) continue;

                var payload = new byte[Dc07ProtocolConstants.PayloadLength];
                Buffer.BlockCopy(buf, 1, payload, 0, payload.Length);
                FrameReceived?.Invoke(payload);
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested && !_disposing)
                    RaiseDisconnected(ex);
                break;
            }
        }
    }

    public async Task SendAsync(byte[] payload8, int retry = 3)
    {
        if (payload8 is not { Length: Dc07ProtocolConstants.PayloadLength })
            throw new ArgumentException("payload must be 8 bytes.", nameof(payload8));
        if (retry < 1) throw new ArgumentOutOfRangeException(nameof(retry));

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var stream = _stream ?? throw new InvalidOperationException("Not opened.");
            var outLen = Math.Max(stream.Device.GetMaxOutputReportLength(), Dc07ProtocolConstants.ReportLength);
            var report = new byte[outLen];
            Buffer.BlockCopy(payload8, 0, report, 1, payload8.Length);

            for (var i = 0; i < retry; i++)
            {
                var delta = Environment.TickCount64 - Interlocked.Read(ref _lastWriteTick);
                if (delta < Dc07ProtocolConstants.MinimumWriteIntervalMs)
                    await Task.Delay((int)(Dc07ProtocolConstants.MinimumWriteIntervalMs - delta)).ConfigureAwait(false);

                try
                {
                    await stream.WriteAsync(report.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                    Interlocked.Exchange(ref _lastWriteTick, Environment.TickCount64);
                    return;
                }
                catch (Exception ex)
                {
                    if (i == retry - 1)
                    {
                        RaiseDisconnected(ex);
                        throw;
                    }
                    await Task.Delay(2).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void RaiseDisconnected(Exception? ex)
    {
        if (Interlocked.Exchange(ref _disconnectRaised, 1) != 0) return;
        try { Disconnected?.Invoke(ex); }
        catch (Exception callbackEx) { Debug.WriteLine(callbackEx); }
    }

    public void Dispose()
    {
        _disposing = true;
        var cts = Interlocked.Exchange(ref _cts, null);
        var stream = Interlocked.Exchange(ref _stream, null);
        var readLoop = Interlocked.Exchange(ref _readLoop, null);

        try { cts?.Cancel(); } catch (Exception ex) { Debug.WriteLine(ex); }
        try { stream?.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
        try { readLoop?.Wait(500); } catch (Exception ex) { Debug.WriteLine(ex); }
        try { cts?.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
        _writeGate.Dispose();
    }
}
