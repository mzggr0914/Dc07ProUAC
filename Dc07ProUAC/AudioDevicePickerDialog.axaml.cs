using Avalonia.Controls;
using Avalonia.Threading;
using HidSharp;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Dc07ProUAC;

public partial class AudioDevicePickerDialog : Window
{
    public AudioDevicePickerDialog()
    {
        InitializeComponent();

        var vm = new ViewModel();
        vm.RequestClose += Vm_RequestClose;
        DataContext = vm;

        Opened += AudioDevicePickerDialog_Opened;
    }

    private void AudioDevicePickerDialog_Opened(object sender, EventArgs e)
        => Vm.RefreshCommand.Execute(null);

    private void Vm_RequestClose(object result)
        => Close(result);

    private ViewModel Vm => (ViewModel)DataContext;

    public sealed class DeviceRow(
        string devicePath,
        int vid,
        int pid,
        string productName,
        string manufacturer,
        string serialNumber,
        int score)
    {
        public string DevicePath { get; } = devicePath;
        public int Vid { get; } = vid;
        public int Pid { get; } = pid;

        public string ProductName { get; } = productName;
        public string Manufacturer { get; } = manufacturer;
        public string SerialNumber { get; } = serialNumber;
        public int MatchScore { get; } = score;

        public DeviceRow() : this("", 0, 0, "", "", "", 0)
        {
        }

        public string VidPidText => $"VID:{Vid:X4} PID:{Pid:X4}";
        public string Title => string.IsNullOrWhiteSpace(ProductName) ? "(Unknown HID Device)" : ProductName;

        public string Subtitle
        {
            get
            {
                var manu = string.IsNullOrWhiteSpace(Manufacturer) ? "" : Manufacturer.Trim();
                var sn = string.IsNullOrWhiteSpace(SerialNumber) ? "" : $"SN:{SerialNumber.Trim()}";
                if (!string.IsNullOrWhiteSpace(manu) && !string.IsNullOrWhiteSpace(sn)) return $"{manu} · {sn}";
                if (!string.IsNullOrWhiteSpace(manu)) return manu;
                return !string.IsNullOrWhiteSpace(sn) ? sn : DevicePath;
            }
        }
    }

    public sealed class ViewModel : INotifyPropertyChanged
    {
        public event Action<object> RequestClose;

        public ObservableCollection<DeviceRow> Devices { get; } = [];

        public ICommand RefreshCommand { get; }
        public ICommand AcceptCommand { get; }
        public ICommand CancelCommand { get; }

        private readonly AsyncRelayCommand _refreshCommand;
        private readonly RelayCommand _acceptCommand;

        public ViewModel()
        {
            _refreshCommand = new AsyncRelayCommand(
                execute: () => RefreshAsync(reScoreOnly: false),
                canExecute: () => !IsLoading);

            _acceptCommand = new RelayCommand(
                execute: () => RequestClose?.Invoke(Selected),
                canExecute: () => CanAccept);

            var cancelCommand = new RelayCommand(
                execute: () => RequestClose?.Invoke(null));

            RefreshCommand = _refreshCommand;
            AcceptCommand = _acceptCommand;
            CancelCommand = cancelCommand;

            Selected = null;
        }

        public DeviceRow Selected
        {
            get;
            set
            {
                if (!SetField(ref field, value)) return;
                OnPropertyChanged(nameof(CanAccept));
                _acceptCommand.RaiseCanExecuteChanged();
            }
        }

        public string SearchText
        {
            get;
            set
            {
                if (SetField(ref field, value))
                    RefreshAsync(reScoreOnly: true).Forget();
            }
        } = "Dc07Pro";

        public bool IsLoading
        {
            get;
            set
            {
                if (!SetField(ref field, value)) return;
                OnPropertyChanged(nameof(CanAccept));
                _refreshCommand.RaiseCanExecuteChanged();
                _acceptCommand.RaiseCanExecuteChanged();
            }
        }

        public string StatusText
        {
            get;
            set => SetField(ref field, value);
        } = string.Empty;

