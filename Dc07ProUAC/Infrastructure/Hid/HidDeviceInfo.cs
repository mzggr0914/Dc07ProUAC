namespace Dc07ProUAC.Infrastructure.Hid;

public sealed record HidDeviceInfo(
    string DevicePath,
    int Vid,
    int Pid,
    string ProductName,
    string Manufacturer,
    string SerialNumber,
    int MatchScore)
{
    public string Title => string.IsNullOrWhiteSpace(ProductName)
        ? "(Unknown HID Device)"
        : ProductName;

}
