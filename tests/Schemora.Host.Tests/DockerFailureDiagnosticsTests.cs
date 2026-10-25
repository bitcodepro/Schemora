using System.Text.Json;
using Schemora.Host;
using Schemora.Workspaces;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class DockerFailureDiagnosticsTests
{
    [Fact]
    public void FailureJournalIsBoundedRedactsBeforeTruncationAndRetainsStage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docker-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var secret = "ScH9!synthetic-password";
            var error = new AppException(
                "DOCKER_SCHEMA_PUBLISH_FAILED",
                "Failed",
                422,
                "SQL error 102\nPassword=other-secret; token='quoted secret'\n" + new string('x', 3900) + secret + new string('z', 5000)
            );
            string detail = "";
            for (int i = 0; i < 22; i++)
                detail = DockerFailureDiagnostics.Record(directory, "schema-publish", 1234, error, secret, 1);
            var report = JsonSerializer.Deserialize<DockerFailureReport>(detail, CatalogStore.Json)!;
            Assert.Equal("schema-publish", report.Stage);
            Assert.Equal(1234, report.ElapsedMs);
            Assert.Equal(1, report.ExitCode);
            Assert.Contains("SQL error 102", report.Output);
            Assert.True(report.Output.Length <= 4000);
            Assert.DoesNotContain(secret, detail);
            Assert.DoesNotContain("other-secret", detail);
            Assert.DoesNotContain("quoted secret", detail);
            var files = Directory.GetFiles(Path.Combine(directory, "docker-diagnostics"));
            Assert.Equal(20, files.Length);
            Assert.All(files, file => Assert.DoesNotContain(secret, File.ReadAllText(file)));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
