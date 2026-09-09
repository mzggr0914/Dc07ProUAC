using Dc07ProUAC.Infrastructure.Hid;

namespace Dc07ProUAC.Infrastructure.Settings;

public sealed class AppSettings
{
    public HidDeviceSnapshot? LastDevice { get; set; }
}
