using System.Collections.Concurrent;
using System.Diagnostics;
using EasyBlackout.Core.Blackout;
using EasyBlackout.Core.Monitors;
using EasyBlackout.Core.Peripherals;
using EasyBlackout.Core.Providers;
using EasyBlackout.Core.Settings;

namespace EasyBlackout.Tests;

public sealed class OrchestratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EasyBlackoutTests", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string> _journal = new();
    private readonly SettingsStore _settings;

    public OrchestratorTests() => _settings = new SettingsStore(Path.Combine(_dir, "settings.json"));

    private sealed class FakeOverlay(ConcurrentQueue<string> journal) : IOverlayController
    {
        public BlackoutContext? LastContext { get; private set; }
        public void ShowOverlays(BlackoutContext context) { LastContext = context; journal.Enqueue("overlay:show"); }
        public void HideOverlays() => journal.Enqueue("overlay:hide");
    }

    private sealed class FakeProvider(string id, ConcurrentQueue<string> journal) : BlackoutProviderBase
    {
        public override string Id => id;
        public override string DisplayName => id;
        public Func<Task>? OnBlackout { get; init; }
        public BlackoutContext? LastContext { get; private set; }
        public int EmergencyCalls;

        protected override Task RefreshCoreAsync(PeripheralSnapshot peripherals, CancellationToken ct)
        {
            SetDevices([new DeviceInfo($"{id}:dev", "Device", DeviceCategory.Keyboard)]);
            SetState(ProviderState.Ready);
            return Task.CompletedTask;
        }

        protected override async Task BlackoutCoreAsync(BlackoutContext context, CancellationToken ct)
        {
            LastContext = context;
            journal.Enqueue($"{id}:blackout");
            if (OnBlackout is not null) await OnBlackout();
        }

        protected override Task RestoreCoreAsync(CancellationToken ct)
        {
            journal.Enqueue($"{id}:restore");
            return Task.CompletedTask;
        }

        protected override void EmergencyRestoreCore()
        {
            Interlocked.Increment(ref EmergencyCalls);
            journal.Enqueue($"{id}:emergency");
        }
    }

    private BlackoutOrchestrator Create(FakeOverlay overlay, params IBlackoutProvider[] providers) =>
        new(providers, overlay, _settings, () => PeripheralSnapshot.Empty) { ProviderTimeout = TimeSpan.FromMilliseconds(400) };

    [Fact]
    public async Task ToggleBlacksOutThenRestores()
    {
        var overlay = new FakeOverlay(_journal);
        var a = new FakeProvider("a", _journal);
        var b = new FakeProvider("b", _journal);
        var orchestrator = Create(overlay, a, b);

        await orchestrator.ToggleAsync();
        Assert.True(orchestrator.IsBlackedOut);
        Assert.True(a.IsBlackedOut && b.IsBlackedOut);
        Assert.Equal("overlay:show", _journal.First()); // screens first, always

        await orchestrator.ToggleAsync();
        Assert.False(orchestrator.IsBlackedOut);
        Assert.False(a.IsBlackedOut || b.IsBlackedOut);
        Assert.Contains("a:restore", _journal);
        Assert.Contains("b:restore", _journal);
        Assert.Contains("overlay:hide", _journal);
    }

    [Fact]
    public async Task DisabledProvidersAreSkipped()
    {
        _settings.Update(s => s.DisabledProviders.Add("b"));
        var a = new FakeProvider("a", _journal);
        var b = new FakeProvider("b", _journal);
        var orchestrator = Create(new FakeOverlay(_journal), a, b);

        await orchestrator.BlackoutAsync();
        await orchestrator.RestoreAsync();

        Assert.DoesNotContain("b:blackout", _journal);
        Assert.DoesNotContain("b:restore", _journal);
    }

    [Fact]
    public async Task ExclusionsReachProvidersAndOverlay()
    {
        _settings.Update(s =>
        {
            s.ExcludedDevices.Add("a:dev");
            s.DdcPowerOffMonitors.Add("monitor:1");
        });
        var overlay = new FakeOverlay(_journal);
        var a = new FakeProvider("a", _journal);
        var orchestrator = Create(overlay, a);

        await orchestrator.BlackoutAsync();

        Assert.False(a.LastContext!.IsIncluded("a:dev"));
        Assert.True(a.LastContext.IsIncluded("a:other"));
        Assert.Contains("monitor:1", a.LastContext.DdcPowerOffMonitors);
        Assert.False(overlay.LastContext!.IsIncluded("a:dev"));
    }

    [Fact]
    public async Task FailingProviderDoesNotStopOthersAndIsStillRestored()
    {
        var bad = new FakeProvider("bad", _journal) { OnBlackout = () => throw new InvalidOperationException("boom") };
        var good = new FakeProvider("good", _journal);
        var orchestrator = Create(new FakeOverlay(_journal), bad, good);

        await orchestrator.BlackoutAsync();
        Assert.True(good.IsBlackedOut);
        Assert.Contains("boom", bad.LastError);

        await orchestrator.RestoreAsync();
        Assert.Contains("bad:restore", _journal); // partial state must be undone
        Assert.Null(bad.LastError);                // cleared by the successful restore
    }

    [Fact]
    public async Task HangingProviderTimesOut()
    {
        var hang = new FakeProvider("hang", _journal) { OnBlackout = () => Task.Delay(Timeout.Infinite) };
        var good = new FakeProvider("good", _journal);
        var orchestrator = Create(new FakeOverlay(_journal), hang, good);

        var sw = Stopwatch.StartNew();
        await orchestrator.BlackoutAsync();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
        Assert.True(good.IsBlackedOut);
        Assert.Contains("timed out", hang.LastError);
    }

    [Fact]
    public async Task EmergencyRestoreUndoesEverythingSynchronously()
    {
        var a = new FakeProvider("a", _journal);
        var orchestrator = Create(new FakeOverlay(_journal), a);
        await orchestrator.BlackoutAsync();

        orchestrator.EmergencyRestore();

        Assert.False(orchestrator.IsBlackedOut);
        Assert.Equal(1, a.EmergencyCalls);
        Assert.Contains("overlay:hide", _journal);

        orchestrator.EmergencyRestore(); // idempotent
        Assert.Equal(1, a.EmergencyCalls);
    }

    [Fact]
    public async Task RefreshIsSkippedWhileBlackedOut()
    {
        var a = new FakeProvider("a", _journal);
        var scans = 0;
        var orchestrator = new BlackoutOrchestrator([a], null, _settings, () => { scans++; return PeripheralSnapshot.Empty; });

        await orchestrator.RefreshAsync();
        Assert.Equal(1, scans);
        Assert.Single(a.Devices);

        await orchestrator.BlackoutAsync();
        await orchestrator.RefreshAsync();
        Assert.Equal(1, scans);
    }

    [Fact]
    public async Task DoubleBlackoutIsIdempotent()
    {
        var a = new FakeProvider("a", _journal);
        var orchestrator = Create(new FakeOverlay(_journal), a);
        await orchestrator.BlackoutAsync();
        await orchestrator.BlackoutAsync();
        Assert.Single(_journal, e => e == "a:blackout");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }
}
