using System;
using System.Threading.Tasks;

namespace Dc07ProUAC.Core.Device;

public interface IDc07Transport : IDisposable
{
    event Action<byte[]>? FrameReceived;

    Task SendAsync(byte[] payload8, int retry = 3);
}
