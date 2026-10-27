using System.Text.RegularExpressions;
using Hopital.Hl7;
using Hopital.Scenarios;
using Microsoft.Extensions.Logging;

namespace Hopital.Web;

/// <param name="File">File name in the scenarios folder, e.g. clinic-day.yaml.</param>
/// <param name="Seconds">Total of the waits between messages.</param>
/// <param name="Problem">Why the file cannot be run, if it cannot.</param>
public sealed record ScenarioInfo(string File, string Name, string? Description, int Messages, double Seconds, string? Problem);

/// <summary>One planned message, built but not sent.</summary>
public sealed record PreviewMessage(int Number, string Label, string ControlId, double DelaySeconds, string Expect, string Framing, string Text);

public sealed record ResultView(int Number, string Label, string ControlId, string Outcome, string Detail, double ElapsedMs, string Expect, bool Passed)
{
    public static ResultView From(SendResult r) => new(
        r.Send.Number, r.Send.Label, r.ControlId, r.Outcome.ToString(), r.Detail, Math.Round(r.Elapsed.TotalMilliseconds, 1), r.Send.Expect, r.Passed);
}

/// <param name="State">running, finished, stopped or failed.</param>
/// <param name="Summary">The same summary lines the command line prints, once the run is over.</param>
public sealed record RunView(
    int Id,
    string Scenario,
    string Name,
    string Host,
    int Port,
    int Planned,
    int Sent,
    int Failed,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<string> Summary);

public sealed record RunSnapshot(RunView Run, IReadOnlyList<ResultView> Results);

/// <summary>A scenario name that is not a plain .yaml file in the scenarios folder.</summary>
public sealed class ScenarioNotFoundException(string message) : Exception(message);

/// <summary>A run was asked for while another is still going.</summary>
public sealed class RunInProgressException(string message) : Exception(message);

/// <summary>
/// Runs scenarios from the web page, one at a time, in the background. Progress goes out
/// through the <see cref="EventHub"/> as "run" (start and end) and "result" (each message) events.
/// Only plain file names in the scenarios folder can be run, so a request cannot reach other files.
/// </summary>
public sealed partial class RunManager(string scenariosFolder, EventHub hub, ILogger logger, TimeProvider clock) : IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Run? _current;
    private int _lastId;

    public string Folder => Path.GetFullPath(scenariosFolder);

    public RunSnapshot? Current
    {
        get
        {
            lock (_lock)
                return _current?.Snapshot();
        }
    }

    /// <summary>Finishes when the current run (if any) has ended.</summary>
    public Task Completion
    {
        get
        {
            lock (_lock)
                return _current?.Done.Task ?? Task.CompletedTask;
        }
    }

    public IReadOnlyList<ScenarioInfo> Scenarios()
    {
        if (!Directory.Exists(Folder))
            return [];
        return Directory.GetFiles(Folder, "*.yaml")
            .Select(Path.GetFileName)
            .Where(f => f is not null && ScenarioFileName().IsMatch(f))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(file =>
            {
                try
                {
                    var plan = Plan(file!);
                    return new ScenarioInfo(file!, plan.Name, plan.Description, plan.Sends.Count, plan.Sends.Sum(s => s.DelayBefore.TotalSeconds), null);
                }
                catch (Exception ex) when (ex is ScenarioException or IOException)
                {
                    return new ScenarioInfo(file!, Path.GetFileNameWithoutExtension(file!), null, 0, 0, ex.Message);
                }
            })
            .ToList();
    }

    /// <summary>Builds every message of a scenario without sending anything.</summary>
    public IReadOnlyList<PreviewMessage> Preview(string file) =>
        Plan(file).Sends.Select(send =>
        {
            var message = send.Build();
            return new PreviewMessage(send.Number, send.Label, message.ControlId, send.DelayBefore.TotalSeconds, send.Expect, send.Framing.ToString(), Hl7Text.ToDisplay(message.Text));
        }).ToList();

    /// <exception cref="ScenarioNotFoundException">Not a scenario file in the folder.</exception>
    /// <exception cref="ScenarioException">The file has mistakes.</exception>
    /// <exception cref="RunInProgressException">Another run is still going.</exception>
    public RunView Start(string file, string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Host is required.", nameof(host));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be 1 to 65535.");
        var plan = Plan(file);

        Run run;
        lock (_lock)
        {
            if (_current is { Finished: false } busy)
                throw new RunInProgressException($"{busy.File} is still running. Stop it first.");
            run = new Run(++_lastId, file, plan, host.Trim(), port, clock.GetLocalNow(), CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
            _current = run;
        }

        logger.LogInformation("Web: running {File} against {Host}:{Port}", file, run.Host, port);
        var started = run.Snapshot().Run;
        hub.Publish("run", started);
        _ = Task.Run(() => ExecuteAsync(run));
        return started;
    }

    /// <summary>Stops the current run; false if nothing is running.</summary>
    public bool Stop()
    {
        lock (_lock)
        {
            if (_current is not { Finished: false } run)
                return false;
            run.Cancel.Cancel();
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        await Completion;
        _shutdown.Dispose();
    }

    private async Task ExecuteAsync(Run run)
    {
        try
        {
            var runner = new ScenarioRunner(run.Host, run.Port, logger, clock);
            var summary = await runner.RunAsync(run.Plan, run.Cancel.Token, result =>
            {
                var view = ResultView.From(result);
                lock (_lock)
                    run.Results.Add(view);
                hub.Publish("result", new { runId = run.Id, result = view });
            });
            lock (_lock)
                run.Finish(summary.Stopped ? "stopped" : "finished", [.. summary.Describe()], clock.GetLocalNow());
        }
        catch (Exception ex)
        {
            logger.LogError("Web run {Id} failed: {Reason}", run.Id, ex.Message);
            lock (_lock)
                run.Finish("failed", [ex.Message], clock.GetLocalNow());
        }
        finally
        {
            run.Cancel.Dispose();
        }
        RunView finished;
        lock (_lock)
            finished = run.Snapshot().Run;
        hub.Publish("run", finished);
        run.Done.TrySetResult();
    }

    private ScenarioPlan Plan(string file)
    {
        if (!ScenarioFileName().IsMatch(file))
            throw new ScenarioNotFoundException($"'{file}' is not a scenario file name like clinic-day.yaml.");
        var path = Path.Combine(Folder, file);
        if (!File.Exists(path))
            throw new ScenarioNotFoundException($"There is no {file} in {Folder}.");
        return ScenarioPlanner.Build(ScenarioLoader.Load(path), clock);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*\.yaml$")]
    private static partial Regex ScenarioFileName();

    private sealed class Run(int id, string file, ScenarioPlan plan, string host, int port, DateTimeOffset startedAt, CancellationTokenSource cancel)
    {
        public int Id => id;
        public string File => file;
        public ScenarioPlan Plan => plan;
        public string Host => host;
        public int Port => port;
        public CancellationTokenSource Cancel => cancel;
        public List<ResultView> Results { get; } = [];
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Finished => _state != "running";

        private string _state = "running";
        private DateTimeOffset? _finishedAt;
        private IReadOnlyList<string> _summary = [];

        public void Finish(string state, IReadOnlyList<string> summary, DateTimeOffset at)
        {
            _state = state;
            _summary = summary;
            _finishedAt = at;
        }

        // Callers hold the manager's lock.
        public RunSnapshot Snapshot() => new(
            new RunView(id, file, plan.Name, host, port, plan.Sends.Count, Results.Count, Results.Count(r => !r.Passed),
                _state, startedAt, _finishedAt, _summary),
            [.. Results]);
    }
}
