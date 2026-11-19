namespace AvawareExportBot.Core.Updates;

/// <summary>
/// The command line an updated (or rolled back) app is started with, so it comes back as it was:
/// <c>--state stopped|armed|running</c>, <c>--tray</c> when its window was hidden, and <c>--updated-from x.y.z</c> or
/// <c>--update-failed x.y.z</c> to say what just happened. A plain launch has none of them.
/// </summary>
public sealed record RestartArgs(RunState State = RunState.Armed, bool Tray = false, Version? UpdatedFrom = null, Version? UpdateFailed = null)
{
    /// <summary>
    /// The state an app restarts into. Idle running becomes armed: the idle rule starts it again by itself
    /// if the user is still away, and stops it otherwise.
    /// </summary>
    public static RunState StartStateAfter(RunState before) => before switch
    {
        RunState.Running => RunState.Running,
        RunState.Stopped => RunState.Stopped,
        _ => RunState.Armed,
    };

    public static RestartArgs Parse(IReadOnlyList<string> args)
    {
        var result = new RestartArgs();
        for (var i = 0; i < args.Count; i++)
        {
            var value = i + 1 < args.Count ? args[i + 1] : null;
            switch (args[i].ToLowerInvariant())
            {
                case "--tray":
                    result = result with { Tray = true };
                    break;
                case "--state" when value is not null:
                    result = result with
                    {
                        State = value.ToLowerInvariant() switch
                        {
                            "running" => RunState.Running,
                            "stopped" => RunState.Stopped,
                            _ => RunState.Armed,
                        },
                    };
                    i++;
                    break;
                case "--updated-from" when value is not null:
                    result = result with { UpdatedFrom = AppVersion.TryParse(value) };
                    i++;
                    break;
                case "--update-failed" when value is not null:
                    result = result with { UpdateFailed = AppVersion.TryParse(value) };
                    i++;
                    break;
            }
        }
        return result;
    }

    public IReadOnlyList<string> ToArgs()
    {
        var args = new List<string> { "--state", StartStateAfter(State).ToString().ToLowerInvariant() };
        if (Tray) args.Add("--tray");
        if (UpdatedFrom is not null) args.AddRange(new[] { "--updated-from", AppVersion.Format(UpdatedFrom) });
        if (UpdateFailed is not null) args.AddRange(new[] { "--update-failed", AppVersion.Format(UpdateFailed) });
        return args;
    }
}
