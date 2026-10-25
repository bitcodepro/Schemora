using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Schemora.SqlServer;

namespace Schemora.Relationships;

public sealed record EfAnalysisResult(IReadOnlyList<RelationshipEdge> Relations, IReadOnlyList<SchemaDiagnostic> Diagnostics);

/// <summary>Static EF analysis with embedded official metadata; no user assembly is built or executed.</summary>
public sealed partial class EfMappingAnalyzer
{
    public EfAnalysisResult Analyze(
        IReadOnlyList<RelationshipDatabase> databases,
        IReadOnlyList<EfSourceSet> sources,
        CancellationToken ct = default
    )
    {
        var edges = new Dictionary<string, RelationshipEdge>(StringComparer.Ordinal);
        var diagnostics = new List<SchemaDiagnostic>();
        void Report(string code, string detail) => diagnostics.Add(new("warning", code, detail));
        foreach (var group in sources.GroupBy(s => s.DatabaseId, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var assigned = databases.Where(d => d.Id == group.Key).ToArray();
            if (assigned.Length != 1)
            {
                Report("EF_DATABASE_UNASSIGNED", "EF sources need one explicit database assignment.");
                continue;
            }
            var contexts = group.Where(s => s.Context is not null).Select(s => s.Context!).ToArray();
            diagnostics.AddRange(contexts.SelectMany(c => c.Diagnostics));
            var projectFiles = contexts
                .SelectMany(c => c.Files)
                .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var trees = new List<SyntaxTree>();
            long bytes = 0;
            foreach (
                var input in group
                    .SelectMany(s => s.Context is null ? s.Paths : s.Context.Files.Select(f => f.Path))
                    .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                    .Take(4097)
            )
            {
                ct.ThrowIfCancellationRequested();
                if (trees.Count >= 4096)
                {
                    Report("EF_SOURCE_LIMIT", "EF analysis stopped at 4096 source files.");
                    break;
                }
                try
                {
                    var path = Path.GetFullPath(input);
                    if (path.StartsWith(@"\\") || new Uri(path).IsUnc)
                    {
                        Report("EF_LOCAL_SOURCE_REQUIRED", "EF analysis accepts local files only.");
                        continue;
                    }
                    var file = new FileInfo(path);
                    if (
                        !file.Exists
                        || !file.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                        || (file.Attributes & FileAttributes.ReparsePoint) != 0
                    )
                    {
                        Report("EF_SOURCE_UNAVAILABLE", $"Unavailable or linked EF source: {path}");
                        continue;
                    }
                    for (var parent = file.Directory; parent is not null; parent = parent.Parent)
                        if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("Linked ancestor.");
                    if (file.Length > 4 * 1024 * 1024 || (bytes += file.Length) > 64 * 1024 * 1024)
                    {
                        Report("EF_SOURCE_LIMIT", $"EF source size limit: {path}");
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
                    Report("EF_SOURCE_UNAVAILABLE", $"Cannot read EF source: {input} ({ex.GetType().Name}).");
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
                Report("EF_SEMANTIC_UNAVAILABLE", "Official embedded EF metadata is unavailable; mappings cannot be trusted.");
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
                "Schemora.EfStaticAnalysis",
                trees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
            );
            foreach (
                var edge in new State(
                    assigned[0],
                    compilation,
                    trees,
                    ct,
                    Report,
                    projectFiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
                    contexts.Any(c => !c.IsComplete)
                ).Read()
            )
            {
                if (!edges.TryGetValue(edge.Relation.Id, out var prior))
                    edges.Add(edge.Relation.Id, edge);
                else
                {
                    var trusted = prior.IsTrusted || edge.IsTrusted;
                    edges[edge.Relation.Id] = edge with
                    {
                        IsTrusted = trusted,
                        Status = trusted ? "mapped" : "candidate",
                        ReviewReason = trusted ? null : edge.ReviewReason,
                        Evidence = prior.Evidence.Concat(edge.Evidence).Distinct().ToArray(),
                    };
                }
            }
        }
        return new(edges.Values.OrderBy(e => e.Relation.Id, StringComparer.Ordinal).ToArray(), diagnostics);
    }

    private sealed class Entity(string key, string name)
    {
        public string Key { get; } = key;
        public string Name { get; } = name;
        public Dictionary<string, Property> Properties { get; } = new(StringComparer.Ordinal);
        public List<TableBinding> Tables { get; } = [];
        public Dictionary<string, List<ColumnBinding>> Columns { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Contexts { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ProvenContexts { get; } = new(StringComparer.Ordinal);
        public bool Unsafe { get; set; }
    }

    private sealed record Property(PropertyDeclarationSyntax Node, IPropertySymbol? Symbol);

    private sealed record TableBinding(string Name, string? Schema, bool Proven);

    private sealed record ColumnBinding(string Name, bool Proven);

    private sealed record Candidate(
        Entity Dependent,
        Entity Principal,
        IReadOnlyList<string> Foreign,
        IReadOnlyList<string>? PrincipalKey,
        bool Proven,
        bool ForeignShadow,
        bool PrincipalShadow,
        SyntaxNode Node,
        string Detail
    );

    private sealed partial class State(
        RelationshipDatabase db,
        CSharpCompilation compilation,
        IReadOnlyList<SyntaxTree> trees,
        CancellationToken ct,
        Action<string, string> report,
        IReadOnlySet<string> projectPaths,
        bool projectPartial
    )
    {
        private readonly Dictionary<string, Entity> entities = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> contexts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> configurations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> applied = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> defaultSchemas = new(StringComparer.Ordinal);
        private readonly List<Candidate> candidates = [];
        private bool syntaxErrors;
        private bool incompleteModel = projectPartial;

        public IReadOnlyList<RelationshipEdge> Read()
        {
            DiscoverClasses();
            DiscoverRegistration();
            ReadAttributes();
            ReadFluent();
            return BuildEdges();
        }

        private void DiscoverClasses()
        {
            foreach (var tree in trees)
            {
                ct.ThrowIfCancellationRequested();
                if (tree.GetDiagnostics(ct).Any(d => d.Severity == DiagnosticSeverity.Error))
                {
                    syntaxErrors = true;
                    Warn("EF_SYNTAX_PARTIAL", tree.GetRoot(ct), "C# syntax errors prevent trusted mapping analysis.");
                }
                if (
                    !projectPaths.Contains(tree.FilePath)
                    && tree.GetRoot(ct)
                        .DescendantTrivia(descendIntoTrivia: true)
                        .Any(t =>
                            t.IsKind(SyntaxKind.IfDirectiveTrivia)
                            || t.IsKind(SyntaxKind.ElifDirectiveTrivia)
                            || t.IsKind(SyntaxKind.DisabledTextTrivia)
                        )
                )
                {
                    incompleteModel = true;
                    Warn(
                        "EF_PREPROCESSOR_PARTIAL",
                        tree.GetRoot(ct),
                        "Project preprocessor symbols are not evaluated; conditional mappings require review."
                    );
                }
                var model = compilation.GetSemanticModel(tree, true);
                foreach (var node in tree.GetRoot(ct).DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    var symbol = model.GetDeclaredSymbol(node, ct);
                    if (symbol is null)
                        continue;
                    var key = Key(symbol);
                    if (!entities.TryGetValue(key, out var entity))
                        entities[key] = entity = new(key, symbol.Name);
                    foreach (var property in node.Members.OfType<PropertyDeclarationSyntax>())
                        entity.Properties[property.Identifier.ValueText] = new(property, model.GetDeclaredSymbol(property, ct));
                    for (var type = symbol.BaseType; type is not null; type = type.BaseType)
                        if (type.Name == "DbContext")
                        {
                            contexts[key] = Official(type);
                            break;
                        }
                    if (!contexts.ContainsKey(key) && node.BaseList?.Types.Any(t => Short(t.Type.ToString()) == "DbContext") == true)
                        contexts[key] = false;
                    foreach (var iface in symbol.AllInterfaces)
                        if (iface.Name == "IEntityTypeConfiguration" && iface.TypeArguments.Length == 1)
                            configurations[key] = Key(iface.TypeArguments[0]);
                }
            }
        }

        private void DiscoverRegistration()
        {
            foreach (var tree in trees)
            {
                var model = compilation.GetSemanticModel(tree, true);
                foreach (var entity in entities.Values)
                foreach (var property in entity.Properties.Values.Where(p => p.Node.SyntaxTree == tree))
                {
                    if (!contexts.TryGetValue(entity.Key, out var contextProven))
                        continue;
                    var type = property.Symbol?.Type as INamedTypeSymbol;
                    if (type?.Name != "DbSet" || type.TypeArguments.Length != 1)
                        continue;
                    if (Find(type.TypeArguments[0]) is { } target)
                        AddContext(target, entity.Key, contextProven && Official(type));
                }
                foreach (var call in tree.GetRoot(ct).DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    ct.ThrowIfCancellationRequested();
                    var context = EnclosingClass(call, model);
                    if (context is null || !contexts.TryGetValue(context, out var contextProven))
                        continue;
                    var name = MethodName(call);
                    if (name == "Entity" && GenericEntity(call, model) is { } target)
                        AddContext(target, context, contextProven && IsOfficialCall(call, model) && InModelMethod(call, model));
                    if (
                        name == "ApplyConfiguration"
                        && call.ArgumentList.Arguments.FirstOrDefault()?.Expression is ObjectCreationExpressionSyntax creation
                    )
                    {
                        var key = Key(model.GetTypeInfo(creation, ct).Type);
                        if (
                            configurations.ContainsKey(key)
                            && IsOfficialCall(call, model)
                            && InModelMethod(call, model)
                            && !Conditional(call)
                        )
                        {
                            if (!applied.TryGetValue(key, out var registrations))
                                applied[key] = registrations = new(StringComparer.Ordinal);
                            registrations.Add(context);
                            if (entities.TryGetValue(configurations[key], out var configured))
                                AddContext(configured, context, contextProven);
                        }
                    }
                    if (name == "ApplyConfigurationsFromAssembly" || name is "SetTableName" or "SetColumnName" or "SetSchema")
                    {
                        incompleteModel = true;
                        Warn(
                            "EF_CONFIGURATION_DYNAMIC",
                            call,
                            "Dynamic/assembly-wide metadata configuration is not executed; model mappings require review."
                        );
                    }
                    var receiver = call.Expression is MemberAccessExpressionSyntax member
                        ? model.GetTypeInfo(member.Expression, ct).Type as INamedTypeSymbol
                        : null;
                    var receivesBuilder = call.ArgumentList.Arguments.Any(a =>
                        model.GetTypeInfo(a.Expression, ct).Type is INamedTypeSymbol t
                        && Official(t)
                        && t.Name is "ModelBuilder" or "EntityTypeBuilder"
                    );
                    if (
                        InModelMethod(call, model)
                        && !IsOfficialCall(call, model)
                        && (
                            receivesBuilder
                            || receiver is not null && Official(receiver) && receiver.Name is "ModelBuilder" or "EntityTypeBuilder"
                        )
                    )
                    {
                        incompleteModel = true;
                        Warn(
                            "EF_CONFIGURATION_DYNAMIC",
                            call,
                            "User configuration helper is not executed; subsequent mapping overrides cannot be ruled out."
                        );
                    }
                    if (name == "HasDefaultSchema")
                    {
                        var schema = Constant(call.ArgumentList.Arguments.FirstOrDefault()?.Expression, model);
                        if (
                            !IsOfficialCall(call, model)
                            || !InModelMethod(call, model)
                            || Conditional(call)
                            || schema is null
                            || defaultSchemas.TryGetValue(context, out var prior) && prior != schema
                        )
                        {
                            defaultSchemas[context] = null;
                            Warn("EF_SCHEMA_DYNAMIC", call, "Default schema is computed or conflicting.");
                        }
                        else
                            defaultSchemas[context] = schema;
                    }
                }
            }
        }

        private void ReadAttributes()
        {
            foreach (var tree in trees)
            {
                var model = compilation.GetSemanticModel(tree, true);
                foreach (var node in tree.GetRoot(ct).DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (!entities.TryGetValue(Key(model.GetDeclaredSymbol(node, ct)), out var entity))
                        continue;
                    foreach (var attr in node.AttributeLists.SelectMany(a => a.Attributes))
                    {
                        if (AttributeName(attr, model) == "NotMapped")
                            entity.Unsafe = true;
                        if (AttributeName(attr, model) != "Table")
                            continue;
                        var name = Constant(attr.ArgumentList?.Arguments.FirstOrDefault()?.Expression, model);
                        var schemaArg = attr.ArgumentList?.Arguments.FirstOrDefault(a =>
                            a.NameEquals?.Name.Identifier.ValueText == "Schema"
                        );
                        var schema = schemaArg is null ? null : Constant(schemaArg.Expression, model);
                        if (name is null || schemaArg is not null && schema is null)
                        {
                            entity.Unsafe = true;
                            Warn("EF_TABLE_DYNAMIC", attr, "Table mapping is not constant.");
                        }
                        else
                            entity.Tables.Add(new(name, schema, IsOfficialAttribute(attr, model, "TableAttribute")));
                    }
                }
            }
            foreach (var entity in entities.Values)
            foreach (var pair in entity.Properties)
            {
                var property = pair.Value;
                var model = compilation.GetSemanticModel(property.Node.SyntaxTree, true);
                foreach (var attr in property.Node.AttributeLists.SelectMany(a => a.Attributes))
                {
                    if (AttributeName(attr, model) == "NotMapped")
                        entity.Unsafe = true;
                    if (AttributeName(attr, model) == "Column")
                    {
                        var column = Constant(attr.ArgumentList?.Arguments.FirstOrDefault()?.Expression, model);
                        if (column is null)
                        {
                            entity.Unsafe = true;
                            Warn("EF_COLUMN_DYNAMIC", attr, "Column mapping is not constant.");
                        }
                        else
                            AddColumn(entity, pair.Key, column, IsOfficialAttribute(attr, model, "ColumnAttribute"));
                    }
                    if (AttributeName(attr, model) != "ForeignKey")
                        continue;
                    var text = Constant(attr.ArgumentList?.Arguments.FirstOrDefault()?.Expression, model);
                    if (text is null)
                    {
                        Warn("EF_FOREIGN_KEY_DYNAMIC", attr, "ForeignKey attribute is not a constant property name.");
                        continue;
                    }
                    var target = ReferenceNavigation(property.Symbol?.Type);
                    IReadOnlyList<string> foreign;
                    if (target is not null)
                        foreign = text.Split(',').Select(x => x.Trim()).ToArray();
                    else if (entity.Properties.TryGetValue(text, out var navigation))
                    {
                        target = ReferenceNavigation(navigation.Symbol?.Type);
                        foreign = [pair.Key];
                    }
                    else
                    {
                        Warn("EF_NAVIGATION_UNRESOLVED", attr, "ForeignKey navigation cannot be resolved.");
                        continue;
                    }
                    if (target is null || foreign.Count == 0 || foreign.Any(string.IsNullOrWhiteSpace))
                    {
                        Warn("EF_NAVIGATION_UNRESOLVED", attr, "ForeignKey does not identify a reference navigation.");
                        continue;
                    }
                    var proof = IsOfficialAttribute(attr, model, "ForeignKeyAttribute");
                    if (entity.ProvenContexts.Count == 1)
                        AddContext(target, entity.ProvenContexts.Single(), proof);
                    candidates.Add(new(entity, target, foreign, null, proof, true, false, attr, "ForeignKey attribute"));
                }
            }
        }

        private void ReadFluent()
        {
            foreach (var tree in trees)
            {
                var model = compilation.GetSemanticModel(tree, true);
                foreach (var outer in tree.GetRoot(ct).DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    ct.ThrowIfCancellationRequested();
                    if (
                        outer.Parent is MemberAccessExpressionSyntax access
                        && access.Expression == outer
                        && access.Parent is InvocationExpressionSyntax
                    )
                        continue;
                    var chain = Chain(outer);
                    var entity = RootEntity(chain[0], model);
                    if (entity is null)
                    {
                        if (chain.Any(c => MethodName(c) is "HasForeignKey" or "ToTable" or "HasColumnName"))
                            Warn(
                                "EF_SEMANTIC_UNRESOLVED",
                                outer,
                                "Mapping receiver/entity symbols could not be resolved; no trusted mapping was inferred."
                            );
                        continue;
                    }
                    var proof =
                        !syntaxErrors
                        && !incompleteModel
                        && !Conditional(outer)
                        && chain.All(c => IsOfficialCall(c, model))
                        && ScopeProven(outer, model, entity);
                    foreach (var call in chain.Where(c => MethodName(c) == "ToTable"))
                    {
                        var args = call.ArgumentList.Arguments;
                        var name = Constant(args.FirstOrDefault()?.Expression, model);
                        var schema = args.Count > 1 ? Constant(args[1].Expression, model) : null;
                        if (name is null || args.Count > 1 && schema is null)
                        {
                            entity.Unsafe = true;
                            Warn("EF_TABLE_DYNAMIC", call, "ToTable requires constant table/schema arguments.");
                        }
                        else
                            entity.Tables.Add(new(name, schema, proof));
                    }
                    var propertyCall = chain.FirstOrDefault(c => MethodName(c) == "Property");
                    var columnCall = chain.LastOrDefault(c => MethodName(c) == "HasColumnName");
                    if (propertyCall is not null && columnCall is not null)
                    {
                        var property = Selectors(propertyCall, model).Names;
                        var column = Constant(columnCall.ArgumentList.Arguments.FirstOrDefault()?.Expression, model);
                        if (property is not { Count: 1 } || column is null)
                        {
                            entity.Unsafe = true;
                            Warn("EF_COLUMN_DYNAMIC", columnCall, "Property/HasColumnName is computed or ambiguous.");
                        }
                        else
                            AddColumn(entity, property[0], column, proof);
                    }
                    if (chain.Any(c => MethodName(c) is "Ignore" or "OwnsOne" or "OwnsMany" or "HasBaseType" or "HasConversion"))
                    {
                        entity.Unsafe = true;
                        Warn("EF_MAPPING_UNSUPPORTED", outer, "Ignored, owned, inherited or converted mapping needs manual review.");
                    }
                    var navigationCall = chain.FirstOrDefault(c => MethodName(c) is "HasOne" or "HasMany");
                    var foreignCall = chain.LastOrDefault(c => MethodName(c) == "HasForeignKey");
                    if (navigationCall is null || foreignCall is null)
                        continue;
                    var target = Related(navigationCall, model, entity);
                    var with = chain.FirstOrDefault(c => MethodName(c) is "WithOne" or "WithMany");
                    if (target is null || with is null || MethodName(navigationCall) == "HasMany" && MethodName(with) == "WithMany")
                    {
                        Warn("EF_RELATIONSHIP_UNRESOLVED", outer, "Navigation endpoints or many-to-many join entity are not explicit.");
                        continue;
                    }
                    var foreign = Selectors(foreignCall, model);
                    var principalCall = chain.LastOrDefault(c => MethodName(c) == "HasPrincipalKey");
                    var principal = principalCall is null
                        ? (Names: (IReadOnlyList<string>?)null, Shadow: false)
                        : Selectors(principalCall, model);
                    if (foreign.Names is null || principalCall is not null && principal.Names is null)
                    {
                        Warn("EF_FOREIGN_KEY_DYNAMIC", outer, "Foreign/principal selectors are computed or unsupported.");
                        continue;
                    }
                    var dependent = MethodName(navigationCall) == "HasMany" ? target : entity;
                    var genericDependent = GenericEntity(foreignCall, model);
                    if (genericDependent is not null)
                        dependent = genericDependent;
                    else if (MethodName(navigationCall) == "HasOne" && MethodName(with) == "WithOne")
                    {
                        Warn("EF_DEPENDENT_AMBIGUOUS", outer, "One-to-one mapping requires explicit dependent generic type.");
                        continue;
                    }
                    if (dependent != entity && dependent != target)
                    {
                        Warn("EF_DEPENDENT_AMBIGUOUS", outer, "Foreign-key type is not an endpoint.");
                        continue;
                    }
                    var principalEntity = dependent == entity ? target : entity;
                    var genericPrincipal = principalCall is null ? null : GenericEntity(principalCall, model);
                    if (genericPrincipal is not null && genericPrincipal != principalEntity)
                    {
                        Warn("EF_PRINCIPAL_AMBIGUOUS", outer, "Principal-key type conflicts with direction.");
                        continue;
                    }
                    foreach (var context in entity.Contexts.ToArray())
                        AddContext(target, context, proof && entity.ProvenContexts.Contains(context));
                    candidates.Add(
                        new(
                            dependent,
                            principalEntity,
                            foreign.Names,
                            principal.Names,
                            proof,
                            foreign.Shadow,
                            principal.Shadow,
                            outer,
                            "Fluent FK/principal-key mapping"
                        )
                    );
                }
            }
        }

        private IReadOnlyList<RelationshipEdge> BuildEdges()
        {
            var result = new List<RelationshipEdge>();
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var from = Bind(candidate.Dependent, candidate.Node, out var fromProof);
                var to = Bind(candidate.Principal, candidate.Node, out var toProof);
                if (from is null || to is null)
                    continue;
                var foreign = Columns(
                    candidate.Dependent,
                    from,
                    candidate.Foreign,
                    candidate.ForeignShadow,
                    candidate.Node,
                    out var foreignProof
                );
                var principalProof = true;
                var principal = candidate.PrincipalKey is null
                    ? to.PrimaryKey
                    : Columns(
                        candidate.Principal,
                        to,
                        candidate.PrincipalKey,
                        candidate.PrincipalShadow,
                        candidate.Node,
                        out principalProof
                    );
                if (
                    foreign is null
                    || principal is null
                    || foreign.Count == 0
                    || foreign.Count != principal.Count
                    || principal.Any(p => !to.Columns.Any(c => c.Name == p))
                )
                {
                    Warn("EF_KEY_UNRESOLVED", candidate.Node, "FK/principal column counts differ or a catalog principal key is absent.");
                    continue;
                }
                if (
                    foreign
                        .Select(
                            (name, index) =>
                                Compatible(
                                    from.Columns.Single(c => c.Name == name).DataType,
                                    to.Columns.Single(c => c.Name == principal[index]).DataType
                                )
                        )
                        .Any(ok => !ok)
                )
                {
                    Warn("EF_KEY_TYPE_MISMATCH", candidate.Node, "FK/principal SQL types are incompatible.");
                    continue;
                }
                var trusted =
                    candidate.Proven
                    && fromProof
                    && toProof
                    && foreignProof
                    && principalProof
                    && !candidate.Dependent.Unsafe
                    && !candidate.Principal.Unsafe
                    && !syntaxErrors
                    && !incompleteModel;
                var reason = trusted
                    ? null
                    : "EF symbols, registration, context or mapping could not be proven statically; confirm the logical relation manually.";
                if (!trusted)
                    Warn("EF_MAPPING_NEEDS_REVIEW", candidate.Node, reason!);
                var identity = db.Id + "|" + from.Id + "|" + to.Id + "|" + string.Join("|", foreign.Zip(principal, (a, b) => a + "=" + b));
                var id = "orm:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                var relation = new SchemaRelation(
                    id,
                    "ORM_" + from.Name + "_" + to.Name,
                    from.Id,
                    to.Id,
                    foreign,
                    principal,
                    "orm-mapping",
                    trusted ? .95 : .65
                );
                var line = candidate.Node.GetLocation().GetLineSpan();
                result.Add(
                    new(
                        relation,
                        trusted ? "mapped" : "candidate",
                        trusted,
                        [
                            new(
                                "ef-mapping",
                                line.Path,
                                line.StartLinePosition.Line + 1,
                                candidate.Detail + ": " + string.Join(", ", foreign.Zip(principal, (a, b) => a + " → " + b))
                            ),
                        ],
                        reason
                    )
                );
            }
            return result;
        }
    }
}
