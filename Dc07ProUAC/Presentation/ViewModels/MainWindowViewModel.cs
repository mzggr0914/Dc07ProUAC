using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

using Dc07ProUAC.Core.Models;
using Dc07ProUAC.Infrastructure.Hid;
using Dc07ProUAC.Infrastructure.Settings;
using Dc07ProUAC.Services;

namespace Dc07ProUAC.Presentation.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<bool> ThemeChanged = delegate { };

    private readonly Dc07DeviceSession _session = new();
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private CancellationTokenSource _applyCts = new();
    private DateTime _lastApplyUtc = DateTime.MinValue;

    private bool _dirtySinceLastApply;
    private bool _dirtySbg;
    private bool _dirtyFilters;
    private bool _dirtyVolume;

    private bool _suppress;

    private bool _hasSelectedDevice;

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
        ["SLOW", "FAST", "LL/F", "LL/S", "NOS"];

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
    } = string.Empty;

    public Func<Task<HidDeviceInfo?>> ShowDevicePickerAsync { get; set; } = DummyPicker;
    public HidDeviceInfo? SelectedDevice
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
        _session.Disconnected += OnSessionDisconnected;

        TouchStatus();
        _ = InitializeAsync();
    }

    public void Dispose()
    {
        _session.Disconnected -= OnSessionDisconnected;

        try { _applyCts.Cancel(); }
        catch (Exception ex) { Debug.WriteLine(ex); }
        try { _applyCts.Dispose(); }
        catch (Exception ex) { Debug.WriteLine(ex); }
        try { _session.Dispose(); }
        catch (Exception ex) { Debug.WriteLine(ex); }
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
        var row = await ShowDevicePickerAsync();
        if (row is null) return;

        SelectedDevice = row;

        try
        {
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
                var current = await HidDeviceService.ListAsync();
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
        var device = SelectedDevice;
        if (device is null)
        {
            IsConnected = false;
            FooterStatus = BuildStatusLine(applied: false, note: "Select a device");
            return false;
        }

        try
        {
            await _session.ConnectAsync(device);
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

        if (!IsConnected || !_session.IsConnected)
        {
            var ok = await TryConnectAsync();
            if (!ok) return;
        }

        try
        {
            var state = await _session.ReadStateAsync();
            _suppress = true;

            Volume = Clamp(state.Volume, 0, 100);
            var uiFilterIndex = Clamp(DeviceFilterToUiIndex(state.DigitalFilter), 0, Filters.Count - 1);
            SelectedFilter = Filters[uiFilterIndex];
            HpFilterEnabled = state.HpFilter != 0;
            SpdifEnabled = state.SpdifMode != 0;

            Gain = state.Gain switch
            {
                0 => GainMode.Low,
                1 => GainMode.Mid,
                _ => GainMode.High
            };
            Balance = Clamp(state.Balance - 10, -10, 10);

            _dirtySbg = _dirtyFilters = _dirtyVolume = false;
            _dirtySinceLastApply = false;
            TouchStatus();
            FooterStatus = BuildStatusLine(applied: true, note: "Refreshed");
        }
        catch (Exception ex)
        {
            IsConnected = _session.IsConnected;
            FooterStatus = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            _suppress = false;
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

        if (!IsConnected || !_session.IsConnected)
        {
            var ok = await TryConnectAsync();
            if (!ok) return;
        }

        if (throttle && (DateTime.UtcNow - _lastApplyUtc).TotalMilliseconds < 120)
        {
            ScheduleDelayedApply(160);
            return;
        }

        _lastApplyUtc = DateTime.UtcNow;

        try
        {
            var sections = Dc07SettingsSection.None;
            if (_dirtySbg) sections |= Dc07SettingsSection.SpdifBalanceGain;
            if (_dirtyFilters) sections |= Dc07SettingsSection.Filters;
            if (_dirtyVolume) sections |= Dc07SettingsSection.Volume;

            var request = new Dc07ApplyRequest(
                sections,
                SpdifEnabled ? 1 : 0,
                Clamp(Balance + 10, 0, 20),
                Gain switch { GainMode.Low => 0, GainMode.Mid => 1, _ => 2 },
                UiFilterToDeviceIndex(Clamp(Filters.IndexOf(SelectedFilter), 0, 4)),
                HpFilterEnabled ? 1 : 0,
                Clamp(Volume, 0, 100));

            await _session.ApplyAsync(request);
            _dirtySbg = _dirtyFilters = _dirtyVolume = false;
            _dirtySinceLastApply = false;
            FooterStatus = BuildStatusLine(applied: true, note: IsAutoApply ? "Auto applied" : "Applied");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            IsConnected = _session.IsConnected;
            FooterStatus = $"{ex.GetType().Name}: {ex.Message}";
        }
    }
    private void ScheduleDelayedApply(int delayMs)
    {
        try { _applyCts.Cancel(); }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        try { _applyCts.Dispose(); }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

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
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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

    private static Task<HidDeviceInfo?> DummyPicker() => Task.FromResult<HidDeviceInfo?>(null);

    private void OnSessionDisconnected(Exception? ex)
    {
        void Update()
        {
            IsConnected = false;
            FooterStatus = ex is null
                ? BuildStatusLine(applied: false, note: "Disconnected")
                : $"{ex.GetType().Name}: {ex.Message}";
            RaiseCommandStates();
        }

        if (_uiContext is null) Update();
        else _uiContext.Post(_ => Update(), null);
    }
    private sealed class RelayCommand(Action execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute();

        public void Execute(object? parameter)
        {
            if (!canExecute()) return;
            execute();
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        private bool _isRunning;

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => !_isRunning && canExecute();

        public void Execute(object? parameter) => _ = ExecuteAsync();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

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
