using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace WpfInspectorMcp;

internal static class InspectionAgentClient
{
    private const int MaximumMessageSize = 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    internal static async Task<string> RequestAsync(
        string pipeName,
        string secret,
        string operation,
        object? arguments = null,
        CancellationToken cancellationToken = default)
    {
        Log($"Agent request starting: operation={operation}, pipe={pipeName}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            Log($"Agent pipe connected: operation={operation}, pipe={pipeName}.");

            var request = JsonSerializer.SerializeToUtf8Bytes(new { secret, operation, arguments });
            await WriteFrameAsync(pipe, request, timeout.Token).ConfigureAwait(false);
            Log($"Agent request written: operation={operation}, pipe={pipeName}, length={request.Length}.");

            var response = await ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false);
            Log($"Agent response received: operation={operation}, pipe={pipeName}, length={response.Length}.");
            return Encoding.UTF8.GetString(response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log($"Agent request timed out: operation={operation}, pipe={pipeName}.");
            throw new TimeoutException($"The inspection agent did not respond within {RequestTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception exception)
        {
            Log($"Agent request failed: operation={operation}, pipe={pipeName}, error={exception.Message}");
            throw;
        }
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length > MaximumMessageSize)
            throw new InvalidDataException($"Inspection request exceeds {MaximumMessageSize} bytes.");

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaximumMessageSize)
            throw new InvalidDataException($"Invalid inspection response length: {length}.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static void Log(string message)
    {
        var line = $"[{DateTimeOffset.UtcNow:O}] WpfInspectorMcp.AgentIpc {message}";
        Console.Error.WriteLine(line);
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), $"WpfInspectorMcp-{Environment.ProcessId}.log"), line + Environment.NewLine); } catch { }
    }
}
