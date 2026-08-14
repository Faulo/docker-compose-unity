namespace ComposeUnity.Tests;

public sealed class CommandAndConfigurationTests {
    [Fact]
    public void RoutesCanonicalSidecarCommand() {
        var command = CommandRouter.Route("compose-unity", ["sidecar", "health"]);

        Assert.Equal(EApplicationMode.SIDECAR, command.mode);
        Assert.Equal(["health"], command.arguments);
    }

    [Fact]
    public void RoutesLegacySidecarAlias() {
        var command = CommandRouter.Route("compose-unity-sidecar", ["status"]);

        Assert.Equal(EApplicationMode.SIDECAR, command.mode);
        Assert.Equal(["status"], command.arguments);
    }

    [Fact]
    public void UsesInvokedAliasInsteadOfResolvedProcessPath() {
        string executable = Program.ResolveExecutable(
            "/usr/local/bin/compose-unity",
            ["/usr/local/bin/compose-unity", "health"],
            "/usr/local/bin/compose-unity-sidecar");

        Assert.Equal("compose-unity-sidecar", executable);
    }

    [Fact]
    public void PreservesComposerArguments() {
        string[] arguments = ["exec", "unity-build", "--", "sidecar"];
        var command = CommandRouter.Route("compose-unity", arguments);

        Assert.Equal(EApplicationMode.COMPOSER, command.mode);
        Assert.Same(arguments, command.arguments);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void ParsesMcpActivation(string? value, bool expected) =>
        Assert.Equal(expected, McpActivation.Parse(value));

    [Theory]
    [InlineData("true")]
    [InlineData(" 1")]
    [InlineData("2")]
    public void RejectsInvalidMcpActivation(string value) =>
        Assert.Throws<ArgumentException>(() => McpActivation.Parse(value));

    [Theory]
    [InlineData(null, 86_400)]
    [InlineData("", 86_400)]
    [InlineData("0", 0)]
    [InlineData("42", 42)]
    public void ParsesCallTimeout(string? value, long expected) =>
        Assert.Equal(expected, Program.ParseTimeout(value));

    [Theory]
    [InlineData("-1")]
    [InlineData("one")]
    [InlineData(" 1")]
    public void RejectsInvalidCallTimeout(string value) =>
        Assert.Throws<ArgumentException>(() => Program.ParseTimeout(value));

    [Fact]
    public void SanitizesLoggedCommandsWithoutArguments() {
        Assert.Equal("unity-command", Program.SanitizeCommand(["exec", "--", "unity-command", "secret"]));
        Assert.Equal("weird_command", Program.SanitizeCommand(["exec", "weird command"]));
    }

    [Fact]
    public void FormatsDurationsAndDeadlines() {
        var started = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal("25:02:03", Program.FormatDuration(TimeSpan.FromHours(25) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3)));
        Assert.Equal(started.AddSeconds(5), Program.ResolveDeadline(started, 5));
        Assert.Null(Program.ResolveDeadline(started, 0));
    }

    [Theory]
    [InlineData("http://localhost:8080", true)]
    [InlineData("https://127.0.0.1:1234", true)]
    [InlineData("http://[::1]:8080", true)]
    [InlineData("https://example.com", false)]
    [InlineData("null", false)]
    [InlineData(null, false)]
    public void ValidatesLoopbackOrigins(string? value, bool expected) =>
        Assert.Equal(expected, McpServerRuntime.IsLoopbackOrigin(value));
}
