using System;
using System.Threading;
using System.Threading.Tasks;

using Dc07ProUAC.Core.Device;
using Dc07ProUAC.Core.Models;
using Dc07ProUAC.Core.Protocol;
using Dc07ProUAC.Infrastructure.Hid;

namespace Dc07ProUAC.Services;

public sealed class Dc07DeviceSession : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dc07ProHidTransport? _transport;
    private Dc07ProController? _controller;
    private bool _disposed;

    public event Action<Exception?>? Disconnected;
    public bool IsConnected => _transport is not null && _controller is not null;

    public async Task ConnectAsync(HidDeviceInfo device)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            DisconnectCore();

            Dc07ProHidTransport? transport = null;
            Dc07ProController? controller = null;
            try
            {
                transport = new Dc07ProHidTransport();
                await transport.OpenAsync(device.Vid, device.Pid, device.DevicePath).ConfigureAwait(false);
                controller = new Dc07ProController(transport);
                await controller.InitializeAsync().ConfigureAwait(false);
                transport.Disconnected += OnTransportDisconnected;
                _transport = transport;
                _controller = controller;
            }
            catch
            {
                controller?.Dispose();
                transport?.Dispose();
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<Dc07DeviceState> ReadStateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var controller = RequireController();
            var volume = await controller.GetVolumeAsync().ConfigureAwait(false);
            var filters = await controller.GetFiltersAsync().ConfigureAwait(false);
            var sbg = await controller.GetSpdifBalanceGainAsync().ConfigureAwait(false);
            return new Dc07DeviceState(volume, filters.DigitalFilter, filters.HpFilter, sbg.SpdifMode, sbg.Balance, sbg.Gain);
        }
        finally { _gate.Release(); }
    }

    public async Task ApplyAsync(Dc07ApplyRequest request)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var controller = RequireController();
            if (request.Sections.HasFlag(Dc07SettingsSection.SpdifBalanceGain))
            {
                await controller.SetSpdifBalanceGainAsync(request.SpdifMode, request.Balance, request.Gain).ConfigureAwait(false);
                await Task.Delay(Dc07ProtocolConstants.MinimumWriteIntervalMs).ConfigureAwait(false);
            }
            if (request.Sections.HasFlag(Dc07SettingsSection.Filters))
            {
                await controller.SetFiltersAsync(request.DigitalFilter, request.HpFilter).ConfigureAwait(false);
                await Task.Delay(Dc07ProtocolConstants.MinimumWriteIntervalMs).ConfigureAwait(false);
            }
            if (request.Sections.HasFlag(Dc07SettingsSection.Volume))
                await controller.SetVolumeAsync(request.Volume).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public void Disconnect()
    {
        _gate.Wait();
        try { DisconnectCore(); }
        finally { _gate.Release(); }
    }

    private Dc07ProController RequireController() => _controller ?? throw new InvalidOperationException("Device is not connected.");

    private void OnTransportDisconnected(Exception? ex)
    {
        Disconnected?.Invoke(ex);
    }

    private void DisconnectCore()
    {
        _transport?.Disconnected -= OnTransportDisconnected;
        _controller?.Dispose();
        _transport?.Dispose();
        _controller = null;
        _transport = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _gate.Dispose();
    }
}
