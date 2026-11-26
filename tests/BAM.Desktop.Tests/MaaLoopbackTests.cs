using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BAM.Adapters;
using BAM.Core;
using BAM.Infrastructure;
using Xunit;

namespace BAM.Desktop.Tests;

public sealed class MaaLoopbackTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string User = "fixture-user";
    private const string Device = "fixture-device";

    [Fact]
    public async Task PairedClientSeesEmptyQueueAndAnIdempotentHeartbeat()
    {
        await using var rig = new Fixture();
        await rig.StartAsync();
        Assert.Empty(await Poll(rig.Client));
        Assert.NotNull(rig.Session.Snapshot().LastPoll);
        Assert.Equal(RemoteCommandState.Unknown, rig.Session.Snapshot().State);

        Assert.True(rig.Session.TryQueueHeartbeat());
        Assert.False(rig.Session.TryQueueHeartbeat());
        Assert.Equal(RemoteCommandState.Queued, rig.Session.Snapshot().State);
        Assert.Equal(HttpStatusCode.BadRequest, await Report(rig.Client, Guid.NewGuid().ToString("N")));
        var offer = Assert.Single(await Poll(rig.Client));
        Assert.Equal("HeartBeat", offer.Type);
        Assert.True(Guid.TryParseExact(offer.Id, "N", out _));
        Assert.Equal(RemoteCommandState.Offered, rig.Session.Snapshot().State);
        Assert.Null(rig.Session.Snapshot().ReportSucceeded);
        // A lost poll response can be recovered without a second command ID.
        Assert.Equal(offer, Assert.Single(await Poll(rig.Client)));

        Assert.Equal(HttpStatusCode.OK, await Report(rig.Client, offer.Id));
        Assert.Equal(RemoteCommandState.Reported, rig.Session.Snapshot().State);
        Assert.True(rig.Session.Snapshot().ReportSucceeded);
        Assert.False(rig.Session.Snapshot().ReportContainsRemoteTask);
        Assert.Equal(HttpStatusCode.OK, await Report(rig.Client, offer.Id));
        Assert.Equal(HttpStatusCode.Conflict, await Report(rig.Client, offer.Id, payload: "different"));
        Assert.Empty(await Poll(rig.Client));

        Assert.True(rig.Session.TryQueueHeartbeat());
        var next = Assert.Single(await Poll(rig.Client));
        Assert.NotEqual(offer.Id, next.Id);
        Assert.Equal(HttpStatusCode.OK, await Report(rig.Client, offer.Id));
        Assert.Equal(RemoteCommandState.Offered, rig.Session.Snapshot().State);
        Assert.Equal(HttpStatusCode.OK, await Report(rig.Client, next.Id, payload: "remote-id-fixture"));
        Assert.True(rig.Session.Snapshot().ReportContainsRemoteTask);
    }

    [Fact]
    public async Task PairingRejectsBothUnknownAndAmbiguousIdentities()
    {
        await using var rig = new Fixture();
        await rig.StartAsync();
        Assert.True(rig.Session.TryQueueHeartbeat());
        using var wrongDevice = await rig.Client.PostAsJsonAsync("/getTask", new { user = User, device = "wrong" }, Token);
        Assert.Equal(HttpStatusCode.Forbidden, wrongDevice.StatusCode);
        using var wrongUser = await rig.Client.PostAsJsonAsync("/reportStatus",
            new { user = "wrong", device = Device, task = Guid.NewGuid().ToString("N"), status = "SUCCESS", payload = "" }, Token);
        Assert.Equal(HttpStatusCode.Forbidden, wrongUser.StatusCode);
        using var duplicateField = await rig.Client.PostAsync("/getTask",
            new StringContent("""{"user":"other","user":"fixture-user","device":"fixture-device"}""", Encoding.UTF8, "application/json"), Token);
        Assert.Equal(HttpStatusCode.BadRequest, duplicateField.StatusCode);
        Assert.Null(rig.Session.Snapshot().LastPoll);
        Assert.Equal(RemoteCommandState.Queued, rig.Session.Snapshot().State);
    }

    [Fact]
    public async Task RejectsInvalidRoutesHeadersAndBodies()
    {
        await using var rig = new Fixture();
        await rig.StartAsync();
        using var unknownRoute = await rig.Client.PostAsJsonAsync("/other", new { user = User, device = Device }, Token);
        Assert.Equal(HttpStatusCode.NotFound, unknownRoute.StatusCode);
        using var get = await rig.Client.GetAsync("/getTask", Token);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        using var badType = await rig.Client.PostAsync("/getTask", new StringContent("fixture"), Token);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, badType.StatusCode);
        using var invalid = await rig.Client.PostAsync("/getTask", new StringContent("{", Encoding.UTF8, "application/json"), Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var oversized = await rig.Client.PostAsJsonAsync("/getTask", new { user = new string('x', 5000), device = Device }, Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        using var browser = new HttpRequestMessage(HttpMethod.Post, "/getTask") { Content = JsonContent.Create(new { user = User, device = Device }) };
        browser.Headers.Add("Origin", "http://example.invalid");
        using var browserResponse = await rig.Client.SendAsync(browser, Token);
        Assert.Equal(HttpStatusCode.Forbidden, browserResponse.StatusCode);
        using var otherHost = new HttpRequestMessage(HttpMethod.Post, "/getTask") { Content = JsonContent.Create(new { user = User, device = Device }) };
        otherHost.Headers.Host = "example.invalid";
        using var hostResponse = await rig.Client.SendAsync(otherHost, Token);
        Assert.Equal(HttpStatusCode.Forbidden, hostResponse.StatusCode);
    }

    [Fact]
    public async Task BodyTimeoutDoesNotConsumeAnOfferAndChunkedBodyHasTheSameLimit()
    {
        await using var rig = new Fixture();
        int port = await rig.StartAsync();
        Assert.True(rig.Session.TryQueueHeartbeat());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        using var stalled = new TcpClient(AddressFamily.InterNetwork);
        await stalled.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
        var partial = Encoding.ASCII.GetBytes($"POST /getTask HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
            "Content-Type: application/json\r\nContent-Length: 42\r\n\r\n{");
        await stalled.GetStream().WriteAsync(partial, deadline.Token);
        using var stalledResponse = new StreamReader(stalled.GetStream());
        Assert.StartsWith("HTTP/1.1 408", await stalledResponse.ReadLineAsync(deadline.Token));
        Assert.Equal(RemoteCommandState.Queued, rig.Session.Snapshot().State);

        using var chunked = new TcpClient(AddressFamily.InterNetwork);
        await chunked.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
        var request = Encoding.ASCII.GetBytes($"POST /getTask HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
            "Content-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n" +
            "1388\r\n" + new string(' ', 5000) + "\r\n0\r\n\r\n");
        await chunked.GetStream().WriteAsync(request, deadline.Token);
        using var chunkedResponse = new StreamReader(chunked.GetStream());
        Assert.StartsWith("HTTP/1.1 413", await chunkedResponse.ReadLineAsync(deadline.Token));
    }

    [Fact]
    public async Task LostReportTimesOutAndLateReportCannotChangeState()
    {
        await using var rig = new Fixture();
        await rig.StartAsync();
        Assert.True(rig.Session.TryQueueHeartbeat());
        var offer = Assert.Single(await Poll(rig.Client));
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Gone, await Report(rig.Client, offer.Id));
        Assert.Equal(RemoteCommandState.TimedOut, rig.Session.Snapshot().State);
        Assert.Empty(await Poll(rig.Client));
        Assert.True(rig.Session.TryQueueHeartbeat());
        var next = Assert.Single(await Poll(rig.Client));
        Assert.Equal(HttpStatusCode.OK, await Report(rig.Client, next.Id, status: "FAILED", payload: "fixture-error"));
        Assert.False(rig.Session.Snapshot().ReportSucceeded);
        Assert.Null(rig.Session.Snapshot().ReportContainsRemoteTask);
    }

    [Fact]
    public async Task ConflictAndCancelledStartLeaveHostReusableAndStopFreesThePort()
    {
        await using var rig = new Fixture();
        using var occupied = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        occupied.Start();
        int port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        await Assert.ThrowsAnyAsync<IOException>(() => rig.Host.StartAsync(port, rig.Session, Token));
        occupied.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Host.StartAsync(port, rig.Session, new(true)));
        Assert.Equal(port, await rig.StartAsync(port));
        Assert.True(rig.Session.TryQueueHeartbeat());
        var old = Assert.Single(await Poll(rig.Client));
        await rig.Host.StopAsync(Token);
        var replacement = new MaaHeartbeatSession(User, Device, rig.Clock);
        Assert.Equal(port, await rig.Host.StartAsync(port, replacement, Token));
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = rig.Client.BaseAddress };
        Assert.Empty(await Poll(client));
        Assert.Equal(HttpStatusCode.BadRequest, await Report(client, old.Id));
    }

    private static async Task<WireTask[]> Poll(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/getTask", new { user = User, device = Device }, Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: Token);
        return document.RootElement.GetProperty("tasks").EnumerateArray()
            .Select(task => new WireTask(task.GetProperty("id").GetString()!, task.GetProperty("type").GetString()!))
            .ToArray();
    }

    private static async Task<HttpStatusCode> Report(HttpClient client, string id, string status = "SUCCESS", string payload = "")
    {
        using var response = await client.PostAsJsonAsync("/reportStatus", new { user = User, device = Device, task = id, status, payload }, Token);
        return response.StatusCode;
    }

    private sealed record WireTask(string Id, string Type);

    private sealed class Fixture : IAsyncDisposable
    {
        public ClockFixture Clock { get; } = new();
        public MaaHeartbeatSession Session { get; }
        public MaaLoopbackHost Host { get; } = new();
        public HttpClient Client { get; } = new(new SocketsHttpHandler { UseProxy = false });

        public Fixture() => Session = new(User, Device, Clock);

        public async Task<int> StartAsync(int port = 0)
        {
            var boundPort = await Host.StartAsync(port, Session, Token);
            Client.BaseAddress = new($"http://127.0.0.1:{boundPort}");
            return boundPort;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.StopAsync(CancellationToken.None);
        }
    }

    private sealed class ClockFixture : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }
}
