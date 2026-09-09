namespace Dc07ProUAC.Core.Protocol;

public static class Dc07ProtocolConstants
{
    public const int PayloadLength = 8;
    public const int ReportLength = 9;
    public const int MinimumWriteIntervalMs = 12;
    public const int InitializationSettleDelayMs = 20;
    public const int RequestTimeoutMs = 1200;

    public static class ResponseIds
    {
        public const byte Volume = 0x42;
        public const byte Filters = 0x59;
        public const byte SpdifBalanceGain = 0x60;
    }
}
