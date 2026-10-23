using System.Net;
using System.Net.Sockets;
using System.Text;

namespace IntelligentScheduler.Infrastructure.Tests;

/// <summary>
/// A minimal OpenAI-compatible endpoint spoken over a raw socket. Using TCP directly rather
/// than HttpListener keeps the test free of URL-reservation permissions on Windows.
/// Each queued response is served once, in order, and every request body is recorded so a
/// test can assert on what the assistant actually sent.
/// </summary>
public sealed class FakeChatEndpoint : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Queue<(int Status, string Body, string ContentType)> _responses = new();
    private readonly List<string> _requests = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;
    private readonly object _gate = new();

    public FakeChatEndpoint()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        BaseAddress = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _loop = Task.Run(AcceptLoopAsync);
    }

    public string BaseAddress { get; }

    public IReadOnlyList<string> Requests
    {
        get { lock (_gate) return [.. _requests]; }
    }

    public FakeChatEndpoint Enqueue(string body, int status = 200, string contentType = "application/json; charset=utf-8")
    {
        lock (_gate) _responses.Enqueue((status, body, contentType));
        return this;
    }

    /// <summary>
    /// Queues a server-sent-event answer: the shape a real endpoint uses when the request asks
    /// to stream, where the text arrives a fragment at a time and a tool call's arguments are
    /// spelled out across frames.
    /// </summary>
    public FakeChatEndpoint EnqueueStream(params string[] deltas)
    {
        var body = new StringBuilder();
        foreach (var delta in deltas) body.Append("data: ").Append(delta).Append("\n\n");
        body.Append("data: [DONE]\n\n");
        return Enqueue(body.ToString(), 200, "text/event-stream");
    }

    /// <summary>One frame carrying a piece of the answer's text.</summary>
    public static string TextDelta(string text) => System.Text.Json.JsonSerializer.Serialize(new
    {
        choices = new[] { new { index = 0, delta = new { content = text } } }
    });

    /// <summary>One frame carrying a piece of a tool call, keyed by its position in the turn.</summary>
    public static string ToolDelta(int index, string? id = null, string? name = null, string? arguments = null)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new
                    {
                        tool_calls = new[]
                        {
                            new { index, id, type = "function", function = new { name, arguments } }
                        }
                    }
                }
            }
        });

    /// <summary>Queues an assistant turn that calls one tool.</summary>
    public FakeChatEndpoint EnqueueToolCall(string toolName, string argumentsJson, string callId = "call_1")
        => Enqueue(System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        content = (string?)null,
                        tool_calls = new[]
                        {
                            new { id = callId, type = "function", function = new { name = toolName, arguments = argumentsJson } }
                        }
                    }
                }
            }
        }));

    /// <summary>Queues a plain assistant turn with no tool calls.</summary>
    public FakeChatEndpoint EnqueueText(string text)
        => Enqueue(System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content = text } } }
        }));

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_shutdown.Token); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var header = new StringBuilder();
                var buffer = new byte[1];
                // Read the request line and headers one byte at a time; bodies here are tiny.
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, _shutdown.Token);
                    if (read == 0) return;
                    header.Append((char)buffer[0]);
                }

                var contentLength = 0;
                var chunked = false;
                foreach (var line in header.ToString().Split("\r\n"))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        _ = int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
                    else if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
                             && line.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                        chunked = true;
                }

                // HttpClient sends JsonContent with chunked encoding because the length is not
                // known upfront, so both framings have to be understood here.
                var body = chunked
                    ? await ReadChunkedAsync(stream)
                    : await ReadFixedAsync(stream, contentLength);

                (int Status, string Body, string ContentType) response;
                lock (_gate)
                {
                    _requests.Add(body);
                    response = _responses.Count > 0
                        ? _responses.Dequeue()
                        : (200, """{"choices":[{"message":{"role":"assistant","content":"(no scripted response left)"}}]}""",
                            "application/json; charset=utf-8");
                }

                var payloadBytes = Encoding.UTF8.GetBytes(response.Body);
                var head = Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 {response.Status} {Reason(response.Status)}\r\n" +
                    $"Content-Type: {response.ContentType}\r\n" +
                    $"Content-Length: {payloadBytes.Length}\r\n" +
                    "Connection: close\r\n\r\n");
                await stream.WriteAsync(head, _shutdown.Token);
                await stream.WriteAsync(payloadBytes, _shutdown.Token);
                await stream.FlushAsync(_shutdown.Token);

                // Half-close and wait for the client to finish reading. Disposing the socket
                // straight after the write truncates the body and HttpClient reports it as a
                // transport failure, which would mask the behaviour under test.
                client.Client.Shutdown(SocketShutdown.Send);
                var drain = new byte[256];
                while (await stream.ReadAsync(drain, _shutdown.Token) > 0) { }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task<string> ReadFixedAsync(Stream stream, int contentLength)
    {
        if (contentLength <= 0) return string.Empty;
        var payload = new byte[contentLength];
        var offset = 0;
        while (offset < contentLength)
        {
            var read = await stream.ReadAsync(payload.AsMemory(offset, contentLength - offset), _shutdown.Token);
            if (read == 0) break;
            offset += read;
        }
        return Encoding.UTF8.GetString(payload, 0, offset);
    }

    private async Task<string> ReadChunkedAsync(Stream stream)
    {
        var payload = new MemoryStream();
        while (true)
        {
            var sizeLine = await ReadLineAsync(stream);
            if (sizeLine is null) break;
            // A chunk size may carry extensions after a semicolon.
            var semicolon = sizeLine.IndexOf(';');
            if (semicolon >= 0) sizeLine = sizeLine[..semicolon];
            if (!int.TryParse(sizeLine.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size)) break;
            if (size == 0)
            {
                // Consume the trailing CRLF after the terminating chunk.
                await ReadLineAsync(stream);
                break;
            }

            var chunk = new byte[size];
            var offset = 0;
            while (offset < size)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(offset, size - offset), _shutdown.Token);
                if (read == 0) break;
                offset += read;
            }
            payload.Write(chunk, 0, offset);
            await ReadLineAsync(stream);
        }
        return Encoding.UTF8.GetString(payload.ToArray());
    }

    private async Task<string?> ReadLineAsync(Stream stream)
    {
        var line = new StringBuilder();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, _shutdown.Token);
            if (read == 0) return line.Length == 0 ? null : line.ToString();
            if (buffer[0] == '\n') return line.ToString().TrimEnd('\r');
            line.Append((char)buffer[0]);
        }
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        422 => "Unprocessable Entity",
        500 => "Internal Server Error",
        _ => "Status"
    };

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _listener.Stop(); } catch (SocketException) { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _shutdown.Dispose();
    }
}
