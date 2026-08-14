using System.Text.Json;
using System.Text.Json.Nodes;

namespace ComposeUnity.Tests;

public sealed class TestResultTests {
    [Fact]
    public void BuildsPassedResultFromJUnit() {
        const string xml = """
                           <testsuites>
                             <testsuite tests="2" failures="0" errors="0" skipped="1" time="1.2345">
                               <testcase name="Passes" classname="Example.Tests" />
                               <testcase name="Skipped" classname="Example.Tests"><skipped /></testcase>
                             </testsuite>
                           </testsuites>
                           """;

        var result = Build(xml, 0);

        Assert.Equal("passed", result["outcome"]?.GetValue<string>());
        Assert.Equal(2, result["counts"]?["total"]?.GetValue<int>());
        Assert.Equal(1, result["counts"]?["passed"]?.GetValue<int>());
        Assert.Equal(1, result["counts"]?["skipped"]?.GetValue<int>());
        Assert.Equal(1.234m, result["durationSeconds"]?.GetValue<decimal>());
        Assert.Empty(result["failures"]!.AsArray());
    }

    [Fact]
    public void BuildsFailedResultWithCompleteDetails() {
        const string xml = """
                           <testsuites>
                             <testsuite tests="1" failures="1" errors="0" skipped="0" time="2">
                               <testcase name="Fails" classname="Example.Tests"><failure message="Expected true" type="AssertionException">line one
                           line two</failure></testcase>
                             </testsuite>
                           </testsuites>
                           """;

        var result = Build(xml, 2);
        var failure = result["failures"]![0]!.AsObject();

        Assert.Equal("failed", result["outcome"]?.GetValue<string>());
        Assert.Equal(2, result["exitCode"]?.GetValue<int>());
        Assert.Equal("Fails", failure["name"]?.GetValue<string>());
        Assert.Contains("line two", failure["stackTrace"]?.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(result["failuresTruncated"]?.GetValue<bool>());
    }

    [Fact]
    public void TruncatesFailureRecordsAtOneHundred() {
        string cases = string.Concat(Enumerable.Range(0, 101)
            .Select(index => $"<testcase name=\"Failure{index}\"><failure message=\"bad\">stack {index}</failure></testcase>"));
        string xml = $"<testsuites><testsuite tests=\"101\" failures=\"101\" errors=\"0\" skipped=\"0\" time=\"0\">{cases}</testsuite></testsuites>";

        var result = Build(xml, 2);

        Assert.Equal(100, result["failures"]!.AsArray().Count);
        Assert.True(result["failuresTruncated"]?.GetValue<bool>());
    }

    [Theory]
    [InlineData("not xml", 1)]
    [InlineData("<testsuites />", 1)]
    [InlineData("<testsuites><testsuite tests=\"0\" failures=\"0\" errors=\"0\" skipped=\"0\" /></testsuites>", 1)]
    public void ReturnsCompleteOrderedLogForUntrustworthyResults(string standardOutput, int exitCode) {
        var result = new ExecResult("id", exitCode, standardOutput, "stderr", "[stdout]\nxml\n[stderr]\nstderr");
        var value = JsonNode.Parse(JsonSerializer.Serialize(UnityMcpController.BuildTestResult(result)))!.AsObject();

        Assert.Equal("error", value["outcome"]?.GetValue<string>());
        Assert.Equal("[stdout]\nxml\n[stderr]\nstderr", value["log"]?.GetValue<string>());
        Assert.Equal(3, value.Count);
    }

    static JsonObject Build(string standardOutput, int exitCode) {
        var result = new ExecResult("id", exitCode, standardOutput, "stderr", "combined");
        return JsonNode.Parse(JsonSerializer.Serialize(UnityMcpController.BuildTestResult(result)))!.AsObject();
    }
}
