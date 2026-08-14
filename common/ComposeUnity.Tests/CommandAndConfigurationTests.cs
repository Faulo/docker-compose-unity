namespace ComposeUnity.Tests;

public sealed class CommandAndConfigurationTests {
    [Test]
    public void RoutesCanonicalSidecarCommand() {
        var command = CommandRouter.Route("compose-unity", ["sidecar", "health"]);

        Assert.That(command.mode, Is.EqualTo(EApplicationMode.SIDECAR));
        Assert.That(command.arguments, Is.EqualTo(new[] { "health" }));
    }

    [Test]
    public void RoutesLegacySidecarAlias() {
        var command = CommandRouter.Route("compose-unity-sidecar", ["status"]);

        Assert.That(command.mode, Is.EqualTo(EApplicationMode.SIDECAR));
        Assert.That(command.arguments, Is.EqualTo(new[] { "status" }));
    }

    [Test]
    public void UsesInvokedAliasInsteadOfResolvedProcessPath() {
        string executable = Program.ResolveExecutable(
            "/usr/local/bin/compose-unity",
            ["/usr/local/bin/compose-unity", "health"],
            "/usr/local/bin/compose-unity-sidecar");

        Assert.That(executable, Is.EqualTo("compose-unity-sidecar"));
    }

    [Test]
    public void PreservesComposerArguments() {
        string[] arguments = ["exec", "unity-build", "--", "sidecar"];
        var command = CommandRouter.Route("compose-unity", arguments);

        Assert.That(command.mode, Is.EqualTo(EApplicationMode.COMPOSER));
        Assert.That(command.arguments, Is.SameAs(arguments));
    }

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("0", false)]
    [TestCase("1", true)]
    public void ParsesMcpActivation(string? value, bool expected) =>
        Assert.That(McpActivation.Parse(value), Is.EqualTo(expected));

    [TestCase("true")]
    [TestCase(" 1")]
    [TestCase("2")]
    public void RejectsInvalidMcpActivation(string value) =>
        Assert.Throws<ArgumentException>(() => McpActivation.Parse(value));

    [TestCase(null, 86_400)]
    [TestCase("", 86_400)]
    [TestCase("0", 0)]
    [TestCase("42", 42)]
    public void ParsesCallTimeout(string? value, long expected) =>
        Assert.That(Program.ParseTimeout(value), Is.EqualTo(expected));

    [TestCase("-1")]
    [TestCase("one")]
    [TestCase(" 1")]
    public void RejectsInvalidCallTimeout(string value) =>
        Assert.Throws<ArgumentException>(() => Program.ParseTimeout(value));

    [Test]
    public void SanitizesLoggedCommandsWithoutArguments() {
        Assert.That(Program.SanitizeCommand(["exec", "--", "unity-command", "secret"]), Is.EqualTo("unity-command"));
        Assert.That(Program.SanitizeCommand(["exec", "weird command"]), Is.EqualTo("weird_command"));
    }

    [Test]
    public void FormatsDurationsAndDeadlines() {
        var started = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);

        Assert.That(Program.FormatDuration(TimeSpan.FromHours(25) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3)), Is.EqualTo("25:02:03"));
        Assert.That(Program.ResolveDeadline(started, 5), Is.EqualTo(started.AddSeconds(5)));
        Assert.That(Program.ResolveDeadline(started, 0), Is.Null);
    }

    [TestCase("http://localhost:8080", true)]
    [TestCase("https://127.0.0.1:1234", true)]
    [TestCase("http://[::1]:8080", true)]
    [TestCase("https://example.com", false)]
    [TestCase("null", false)]
    [TestCase(null, false)]
    public void ValidatesLoopbackOrigins(string? value, bool expected) =>
        Assert.That(McpServerRuntime.IsLoopbackOrigin(value), Is.EqualTo(expected));
}
