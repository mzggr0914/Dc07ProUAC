using System;

namespace Dc07ProUAC.Core.Models;

[Flags]
public enum Dc07SettingsSection
{
    None = 0,
    SpdifBalanceGain = 1 << 0,
    Filters = 1 << 1,
    Volume = 1 << 2
}

public sealed record Dc07ApplyRequest(
    Dc07SettingsSection Sections,
    int SpdifMode,
    int Balance,
    int Gain,
    int DigitalFilter,
    int HpFilter,
    int Volume);
