using System.Net;
using System.Net.Sockets;
using System.Text;
using Locara.Core;
using Microsoft.Data.Sqlite;

namespace Locara.Checks;

internal sealed class TempWorkspace : IDisposable
{
    public string Root { get; }

    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "locara-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

internal static class TestHelpers
{
    public static string SamplesRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../samples"));

    public static ProviderOptions ScopedOptions =>
        new("openai-compatible", "fixture", "", "http://127.0.0.1:4242/v1", BatchSize: 1, MaxRequests: 1);

    public static async Task<(T Result, string RequestBody)> CallLoopback<T>(
        int statusCode, string responseJson, Func<string, Task<T>> run)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buffer = new byte[4096];
            var received = new MemoryStream();
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int read = await stream.ReadAsync(buffer);
                if (read == 0) break;
                received.Write(buffer, 0, read);
                headerEnd = Encoding.ASCII.GetString(received.GetBuffer(), 0, (int)received.Length)
                    .IndexOf("\r\n\r\n", StringComparison.Ordinal);
            }
            string requestBody = "";
            if (headerEnd >= 0)
            {
                string headers = Encoding.ASCII.GetString(received.GetBuffer(), 0, headerEnd);
                int bodyOffset = headerEnd + 4;
                int contentLength = 0;
                foreach (string line in headers.Split("\r\n"))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
                }
                if (contentLength > 0)
                {
                    while ((int)received.Length - bodyOffset < contentLength)
                    {
                        int read = await stream.ReadAsync(buffer);
                        if (read == 0) break;
                        received.Write(buffer, 0, read);
                    }
                    requestBody = Encoding.UTF8.GetString(received.GetBuffer(), bodyOffset,
                        Math.Min(contentLength, (int)received.Length - bodyOffset));
                }
                else if (headers.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase))
                {
                    while (!Encoding.ASCII.GetString(received.GetBuffer(), bodyOffset, (int)received.Length - bodyOffset)
                        .Contains("0\r\n\r\n", StringComparison.Ordinal))
                    {
                        int read = await stream.ReadAsync(buffer);
                        if (read == 0) break;
                        received.Write(buffer, 0, read);
                    }
                    requestBody = DecodeChunked(received.GetBuffer().AsSpan(bodyOffset, (int)received.Length - bodyOffset));
                }
            }
            byte[] body = Encoding.UTF8.GetBytes(responseJson);
            byte[] head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {statusCode} {(statusCode == 200 ? "OK" : "Error")}\r\n"
                + $"Content-Type: application/json\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n");
            await stream.WriteAsync(head);
            await stream.WriteAsync(body);
            await stream.FlushAsync();
            return requestBody;
        });

        try
        {
            T result = await run($"http://127.0.0.1:{port}");
            return (result, await server);
        }
        catch
        {
            await server;
            throw;
        }
    }

    private static string DecodeChunked(ReadOnlySpan<byte> raw)
    {
        var output = new MemoryStream();
        int pos = 0;
        while (pos < raw.Length)
        {
            int crlf = raw[pos..].IndexOf("\r\n"u8);
            if (crlf < 0) break;
            string sizeHex = Encoding.ASCII.GetString(raw.Slice(pos, crlf)).Split(';')[0].Trim();
            if (!int.TryParse(sizeHex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out int chunkSize) || chunkSize == 0)
                break;
            pos += crlf + 2;
            int take = Math.Min(chunkSize, raw.Length - pos);
            output.Write(raw.Slice(pos, take));
            pos += take + 2;
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}

internal sealed class FixtureProvider(CancellationTokenSource? cancel = null) : ITranslationProvider
{
    public int Calls { get; private set; }

    public Task<Dictionary<string, string>> Translate(IReadOnlyList<Unit> units,
        IReadOnlyDictionary<string, string> glossary, CancellationToken cancellation)
    {
        _ = cancel;
        Calls++;
        return Task.FromResult(units.ToDictionary(u => u.Id, u => u.Source + " عربي"));
    }
}

internal sealed class InlineProgress(Action<TranslationProgress> action) : IProgress<TranslationProgress>
{
    public void Report(TranslationProgress value) => action(value);
}
