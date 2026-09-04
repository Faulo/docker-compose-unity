using System.Diagnostics;

namespace ComposeUnity.Tests;

public sealed class RuntimeCredentialsTests {
    [Test]
    public void ResolvesDirectFileBackedAndMixedPairs() {
        var environment = new Dictionary<string, string?> {
            ["UNITY_CREDENTIALS_USR"] = "unity-user",
            ["UNITY_CREDENTIALS_PSW"] = "unity-password",
            ["EMAIL_CREDENTIALS_USR"] = "email-user",
            ["EMAIL_CREDENTIALS_PSW_FILE"] = "/secrets/email-password",
            ["STEAM_CREDENTIALS_USR_FILE"] = "/secrets/steam-user",
            ["STEAM_CREDENTIALS_PSW_FILE"] = "/secrets/steam-password"
        };
        var files = new Dictionary<string, string> {
            ["/secrets/email-password"] = "email-password\n",
            ["/secrets/steam-user"] = "steam-user\r\n",
            ["/secrets/steam-password"] = "steam-password"
        };

        var credentials = Resolve(environment, path => files[path]);
        var startInfo = new ProcessStartInfo();
        startInfo.Environment.Clear();
        foreach (var variable in environment) {
            startInfo.Environment[variable.Key] = variable.Value;
        }
        credentials.ApplyTo(startInfo);

        Assert.Multiple(() => {
            Assert.That(startInfo.Environment["UNITY_CREDENTIALS_USR"], Is.EqualTo("unity-user"));
            Assert.That(startInfo.Environment["UNITY_CREDENTIALS_PSW"], Is.EqualTo("unity-password"));
            Assert.That(startInfo.Environment["EMAIL_CREDENTIALS_USR"], Is.EqualTo("email-user"));
            Assert.That(startInfo.Environment["EMAIL_CREDENTIALS_PSW"], Is.EqualTo("email-password"));
            Assert.That(startInfo.Environment["STEAM_CREDENTIALS_USR"], Is.EqualTo("steam-user"));
            Assert.That(startInfo.Environment["STEAM_CREDENTIALS_PSW"], Is.EqualTo("steam-password"));
            Assert.That(startInfo.Environment.Keys, Has.None.EndsWith("_FILE"));
        });
    }

    [TestCase("UNITY_CREDENTIALS_USR")]
    [TestCase("UNITY_CREDENTIALS_PSW")]
    [TestCase("EMAIL_CREDENTIALS_USR")]
    [TestCase("EMAIL_CREDENTIALS_PSW")]
    [TestCase("STEAM_CREDENTIALS_USR")]
    [TestCase("STEAM_CREDENTIALS_PSW")]
    public void RejectsDirectAndFileBackedFormsTogether(string name) {
        var environment = new Dictionary<string, string?> {
            [name] = "direct-secret",
            [name + "_FILE"] = "/secrets/value"
        };

        var exception = Assert.Throws<InvalidOperationException>(() => Resolve(environment, _ => "file-secret"));

        Assert.Multiple(() => {
            Assert.That(exception!.Message, Does.Contain(name));
            Assert.That(exception.Message, Does.Contain(name + "_FILE"));
            Assert.That(exception.Message, Does.Not.Contain("direct-secret"));
            Assert.That(exception.Message, Does.Not.Contain("file-secret"));
        });
    }

    [Test]
    public void RejectsMissingCredentialFileWithoutLeakingPairedValue() {
        var environment = new Dictionary<string, string?> {
            ["UNITY_CREDENTIALS_USR_FILE"] = "/secrets/missing",
            ["UNITY_CREDENTIALS_PSW"] = "paired-secret"
        };

        var exception = Assert.Throws<InvalidOperationException>(() => Resolve(
            environment,
            path => throw new FileNotFoundException("missing", path)));

        Assert.Multiple(() => {
            Assert.That(exception!.Message, Does.Contain("UNITY_CREDENTIALS_USR_FILE"));
            Assert.That(exception.Message, Does.Contain("/secrets/missing"));
            Assert.That(exception.Message, Does.Not.Contain("paired-secret"));
        });
    }

    [Test]
    public void RejectsUnreadableCredentialFile() {
        var environment = new Dictionary<string, string?> {
            ["EMAIL_CREDENTIALS_USR_FILE"] = "/secrets/email-user",
            ["EMAIL_CREDENTIALS_PSW_FILE"] = "/secrets/email-password"
        };

        var exception = Assert.Throws<InvalidOperationException>(() => Resolve(
            environment,
            _ => throw new UnauthorizedAccessException("denied")));

        Assert.That(exception!.Message, Does.Contain("EMAIL_CREDENTIALS_USR_FILE"));
    }

