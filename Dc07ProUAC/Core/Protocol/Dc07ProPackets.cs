namespace Dc07ProUAC.Core.Protocol;

public static class Dc07ProPackets
{
    private static byte Not(byte value) => (byte)~value;

    public static byte[] PingFpga()
        => [0x58, 0xF1, 0x0E, 0x00, 0x00, 0x00, 0x00, 0x00];

    public static byte[] SetVolume(byte value)
        => [0x01, 0x10, Not(0x10), value, value, 0x00, 0x00, 0x00];

    public static byte[] QueryVolume()
        => [0x42, 0x11, Not(0x11), 0x00, 0x00, 0x00, 0x00, 0x00];

    public static byte[] SetFilters(byte digitalFilterDevice, byte hpFilter)
        => [0x11, 0x20, Not(0x20), 0x00, digitalFilterDevice, hpFilter, 0x00, 0x00];

    public static byte[] QueryFilters()
        => [0x59, 0x21, Not(0x21), 0x00, 0x00, 0x00, 0x00, 0x00];

    public static byte[] SetSpdifBalanceGain(byte spdifMode, byte balance, byte gainRaw0To2)
        => [0x19, 0x30, Not(0x30), 0x00, spdifMode, balance, gainRaw0To2, 0x00];

    public static byte[] QuerySpdifBalanceGain()
        => [0x60, 0x31, Not(0x31), 0x00, 0x00, 0x00, 0x00, 0x00];
}
