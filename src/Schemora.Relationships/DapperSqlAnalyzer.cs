using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Schemora.SqlServer;

namespace Schemora.Relationships;

public sealed record DapperSqlAnalysisResult(IReadOnlyList<RelationshipEdge> Relations, IReadOnlyList<SchemaDiagnostic> Diagnostics);

/// <summary>Extracts constant SQL call arguments only; parameters and user code are never evaluated.</summary>
public sealed class DapperSqlAnalyzer
{
    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "Query",
        "QueryAsync",
        "QueryFirst",
        "QueryFirstAsync",
        "QueryFirstOrDefault",
        "QueryFirstOrDefaultAsync",
        "QuerySingle",
        "QuerySingleAsync",
        "QuerySingleOrDefault",
        "QuerySingleOrDefaultAsync",
        "QueryMultiple",
        "QueryMultipleAsync",
        "Execute",
        "ExecuteAsync",
    };

    public DapperSqlAnalysisResult Analyze(
        IReadOnlyList<RelationshipDatabase> databases,
        IReadOnlyList<EfSourceSet> sources,
        CancellationToken ct = default
    )
    {
        var edges = new Dictionary<string, RelationshipEdge>(StringComparer.Ordinal);
        var diagnostics = new List<SchemaDiagnostic>();
        var sqlBytes = 0L;
        var queryCount = 0;
        foreach (var scope in sources.GroupBy(source => source.DatabaseId, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var assigned = databases.Where(database => database.Id == scope.Key).ToArray();
            if (assigned.Length != 1)
            {
                Warn("DAPPER_DATABASE_UNASSIGNED", "C# SQL evidence needs one explicit database assignment.");
                continue;
            }
            var contexts = scope.Where(s => s.Context is not null).Select(s => s.Context!).ToArray();
            diagnostics.AddRange(contexts.SelectMany(c => c.Diagnostics));
            var projectFiles = contexts
                .SelectMany(c => c.Files)
                .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var trees = new List<SyntaxTree>();
            long sourceBytes = 0;
            foreach (
                var input in scope
                    .SelectMany(s => s.Context is null ? s.Paths : s.Context.Files.Select(f => f.Path))
                    .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                    .Take(4097)
            )
            {
                ct.ThrowIfCancellationRequested();
                if (trees.Count >= 4096)
                {
                    Warn("DAPPER_SOURCE_LIMIT", "Embedded SQL analysis stopped at 4096 C# files.");
                    break;
                }
                try
                {
                    var path = Path.GetFullPath(input);
                    if (path.StartsWith(@"\\", StringComparison.Ordinal) || new Uri(path).IsUnc)
                    {
                        Warn("DAPPER_LOCAL_SOURCE_REQUIRED", "Embedded SQL analysis accepts local C# files only.");
                        continue;
                    }
                    var info = new FileInfo(path);
                    if (!info.Exists || !info.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
                    {
                        Warn("DAPPER_SOURCE_UNAVAILABLE", $"C# source is unavailable: {path}");
                        continue;
                    }
                    for (
                        FileSystemInfo? entry = info;
                        entry is not null;
                        entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent
                    )
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("Linked source.");
                    if (info.Length > 4 * 1024 * 1024 || (sourceBytes += info.Length) > 64 * 1024 * 1024)
                    {
                        Warn("DAPPER_SOURCE_LIMIT", $"C# source size limit: {path}");
                        continue;
                    }
                    trees.Add(
                        CSharpSyntaxTree.ParseText(
                            File.ReadAllText(path),
                            CSharpProjectContextEvaluator.ParseOptions(projectFiles.GetValueOrDefault(path)),
                            path,
                            cancellationToken: ct
                        )
                    );
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    Warn("DAPPER_SOURCE_UNAVAILABLE", $"C# source cannot be read: {input} ({ex.GetType().Name}).");
                }
            }
            if (trees.Count == 0)
                continue;
            IReadOnlyList<MetadataReference> references;
            try
            {
                references = EfMetadataReferences.All;
            }
            catch (Exception ex) when (ex is InvalidOperationException or BadImageFormatException)
            {
                references = [];
                Warn(
                    "DAPPER_SEMANTIC_UNAVAILABLE",
                    "Embedded reference metadata unavailable; only unresolved literal call evidence can be considered."
                );
            }
            var selectedReferences = references.ToList();
            foreach (var reference in contexts.SelectMany(c => c.ReferencePaths).Distinct(StringComparer.OrdinalIgnoreCase))
                try
                {
                    CSharpProjectContextEvaluator.Check(reference);
                    selectedReferences.Add(MetadataReference.CreateFromFile(reference));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or BadImageFormatException)
                {
                    diagnostics.Add(
                        new(
                            "warning",
                            "CSHARP_REFERENCE_UNAVAILABLE",
                            $"Project metadata reference unavailable: {reference} ({ex.GetType().Name})."
                        )
                    );
                }
            references = selectedReferences;
            var compilation = CSharpCompilation.Create(
                "Schemora.DapperStaticAnalysis",
                trees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );
            foreach (var tree in trees)
            {
                ct.ThrowIfCancellationRequested();
                var root = tree.GetRoot(ct);
                if (tree.GetDiagnostics(ct).Any(d => d.Severity == DiagnosticSeverity.Error))
                    Warn(
                        "DAPPER_CSHARP_PARTIAL",
                        $"{tree.FilePath}: source has C# syntax errors; embedded SQL is candidate evidence only."
                    );
                if (
                    !projectFiles.ContainsKey(tree.FilePath)
                    && root.DescendantTrivia(descendIntoTrivia: true)
                        .Any(t => t.IsKind(SyntaxKind.IfDirectiveTrivia) || t.IsKind(SyntaxKind.DisabledTextTrivia))
                )
                    Warn(
                        "DAPPER_PREPROCESSOR_PARTIAL",
                        $"{tree.FilePath}: project preprocessor symbols are not evaluated; conditional SQL branches may be omitted."
                    );
                var model = compilation.GetSemanticModel(tree, true);
                foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    ct.ThrowIfCancellationRequested();
                    var methodName = call.Expression switch
                    {
                        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                        SimpleNameSyntax simple => simple.Identifier.ValueText,
                        _ => "",
                    };
                    if (!Methods.Contains(methodName))
                        continue;
                    if (++queryCount > 4096 || sqlBytes > 16 * 1024 * 1024)
                    {
                        Warn("DAPPER_SQL_LIMIT", "Embedded SQL analysis reached its 4096 calls / 16 MiB SQL budget.");
                        return Result();
                    }
                    var location = call.GetLocation().GetLineSpan();
                    var path = location.Path;
                    var callLine = location.StartLinePosition.Line + 1;
                    var expression = SqlArgument(call);
                    if (
                        expression is ObjectCreationExpressionSyntax creation
                        && creation.Type.ToString().Split('.').Last() == "CommandDefinition"
                    )
                        expression =
                            creation
                                .ArgumentList?.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "commandText")
                                ?.Expression
                            ?? creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
                    if (
                        expression is null
                        || expression.DescendantNodesAndSelf().Any(node => node is InterpolatedStringExpressionSyntax)
                        || model.GetConstantValue(expression, ct) is not { HasValue: true, Value: string sql }
                    )
                    {
                        Warn(
                            "DAPPER_SQL_DYNAMIC",
                            $"{path}({callLine}): SQL is not a static string/const expression; interpolation, local variables, builders and computed commands were not evaluated."
                        );
                        continue;
                    }
                    if ((sqlBytes += System.Text.Encoding.UTF8.GetByteCount(sql)) > 16 * 1024 * 1024 || sql.Length > 1024 * 1024)
                    {
                        Warn("DAPPER_SQL_LIMIT", $"{path}({callLine}): embedded SQL exceeds the analysis budget; contents were omitted.");
                        continue;
                    }
                    var bound = model.GetSymbolInfo(call, ct).Symbol as IMethodSymbol;
                    var official =
                        bound?.ContainingAssembly.Name is "Dapper" or "Dapper.StrongName"
                        && bound.ContainingNamespace.ToDisplayString() == "Dapper";
                    if (!official)
                        Warn(
                            "DAPPER_SYMBOL_UNRESOLVED",
                            $"{path}({callLine}): Dapper API identity is unproven; this is embedded SQL candidate evidence only."
                        );
                    var observed = new ObservedJoinAnalyzer().AnalyzeSqlText(assigned, sql, path, callLine, ct);
                    foreach (var diagnostic in observed.Diagnostics)
                    {
                        // Re-anchor diagnostics at the original C# call. Existing binder details contain schema identifiers only.
                        var separator = diagnostic.Message.IndexOf("): ", StringComparison.Ordinal);
                        Warn(
                            diagnostic.Code,
                            $"{path}({callLine}): "
                                + (separator >= 0 ? diagnostic.Message[(separator + 3)..] : "Embedded SQL could not be bound safely.")
                        );
                    }
                    foreach (var edge in observed.Relations)
                    {
                        var evidence = edge
                            .Evidence.Select(item =>
                                item with
                                {
                                    Kind = "dapper-sql",
                                    SourcePath = path,
                                    Line = OriginalLine(expression, item.Line.GetValueOrDefault(callLine) - callLine + 1, callLine),
                                    Detail =
                                        "C# "
                                        + methodName
                                        + ": "
                                        + item.Detail
                                        + " Dapper multi-mapping/splitOn does not establish a foreign key; parameter values were not inspected.",
                                }
                            )
                            .ToArray();
                        var identity = RelationshipRules.Identity(edge.Relation);
                        var candidate = edge with
                        {
                            Relation = edge.Relation with { Id = identity, Kind = "embedded-sql-candidate" },
                            Status = "candidate",
                            IsTrusted = false,
                            Evidence = evidence,
                        };
                        edges[identity] = edges.TryGetValue(identity, out var old)
                            ? candidate with
                            {
                                Evidence = old.Evidence.Concat(evidence).Distinct().ToArray(),
                            }
                            : candidate;
                    }
                }
            }
        }
        return Result();
        DapperSqlAnalysisResult Result() =>
            new(edges.Values.OrderBy(edge => edge.Relation.Id, StringComparer.Ordinal).ToArray(), diagnostics);
        void Warn(string code, string message)
        {
            if (diagnostics.Count < 1000)
                diagnostics.Add(new("warning", code, message));
        }
    }

    private static ExpressionSyntax? SqlArgument(InvocationExpressionSyntax call)
    {
        var args = call.ArgumentList.Arguments;
        var named = args.FirstOrDefault(argument => argument.NameColon?.Name.Identifier.ValueText is "sql" or "command");
        if (named is not null)
            return named.Expression;
        var staticMapper =
            call.Expression is MemberAccessExpressionSyntax member && member.Expression.ToString().Split('.').Last() == "SqlMapper";
        var positional = args.Where(argument => argument.NameColon is null).ToArray();
        var namedConnection = args.Any(argument => argument.NameColon?.Name.Identifier.ValueText is "cnn" or "connection");
        return positional.ElementAtOrDefault(staticMapper && !namedConnection ? 1 : 0)?.Expression;
    }

    private static int OriginalLine(ExpressionSyntax expression, int sqlLine, int callLine)
    {
        if (expression is not LiteralExpressionSyntax literal)
            return callLine;
        var start = literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        if (literal.Token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken))
            return start + Math.Max(1, sqlLine);
        if (literal.Token.Text.StartsWith("@\"", StringComparison.Ordinal))
            return start + Math.Max(0, sqlLine - 1);
        // Escaped newlines in a regular C# string are still on the same physical source line.
        return start;
    }
}
