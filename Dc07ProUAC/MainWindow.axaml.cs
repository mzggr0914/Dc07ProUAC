using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using static Dc07ProUAC.AudioDevicePickerDialog;

namespace Dc07ProUAC
{
    public partial class MainWindow : Window
    {
        private MainWindowViewModel _vm;

        public MainWindow()
        {
            InitializeComponent();

            HookVm();

            DataContextChanged += (_, _) => HookVm();

            AttachedToVisualTree += (_, _) =>
            {
                SyncVmFromSystemTheme();
                Dispatcher.UIThread.Post(SyncVmFromSystemTheme, DispatcherPriority.Loaded);
            };

            Application.Current?.ActualThemeVariantChanged += (_, _) =>
            {
                Dispatcher.UIThread.Post(SyncVmFromSystemTheme, DispatcherPriority.Background);
            };

        }

        private void HookVm()
        {
            _vm?.ThemeChanged -= VmOnThemeChanged;

            _vm = DataContext as MainWindowViewModel;

            if (_vm is null) return;
           
            _vm.ThemeChanged += VmOnThemeChanged;
            SyncVmFromSystemTheme();

            _vm.ShowDevicePickerAsync = async () =>
            {
                var dlg = new AudioDevicePickerDialog();
                return await dlg.ShowDialog<DeviceRow>(this);
            };
        }

        private void VmOnThemeChanged(object sender, bool isDark)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var v = isDark ? ThemeVariant.Dark : ThemeVariant.Light;

                RequestedThemeVariant = v;
                    Application.Current?.RequestedThemeVariant = v;
            }, DispatcherPriority.Send);
        }

        private void SyncVmFromSystemTheme()
        {
            if (_vm is null) return;

            var sys = Application.Current?.ActualThemeVariant;

            var isDark =
                sys == ThemeVariant.Dark ||
                ActualThemeVariant == ThemeVariant.Dark;

            _vm.SetDarkModeFromSystem(isDark);
        }
    }
}