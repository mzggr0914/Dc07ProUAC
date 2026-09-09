namespace Dc07ProUAC.Core.Models;

public enum GainMode
{
    Low,
    Mid,
    High
}

public sealed record Dc07DeviceState(
    int Volume,
    byte DigitalFilter,
    byte HpFilter,
    byte SpdifMode,
    byte Balance,
    byte Gain);

public record FilterStatus(byte DigitalFilter, byte HpFilter);
public record SpdifBalanceGainStatus(byte SpdifMode, byte Balance, byte Gain);
