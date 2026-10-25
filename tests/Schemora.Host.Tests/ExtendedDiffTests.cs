using Schemora.SqlServer;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class ExtendedDiffTests
{
    [Fact]
    public void ObjectBodyIndexAndUniqueChangesAppearInTheSameDiff()
    {
        var before = new SchemaGraph(
            "Db",
            "schema.sql",
            DateTimeOffset.UtcNow,
            [new("t", "dbo", "Data", [new("Id", "int", false)], ["Id"], [new("uq", "UQ_Id", ["Id"])])],
            [],
            [],
            [new("v", "dbo", "View", "view", [new("Id", "int", false)], Definition: "SELECT Id FROM dbo.Data")],
            [new("ix", "t", "IX_Id", "nonclustered", false, false, [new("Id", false)], [])],
            [new("dep", "v", "t", "dbo.Data", "static-reference", "schema.sql", 1, 1)]
        );
        var after = before with
        {
            Tables = [before.Tables[0] with { UniqueKeys = [] }],
            Objects = [before.Objects![0] with { Definition = "SELECT Id FROM dbo.Data WHERE Id > 0" }],
            Indexes = [before.Indexes![0] with { Columns = [new("Id", true)] }],
            ObjectDependencies = [],
        };
        var diff = new SchemaDiffer().Compare(before, after);
        Assert.Contains(diff.Changes, c => c.Kind == "objectChanged" && c.Table == "View");
        Assert.Contains(diff.Changes, c => c.Kind == "indexChanged" && c.Table == "Data");
        Assert.Contains(diff.Changes, c => c.Kind == "uniqueKeysChanged");
        Assert.Contains(diff.Changes, c => c.Kind == "objectDependencyRemoved");
        Assert.Empty(diff.RenameCandidates);
    }

    [Fact]
    public void ExtractionTimestampSourceLocationsAndRawIdsDoNotInventChanges()
    {
        var before = new SchemaGraph(
            "Db",
            "a.sql",
            DateTimeOffset.UtcNow,
            [new("t", "dbo", "Data", [new("Id", "int", false)], ["Id"])],
            [],
            [],
            [new("v", "dbo", "View", "view", [], Definition: "SELECT Id FROM dbo.Data")],
            [],
            [new("dep", "v", "t", "dbo.Data", "static-reference", "a.sql", 1, 1)]
        );
        var after = before with
        {
            ExtractedAt = DateTimeOffset.UtcNow.AddDays(1),
            SourcePath = "b.sql",
            Tables = [before.Tables[0] with { Id = "new-t" }],
            Objects = [before.Objects![0] with { Id = "new-v", SourcePath = "b.sql", Line = 500 }],
            ObjectDependencies =
            [
                before.ObjectDependencies![0] with
                {
                    Id = "new-dep",
                    FromObjectId = "new-v",
                    ToObjectId = "new-t",
                    SourcePath = "b.sql",
                    Line = 500,
                },
            ],
        };
        Assert.Empty(new SchemaDiffer().Compare(before, after).Changes);
    }
}
