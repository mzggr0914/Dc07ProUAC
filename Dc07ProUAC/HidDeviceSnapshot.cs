using System;

namespace Dc07ProUAC;

public sealed class HidDeviceSnapshot
{
    public int Vid { get; set; }
    public int Pid { get; set; }

    public string ProductName { get; set; }
    public string Manufacturer { get; set; }
    public string SerialNumber { get; set; }
    public string DevicePath { get; set; }

    public DateTime LastSelectedUtc { get; set; } = DateTime.UtcNow;
}