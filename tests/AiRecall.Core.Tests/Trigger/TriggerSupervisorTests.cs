using AiRecall.Core.Configuration;
using AiRecall.Trigger;
using Serilog;
using Xunit;

namespace AiRecall.Core.Tests.Trigger;

public class TriggerSupervisorTests
{
    private static ILogger NullLogger => Serilog.Core.Logger.None;

    private static AppConfig MinimalConfig() => new();

    private static (TriggerSupervisor Supervisor, FakeTriggerServiceFactory Factory) MakeSupervisor(
        bool throwOnStart = false)
    {
        var factory = new FakeTriggerServiceFactory { ThrowOnStart = throwOnStart };
        var supervisor = new TriggerSupervisor(NullLogger, factory.Create);
        return (supervisor, factory);
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new TriggerSupervisor(null!));
    }

    [Fact]
    public void Ctor_NullServiceFactory_UsesDefault()
    {
        using var s = new TriggerSupervisor(NullLogger, serviceFactory: null);
        Assert.Equal(TriggerState.Stopped, s.State);
    }

    [Fact]
    public void State_Initial_IsStopped()
    {
        using var s = new TriggerSupervisor(NullLogger);
        Assert.Equal(TriggerState.Stopped, s.State);
        Assert.Null(s.Service);
        Assert.Equal(0, s.CrashCount);
        Assert.Null(s.LastCrashAt);
        Assert.Null(s.CurrentConfig);
    }

    [Fact]
    public void Start_ValidConfig_SetsRunning()
    {
        var (s, factory) = MakeSupervisor();
        using (s)
        {
            s.Start(MinimalConfig());
            Assert.Equal(TriggerState.Running, s.State);
            Assert.NotNull(s.Service);
            Assert.Same(factory.LastCreated, s.Service);
            Assert.True(factory.LastCreated.StartCalled);
            Assert.True(s.Service!.IsRunning);
            Assert.NotNull(s.CurrentConfig);
        }
    }

    [Fact]
    public void Start_NullConfig_Throws()
    {
        using var s = new TriggerSupervisor(NullLogger);
        Assert.Throws<ArgumentNullException>(() => s.Start(null!));
        Assert.Equal(TriggerState.Stopped, s.State);
    }

    [Fact]
    public void Start_AlreadyRunning_IsNoOp()
    {
        var (s, factory) = MakeSupervisor();
        using (s)
        {
            s.Start(MinimalConfig());
            var firstService = s.Service;
            var callsBefore = factory.CallCount;

            s.Start(MinimalConfig()); // no-op

            Assert.Same(firstService, s.Service);
            Assert.Equal(callsBefore, factory.CallCount);
            Assert.Equal(TriggerState.Running, s.State);
        }
    }

    [Fact]
    public void Stop_RunningService_SetsStopped()
    {
        var (s, factory) = MakeSupervisor();
        using (s)
        {
            s.Start(MinimalConfig());
            s.Stop();
            Assert.Equal(TriggerState.Stopped, s.State);
            Assert.True(factory.LastCreated.StopCalled);
            Assert.False(s.Service!.IsRunning);
        }
    }

    [Fact]
    public void Stop_WhenStopped_IsNoOp()
    {
        using var s = new TriggerSupervisor(NullLogger);
        s.Stop(); // no-op
        Assert.Equal(TriggerState.Stopped, s.State);
    }

    [Fact]
    public void Restart_RunningService_DisposesOldAndStartsNew()
    {
        var (s, factory) = MakeSupervisor();
        using (s)
        {
            s.Start(MinimalConfig());
            var firstFake = factory.LastCreated;
            var callsAfterStart = factory.CallCount;

            s.Restart(MinimalConfig());

            Assert.Equal(TriggerState.Running, s.State);
            Assert.NotSame(firstFake, s.Service);
            Assert.True(firstFake.DisposeCalled);          // alter Service wurde disposed
            Assert.True(factory.LastCreated.StartCalled);  // neuer Service wurde gestartet
            Assert.Equal(callsAfterStart + 1, factory.CallCount);
        }
    }

    [Fact]
    public void Restart_NullConfig_Throws()
    {
        using var s = new TriggerSupervisor(NullLogger);
        Assert.Throws<ArgumentNullException>(() => s.Restart(null!));
    }

    [Fact]
    public void Start_ServiceFactoryThrows_SetsCrashed()
    {
        var (s, factory) = MakeSupervisor(throwOnStart: true);
        using (s)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => s.Start(MinimalConfig()));
            Assert.Equal("boom", ex.Message);
            Assert.Equal(TriggerState.Crashed, s.State);
            Assert.Equal(1, s.CrashCount);
            Assert.NotNull(s.LastCrashAt);
            Assert.Null(s.Service);
            // factory.LastCreated.Start wurde aufgerufen, hat geworfen → wurde disposed
            Assert.True(factory.LastCreated.DisposeCalled);
        }
    }

    [Fact]
    public void Dispose_StopsAndDisposesService()
    {
        var (s, factory) = MakeSupervisor();
        s.Start(MinimalConfig());
        s.Dispose();
        Assert.Equal(TriggerState.Stopped, s.State);
        Assert.True(factory.LastCreated.StopCalled);
        Assert.True(factory.LastCreated.DisposeCalled);
    }

    [Fact]
    public void StateChanged_RaisedOnTransitions()
    {
        var (s, _) = MakeSupervisor();
        using (s)
        {
            var transitions = new List<TriggerStateChangedEventArgs>();
            s.StateChanged += (_, e) => transitions.Add(e);

            s.Start(MinimalConfig());
            s.Stop();

            Assert.Equal(4, transitions.Count);
            Assert.Equal(TriggerState.Stopped, transitions[0].OldState);
            Assert.Equal(TriggerState.Starting, transitions[0].NewState);
            Assert.Equal(TriggerState.Starting, transitions[1].OldState);
            Assert.Equal(TriggerState.Running, transitions[1].NewState);
            Assert.Equal(TriggerState.Running, transitions[2].OldState);
            Assert.Equal(TriggerState.Stopping, transitions[2].NewState);
            Assert.Equal(TriggerState.Stopping, transitions[3].OldState);
            Assert.Equal(TriggerState.Stopped, transitions[3].NewState);
        }
    }

    // ---------- Test Helpers ----------

    /// <summary>
    /// Factory, die bei jedem <see cref="Create"/> eine neue
    /// <see cref="FakeTriggerService"/> zurückgibt. Simuliert echten
    /// Lifecycle, in dem <c>Restart</c> einen neuen Service erzeugt.
    /// </summary>
    private sealed class FakeTriggerServiceFactory
    {
        public bool ThrowOnStart { get; set; }
        public int CallCount { get; private set; }
        public FakeTriggerService LastCreated { get; private set; } = null!;

        public ITriggerService Create(AppConfig config, ILogger logger)
        {
            CallCount++;
            var fake = new FakeTriggerService { ThrowOnStart = ThrowOnStart };
            LastCreated = fake;
            return fake;
        }
    }

    /// <summary>
    /// In-memory <see cref="ITriggerService"/> für Unit-Tests. Vermeidet
    /// den echten <c>TriggerService</c> (WinEventHook + Heartbeat + Channel)
    /// damit Tests ohne Windows-Setup laufen.
    /// </summary>
    private sealed class FakeTriggerService : ITriggerService
    {
        public bool ThrowOnStart { get; set; }

        public bool StartCalled { get; private set; }
        public bool StopCalled { get; private set; }
        public bool DisposeCalled { get; private set; }

        public bool IsRunning { get; private set; }

        public long CaptureCount => 0;
        public long SkippedCount => 0;
        public long ThrottleCount => 0;
        public long DuplicateCount => 0;
        public long BlacklistCount => 0;
        public long SelfCaptureCount => 0;
        public long ErrorCount => 0;

        public void Start()
        {
            if (ThrowOnStart) throw new InvalidOperationException("boom");
            StartCalled = true;
            IsRunning = true;
        }

        public void Stop()
        {
            StopCalled = true;
            IsRunning = false;
        }

        public void Dispose()
        {
            DisposeCalled = true;
            IsRunning = false;
        }
    }
}