using System.IO.Pipes;
using Grpc.Net.Client;

namespace sing_box_for_windows.Services.Core;

/// <summary>gRPC channel over a Windows named pipe (boxdd worker relay).</summary>
public static class NamedPipeChannel
{
    public static GrpcChannel Create(string pipeName)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var pipe = new NamedPipeClientStream(
                    ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(cancellationToken);
                return pipe;
            },
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            EnableMultipleHttp2Connections = true,
        };
        return GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions
            {
                HttpHandler = handler,
                MaxReceiveMessageSize = 16 * 1024 * 1024,
            });
    }

    /// <summary>Strips the \\.\pipe\ prefix.</summary>
    public static string PipeName(string pipePath) =>
        pipePath.Replace(@"\\.\pipe\", string.Empty);
}
