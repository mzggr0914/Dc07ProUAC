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
        int matchScore)
    {
        public string DevicePath { get; } = devicePath;
        public int Vid { get; } = vid;
        public int Pid { get; } = pid;

        public string ProductName { get; } = productName;
        public string Manufacturer { get; } = manufacturer;
        public string SerialNumber { get; } = serialNumber;

        public int MatchScore { get; } = matchScore;

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
                if (!string.IsNullOrWhiteSpace(sn)) return sn;
                return DevicePath;
            }
        }

        public string MatchText => $"{MatchScore}%";
    }

    public sealed class ViewModel : INotifyPropertyChanged
    {
        private static readonly char[] TokenSplitChars = [' '];

        private static readonly string[] NoiseProductNames =
        [
            "hid interface",
            "hid-compliant device",
            "usb input device",
            "composite device",
            "generic hid",
            "hid device"
        ];

        private static readonly string[] PreferredBrandTokens = ["ibasso"];
        private static readonly string[] PreferredModelTokens = ["dc07pro", "dc07", "dc07 pro"];

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

        private DeviceRow _selected;
        public DeviceRow Selected
        {
            get => _selected;
            set
            {
                if (SetField(ref _selected, value))
                {
                    OnPropertyChanged(nameof(CanAccept));
                    _acceptCommand.RaiseCanExecuteChanged();
                }
            }
        }

        private string _searchText = "Dc07Pro";
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetField(ref _searchText, value))
                    RefreshAsync(reScoreOnly: true).Forget();
            }
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (SetField(ref _isLoading, value))
                {
                    OnPropertyChanged(nameof(CanAccept));
                    _refreshCommand.RaiseCanExecuteChanged();
                    _acceptCommand.RaiseCanExecuteChanged();
                }
            }
        }

        private string _statusText = string.Empty;
        public string StatusText
        {
            get => _statusText;
            set => SetField(ref _statusText, value);
        }

        private string _errorText = string.Empty;
        public string ErrorText
        {
            get => _errorText;
            set
            {
                if (SetField(ref _errorText, value))
                    OnPropertyChanged(nameof(HasError));
            }
        }

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
                                var score = ComputeScore(query, product, manu, haystack);

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

        private static int ComputeScore(string query, string product, string manufacturer, string haystack)
        {
            var baseScore = SimilarityScore(query, haystack);

            var pNorm = Normalize(product);
            var mNorm = Normalize(manufacturer);

            if (IsNoiseProductName(pNorm)) baseScore -= 15;

            if (ContainsAny(mNorm, PreferredBrandTokens)) baseScore += 35;
            if (ContainsAny(pNorm, PreferredModelTokens) || ContainsAny(Normalize(query), PreferredModelTokens)) baseScore += 25;

            if (pNorm.Contains("dc07", StringComparison.Ordinal)) baseScore += 10;
            if (mNorm.Contains("ibasso", StringComparison.Ordinal)) baseScore += 10;

            return Clamp(baseScore, 0, 100);

            static bool IsNoiseProductName(string p)
                => NoiseProductNames.Any(n => p.Contains(n, StringComparison.Ordinal));

            static bool ContainsAny(string s, string[] tokens)
                => tokens.Any(t => s.Contains(Normalize(t), StringComparison.Ordinal));
        }

        private static int SimilarityScore(string query, string haystack)
        {
            if (string.IsNullOrWhiteSpace(query)) return 0;

            var q = Normalize(query);
            var h = Normalize(haystack);

            if (h.Contains(q, StringComparison.Ordinal)) return 100;

            var tokens = q.Split(TokenSplitChars, StringSplitOptions.RemoveEmptyEntries)
                          .Distinct()
                          .ToArray();

            var tokenHits = tokens.Count(t => t.Length >= 2 && h.Contains(t, StringComparison.Ordinal));
            var tokenScore = tokens.Length == 0 ? 0 : (int)Math.Round(70.0 * tokenHits / tokens.Length);

            var hShort = h.Length > 80 ? h[..80] : h;
            var lev = LevenshteinDistance(q, hShort);
            var maxLen = Math.Max(q.Length, hShort.Length);
            var levScore = maxLen == 0 ? 0 : (int)Math.Round(30.0 * (1.0 - (double)lev / maxLen));

            return Clamp(tokenScore + Clamp(levScore, 0, 30), 0, 100);
        }

        private static string Normalize(string s)
        {
            var lowered = s.Trim().ToLowerInvariant();
            var chars = lowered.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray();
            var normalized = new string(chars);
            while (normalized.Contains("  ", StringComparison.Ordinal)) normalized = normalized.Replace("  ", " ");
            return normalized.Trim();
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        private static int LevenshteinDistance(string a, string b)
        {
            if (a == b) return 0;
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            var prev = new int[b.Length + 1];
            var curr = new int[b.Length + 1];

            for (var j = 0; j <= b.Length; j++) prev[j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                curr[0] = i;
                var ca = a[i - 1];

                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = ca == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(
                        Math.Min(curr[j - 1] + 1, prev[j] + 1),
                        prev[j - 1] + cost
                    );
                }

                (prev, curr) = (curr, prev);
            }

            return prev[b.Length];
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
        if (task is null) return;

        task.ContinueWith(
            t => Debug.WriteLine(t.Exception),
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously
        );
    }
}
