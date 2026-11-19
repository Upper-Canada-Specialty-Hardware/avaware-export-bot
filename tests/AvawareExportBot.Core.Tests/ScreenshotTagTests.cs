using AvawareExportBot.Core;
using Xunit;

namespace AvawareExportBot.Core.Tests;

public sealed class ScreenshotTagTests
{
    [Theory]
    [InlineData("export: timed out [screenshot: C:\\s\\a.png]", "C:\\s\\a.png")]
    [InlineData("menu: odd [x] [screenshot: C:\\s\\job [rev 2].png]", "C:\\s\\job [rev 2].png")]
    [InlineData("no screenshot here", null)]
    [InlineData(null, null)]
    public void Finds_the_screenshot_path_even_with_brackets(string? error, string? expected) =>
        Assert.Equal(expected, RunController.ScreenshotIn(error));
}
