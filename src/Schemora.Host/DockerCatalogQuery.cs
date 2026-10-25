using System.Text;
using System.Text.Json;
using Schemora.SqlServer;

namespace Schemora.Host;

internal static class DockerCatalogQuery
{
    // One base64 value avoids sqlcmd width/row wrapping corrupting quoted identifiers inside JSON.
    internal const string Text = """
        SET NOCOUNT ON;
        SET QUOTED_IDENTIFIER ON;
        SET ANSI_NULLS ON;
        DECLARE @columns TABLE(object_id int,column_id int,name sysname,dataType nvarchar(1024),isNullable bit);
        INSERT @columns
        SELECT c.object_id,c.column_id,c.name,
          CASE WHEN ty.is_user_defined=1 THEN QUOTENAME(SCHEMA_NAME(ty.schema_id))+N'.'+QUOTENAME(ty.name)
            WHEN ty.name IN ('varchar','char','varbinary','binary','nvarchar','nchar') THEN ty.name+N'('+CASE WHEN c.max_length=-1 THEN N'max' ELSE CONVERT(nvarchar(10),CASE WHEN ty.name IN ('nvarchar','nchar') THEN c.max_length/2 ELSE c.max_length END) END+N')'
            WHEN ty.name IN ('decimal','numeric') THEN ty.name+N'('+CONVERT(nvarchar(10),c.precision)+N','+CONVERT(nvarchar(10),c.scale)+N')'
            WHEN ty.name IN ('time','datetime2','datetimeoffset') THEN ty.name+N'('+CONVERT(nvarchar(10),c.scale)+N')'
            WHEN ty.name='float' THEN ty.name+N'('+CONVERT(nvarchar(10),c.precision)+N')' ELSE ty.name END,c.is_nullable
        FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id JOIN sys.objects o ON o.object_id=c.object_id
        WHERE o.is_ms_shipped=0 AND o.type IN ('U','V','IF','TF','FT');
        DECLARE @parameters TABLE(object_id int,parameter_id int,name sysname,dataType nvarchar(1024),isNullable bit,isOutput bit);
        INSERT @parameters
        SELECT p.object_id,p.parameter_id,p.name,
          CASE WHEN ty.is_user_defined=1 THEN QUOTENAME(SCHEMA_NAME(ty.schema_id))+N'.'+QUOTENAME(ty.name)
            WHEN ty.name IN ('varchar','char','varbinary','binary','nvarchar','nchar') THEN ty.name+N'('+CASE WHEN p.max_length=-1 THEN N'max' ELSE CONVERT(nvarchar(10),CASE WHEN ty.name IN ('nvarchar','nchar') THEN p.max_length/2 ELSE p.max_length END) END+N')'
            WHEN ty.name IN ('decimal','numeric') THEN ty.name+N'('+CONVERT(nvarchar(10),p.precision)+N','+CONVERT(nvarchar(10),p.scale)+N')'
            WHEN ty.name IN ('time','datetime2','datetimeoffset') THEN ty.name+N'('+CONVERT(nvarchar(10),p.scale)+N')'
            WHEN ty.name='float' THEN ty.name+N'('+CONVERT(nvarchar(10),p.precision)+N')' ELSE ty.name END,p.is_nullable,p.is_output
        FROM sys.parameters p JOIN sys.types ty ON ty.user_type_id=p.user_type_id JOIN sys.objects o ON o.object_id=p.object_id
        WHERE o.is_ms_shipped=0 AND o.type IN ('P','PC','FN','FS','IF','TF','FT');
        DECLARE @definitions TABLE(object_id int PRIMARY KEY,definition nvarchar(max),isPartial bit);
        INSERT @definitions
        SELECT object_id,CASE WHEN definitionBytes<=262144 AND runningBytes<=33554432 THEN definition END,
          CONVERT(bit,CASE WHEN definition IS NULL OR definitionBytes>262144 OR runningBytes>33554432 THEN 1 ELSE 0 END)
        FROM(SELECT o.object_id,m.definition,DATALENGTH(CONVERT(varchar(max),m.definition COLLATE Latin1_General_100_BIN2_UTF8)) AS definitionBytes,
          SUM(CONVERT(bigint,CASE WHEN DATALENGTH(CONVERT(varchar(max),m.definition COLLATE Latin1_General_100_BIN2_UTF8))<=262144
            THEN DATALENGTH(CONVERT(varchar(max),m.definition COLLATE Latin1_General_100_BIN2_UTF8)) ELSE 0 END)) OVER(ORDER BY s.name,o.name,o.object_id ROWS UNBOUNDED PRECEDING) AS runningBytes
          FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id LEFT JOIN sys.sql_modules m ON m.object_id=o.object_id
          WHERE o.is_ms_shipped=0 AND o.type IN ('V','P','PC','FN','FS','IF','TF','FT')) d;
        DECLARE @dependencies TABLE(fromSchema sysname,fromName sysname,targetServer sysname NULL,targetDatabase sysname NULL,targetSchema sysname NULL,targetName sysname,targetKnown bit,kind varchar(32));
        INSERT @dependencies
        SELECT DISTINCT s.name,o.name,d.referenced_server_name,d.referenced_database_name,COALESCE(rs.name,d.referenced_schema_name),COALESCE(r.name,d.referenced_entity_name),
          CONVERT(bit,CASE WHEN r.object_id IS NOT NULL AND r.type IN ('U','V','P','PC','FN','FS','IF','TF','FT','SN') THEN 1 ELSE 0 END),'catalog-reference'
        FROM sys.sql_expression_dependencies d JOIN sys.objects o ON o.object_id=d.referencing_id JOIN sys.schemas s ON s.schema_id=o.schema_id
          LEFT JOIN sys.objects r ON r.object_id=d.referenced_id LEFT JOIN sys.schemas rs ON rs.schema_id=r.schema_id
        WHERE o.is_ms_shipped=0 AND o.type IN ('U','V','P','PC','FN','FS','IF','TF','FT') AND d.referenced_class=1
          AND d.referenced_entity_name IS NOT NULL AND (r.object_id IS NULL OR r.is_ms_shipped=0);
        INSERT @dependencies
        SELECT s.name,n.name,PARSENAME(n.base_object_name,4),PARSENAME(n.base_object_name,3),PARSENAME(n.base_object_name,2),PARSENAME(n.base_object_name,1),
          CONVERT(bit,CASE WHEN PARSENAME(n.base_object_name,4) IS NULL AND (PARSENAME(n.base_object_name,3) IS NULL OR PARSENAME(n.base_object_name,3)=DB_NAME())
            AND PARSENAME(n.base_object_name,2) IS NOT NULL AND OBJECT_ID(QUOTENAME(PARSENAME(n.base_object_name,2))+N'.'+QUOTENAME(PARSENAME(n.base_object_name,1))) IS NOT NULL THEN 1 ELSE 0 END),'synonym-target'
        FROM sys.synonyms n JOIN sys.schemas s ON s.schema_id=n.schema_id WHERE n.is_ms_shipped=0;
        DECLARE @json nvarchar(max) = (
          SELECT JSON_QUERY((SELECT s.name AS [schema],t.name AS [name],
            JSON_QUERY((SELECT c.name,c.dataType,c.isNullable FROM @columns c WHERE c.object_id=t.object_id ORDER BY c.column_id FOR JSON PATH)) AS columns,
            JSON_QUERY((SELECT c.name FROM sys.key_constraints k JOIN sys.index_columns ic ON ic.object_id=k.parent_object_id AND ic.index_id=k.unique_index_id
              JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE k.parent_object_id=t.object_id AND k.type='PK' ORDER BY ic.key_ordinal FOR JSON PATH)) AS primaryKey,
            JSON_QUERY((SELECT k.name,JSON_QUERY((SELECT c.name FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
              WHERE ic.object_id=k.parent_object_id AND ic.index_id=k.unique_index_id ORDER BY ic.key_ordinal FOR JSON PATH)) AS columns
              FROM sys.key_constraints k WHERE k.parent_object_id=t.object_id AND k.type='UQ' ORDER BY k.name FOR JSON PATH)) AS uniqueKeys
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_ms_shipped=0 ORDER BY s.name,t.name FOR JSON PATH)) AS tables,
          JSON_QUERY((SELECT fk.name AS [name],s.name AS fromSchema,t.name AS fromTable,rs.name AS toSchema,rt.name AS toTable,
            JSON_QUERY((SELECT c.name AS [source],rc.name AS [target] FROM sys.foreign_key_columns fkc
              JOIN sys.columns c ON c.object_id=fkc.parent_object_id AND c.column_id=fkc.parent_column_id
              JOIN sys.columns rc ON rc.object_id=fkc.referenced_object_id AND rc.column_id=fkc.referenced_column_id
              WHERE fkc.constraint_object_id=fk.object_id ORDER BY fkc.constraint_column_id FOR JSON PATH)) AS columns
            FROM sys.foreign_keys fk JOIN sys.tables t ON t.object_id=fk.parent_object_id JOIN sys.schemas s ON s.schema_id=t.schema_id
              JOIN sys.tables rt ON rt.object_id=fk.referenced_object_id JOIN sys.schemas rs ON rs.schema_id=rt.schema_id
            WHERE fk.is_ms_shipped=0 ORDER BY s.name,t.name,fk.name FOR JSON PATH)) AS relations,
          JSON_QUERY((SELECT s.name AS [schema],o.name,CASE o.type WHEN 'V' THEN 'view' WHEN 'P' THEN 'procedure' WHEN 'PC' THEN 'procedure'
              WHEN 'FN' THEN 'scalarFunction' WHEN 'FS' THEN 'scalarFunction' WHEN 'SN' THEN 'synonym' ELSE 'tableFunction' END AS kind,
            JSON_QUERY((SELECT c.name,c.dataType,c.isNullable FROM @columns c WHERE c.object_id=o.object_id ORDER BY c.column_id FOR JSON PATH)) AS columns,
            JSON_QUERY((SELECT p.name,p.dataType,p.isNullable,p.isOutput FROM @parameters p WHERE p.object_id=o.object_id AND p.parameter_id>0 ORDER BY p.parameter_id FOR JSON PATH)) AS parameters,
            (SELECT p.dataType FROM @parameters p WHERE p.object_id=o.object_id AND p.parameter_id=0) AS returnType,n.base_object_name AS target,
            d.definition,ISNULL(d.isPartial,CONVERT(bit,0)) AS isPartial
            FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id LEFT JOIN sys.synonyms n ON n.object_id=o.object_id LEFT JOIN @definitions d ON d.object_id=o.object_id
            WHERE o.is_ms_shipped=0 AND o.type IN ('V','P','PC','FN','FS','IF','TF','FT','SN') ORDER BY s.name,o.name FOR JSON PATH)) AS objects,
          JSON_QUERY((SELECT s.name AS [schema],o.name AS objectName,i.name,
            CASE i.type WHEN 1 THEN 'clustered' WHEN 2 THEN 'nonclustered' WHEN 3 THEN 'xml' WHEN 4 THEN 'spatial' WHEN 5 THEN 'columnstore' WHEN 6 THEN 'columnstore' WHEN 7 THEN 'hash' ELSE 'other' END AS kind,
            i.is_unique AS isUnique,i.is_primary_key AS isPrimaryKey,i.filter_definition AS filter,
            JSON_QUERY((SELECT c.name,ic.is_descending_key AS descending FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
              WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ((ic.key_ordinal>0 AND ic.is_included_column=0) OR i.type IN (3,4,5,6))
              ORDER BY CASE WHEN ic.key_ordinal=0 THEN ic.index_column_id ELSE ic.key_ordinal END FOR JSON PATH)) AS columns,
            JSON_QUERY((SELECT c.name FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
              WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.is_included_column=1 AND i.type NOT IN (5,6) ORDER BY ic.index_column_id FOR JSON PATH)) AS includedColumns
            FROM sys.indexes i JOIN sys.objects o ON o.object_id=i.object_id JOIN sys.schemas s ON s.schema_id=o.schema_id
            WHERE o.is_ms_shipped=0 AND o.type IN ('U','V') AND i.index_id>0 AND i.is_hypothetical=0 ORDER BY s.name,o.name,i.name FOR JSON PATH)) AS indexes,
          JSON_QUERY((SELECT DISTINCT fromSchema,fromName,targetServer,targetDatabase,targetSchema,targetName,targetKnown,kind FROM @dependencies ORDER BY fromSchema,fromName,kind,targetServer,targetDatabase,targetSchema,targetName FOR JSON PATH)) AS objectDependencies
          FOR JSON PATH,WITHOUT_ARRAY_WRAPPER
        );
        DECLARE @bytes varbinary(max)=CONVERT(varbinary(max),@json);
        DECLARE @base64 varchar(max)=CAST(N'' AS xml).value('xs:base64Binary(sql:variable("@bytes"))','varchar(max)');
        DECLARE @chunks TABLE ([Ordinal] int IDENTITY(1,1) NOT NULL,[Chunk] varchar(400) NOT NULL);
        DECLARE @position int=1;
        WHILE @position<=LEN(@base64)
        BEGIN
          INSERT INTO @chunks([Chunk]) VALUES(CONVERT(varchar(400),SUBSTRING(@base64,@position,400)));
          SET @position+=400;
        END;
        SELECT [Chunk] FROM @chunks ORDER BY [Ordinal];
        """;

