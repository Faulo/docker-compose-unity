using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed class DockerEngineClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient client;

    internal DockerEngineClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = ConnectAsync
        };
        client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://docker"),
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    internal async Task<JsonObject> VersionAsync(CancellationToken cancellationToken) =>
        await GetObjectAsync("/version", cancellationToken);

    internal async Task<JsonObject> InspectContainerAsync(string idOrName, CancellationToken cancellationToken) =>
        await GetObjectAsync($"/containers/{Escape(idOrName)}/json", cancellationToken);

    internal async Task<JsonObject?> TryInspectContainerAsync(string idOrName, CancellationToken cancellationToken)
    {
        try
        {
            return await InspectContainerAsync(idOrName, cancellationToken);
        }
        catch (DockerApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    internal async Task<JsonObject> InspectSelfAsync(CancellationToken cancellationToken)
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("HOSTNAME"),
            Environment.MachineName
        };

        foreach (var candidate in candidates.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var inspected = await TryInspectContainerAsync(candidate!, cancellationToken);
            if (inspected is not null)
            {
                return inspected;
            }
        }

        throw new InvalidOperationException(
            "Docker Engine access is available, but the sidecar could not inspect its own container. " +
            "Mount the platform Docker socket/pipe and do not override the container hostname.");
    }

    internal async Task<string> CreateContainerAsync(string name, JsonObject configuration, CancellationToken cancellationToken)
    {
        var result = await SendJsonAsync(
            HttpMethod.Post,
            $"/containers/create?name={Uri.EscapeDataString(name)}",
            configuration,
            cancellationToken);
        return result["Id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Docker did not return the created container ID.");
    }

    internal async Task StartContainerAsync(string idOrName, CancellationToken cancellationToken) =>
        await SendNoContentAsync(HttpMethod.Post, $"/containers/{Escape(idOrName)}/start", null, cancellationToken);

    internal async Task StopContainerAsync(string idOrName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
        try
        {
            await SendNoContentAsync(
                HttpMethod.Post,
                $"/containers/{Escape(idOrName)}/stop?t={seconds}",
                null,
                cancellationToken);
        }
        catch (DockerApiException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotModified)
        {
        }
    }

    internal async Task<int> WaitContainerAsync(string idOrName, CancellationToken cancellationToken)
    {
        var result = await SendJsonAsync(
            HttpMethod.Post,
            $"/containers/{Escape(idOrName)}/wait?condition=not-running",
            null,
            cancellationToken);
        return result["StatusCode"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Docker did not return the container exit status.");
    }

    internal async Task RemoveContainerAsync(string idOrName, bool force, bool removeVolumes, CancellationToken cancellationToken)
    {
        var path = $"/containers/{Escape(idOrName)}?force={(force ? 1 : 0)}&v={(removeVolumes ? 1 : 0)}";
        try
        {
            await SendNoContentAsync(HttpMethod.Delete, path, null, cancellationToken);
        }
        catch (DockerApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    internal async Task<CapturedOutput> ContainerLogsAsync(string idOrName, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"/containers/{Escape(idOrName)}/logs?stdout=1&stderr=1",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await ReadMultiplexedAsync(stream, cancellationToken);
    }

    internal async Task<ExecResult> ExecAsync(
        string container,
        string workingDirectory,
        IReadOnlyList<string> command,
        CancellationToken cancellationToken)
    {
        var configuration = new JsonObject
        {
            ["AttachStdout"] = true,
            ["AttachStderr"] = true,
            ["Tty"] = false,
            ["WorkingDir"] = workingDirectory,
            ["Cmd"] = ToArray(command)
        };
        var created = await SendJsonAsync(
            HttpMethod.Post,
            $"/containers/{Escape(container)}/exec",
            configuration,
            cancellationToken);
        var execId = created["Id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Docker did not return the exec ID.");

        using var request = CreateRequest(HttpMethod.Post, $"/exec/{Escape(execId)}/start", new JsonObject
        {
            ["Detach"] = false,
            ["Tty"] = false
        });
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var output = await ReadMultiplexedAsync(stream, cancellationToken);
        var inspected = await GetObjectAsync($"/exec/{Escape(execId)}/json", cancellationToken);
        var exitCode = inspected["ExitCode"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Docker did not return the exec exit status.");
        return new ExecResult(
            execId,
            exitCode,
            output.StandardOutput,
            output.StandardError,
            output.CombinedOutput);
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<JsonObject> GetObjectAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return node as JsonObject ?? throw new InvalidOperationException("Docker returned an unexpected JSON response.");
    }

    private async Task<JsonObject> SendJsonAsync(
        HttpMethod method,
        string path,
        JsonObject? body,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path, body);
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return new JsonObject();
        }
        return JsonNode.Parse(content) as JsonObject
            ?? throw new InvalidOperationException("Docker returned an unexpected JSON response.");
    }

    private async Task SendNoContentAsync(
        HttpMethod method,
        string path,
        JsonObject? body,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path, body);
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, JsonObject? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(JsonOptions), Encoding.UTF8, "application/json");
        }
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            detail = JsonNode.Parse(detail)?["message"]?.GetValue<string>() ?? detail;
        }
        catch (JsonException)
        {
        }
        throw new DockerApiException(response.StatusCode, string.IsNullOrWhiteSpace(detail) ? response.ReasonPhrase : detail);
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(
                ".",
                "docker_engine",
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cancellationToken);
            return pipe;
        }

        const string socketPath = "/var/run/docker.sock";
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<CapturedOutput> ReadMultiplexedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();
        using var combinedOutput = new MemoryStream();
        var header = new byte[8];
        byte? previousStream = null;

        while (true)
        {
            var headerBytes = await ReadAtMostAsync(stream, header, cancellationToken);
            if (headerBytes == 0)
            {
                break;
            }
            if (headerBytes != header.Length || header[1] != 0 || header[2] != 0 || header[3] != 0)
            {
                await WriteStreamMarkerAsync(combinedOutput, 1, previousStream, cancellationToken);
                await standardOutput.WriteAsync(header.AsMemory(0, headerBytes), cancellationToken);
                await combinedOutput.WriteAsync(header.AsMemory(0, headerBytes), cancellationToken);
                await CopyAsync(stream, standardOutput, combinedOutput, cancellationToken);
                break;
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
            if (length < 0)
            {
                throw new InvalidOperationException("Docker returned an invalid output frame.");
            }

            var streamType = header[0];
            var target = streamType == 2 ? standardError : standardOutput;
            await WriteStreamMarkerAsync(combinedOutput, streamType, previousStream, cancellationToken);
            await CopyFrameAsync(stream, target, combinedOutput, length, cancellationToken);
            previousStream = streamType;
        }

        return new CapturedOutput(
            Encoding.UTF8.GetString(standardOutput.ToArray()),
            Encoding.UTF8.GetString(standardError.ToArray()),
            Encoding.UTF8.GetString(combinedOutput.ToArray()));
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                break;
            }
            offset += read;
        }
        return offset;
    }

    private static async Task CopyFrameAsync(
        Stream source,
        Stream destination,
        Stream combinedOutput,
        int length,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(16 * 1024, Math.Max(1, length))];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Docker output ended in the middle of a frame.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await combinedOutput.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    private static async Task CopyAsync(
        Stream source,
        Stream destination,
        Stream combinedOutput,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await combinedOutput.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static async Task WriteStreamMarkerAsync(
        Stream destination,
        byte streamType,
        byte? previousStream,
        CancellationToken cancellationToken)
    {
        if (streamType == previousStream)
        {
            return;
        }

        var name = streamType == 2 ? "stderr" : "stdout";
        var marker = Encoding.UTF8.GetBytes($"{(destination.Length == 0 ? string.Empty : "\n")}[{name}]\n");
        await destination.WriteAsync(marker, cancellationToken);
    }

    internal static JsonArray ToArray(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
        {
            result.Add(value);
        }
        return result;
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}

internal sealed class DockerApiException(HttpStatusCode statusCode, string? message)
    : InvalidOperationException($"Docker Engine returned {(int)statusCode}: {message}")
{
    internal HttpStatusCode StatusCode { get; } = statusCode;
}

internal sealed record CapturedOutput(string StandardOutput, string StandardError, string CombinedOutput);

internal sealed record ExecResult(
    string Id,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string CombinedOutput);