    [TestCase("")]
    [TestCase("\n")]
    [TestCase("\r\n")]
    public void RejectsEmptyCredentialFile(string contents) {
        var environment = new Dictionary<string, string?> {
            ["STEAM_CREDENTIALS_USR_FILE"] = "/secrets/steam-user",
            ["STEAM_CREDENTIALS_PSW_FILE"] = "/secrets/steam-password"
        };

        var exception = Assert.Throws<InvalidOperationException>(() => Resolve(environment, _ => contents));

        Assert.That(exception!.Message, Does.Contain("STEAM_CREDENTIALS_USR_FILE").And.Contain("empty"));
    }

    [TestCase("UNITY_CREDENTIALS_USR", "UNITY_CREDENTIALS_PSW")]
    [TestCase("EMAIL_CREDENTIALS_USR", "EMAIL_CREDENTIALS_PSW")]
    [TestCase("STEAM_CREDENTIALS_USR", "STEAM_CREDENTIALS_PSW")]
    public void RejectsIncompleteCredentialPair(string present, string missing) {
        var environment = new Dictionary<string, string?> { [present] = "configured-secret" };

        var exception = Assert.Throws<InvalidOperationException>(() => Resolve(environment, _ => throw new InvalidOperationException()));

        Assert.Multiple(() => {
            Assert.That(exception!.Message, Does.Contain(present));
            Assert.That(exception.Message, Does.Contain(missing));
            Assert.That(exception.Message, Does.Not.Contain("configured-secret"));
        });
    }

    [Test]
    public void TreatsCompleteEmptyDirectPairAsUnconfigured() {
        var environment = new Dictionary<string, string?> {
            ["UNITY_CREDENTIALS_USR"] = string.Empty,
            ["UNITY_CREDENTIALS_PSW"] = string.Empty
        };

        var credentials = Resolve(environment, _ => throw new InvalidOperationException());
        var startInfo = new ProcessStartInfo();
        startInfo.Environment.Clear();
        startInfo.Environment["UNITY_CREDENTIALS_USR"] = string.Empty;
        startInfo.Environment["UNITY_CREDENTIALS_PSW"] = string.Empty;
        credentials.ApplyTo(startInfo);

        Assert.Multiple(() => {
            Assert.That(startInfo.Environment.ContainsKey("UNITY_CREDENTIALS_USR"), Is.False);
            Assert.That(startInfo.Environment.ContainsKey("UNITY_CREDENTIALS_PSW"), Is.False);
        });
    }

    [Test]
    public void RejectsEmptyCredentialFilePath() {
        var environment = new Dictionary<string, string?> {
            ["UNITY_CREDENTIALS_USR_FILE"] = " ",
            ["UNITY_CREDENTIALS_PSW_FILE"] = "/secrets/unity-password"
        };

        var exception = Assert.Throws<InvalidOperationException>(() => Resolve(environment, _ => "secret"));

        Assert.That(exception!.Message, Does.Contain("UNITY_CREDENTIALS_USR_FILE"));
    }

    [Test]
    public void ExposesOnlyUnityAndEmailCredentialsToWorkers() {
        var environment = new Dictionary<string, string?> {
            ["UNITY_CREDENTIALS_USR"] = "unity-user",
            ["UNITY_CREDENTIALS_PSW"] = "unity-password",
            ["EMAIL_CREDENTIALS_USR"] = "email-user",
            ["EMAIL_CREDENTIALS_PSW"] = "email-password",
            ["STEAM_CREDENTIALS_USR"] = "steam-user",
            ["STEAM_CREDENTIALS_PSW"] = "steam-password"
        };

        var credentials = Resolve(environment, _ => throw new InvalidOperationException());

        Assert.That(credentials.WorkerEnvironment(), Is.EqualTo(new[] {
            "UNITY_CREDENTIALS_USR=unity-user",
            "UNITY_CREDENTIALS_PSW=unity-password",
            "EMAIL_CREDENTIALS_USR=email-user",
            "EMAIL_CREDENTIALS_PSW=email-password"
        }));
    }

    static RuntimeCredentials Resolve(
        IReadOnlyDictionary<string, string?> environment,
        Func<string, string> readFile) =>
        RuntimeCredentials.Resolve(
            name => environment.TryGetValue(name, out string? value) ? value : null,
            readFile);
}
