using System.Net;
using System.Threading.Channels;
using ControlSystem.Agent;
using ControlSystem.Agent.Telemetry;
using ControlSystem.Agent.Transport;
using ControlSystem.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
namespace ControlSystem.Tests;

public sealed class WindowsAgentTests
{
    [Fact]
    public async Task Cpu_uses_system_deltas_and_recovers_after_missing_or_regressed_sample()
    {
        var source = new Source();
        var collector = Collector(source);
        Assert.Null((await collector.CollectAsync(TestContext.Current.CancellationToken)).CpuUsagePercent);
        source.Cpu = new(150, 300, 100);
        Assert.Equal(75, (await collector.CollectAsync(TestContext.Current.CancellationToken)).CpuUsagePercent);
        source.Cpu = null;
        Assert.Null((await collector.CollectAsync(TestContext.Current.CancellationToken)).CpuUsagePercent);
        source.Cpu = new(150, 300, 100);
        Assert.Null((await collector.CollectAsync(TestContext.Current.CancellationToken)).CpuUsagePercent);
        Assert.Null((await collector.CollectAsync(TestContext.Current.CancellationToken)).CpuUsagePercent);
        source.Cpu = new(1, 2, 3);
        Assert.Null((await collector.CollectAsync(TestContext.Current.CancellationToken)).CpuUsagePercent);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    public async Task Cpu_boundaries_and_memory_uptime_units_are_exact(ulong idleDelta, double expected)
    {
        var source = new Source { Cpu = new(0, 0, 0), Uptime = 1234567 };
        var collector = Collector(source);
        await collector.CollectAsync(TestContext.Current.CancellationToken);
        source.Cpu = new(idleDelta, 100, 0);
        var result = await collector.CollectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(100 - expected, result.CpuUsagePercent);
        Assert.Equal(1000, result.MemoryTotalBytes);
        Assert.Equal(400, result.MemoryAvailableBytes);
        Assert.Equal(1234, result.UptimeSeconds);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 101)]
    [InlineData(ulong.MaxValue, 0)]
    public async Task Invalid_required_memory_is_not_replaced_with_synthetic_values(ulong total, ulong available)
    {
        var source = new Source { Memory = new(total, available) };
        await Assert.ThrowsAsync<IOException>(() => Collector(source).CollectAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Disks_skip_unavailable_removable_invalid_and_denied_and_cap_at_protocol_limit()
    {
        var source = new Source { Disks = [new Disk { DriveType = DriveType.Removable }, new Disk { IsReady = false },
            new Disk { Free = 101 }, new Disk { Denied = true }, .. Enumerable.Range(0, 40).Select(i => new Disk { Name = $"{i}:\\" })] };
        var result = await Collector(source).CollectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(MonitoringProtocol.MaxDisks, result.Disks.Count);
        Assert.All(result.Disks, disk => { Assert.Equal(100, disk.TotalBytes); Assert.Equal(30, disk.FreeBytes); });
    }

    [Fact]
    public async Task Collector_honors_cancellation_before_native_call()
    {
        var source = new Source();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Collector(source).CollectAsync(cancellation.Token));
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task Transport_does_not_buffer_response_body()
    {
        using var http = new HttpClient(new Handler()) { BaseAddress = new("https://localhost/") };
        await new HeartbeatClient(http).SendAsync(TestData.Heartbeat(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Worker_stop_cancels_inflight_request_without_parallel_heartbeats()
    {
        var client = new BlockingClient();
        using var worker = new TelemetryWorker(new SimulatedTelemetryCollector(), client, Options.Create(ValidOptions()), TimeProvider.System, NullLogger<TelemetryWorker>.Instance);
        await worker.StartAsync(TestContext.Current.CancellationToken);
        var request = await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(ValidOptions().DeviceId, request.DeviceId);
        Assert.Equal(TelemetryWorker.AgentVersion, request.AgentVersion);
        Assert.Empty(HeartbeatValidator.Validate(request, request.SampledAtUtc));
        await worker.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, client.Calls);
        Assert.True(client.Cancelled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("                                ")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\r\n")]
    public void Options_reject_invalid_header_tokens(string? token) => Assert.False(new AgentOptions { DeviceId = Guid.NewGuid(), Token = token! }.IsValid());

    [Fact]
    public async Task Worker_retries_with_bounded_backoff_then_resets_without_queueing_old_samples()
    {
        var clock = new ManualClock();
        var client = new RetryClient();
        using var worker = new TelemetryWorker(new SimulatedTelemetryCollector(), client, Options.Create(ValidOptions()), clock, NullLogger<TelemetryWorker>.Instance);
        await worker.StartAsync(TestContext.Current.CancellationToken);
        foreach (var expected in new[] { 10, 20, 40, 60, 60, 10 })
        {
            var timer = await clock.Timers.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.InRange(timer.Delay.TotalSeconds, expected, expected == 10 && client.Calls == 6 ? 10 : expected + 2);
            if (client.Calls < 6) clock.Fire(timer);
        }
        await worker.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(6, client.Calls);
        Assert.Equal(6, client.Samples.Distinct().Count());
    }

    [Fact]
    public async Task Fatal_worker_failure_requests_immediate_nonzero_process_exit()
    {
        int? exitCode = null;
        using var worker = new TelemetryWorker(new BrokenCollector(), new BlockingClient(), Options.Create(ValidOptions()), TimeProvider.System, NullLogger<TelemetryWorker>.Instance, code => exitCode = code);
        await worker.StartAsync(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ExecuteTask!);
        Assert.Equal(1, exitCode);
        Assert.Equal("Agent terminated after an unexpected worker failure.", error.Message);
    }

    [Fact]
    public async Task Invalid_uptime_does_not_produce_a_heartbeat_snapshot()
    {
        var source = new Source { Uptime = 3_155_760_001_000 };
        await Assert.ThrowsAsync<IOException>(() => Collector(source).CollectAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Native_memory_failure_retries_and_stop_cancels_backoff()
    {
        var clock = new ManualClock();
        var client = new BlockingClient();
        using var worker = new TelemetryWorker(Collector(new Source { MemoryFailure = true }), client, Options.Create(ValidOptions()), clock, NullLogger<TelemetryWorker>.Instance);
        await worker.StartAsync(TestContext.Current.CancellationToken);
        var timer = await clock.Timers.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.InRange(timer.Delay.TotalSeconds, 10, 12);
        await worker.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, client.Calls);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Slow_failed_attempt_still_waits_full_backoff()
    {
        var clock = new ManualClock();
        using var worker = new TelemetryWorker(new SimulatedTelemetryCollector(), new SlowFailedClient(clock), Options.Create(ValidOptions()), clock, NullLogger<TelemetryWorker>.Instance);
        await worker.StartAsync(TestContext.Current.CancellationToken);
        var timer = await clock.Timers.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.InRange(timer.Delay.TotalSeconds, 10, 12);
        await worker.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Http_transport_propagates_cancellation_to_pending_request()
    {
        var handler = new PendingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://localhost/") };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var sending = new HeartbeatClient(http).SendAsync(TestData.Heartbeat(), cancel.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.True(handler.Cancelled);
    }

    private sealed class PendingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = cancellationToken.IsCancellationRequested; throw; }
            return new(HttpStatusCode.OK);
        }
    }
    private sealed class SlowFailedClient(ManualClock clock) : IHeartbeatClient
    {
        public Task SendAsync(HeartbeatRequest heartbeat, CancellationToken cancellationToken)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            throw new HttpRequestException("network unavailable");
        }
    }

    private sealed class BrokenCollector : ITelemetryCollector
    {
        public bool IsSimulated => false;
        public string Hostname => "PC";
        public string OperatingSystemName => "Windows";
        public Task<TelemetrySnapshot> CollectAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
    private sealed class RetryClient : IHeartbeatClient
    {
        public int Calls { get; private set; }
        public List<DateTimeOffset> Samples { get; } = [];
        public Task SendAsync(HeartbeatRequest heartbeat, CancellationToken cancellationToken)
        {
            Calls++;
            Samples.Add(heartbeat.SampledAtUtc);
            return Calls <= 5 ? Task.FromException(new HttpRequestException("network unavailable")) : Task.CompletedTask;
        }
    }
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public Channel<PendingTimer> Timers { get; } = Channel.CreateUnbounded<PendingTimer>();
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new PendingTimer(callback, state, dueTime);
            Timers.Writer.TryWrite(timer);
            return timer;
        }
        public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
        public void Fire(PendingTimer timer) { _ticks += timer.Delay.Ticks; timer.Callback(timer.State); }
    }
    private sealed class PendingTimer(TimerCallback callback, object? state, TimeSpan delay) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public TimeSpan Delay { get; } = delay;
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static AgentOptions ValidOptions() => new() { DeviceId = TestData.DeviceId, Token = new('a', 43) };
    private static WindowsTelemetryCollector Collector(Source source) => new(NullLogger<WindowsTelemetryCollector>.Instance, source);
    private sealed class Source : IWindowsTelemetrySource
    {
        public CpuTimes? Cpu { get; set; } = new(100, 200, 0);
        public PhysicalMemory Memory { get; set; } = new(1000, 400);
        public ulong Uptime { get; set; } = 1000;
        public IEnumerable<IWindowsDisk> Disks { get; set; } = [];
        public int Reads { get; private set; }
        public CpuTimes? ReadCpuTimes() { Reads++; return Cpu; }
        public bool MemoryFailure { get; init; }
        public PhysicalMemory ReadMemory() => MemoryFailure ? throw new System.ComponentModel.Win32Exception(5) : Memory;
        public ulong ReadUptimeMilliseconds() => Uptime;
        public IEnumerable<IWindowsDisk> GetDrives() => Disks;
    }
    private sealed class Disk : IWindowsDisk
    {
        public string Name { get; init; } = "C:\\";
        public DriveType DriveType { get; init; } = DriveType.Fixed;
        public bool IsReady { get; init; } = true;
        public bool Denied { get; init; }
        public long TotalSize => Denied ? throw new UnauthorizedAccessException() : 100;
        public long Free { get; init; } = 30;
        public long TotalFreeSpace => Free;
    }
    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnreadableContent() });
    }
    private sealed class UnreadableContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("Body must not be buffered.");
        protected override bool TryComputeLength(out long length) { length = 1; return true; }
    }
    private sealed class BlockingClient : IHeartbeatClient
    {
        public TaskCompletionSource<HeartbeatRequest> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public bool Cancelled { get; private set; }
        public async Task SendAsync(HeartbeatRequest heartbeat, CancellationToken cancellationToken)
        {
            Calls++;
            Started.TrySetResult(heartbeat);
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = cancellationToken.IsCancellationRequested; throw; }
        }
    }
}
