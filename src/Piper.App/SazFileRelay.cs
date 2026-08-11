using System.IO.Pipes;
using System.Text.Json;

namespace Piper.App;

/// <summary>Forwards SAZ open requests from a second Piper launch to the existing window.</summary>
internal sealed class SazFileRelay : IDisposable
{
    public const string MutexName = "Local\\Piper.SazImport.SingleInstance";
    private const string PipeName = "Piper.SazImport.OpenFiles";

    private readonly Action<IReadOnlyList<string>> _onFilesReceived;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _listener;

    public SazFileRelay(Action<IReadOnlyList<string>> onFilesReceived)
    {
        _onFilesReceived = onFilesReceived;
        _listener = Task.Run(ListenAsync);
    }

    public static bool TryForward(IEnumerable<string> filePaths)
    {
        var payload = JsonSerializer.Serialize(filePaths.Where(IsSazFile).ToArray());
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.None);
                client.Connect(350);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.Write(payload);
                return true;
            }
            catch (TimeoutException) { }
            catch (IOException) { }
            Thread.Sleep(100);
        }
        return false;
    }

    public static bool IsSazFile(string path) =>
        path.EndsWith(".saz", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    private async Task ListenAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_cancellation.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                var payload = await reader.ReadToEndAsync(_cancellation.Token).ConfigureAwait(false);
                var files = JsonSerializer.Deserialize<string[]>(payload) ?? [];
                var validFiles = files.Where(IsSazFile).ToArray();
                _onFilesReceived(validFiles);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { return; }
            catch (IOException) { }
            catch (JsonException) { }
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}
