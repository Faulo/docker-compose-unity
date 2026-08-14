using System.Text.Json.Nodes;

namespace ComposeUnity.Tests;

public sealed class ProjectProbeTests {
    static string ValidProject {
        get => Path.Combine(AppContext.BaseDirectory, "test-files", "ValidProject");
    }

    [Fact]
    public void ReadsCompleteUnityProject() {
        var result = ProjectProbe.Read(ValidProject);

        Assert.Equal("Example Company", result.companyName);
        Assert.Equal("Example Game", result.projectName);
        Assert.Equal("1.2.3", result.projectVersion);
        Assert.Equal("6000.3.13f1", result.editorVersion);
        Assert.Equal("8c4f11e4fb20", result.editorRevision);
        Assert.Equal(".NET Standard 2.1", result.apiCompatibility);
        Assert.False(result.allowUnsafeCode);
        Assert.Equal("IL2CPP", result.scriptingBackendOverrides["WindowsStandaloneSupport"]);
        Assert.Equal("Unknown (99)", result.scriptingBackendOverrides["UnknownTarget"]);
        Assert.Equal("Universal", result.renderPipeline);
        Assert.Equal("Linear", result.colorSpace);
        Assert.Equal("InputSystem", result.inputHandling);
        Assert.False(result.graphicsApis["WindowsStandaloneSupport"].automatic);
        Assert.Equal(["Direct3D11", "Direct3D12"], result.graphicsApis["WindowsStandaloneSupport"].apis);
        Assert.Equal("1.2.3", result.packages["custom"]?.GetValue<string>());
    }

    [Fact]
    public void ReportsUnknownWhenGraphicsSettingsIsOptionalAndMissing() {
        using var project = TemporaryProject.CopyOf(ValidProject);
        File.Delete(Path.Combine(project.path, "ProjectSettings", "GraphicsSettings.asset"));

        Assert.Equal("Unknown", ProjectProbe.Read(project.path).renderPipeline);
    }

    [Fact]
    public void RejectsMissingUnityProjectDirectory() {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try {
            var exception = Assert.Throws<InvalidOperationException>(() => ProjectProbe.Read(path));
            Assert.Contains("Assets", exception.Message, StringComparison.Ordinal);
        } finally {
            Directory.Delete(path, true);
        }
    }

    [Fact]
    public void PreservesCompleteManifest() {
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(ValidProject, "Packages", "manifest.json")))!.AsObject();

        Assert.True(JsonNode.DeepEquals(expected, ProjectProbe.Read(ValidProject).packages));
    }

    sealed class TemporaryProject : IDisposable {
        TemporaryProject(string path) => this.path = path;

        internal string path { get; }

        public void Dispose() => Directory.Delete(path, true);

        internal static TemporaryProject CopyOf(string source) {
            string destination = Path.Combine(Path.GetTempPath(), "compose-unity-tests-" + Guid.NewGuid().ToString("N"));
            CopyDirectory(source, destination);
            return new TemporaryProject(destination);
        }

        static void CopyDirectory(string source, string destination) {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.EnumerateFiles(source)) {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }

            foreach (string directory in Directory.EnumerateDirectories(source)) {
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }
    }
}
