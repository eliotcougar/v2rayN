namespace ServiceLib.Services.AppRouting;

/// <summary>Uses the packaged core's API client, so its protobuf schema always matches the server.
/// Runs only when preparing a new match combination; no subprocess is used on the packet path.</summary>
internal sealed class RouteCoreApi(string core, IReadOnlyDictionary<string, string>? environment, int port)
{
    internal async Task Execute(string command, JsonNode? input, CancellationToken token, params string[] arguments)
    {
        var start = new ProcessStartInfo(core) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(core)! };
        foreach (var value in new[] { "api", command, $"--server=127.0.0.1:{port}", "--timeout=5" }.Concat(arguments))
        { start.ArgumentList.Add(value); }
        if (environment != null) { foreach (var pair in environment) { start.Environment[pair.Key] = pair.Value; } }
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        token = timeout.Token;
        token.ThrowIfCancellationRequested();
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            if (input != null) { await process.StandardInput.WriteAsync(input.ToJsonString().AsMemory(), token); }
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            var detail = await error;
            var result = await output;
            if (process.ExitCode != 0)
            {
                var message = $"Xray API {command} failed: {detail} {result}";
                // GeoSite/GeoIP references expand in the CLI before gRPC sends the request.
                // Even one JSON rule can exceed the server's 4 MiB receive limit.
                if (command == "adrules" && detail.Contains("code = ResourceExhausted", StringComparison.Ordinal)
                    && detail.Contains("grpc: received message larger than max", StringComparison.Ordinal))
                { throw new RouteRuleSizeException(message); }
                throw new IOException(message);
            }
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
        }
    }
}

internal sealed class RouteRuleSizeException(string message) : IOException(message);
