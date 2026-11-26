using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BAM.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BAM.Infrastructure;

public sealed class MaaLoopbackHost : IMaaLoopbackHost
{
    private const int BodyLimit = 4096;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 8, AllowDuplicateProperties = false };
    private readonly SemaphoreSlim _lifecycle = new(1);
    private IHost? _host;

    public async Task<int> StartAsync(int port, IMaaHeartbeatSession session, CancellationToken cancellation)
    {
        await _lifecycle.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (_host is not null) throw new InvalidOperationException("MAA 回环服务已启动。");
            var host = new HostBuilder()
                .ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureWebHost(web => web
                    .UseSetting(WebHostDefaults.PreventHostingStartupKey, "true")
                    .UseKestrelCore()
                    .ConfigureKestrel(kestrel =>
                    {
                        kestrel.AddServerHeader = false;
                        kestrel.Limits.MaxConcurrentConnections = 16;
                        kestrel.Limits.MaxRequestBodySize = BodyLimit;
                        kestrel.Limits.MaxRequestHeadersTotalSize = 8192;
                        kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(10);
                        kestrel.Limits.RequestHeadersTimeout = RequestTimeout;
                        kestrel.Listen(IPAddress.Loopback, port, listen => listen.Protocols = HttpProtocols.Http1);
                    })
                    .Configure(app => app.Run(context => HandleAsync(context, session))),
                    options => options.SuppressEnvironmentConfiguration = true)
                .Build();
            try
            {
                await host.StartAsync(cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                var addresses = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
                int boundPort = new Uri(addresses.Single()).Port;
                _host = host;
                return boundPort;
            }
            catch
            {
                await DisposeHostAsync(host).ConfigureAwait(false);
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellation)
    {
        await _lifecycle.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (_host is not { } host) return;
            _host = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try { await host.StopAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && timeout.IsCancellationRequested) { }
            finally { await DisposeHostAsync(host).ConfigureAwait(false); }
        }
        finally { _lifecycle.Release(); }
    }

    private static async ValueTask DisposeHostAsync(IHost host)
    {
        if (host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync().ConfigureAwait(false);
        else host.Dispose();
    }

    private static async Task HandleAsync(HttpContext context, IMaaHeartbeatSession session)
    {
        var request = context.Request;
        if (!string.Equals(request.Host.Host, "127.0.0.1", StringComparison.Ordinal) ||
            request.Headers.ContainsKey("Origin") ||
            context.Connection.RemoteIpAddress is not { AddressFamily: AddressFamily.InterNetwork } remote ||
            !remote.Equals(IPAddress.Loopback))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        string? path = request.Path.Value;
        if (request.QueryString.HasValue || path is not ("/getTask" or "/reportStatus"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!HttpMethods.IsPost(request.Method))
        {
            context.Response.Headers.Allow = "POST";
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        if (request.ContentLength > BodyLimit)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        if (!string.Equals(request.ContentType?.Split(';', 2)[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var json = await JsonDocument.ParseAsync(request.Body, JsonOptions, timeout.Token).ConfigureAwait(false);
            var body = json.RootElement;
            if (body.ValueKind != JsonValueKind.Object ||
                ReadString(body, "user") is not { Length: > 0 and <= 128 } user ||
                ReadString(body, "device") is not { Length: > 0 and <= 128 } device)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            if (!session.IsPaired(user, device))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            timeout.Token.ThrowIfCancellationRequested();
            if (path == "/getTask")
            {
                HeartbeatTask[] tasks = session.Poll() is { } probe ? [new(probe.ToString("N"), "HeartBeat")] : [];
                await context.Response.WriteAsJsonAsync(new { tasks }, timeout.Token).ConfigureAwait(false);
            }
            else
            {
                context.Response.StatusCode = Report(body, session);
                if (context.Response.StatusCode == StatusCodes.Status200OK)
                    await context.Response.WriteAsync("{}", timeout.Token).ConfigureAwait(false);
            }
        }
        catch (JsonException) { context.Response.StatusCode = StatusCodes.Status400BadRequest; }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException error) { context.Response.StatusCode = error.StatusCode; }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested) context.Abort();
            else
            {
                context.Response.Headers.Connection = "close";
                context.Response.StatusCode = StatusCodes.Status408RequestTimeout;
            }
        }
        catch (IOException) { context.Abort(); }
    }

    private static int Report(JsonElement body, IMaaHeartbeatSession session)
    {
        var status = ReadString(body, "status");
        if (status is not ("SUCCESS" or "FAILED") || ReadString(body, "payload") is not { } payload ||
            !Guid.TryParseExact(ReadString(body, "task"), "N", out var id))
            return StatusCodes.Status400BadRequest;

        return session.Report(id, status == "SUCCESS", payload) switch
        {
            MaaHeartbeatReport.Accepted => StatusCodes.Status200OK,
            MaaHeartbeatReport.Conflict => StatusCodes.Status409Conflict,
            MaaHeartbeatReport.Expired => StatusCodes.Status410Gone,
            _ => StatusCodes.Status400BadRequest
        };
    }

    private static string? ReadString(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record HeartbeatTask(string Id, string Type);
}
