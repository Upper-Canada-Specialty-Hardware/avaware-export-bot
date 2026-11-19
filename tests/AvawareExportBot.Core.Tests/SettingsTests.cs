using AvawareExportBot.Core;
using Xunit;

namespace AvawareExportBot.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly TempDir _dir = new();

    [Fact]
    public void Defaults_follow_the_product_ruling()
    {
        var s = new Settings();
        Assert.Equal(@"T:\", s.RootFolder);
        Assert.Equal(Math.Max(1, Environment.ProcessorCount - 1), s.Workers);
        Assert.Equal(60, s.IdleMinutes);
        Assert.Equal(60, s.RescanMinutes);
        Assert.Equal(3, s.MaxAttempts);
    }

    [Fact]
    public void Round_trips_and_falls_back_to_defaults()
    {
        var path = _dir.Combine("settings.json");
        Assert.Equal(60, Settings.Load(path).IdleMinutes);
        new Settings { IdleMinutes = 15, RootFolder = @"X:\" }.Save(path);
        var loaded = Settings.Load(path);
        Assert.Equal(15, loaded.IdleMinutes);
        Assert.Equal(@"X:\", loaded.RootFolder);
        File.WriteAllText(path, "{ not json");
        Assert.Equal(60, Settings.Load(path).IdleMinutes);
    }

    [Fact]
    public void Validation_reports_each_problem()
    {
        var s = new Settings { Workers = 0, IdleMinutes = 0, RescanMinutes = 0, MaxAttempts = 0 };
        var problems = s.Validate(_ => false, _ => false);
        Assert.Equal(6, problems.Count);
        Assert.Empty(new Settings().Validate(_ => true, _ => true));
    }

    public void Dispose() => _dir.Dispose();
}
