using System.IO.Pipes;
using System.Text.Json;

namespace Piper.App;

/// <summary>Forwards SAZ open requests from a second Piper launch to the existing window.</summary>
internal sealed class SazFileRelay : IDisposable
{
    public const string MutexName = "Local\\Piper.SazImport.SingleInstance";
    private const string PipeName = "Piper.SazImport.OpenFiles";

    // A launch hands over a command line, which Windows caps at 32,767 characters; the quarter
    // megabyte leaves room for JSON escaping and is the most a local process can make us buffer.
    internal const int MaxPayloadBytes = 256 * 1024;
    // A client that connects and then goes quiet would hold the single pipe instance for ever.
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly Action<IReadOnlyList<string>> _onFilesReceived;
    private readonly string _pipeName;
    private readonly TimeSpan _readTimeout;
    private readonly CancellationTokenSource _cancellation = new();
    // Taken once, before the listener starts: reading Token from a disposed source throws.
    private readonly CancellationToken _token;
    private readonly Task _listener;

    public SazFileRelay(Action<IReadOnlyList<string>> onFilesReceived) : this(onFilesReceived, PipeName) { }

    internal SazFileRelay(Action<IReadOnlyList<string>> onFilesReceived, string pipeName, TimeSpan? readTimeout = null)
    {
        _onFilesReceived = onFilesReceived;
        _pipeName = pipeName;
        _readTimeout = readTimeout ?? DefaultReadTimeout;
        _token = _cancellation.Token;
        _listener = Task.Run(ListenAsync);
    }

    /// <summary>Completes when the listener has stopped; only <see cref="Dispose"/> makes it.</summary>
    internal Task Listener => _listener;

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

    // Some Fiddler builds export the identical SAZ zip layout under a ".raz" extension.
    public static bool IsSazFile(string path) =>
        (path.EndsWith(".saz", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".raz", StringComparison.OrdinalIgnoreCase)) && File.Exists(path);

    private async Task ListenAsync()
    {
        while (!_token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_token).ConfigureAwait(false);

                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(_token);
                readTimeout.CancelAfter(_readTimeout);
                var payload = await ReadPayloadAsync(server, readTimeout.Token).ConfigureAwait(false);
                if (payload is null) continue;

                var files = JsonSerializer.Deserialize<string[]>(payload) ?? [];
                var validFiles = files.Where(path => path is not null && IsSazFile(path)).ToArray();
                _onFilesReceived(validFiles);
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { return; }
            // The read deadline, not shutdown: this client was too slow, so drop it.
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (JsonException) { }
        }
    }

    /// <summary>
    /// Reads one message, at most <see cref="MaxPayloadBytes"/> bytes. Returns null for anything
    /// longer, which no real launch produces, without reading the rest of it.
    /// </summary>
    internal static async Task<byte[]?> ReadPayloadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxPayloadBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        return length > MaxPayloadBytes ? null : buffer.AsSpan(0, length).ToArray();
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        // The listener must be gone before the source is: it reads the token on every turn.
        var stopped = false;
        try { stopped = _listener.Wait(StopTimeout); }
        catch (AggregateException) { stopped = true; }
        if (stopped) _cancellation.Dispose();
    }
}