        public string ErrorText
        {
            get;
            set
            {
                if (SetField(ref field, value))
                    OnPropertyChanged(nameof(HasError));
            }
        } = string.Empty;

        public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
        public bool CanAccept => Selected != null && !IsLoading;

        private HidDevice[] _cachedDevices = [];

        public async Task RefreshAsync(bool reScoreOnly = false)
        {
            if (IsLoading) return;

            IsLoading = true;
            ErrorText = "";
            StatusText = reScoreOnly ? "Updating ranking..." : "Scanning HID devices...";

            try
            {
                var query = (SearchText ?? "").Trim();

                if (!reScoreOnly)
                {
                    _cachedDevices = await Task.Run(() =>
                    {
                        try
                        {
                            return DeviceList.Local.GetHidDevices().ToArray();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(ex);
                            throw;
                        }
                    });
                }

                var rows = await Task.Run(() =>
                {
                    try
                    {
                        return _cachedDevices
                            .Select(d =>
                            {
                                int vid = 0, pid = 0;
                                string product = "", manu = "", sn = "", path = "";

                                try { vid = d.VendorID; } catch (Exception ex) { Debug.WriteLine(ex); }
                                try { pid = d.ProductID; } catch (Exception ex) { Debug.WriteLine(ex); }

                                try
                                {
                                    var p1 = d.GetProductName() ?? "";
                                    var p2 = d.GetFriendlyName() ?? "";
                                    product = string.IsNullOrWhiteSpace(p2) ? p1 : $"{p1}: {p2}";
                                }
                                catch (Exception ex) { Debug.WriteLine(ex); }

                                try { manu = d.GetManufacturer() ?? ""; } catch (Exception ex) { Debug.WriteLine(ex); }
                                try { sn = d.GetSerialNumber() ?? ""; } catch (Exception ex) { Debug.WriteLine(ex); }
                                try { path = d.DevicePath ?? ""; } catch (Exception ex) { Debug.WriteLine(ex); }

                                var haystack = $"{product} {manu} {sn} {vid:X4}:{pid:X4} {path}";
                                var score = HidDeviceService.ComputeScore(query, product, manu, haystack);

                                return new DeviceRow(path, vid, pid, product, manu, sn, score);
                            })
                            .OrderByDescending(r => r.MatchScore)
                            .ThenBy(r => r.Title)
                            .ThenBy(r => r.Vid)
                            .ThenBy(r => r.Pid)
                            .ToArray();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                        throw;
                    }
                });

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var prevPath = Selected?.DevicePath;

                    Devices.Clear();
                    foreach (var r in rows) Devices.Add(r);

                    Selected = prevPath is null
                        ? Devices.FirstOrDefault() ?? new DeviceRow()
                        : Devices.FirstOrDefault(x => x.DevicePath == prevPath) ?? Devices.FirstOrDefault() ?? new DeviceRow();

                    var shownQuery = string.IsNullOrWhiteSpace(query) ? "(empty)" : query;
                    StatusText = $"Found {Devices.Count} HID devices (ranked by \"{shownQuery}\")";
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ErrorText = ex.Message;
                    StatusText = "Failed to load devices";
                });
            }
            finally
            {
                IsLoading = false;
            }
        }


        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    private sealed class RelayCommand(Action execute, Func<bool> canExecute = null) : ICommand
    {
        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter) => canExecute?.Invoke() ?? true;

        public void Execute(object parameter) => execute();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute = null) : ICommand
    {
        private bool _running;

        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter) => !_running && (canExecute?.Invoke() ?? true);

        public void Execute(object parameter) => Run().Forget();

        private async Task Run()
        {
            if (!CanExecute(null)) return;

            try
            {
                _running = true;
                RaiseCanExecuteChanged();
                await execute();
            }
            finally
            {
                _running = false;
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal static class TaskExtensions
{
    public static void Forget(this Task task)
    {
        task?.ContinueWith(
            t => Debug.WriteLine(t.Exception),
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously
        );
    }
}
