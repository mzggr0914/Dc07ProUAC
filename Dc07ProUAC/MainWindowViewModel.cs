using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Dc07ProUAC;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    public enum GainMode { Low, Mid, High }

    public event PropertyChangedEventHandler PropertyChanged = delegate { };
    public event EventHandler<bool> ThemeChanged = delegate { };

    private Dc07ProHidTransport _transport = null!;
    private Dc07ProController _dc07 = null!;
    private bool _hasTransport;
    private bool _hasController;

    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private CancellationTokenSource _applyCts = new();
    private DateTime _lastApplyUtc = DateTime.MinValue;

    private bool _dirtySinceLastApply;
    private bool _dirtySbg;
    private bool _dirtyFilters;
    private bool _dirtyVolume;

    private bool _suppress;

    private bool _hasSelectedDevice;

    private bool _hasPicker;

    private readonly RelayCommand _volumeUpCommand;
    private readonly RelayCommand _volumeDownCommand;
    private readonly RelayCommand _balanceCenterCommand;
    private readonly AsyncRelayCommand _refreshCommand;
    private readonly AsyncRelayCommand _applyCommand;
    private readonly AsyncRelayCommand _selectDeviceCommand;

    private readonly JsonSettingsStore _settingsStore = new();
    private AppSettings _settings = new();

    public bool IsConnected
    {
        get;
        private set
        {
            if (!SetProperty(ref field, value)) return;
            OnPropertyChanged(nameof(ConnectionText));
            OnPropertyChanged(nameof(ConnectionIcon));
            TouchStatus();
        }
    }

    public string ConnectionText => IsConnected ? "Device Connected" : "Device Disconnected";
    public string ConnectionIcon => IsConnected ? "PlugConnected" : "PlugDisconnected";

    public bool IsDarkMode
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            if (!_suppress) ThemeChanged(this, value);
        }
    }

    public void SetDarkModeFromSystem(bool isDark)
    {
        _suppress = true;
        IsDarkMode = isDark;
        _suppress = false;
    }

    public bool IsAutoApply
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            TouchStatus();
            if (value) _ = ApplyNowAsync(throttle: false);
        }
    }

    public int Volume
    {
        get;
        set
        {
            var v = Clamp(value, 0, 100);
            if (!SetProperty(ref field, v)) return;
            OnPropertyChanged(nameof(VolumeDisplay));
            if (_suppress) return;
            _dirtyVolume = true;
            MarkDirtyAndMaybeApply();
        }
    }

    public string VolumeDisplay => $"{Volume}%";

    public bool SpdifEnabled
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            if (_suppress) return;
            _dirtySbg = true;
            MarkDirtyAndMaybeApply();
        }
    }

    public bool HpFilterEnabled
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            if (_suppress) return;
            _dirtyFilters = true;
            MarkDirtyAndMaybeApply();
        }
    }

    public ObservableCollection<string> Filters { get; } =
        new(new[] { "SLOW", "FAST", "LL/F", "LL/S", "NOS" });

    public string SelectedFilter
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            if (_suppress) return;
            _dirtyFilters = true;
            MarkDirtyAndMaybeApply();
        }
    } = "FAST";

    public GainMode Gain
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            OnPropertyChanged(nameof(IsGainLow));
            OnPropertyChanged(nameof(IsGainMid));
            OnPropertyChanged(nameof(IsGainHigh));
            if (_suppress) return;
            _dirtySbg = true;
            MarkDirtyAndMaybeApply();
        }
    } = GainMode.Low;

    public bool IsGainLow
    {
        get => Gain == GainMode.Low;
        set { if (value) Gain = GainMode.Low; }
    }

    public bool IsGainMid
    {
        get => Gain == GainMode.Mid;
        set { if (value) Gain = GainMode.Mid; }
    }

    public bool IsGainHigh
    {
        get => Gain == GainMode.High;
        set { if (value) Gain = GainMode.High; }
    }

    public int Balance
    {
        get;
        set
        {
            var v = Clamp(value, -10, 10);
            if (!SetProperty(ref field, v)) return;
            OnPropertyChanged(nameof(BalanceDisplay));
            if (_suppress) return;
            _dirtySbg = true;
            MarkDirtyAndMaybeApply();
        }
    }

    public string BalanceDisplay =>
        Balance == 0 ? "Center" : (Balance < 0 ? $"L {Math.Abs(Balance)}" : $"R {Balance}");

    public string FooterStatus
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public Func<Task<AudioDevicePickerDialog.DeviceRow>> ShowDevicePickerAsync
    {
        get;
        set
        {
            if (value is null)
            {
                field = DummyPicker;
                _hasPicker = false;
                return;
            }

            field = value;
            _hasPicker = true;
        }
    }

    public AudioDevicePickerDialog.DeviceRow SelectedDevice
    {
        get;
        private set
        {
            field = value;
            _hasSelectedDevice = value is not null;
            RaiseCommandStates();
            TouchStatus();
        }
    }

    public ICommand VolumeUpCommand => _volumeUpCommand;
    public ICommand VolumeDownCommand => _volumeDownCommand;
    public ICommand BalanceCenterCommand => _balanceCenterCommand;
    public ICommand RefreshCommand => _refreshCommand;
    public ICommand ApplyCommand => _applyCommand;
    public ICommand SelectDeviceCommand => _selectDeviceCommand;

    public MainWindowViewModel()
    {
        _volumeUpCommand = new RelayCommand(() => Volume += 1, CanUseDeviceCommands);
        _volumeDownCommand = new RelayCommand(() => Volume -= 1, CanUseDeviceCommands);
        _balanceCenterCommand = new RelayCommand(() => Balance = 0, CanUseDeviceCommands);

        _refreshCommand = new AsyncRelayCommand(RefreshFromDeviceAsync, CanUseDeviceCommands);
        _applyCommand = new AsyncRelayCommand(() => ApplyNowAsync(throttle: false), CanUseDeviceCommands);
        _selectDeviceCommand = new AsyncRelayCommand(PickDeviceAsync, () => true);

        TouchStatus();
        _ = InitializeAsync();
    }

    public void Dispose()
    {
        try { _applyCts.Cancel(); } catch { }
        try { _applyCts.Dispose(); } catch { }

        try { _ioLock.Dispose(); } catch { }

        if (_hasController)
            try { _dc07.Dispose(); } catch { }

        if (!_hasTransport) return;
        try { _transport.Dispose(); } catch { }
    }

    private bool CanUseDeviceCommands() => _hasSelectedDevice;

    private void RaiseCommandStates()
    {
        _volumeUpCommand.RaiseCanExecuteChanged();
        _volumeDownCommand.RaiseCanExecuteChanged();
        _balanceCenterCommand.RaiseCanExecuteChanged();
        _refreshCommand.RaiseCanExecuteChanged();
        _applyCommand.RaiseCanExecuteChanged();
        _selectDeviceCommand.RaiseCanExecuteChanged();
    }

    public async Task PickDeviceAsync()
    {
        if (!_hasPicker) return;

        var row = await ShowDevicePickerAsync();
        if (row is null) return;

        SelectedDevice = row;

        try
        {
            _settings ??= new AppSettings();
            _settings.LastDevice = HidDeviceService.ToSnapshot(row);
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        var ok = await TryConnectAsync();
        if (ok) await RefreshFromDeviceAsync();
    }

    private async Task InitializeAsync()
    {
        try { _settings = await _settingsStore.LoadAsync(); }
        catch (Exception ex) { Debug.WriteLine(ex); _settings = new AppSettings(); }

        if (!_hasSelectedDevice && _settings.LastDevice is not null)
        {
            try
            {
                var current = await HidDeviceService.ListRowsAsync();
                var match = HidDeviceService.FindBestMatch(current, _settings.LastDevice);

                if (match is not null)
                {
                    SelectedDevice = match;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        if (!_hasSelectedDevice)
        {
            IsConnected = false;
            FooterStatus = BuildStatusLine(applied: false, note: "Select a device");
            return;
        }

        await TryConnectAsync();
        if (IsConnected) await RefreshFromDeviceAsync();
    }

    private async Task<bool> TryConnectAsync()
    {
        if (!_hasSelectedDevice)
        {
            IsConnected = false;
            FooterStatus = BuildStatusLine(applied: false, note: "Select a device");
            return false;
        }

        await _ioLock.WaitAsync();
        try
        {
            if (_hasController)
            {
                _dc07.Dispose();
                _hasController = false;
            }

            if (_hasTransport)
            {
                _transport.Dispose();
                _hasTransport = false;
            }

            var t = new Dc07ProHidTransport();
            await t.OpenAsync(SelectedDevice.Vid, SelectedDevice.Pid);
            _transport = t;
            _hasTransport = true;

            _dc07 = new Dc07ProController(_transport);
            await _dc07.InitializeAsync();
            _hasController = true;

            IsConnected = true;
            FooterStatus = BuildStatusLine(applied: true, note: "Connected");
            return true;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            FooterStatus = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            _ioLock.Release();
            RaiseCommandStates();
        }
    }

    private async Task RefreshFromDeviceAsync()
    {
        if (!_hasSelectedDevice)
        {
            IsConnected = false;
            FooterStatus = BuildStatusLine(applied: false, note: "Select a device");
            return;
        }

        if (!IsConnected || !_hasController)
        {
            var ok = await TryConnectAsync();
            if (!ok) return;
        }

        await _ioLock.WaitAsync();
        try
        {
            var vol = await _dc07.GetVolumeAsync();
            var f = await _dc07.GetFiltersAsync();
            var sbg = await _dc07.GetSpdifBalanceGainAsync();

            _suppress = true;

            Volume = Clamp(vol, 0, 100);

            var uiFilterIndex = DeviceFilterToUiIndex(f.DigitalFilter);
            uiFilterIndex = Clamp(uiFilterIndex, 0, Filters.Count - 1);
            SelectedFilter = Filters[uiFilterIndex];

            HpFilterEnabled = f.HpFilter != 0;
            SpdifEnabled = sbg.SpdifMode != 0;

            Gain = sbg.Gain switch
            {
                0 => GainMode.Low,
                1 => GainMode.Mid,
                _ => GainMode.High
            };

            Balance = Clamp(sbg.Balance - 10, -10, 10);

            _dirtySbg = _dirtyFilters = _dirtyVolume = false;
            _dirtySinceLastApply = false;

            _suppress = false;

            TouchStatus();
            FooterStatus = BuildStatusLine(applied: true, note: "Refreshed");
        }
        catch (Exception ex)
        {
            FooterStatus = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            _suppress = false;
            _ioLock.Release();
        }
    }

    private void MarkDirtyAndMaybeApply()
    {
        _dirtySinceLastApply = true;
        TouchStatus();
        if (!IsAutoApply) return;
        ScheduleDelayedApply(150);
    }

    private async Task ApplyNowAsync(bool throttle)
    {
        if (!_hasSelectedDevice)
        {
            IsConnected = false;
            FooterStatus = BuildStatusLine(applied: false, note: "Select a device");
            return;
        }

        if (!IsConnected || !_hasController)
        {
            var ok = await TryConnectAsync();
            if (!ok) return;
        }

        if (throttle)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastApplyUtc).TotalMilliseconds < 120)
            {
                ScheduleDelayedApply(160);
                return;
            }
        }

        _lastApplyUtc = DateTime.UtcNow;

        await _ioLock.WaitAsync();
        try
        {
            var spdif = SpdifEnabled ? 1 : 0;
            var bal = Clamp(Balance + 10, 0, 20);
            var gain = Gain switch
            {
                GainMode.Low => 0,
                GainMode.Mid => 1,
                _ => 2
            };

            var uiFilterIndex = Clamp(Filters.IndexOf(SelectedFilter), 0, 4);
            var deviceFilter = UiFilterToDeviceIndex(uiFilterIndex);
            var hp = HpFilterEnabled ? 1 : 0;

            if (_dirtySbg)
            {
                await _dc07.SetSpdifBalanceGainAsync(spdif, bal, gain);
                _dirtySbg = false;
                await Task.Delay(12);
            }

            if (_dirtyFilters)
            {
                await _dc07.SetFiltersAsync(deviceFilter, hp);
                _dirtyFilters = false;
                await Task.Delay(12);
            }

            if (_dirtyVolume)
            {
                await _dc07.SetVolumeAsync(Clamp(Volume, 0, 100));
                _dirtyVolume = false;
            }

            _dirtySinceLastApply = false;
            FooterStatus = BuildStatusLine(applied: true, note: IsAutoApply ? "Auto applied" : "Applied");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            FooterStatus = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private void ScheduleDelayedApply(int delayMs)
    {
        try { _applyCts.Cancel(); } catch { }
        try { _applyCts.Dispose(); } catch { }

        _applyCts = new CancellationTokenSource();
        var token = _applyCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, token);
                if (token.IsCancellationRequested) return;
                await ApplyNowAsync(throttle: false);
            }
            catch
            {
            }
        }, token);
    }

    private void TouchStatus()
    {
        if (!_hasSelectedDevice)
        {
            FooterStatus = BuildStatusLine(applied: false, note: "Select a device");
            return;
        }

        if (!IsConnected)
        {
            FooterStatus = BuildStatusLine(applied: false, note: "Disconnected");
            return;
        }

        FooterStatus = _dirtySinceLastApply
            ? BuildStatusLine(applied: false, note: IsAutoApply ? "Pending…" : "Changed")
            : BuildStatusLine(applied: true, note: IsAutoApply ? "Synced" : "Ready");
    }

    private string BuildStatusLine(bool applied, string note)
    {
        var g = Gain switch
        {
            GainMode.Low => "Low",
            GainMode.Mid => "Middle",
            _ => "High"
        };

        var spdif = SpdifEnabled ? "ON" : "OFF";
        var hp = HpFilterEnabled ? "ON" : "OFF";
        var applyMode = IsAutoApply ? "Auto" : "Manual";
        var state = applied ? "OK" : "…";

        return $"[{state}] {note} • Vol {Volume}% • Bal {Balance} • Gain {g} • Filter {SelectedFilter} • SPDIF {spdif} • HP {hp} • {applyMode}";
    }

    private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string name)
    {
        if (name.Length == 0) return;
        PropertyChanged(this, new PropertyChangedEventArgs(name));
    }

    private static int UiFilterToDeviceIndex(int uiIndex) =>
        uiIndex switch
        {
            0 => 1,
            1 => 0,
            _ => uiIndex
        };

    private static int DeviceFilterToUiIndex(int deviceIndex) =>
        deviceIndex switch
        {
            0 => 1,
            1 => 0,
            _ => deviceIndex
        };

    private static Task<AudioDevicePickerDialog.DeviceRow> DummyPicker() =>
        Task.FromResult<AudioDevicePickerDialog.DeviceRow>(null!);

    private sealed class RelayCommand(Action execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler CanExecuteChanged = delegate { };

        public bool CanExecute(object parameter) => canExecute();

        public void Execute(object parameter)
        {
            if (!canExecute()) return;
            execute();
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged(this, EventArgs.Empty);
    }

    private sealed class AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        private bool _isRunning;

        public event EventHandler CanExecuteChanged = delegate { };

        public bool CanExecute(object parameter) => !_isRunning && canExecute();

        public void Execute(object parameter) => _ = ExecuteAsync();

        public void RaiseCanExecuteChanged() => CanExecuteChanged(this, EventArgs.Empty);

        private async Task ExecuteAsync()
        {
            if (_isRunning) return;
            if (!canExecute()) return;

            _isRunning = true;
            RaiseCanExecuteChanged();

            try
            {
                await execute().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
            finally
            {
                _isRunning = false;
                RaiseCanExecuteChanged();
            }
        }
    }
}