    internal static SchemaGraph Parse(string base64, string databaseName, string sourcePath)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64.Trim('\uFEFF', ' ', '\r', '\n', '\t'));
        }
        catch (FormatException)
        {
            throw new AppException("DOCKER_CATALOG_INVALID", "SQL Server вернул некорректный пакет метаданных.", 422);
        }
        if (bytes.Length > 64 * 1024 * 1024 || bytes.Length % 2 != 0)
            throw new AppException("DOCKER_CATALOG_SIZE", "Декодированные метаданные Docker превышают допустимый объём.", 422);
        try
        {
            using var json = JsonDocument.Parse(Encoding.Unicode.GetString(bytes));
            var root = json.RootElement;
            if (
                !root.TryGetProperty("tables", out var tableRows)
                || tableRows.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("relations", out var relationRows)
                || relationRows.ValueKind != JsonValueKind.Array
            )
                throw new InvalidOperationException("Missing required catalog arrays.");
            var diagnostics = new List<SchemaDiagnostic>
            {
                new(
                    "info",
                    "DOCKER_SCHEMA_VERIFIED",
                    "Схема сгенерирована DacFx и проверена по sys metadata отдельного SQL Server 2022 без сети, опубликованных портов, host mounts, pre/post scripts и данных. Контейнер удалён после проверки."
                ),
            };
            string Id(params string[] parts) => string.Join('.', new[] { databaseName }.Concat(parts).Select(Quote));
            var tables = Elements(root, "tables")
                .Select(table => new SchemaTable(
                    Id(Required(table, "schema"), Required(table, "name")),
                    Required(table, "schema"),
                    Required(table, "name"),
                    Columns(table),
                    Elements(table, "primaryKey").Select(column => Required(column, "name")).ToArray(),
                    Elements(table, "uniqueKeys")
                        .Select(key => new SchemaUniqueKey(
                            Id(Required(table, "schema"), Required(key, "name")),
                            Required(key, "name"),
                            Elements(key, "columns").Select(column => Required(column, "name")).ToArray()
                        ))
                        .ToArray()
                ))
                .ToArray();
            var definitionBytes = 0;
            var objects = Elements(root, "objects")
                .Select(value =>
                {
                    var kind = Required(value, "kind");
                    var schema = Required(value, "schema");
                    var name = Required(value, "name");
                    var definition = Optional(value, "definition");
                    var partial = value.TryGetProperty("isPartial", out var flag) && flag.GetBoolean();
                    var target = Optional(value, "target");
                    if (kind == "synonym" && definition is null && target is not null)
                        definition = "CREATE SYNONYM " + Quote(schema) + "." + Quote(name) + " FOR " + target + ";";
                    if (definition is not null)
                    {
                        var size = Encoding.UTF8.GetByteCount(definition);
                        if (
                            size > SchemaMetadataLimits.MaximumDefinitionBytes
                            || (long)definitionBytes + size > SchemaMetadataLimits.MaximumDefinitionsBytes
                        )
                        {
                            definition = null;
                            partial = true;
                            Warn(
                                "OBJECT_DEFINITION_LIMIT",
                                "Object source SQL was omitted after reaching its 256 KiB object or 32 MiB snapshot limit."
                            );
                        }
                        else
                            definitionBytes += size;
                    }
                    if (definition is null)
                    {
                        partial = true;
                        Warn(
                            "OBJECT_DEFINITION_UNAVAILABLE",
                            "An encrypted, CLR or size-limited object has no SQL definition available from sys.sql_modules."
                        );
                    }
                    return new SchemaObject(
                        Id(schema, name),
                        schema,
                        name,
                        kind,
                        Columns(value),
                        Elements(value, "parameters")
                            .Select(parameter => new SchemaParameter(
                                Required(parameter, "name"),
                                Required(parameter, "dataType"),
                                parameter.GetProperty("isOutput").GetBoolean(),
                                parameter.GetProperty("isNullable").GetBoolean()
                            ))
                            .ToArray(),
                        Optional(value, "returnType"),
                        target,
                        definition,
                        sourcePath,
                        null,
                        partial
                    );
                })
                .ToArray();
            var known = tables.Select(table => table.Id).Concat(objects.Select(value => value.Id)).ToHashSet(StringComparer.Ordinal);
            var indexes = Elements(root, "indexes")
                .Select(value =>
                {
                    var owner = Id(Required(value, "schema"), Required(value, "objectName"));
                    var name = Required(value, "name");
                    return new SchemaIndex(
                        owner + ":index:" + Quote(name),
                        owner,
                        name,
                        Required(value, "kind"),
                        value.GetProperty("isUnique").GetBoolean(),
                        value.GetProperty("isPrimaryKey").GetBoolean(),
                        Elements(value, "columns")
                            .Select(column => new SchemaIndexColumn(
                                Required(column, "name"),
                                column.GetProperty("descending").GetBoolean()
                            ))
                            .ToArray(),
                        Elements(value, "includedColumns").Select(column => Required(column, "name")).ToArray(),
                        Optional(value, "filter")
                    );
                })
                .Where(index => known.Contains(index.ObjectId))
                .ToArray();
            var relations = Elements(root, "relations")
                .Select(relation => new SchemaRelation(
                    Id(Required(relation, "fromSchema"), Required(relation, "name")),
                    Required(relation, "name"),
                    Id(Required(relation, "fromSchema"), Required(relation, "fromTable")),
                    Id(Required(relation, "toSchema"), Required(relation, "toTable")),
                    Elements(relation, "columns").Select(column => Required(column, "source")).ToArray(),
                    Elements(relation, "columns").Select(column => Required(column, "target")).ToArray(),
                    "declared-fk",
                    1
                ))
                .ToArray();
            var dependencies = Elements(root, "objectDependencies")
                .Select(value =>
                {
                    var from = Id(Required(value, "fromSchema"), Required(value, "fromName"));
                    var schema = Optional(value, "targetSchema");
                    var name = Required(value, "targetName");
                    var targetName = string.Join(
                        '.',
                        new[] { Optional(value, "targetServer"), Optional(value, "targetDatabase"), schema, name }
                            .Where(part => part is not null)
                            .Select(part => Quote(part!))
                    );
                    var target = value.GetProperty("targetKnown").GetBoolean() && schema is not null ? Id(schema, name) : null;
                    if (target is not null && !known.Contains(target))
                        target = null;
                    if (target is not null)
                        targetName = Quote(schema!) + "." + Quote(name);
                    var kind = Required(value, "kind");
                    if (target is null)
                        Warn(
                            "OBJECT_DEPENDENCY_UNRESOLVED",
                            "An external or unresolved catalog dependency was retained without a fabricated local target."
                        );
                    var id =
                        "dependency:"
                        + Convert
                            .ToHexString(
                                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(from + "\0" + targetName + "\0" + kind))
                            )
                            .ToLowerInvariant();
                    return new SchemaObjectDependency(id, from, target, targetName, kind, sourcePath, null, target is null ? 0.5 : 1);
                })
                .Where(value => known.Contains(value.FromObjectId) && value.FromObjectId != value.ToObjectId)
                .DistinctBy(value => value.Id)
                .OrderBy(value => value.Id, StringComparer.Ordinal)
                .ToArray();
            if (tables.Length > 100_000 || objects.Length > 100_000 || indexes.Length > 200_000 || dependencies.Length > 200_000)
                throw new AppException("DOCKER_CATALOG_SIZE", "Каталог Docker превысил лимит объектов или доказанных зависимостей.", 422);
            return new(databaseName, sourcePath, DateTimeOffset.UtcNow, tables, relations, diagnostics, objects, indexes, dependencies);
            void Warn(string code, string message)
            {
                if (!diagnostics.Any(value => value.Code == code))
                    diagnostics.Add(new("warning", code, message));
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AppException("DOCKER_CATALOG_INVALID", "SQL Server вернул неполные метаданные схемы.", 422);
        }
    }

    private static IEnumerable<JsonElement> Elements(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    private static string Required(JsonElement value, string name) =>
        value.GetProperty(name).GetString() ?? throw new InvalidOperationException("Missing catalog string.");

    private static string? Optional(JsonElement value, string name) =>
        value.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static SchemaColumn[] Columns(JsonElement parent) =>
        Elements(parent, "columns")
            .Select(column => new SchemaColumn(
                Required(column, "name"),
                Required(column, "dataType"),
                column.GetProperty("isNullable").GetBoolean()
            ))
            .ToArray();

    private static string Quote(string value) => "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
