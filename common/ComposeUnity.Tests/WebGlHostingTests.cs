namespace ComposeUnity.Tests;

public sealed class WebGlHostingTests {
    [TestCase("Example Game", "example-game")]
    [TestCase("  A/B:C  ", "a-b-c")]
    [TestCase("___", "project")]
    [TestCase(null, "project")]
    public void CreatesReadableSafeProjectSlugs(string? value, string expected) {
        Assert.That(WebGlHosting.ProjectSlug(value), Is.EqualTo(expected));
    }

    [TestCase("game.wasm", "application/wasm")]
    [TestCase("game.wasm.br", "application/wasm")]
    [TestCase("game.js.gz", "application/javascript")]
    [TestCase("game.data", "application/octet-stream")]
    [TestCase("game.data.gz", "application/gzip")]
    [TestCase("game.symbols.json.br", "application/octet-stream")]
    [TestCase("game.unityweb", "application/octet-stream")]
    public void ProvidesUnityWebContentTypes(string path, string expected) {
        var provider = new UnityWebContentTypeProvider();

        bool found = provider.TryGetContentType(path, out string contentType);

        using (Assert.EnterMultipleScope()) {
            Assert.That(found, Is.True);
            Assert.That(contentType, Is.EqualTo(expected));
        }
    }

    [Test]
    public async Task ClaimsHumanReadableTimestampDirectory() {
        string root = Path.Combine(Path.GetTempPath(), "compose-unity-webgl-tests-" + Guid.NewGuid().ToString("N"));
        try {
            var build = await WebGlHosting.ClaimBuildDirectoryAsync(root, "example-game", CancellationToken.None);

            using (Assert.EnterMultipleScope()) {
                Assert.That(build.projectSlug, Is.EqualTo("example-game"));
                Assert.That(build.buildId, Does.Match(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}Z$"));
                Assert.That(Directory.Exists(build.directory), Is.True);
                Assert.That(WebGlHosting.PublicPath(build), Is.EqualTo($"/webgl/example-game/{build.buildId}/"));
            }
        } finally {
            if (Directory.Exists(root)) {
                Directory.Delete(root, true);
            }
        }
    }
}
