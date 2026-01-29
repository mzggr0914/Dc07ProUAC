using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Dc07ProUAC;

public record FilterStatus(byte DigitalFilter, byte HpFilter);
public record SpdifBalanceGainStatus(byte SpdifMode, byte Balance, byte Gain);

public sealed class Dc07ProController : IDisposable
{
    private readonly Dc07ProHidTransport _t;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly ConcurrentDictionary<byte, TaskCompletionSource<byte[]>> _waiters = new();

    public Dc07ProController(Dc07ProHidTransport transport)
    {
        _t = transport ?? throw new ArgumentNullException(nameof(transport));
        _t.FrameReceived += OnFrame;
    }

    public void Dispose()
    {
        _t.FrameReceived -= OnFrame;

        foreach (var kv in _waiters)
            kv.Value.TrySetCanceled();

        _waiters.Clear();
        _ioGate.Dispose();
    }

    private void OnFrame(byte[] payload)
    {
        if (payload == null || payload.Length == 0) return;

        var id = payload[0];

        if (_waiters.TryGetValue(id, out var tcs))
            tcs.TrySetResult(payload);
    }

    private async Task SendAsync(byte[] payload8)
    {
        if (payload8 is not { Length: 8 })
            throw new ArgumentException("payload must be 8 bytes.", nameof(payload8));

        await _ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _t.SendAsync(payload8).ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private async Task<byte[]> RequestOnceAsync(byte expectedId, byte[] requestPayload8, int timeoutMs)
    {
        if (requestPayload8 is not { Length: 8 })
            throw new ArgumentException("payload must be 8 bytes.", nameof(requestPayload8));

        await _ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (_waiters.TryGetValue(expectedId, out var prev))
                prev.TrySetCanceled();

            _waiters[expectedId] = tcs;

            await _t.SendAsync(requestPayload8).ConfigureAwait(false);

            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            _waiters.TryRemove(expectedId, out _);

            if (done != tcs.Task)
                throw new TimeoutException($"Timeout waiting response id=0x{expectedId:X2}");

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private async Task<byte[]> RequestAsync(byte expectedId, byte[] requestPayload8, int timeoutMs = 1200)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await RequestOnceAsync(expectedId, requestPayload8, timeoutMs).ConfigureAwait(false);
            }
            catch (TimeoutException) when (attempt < 2)
            {
                await Task.Delay(40).ConfigureAwait(false);
            }
        }

        throw new TimeoutException($"Timeout waiting response id=0x{expectedId:X2}");
    }

    public async Task InitializeAsync()
    {
        await SendAsync(Dc07ProPackets.PingFpga58()).ConfigureAwait(false);
        await Task.Delay(20).ConfigureAwait(false);
        await SendAsync(Dc07ProPackets.PingFpga58()).ConfigureAwait(false);
        await Task.Delay(20).ConfigureAwait(false);

        await GetFiltersAsync().ConfigureAwait(false);
        await Task.Delay(20).ConfigureAwait(false);
        await GetVolumeAsync().ConfigureAwait(false);
        await Task.Delay(20).ConfigureAwait(false);
        await GetSpdifBalanceGainAsync().ConfigureAwait(false);
    }

    public async Task<int> GetVolumeAsync()
    {
        var resp = await RequestAsync(0x42, Dc07ProPackets.QueryVolume66()).ConfigureAwait(false);
        if (resp.Length >= 5) return resp[4] & 0xFF;
        throw new InvalidOperationException("Bad volume response.");
    }

    public async Task<FilterStatus> GetFiltersAsync()
    {
        var resp = await RequestAsync(0x59, Dc07ProPackets.QueryFilter89()).ConfigureAwait(false);
        return resp.Length >= 6 ? new FilterStatus(resp[4], resp[5]) : throw new InvalidOperationException("Bad filter response.");
    }

    public async Task<SpdifBalanceGainStatus> GetSpdifBalanceGainAsync()
    {
        var resp = await RequestAsync(0x60, Dc07ProPackets.QuerySpdif60()).ConfigureAwait(false);
        return resp.Length >= 7 ? new SpdifBalanceGainStatus(resp[4], resp[5], resp[6]) : throw new InvalidOperationException("Bad SPDIF/BAL/GAIN response.");
    }

    public Task SetVolumeAsync(int v0To100)
    {
        return (uint)v0To100 > 100 ? throw new ArgumentOutOfRangeException(nameof(v0To100)) 
            : SendAsync(Dc07ProPackets.SetVolume((byte)v0To100));
    }

    public Task SetFiltersAsync(int digital0To4, int hp0Or1)
    {
        digital0To4 = Math.Clamp(digital0To4, 0, 4);
        hp0Or1 = Math.Clamp(hp0Or1, 0, 1);
        return SendAsync(Dc07ProPackets.SetFilters((byte)digital0To4, (byte)hp0Or1));
    }

    public Task SetSpdifBalanceGainAsync(int spdif0Or1, int balance0To20, int gain0To2)
    {
        spdif0Or1 = Math.Clamp(spdif0Or1, 0, 1);
        balance0To20 = Math.Clamp(balance0To20, 0, 20);
        gain0To2 = Math.Clamp(gain0To2, 0, 2);
        return SendAsync(Dc07ProPackets.SetSpdifBalanceGain((byte)spdif0Or1, (byte)balance0To20, (byte)gain0To2));
    }
}
