using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Schemora.Relationships;

/// <summary>Known reference metadata embedded at build time; safe in single-file releases and never loads user assemblies.</summary>
internal static class EfMetadataReferences
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(Load);
    internal static IReadOnlyList<MetadataReference> All => References.Value;

    private static IReadOnlyList<MetadataReference> Load()
    {
        var assembly = typeof(EfMetadataReferences).Assembly;
        var result = new List<MetadataReference>();
        foreach (
            var name in assembly
                .GetManifestResourceNames()
                .Where(name => name.StartsWith("Schemora.Reference/", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
        )
        {
            using var input = assembly.GetManifestResourceStream(name)!;
            using var output = new MemoryStream();
            input.CopyTo(output);
            result.Add(MetadataReference.CreateFromImage(ImmutableArray.Create(output.ToArray()), filePath: name));
        }
        if (result.Count == 0)
            throw new InvalidOperationException("Embedded EF/framework reference metadata is unavailable.");
        return result;
    }
}
