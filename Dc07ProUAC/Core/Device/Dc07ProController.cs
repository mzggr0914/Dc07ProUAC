using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using Dc07ProUAC.Core.Models;
using Dc07ProUAC.Core.Protocol;

namespace Dc07ProUAC.Core.Device;

public sealed class Dc07ProController : IDisposable
{
    private readonly IDc07Transport _transport;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly ConcurrentDictionary<byte, TaskCompletionSource<byte[]>> _waiters = new();

    public Dc07ProController(IDc07Transport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _transport.FrameReceived += OnFrame;
    }

    public void Dispose()
    {
        _transport.FrameReceived -= OnFrame;
        foreach (var waiter in _waiters.Values) waiter.TrySetCanceled();
        _waiters.Clear();
        _ioGate.Dispose();
    }

    private void OnFrame(byte[] payload)
    {
        if (payload.Length == 0) return;
        if (_waiters.TryGetValue(payload[0], out var tcs))
            tcs.TrySetResult(payload);
    }

    private async Task SendAsync(byte[] payload8)
    {
        await _ioGate.WaitAsync().ConfigureAwait(false);
        try { await _transport.SendAsync(payload8).ConfigureAwait(false); }
        finally { _ioGate.Release(); }
    }

    private async Task<byte[]> RequestOnceAsync(byte expectedId, byte[] requestPayload8, int timeoutMs)
    {
        await _ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_waiters.TryGetValue(expectedId, out var previous)) previous.TrySetCanceled();
            _waiters[expectedId] = tcs;

            try
            {
                await _transport.SendAsync(requestPayload8).ConfigureAwait(false);
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (completed != tcs.Task)
                    throw new TimeoutException($"Timeout waiting response id=0x{expectedId:X2}");
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                _waiters.TryRemove(expectedId, out _);
            }
        }
        finally { _ioGate.Release(); }
    }

    private async Task<byte[]> RequestAsync(byte expectedId, byte[] requestPayload8, int timeoutMs = Dc07ProtocolConstants.RequestTimeoutMs)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { return await RequestOnceAsync(expectedId, requestPayload8, timeoutMs).ConfigureAwait(false); }
            catch (TimeoutException) when (attempt < 2) { await Task.Delay(40).ConfigureAwait(false); }
        }
        throw new TimeoutException($"Timeout waiting response id=0x{expectedId:X2}");
    }

    public async Task InitializeAsync()
    {
        await SendAsync(Dc07ProPackets.PingFpga()).ConfigureAwait(false);
        await Task.Delay(Dc07ProtocolConstants.InitializationSettleDelayMs).ConfigureAwait(false);
        await SendAsync(Dc07ProPackets.PingFpga()).ConfigureAwait(false);
        await Task.Delay(Dc07ProtocolConstants.InitializationSettleDelayMs).ConfigureAwait(false);
        await GetFiltersAsync().ConfigureAwait(false);
        await Task.Delay(Dc07ProtocolConstants.InitializationSettleDelayMs).ConfigureAwait(false);
        await GetVolumeAsync().ConfigureAwait(false);
        await Task.Delay(Dc07ProtocolConstants.InitializationSettleDelayMs).ConfigureAwait(false);
        await GetSpdifBalanceGainAsync().ConfigureAwait(false);
    }

    public async Task<int> GetVolumeAsync()
    {
        var response = await RequestAsync(Dc07ProtocolConstants.ResponseIds.Volume, Dc07ProPackets.QueryVolume()).ConfigureAwait(false);
        return response.Length >= 5 ? response[4] : throw new InvalidOperationException("Bad volume response.");
    }

    public async Task<FilterStatus> GetFiltersAsync()
    {
        var response = await RequestAsync(Dc07ProtocolConstants.ResponseIds.Filters, Dc07ProPackets.QueryFilters()).ConfigureAwait(false);
        return response.Length >= 6 ? new FilterStatus(response[4], response[5]) : throw new InvalidOperationException("Bad filter response.");
    }

    public async Task<SpdifBalanceGainStatus> GetSpdifBalanceGainAsync()
    {
        var response = await RequestAsync(Dc07ProtocolConstants.ResponseIds.SpdifBalanceGain, Dc07ProPackets.QuerySpdifBalanceGain()).ConfigureAwait(false);
        return response.Length >= 7 ? new SpdifBalanceGainStatus(response[4], response[5], response[6]) : throw new InvalidOperationException("Bad SPDIF/BAL/GAIN response.");
    }

    public Task SetVolumeAsync(int value) => value is < 0 or > 100
        ? throw new ArgumentOutOfRangeException(nameof(value))
        : SendAsync(Dc07ProPackets.SetVolume((byte)value));

    public Task SetFiltersAsync(int digitalFilter, int hpFilter) =>
        SendAsync(Dc07ProPackets.SetFilters((byte)Math.Clamp(digitalFilter, 0, 4), (byte)Math.Clamp(hpFilter, 0, 1)));

    public Task SetSpdifBalanceGainAsync(int spdif, int balance, int gain) =>
        SendAsync(Dc07ProPackets.SetSpdifBalanceGain((byte)Math.Clamp(spdif, 0, 1), (byte)Math.Clamp(balance, 0, 20), (byte)Math.Clamp(gain, 0, 2)));
}
