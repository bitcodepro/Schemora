using System.Text;
using Schemora.Host;
using Schemora.RecipeEngine;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class ValueSetTests
{
    private static readonly RecipeDefinition Recipe = new(
        Guid.NewGuid().ToString("N"),
        "Orders",
        "",
        [],
        "sqlserver",
        ["db"],
        1,
        "SELECT o.OrderId FROM dbo.Orders AS o WHERE o.OrderId IN ({{ids:Ids}}) AND o.CreatedAt >= {{value:From}};",
        [new("Ids", "bigint"), new("From", "date")]
    );

    private static void InTemporaryDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "value-sets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            test(directory);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void SavedValuesAreEncryptedAndBoundToTheRecipe()
    {
        InTemporaryDirectory(directory =>
        {
            var store = new ValueSetStore(directory);
            var sentinel = "987654321" + Random.Shared.Next(100_000, 999_999);
            var saved = store.Save(
                Recipe.Id,
                Recipe,
                new("Customer A", new Dictionary<string, string> { ["Ids"] = sentinel + ",42", ["From"] = "2026-10-01" })
            );
            Assert.Equal(["From", "Ids"], saved.Inputs);
            var file = Assert.Single(Directory.GetFiles(Path.Combine(directory, "value-sets")));
            var bytes = File.ReadAllBytes(file);
            Assert.DoesNotContain(sentinel, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
            Assert.DoesNotContain(sentinel, Encoding.Unicode.GetString(bytes), StringComparison.Ordinal);
            Assert.DoesNotContain("Customer A", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
            // A fresh store (as after a restart) decrypts what the same Windows account saved.
            Assert.Equal(sentinel + ",42", new ValueSetStore(directory).Get(Recipe.Id, saved.Id).Values["Ids"]);
            // The same bytes under another recipe's name do not decrypt.
            var other = Guid.NewGuid().ToString("N");
            File.Copy(file, Path.Combine(Path.GetDirectoryName(file)!, other + ".dat"));
            Assert.Equal("VALUE_SETS_UNREADABLE", Assert.Throws<AppException>(() => store.List(other)).Code);
            store.DeleteAll(other);
            Assert.Empty(store.List(other));
        });
    }

    [Fact]
    public void SavingUnderTheSameNameReplacesAndDeletingTheLastSetRemovesTheFile()
    {
        InTemporaryDirectory(directory =>
        {
            var store = new ValueSetStore(directory);
            var first = store.Save(Recipe.Id, Recipe, new("Week", new Dictionary<string, string> { ["From"] = "2026-10-01" }));
            var second = store.Save(Recipe.Id, Recipe, new(" week ", new Dictionary<string, string> { ["From"] = "2026-10-08" }));
            Assert.Equal(first.Id, second.Id);
            Assert.Equal(first.Id, Assert.Single(store.List(Recipe.Id)).Id);
            Assert.Equal("2026-10-08", store.Get(Recipe.Id, first.Id).Values["From"]);
            store.Delete(Recipe.Id, first.Id);
            Assert.Empty(store.List(Recipe.Id));
            Assert.Empty(Directory.GetFiles(Path.Combine(directory, "value-sets")));
            Assert.Equal("VALUE_SET_NOT_FOUND", Assert.Throws<AppException>(() => store.Delete(Recipe.Id, first.Id)).Code);
        });
    }

    [Fact]
    public void RequestsAreCheckedAgainstTheRecipe()
    {
        InTemporaryDirectory(directory =>
        {
            var store = new ValueSetStore(directory);
            Assert.Equal(
                "VALUE_SET_INPUT_UNKNOWN",
                Assert
                    .Throws<AppException>(() => store.Save(Recipe.Id, Recipe, new("x", new Dictionary<string, string> { ["Other"] = "1" })))
                    .Code
            );
            Assert.Equal(
                "VALUE_SET_NAME_INVALID",
                Assert
                    .Throws<AppException>(() => store.Save(Recipe.Id, Recipe, new("  ", new Dictionary<string, string> { ["Ids"] = "1" })))
                    .Code
            );
            Assert.Equal(
                "VALUE_SET_EMPTY",
                Assert.Throws<AppException>(() => store.Save(Recipe.Id, Recipe, new("x", new Dictionary<string, string>()))).Code
            );
            Assert.Equal("RECIPE_NOT_FOUND", Assert.Throws<AppException>(() => store.List("..\\catalog")).Code);
            for (var index = 0; index < ValueSetStore.MaximumSets; index++)
                store.Save(Recipe.Id, Recipe, new("Set " + index, new Dictionary<string, string> { ["Ids"] = index.ToString() }));
            Assert.Equal(
                "VALUE_SET_LIMIT",
                Assert
                    .Throws<AppException>(() =>
                        store.Save(Recipe.Id, Recipe, new("One more", new Dictionary<string, string> { ["Ids"] = "1" }))
                    )
                    .Code
            );
        });
    }
}
