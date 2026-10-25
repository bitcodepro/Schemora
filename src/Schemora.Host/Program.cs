using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.StaticFiles;
using Schemora.Host;
using Schemora.RecipeEngine;
using Schemora.Relationships;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Schemora.Workspaces.Discovery;

if (args.Length > 0 && args[0] == "render")
{
    // Command-line rendering needs neither the web host nor the catalog lock, so it can run next to the app.
    Console.OutputEncoding = Encoding.UTF8;
    Environment.ExitCode = RenderCommand.Run(args[1..], Console.Out, Console.Error);
    return;
}
LaunchConfiguration launch;
try
{
    launch = LaunchConfiguration.Load(args);
}
catch (AppException error)
{
    Console.Error.WriteLine(error.Code + ": " + error.Message);
    Environment.ExitCode = 1;
    return;
}
var noBrowser = args.Contains("--no-browser");
string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    if (index < 0)
        return null;
    if (index + 1 >= args.Length)
        throw new ArgumentException($"Missing value for {name}");
    return args[index + 1];
}
var dataDirectory = Path.GetFullPath(
    Option("--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Schemora")
);
if (dataDirectory.StartsWith(@"\\", StringComparison.Ordinal) || new Uri(dataDirectory).IsUnc)
    throw new ArgumentException("Data directory must be local.");
Directory.CreateDirectory(dataDirectory);
if (Option("--wait-for-process") is { } waitPid)
{
    if (!int.TryParse(waitPid, out var pid) || pid <= 0 || pid == Environment.ProcessId)
        throw new ArgumentException("Invalid wait process.");
    try
    {
        using var previous = Process.GetProcessById(pid);
        if (!previous.WaitForExit(30000))
            throw new InvalidOperationException("Previous Schemora process did not exit.");
    }
    catch (ArgumentException) { }
}
using var localInstance = LocalInstance.TryAcquire(dataDirectory);
if (localInstance is null)
{
    var running = LocalInstance.ReadRunning(dataDirectory);
    if (!noBrowser && running is not null)
        try
        {
            Process.Start(new ProcessStartInfo(running.Origin + "/#token=" + running.Token) { UseShellExecute = true });
        }
        catch { }
    Console.WriteLine("Schemora is already running with this data directory.");
    return;
}
if (Option("--restore-backup") is { } restorePath)
{
    var preserved = CatalogRecovery.Restore(Path.Combine(dataDirectory, "catalog.sqlite"), restorePath);
    Console.WriteLine("Catalog restored; the previous copy was preserved: " + preserved);
}
var sessionFile = Option("--session-file");
if (sessionFile is not null)
{
    sessionFile = Path.GetFullPath(sessionFile);
    if (File.Exists(sessionFile))
        throw new InvalidOperationException("Session file already exists; choose a new path.");
    Directory.CreateDirectory(Path.GetDirectoryName(sessionFile)!);
}
var port = launch.Port;
var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var expectedToken = Encoding.UTF8.GetBytes(token);
var origin = "";
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
builder.Configuration.Sources.Clear();
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, port);
    options.Limits.MaxRequestBodySize = 64L * 1024 * 1024;
    options.AddServerHeader = false;
});
builder.Services.AddSingleton(new Toolchain(dataDirectory));
builder.Services.AddSingleton<DacpacReader>();
builder.Services.AddSingleton<DockerSchemaReader>();
builder.Services.AddHostedService(p => p.GetRequiredService<DockerSchemaReader>());
builder.Services.AddSingleton<SqlProjectBuilder>();
builder.Services.AddSingleton(new CatalogStore(Path.Combine(dataDirectory, "catalog.sqlite")));
builder.Services.AddSingleton<WorkspaceDiscovery>();
builder.Services.AddSingleton<StaticSqlReader>();
builder.Services.AddSingleton<SqlSourceClassifier>();
builder.Services.AddSingleton<WorkspaceScanner>();
builder.Services.AddSingleton<IdProcessor>();
builder.Services.AddSingleton<SqlRecipeRenderer>();
builder.Services.AddSingleton<RecipeValidator>();
builder.Services.AddSingleton<RecipeService>();
builder.Services.AddSingleton<SchemaDiffer>();
builder.Services.AddSingleton<RecipeColumnRewriter>();
builder.Services.AddSingleton<ChangeService>();
builder.Services.AddSingleton<RelationshipAnalyzer>();
builder.Services.AddSingleton<RelationshipService>();
builder.Services.AddSingleton<IRelationshipContext>(p => p.GetRequiredService<RelationshipService>());
builder.Services.AddSingleton<ManualRelationService>();
builder.Services.AddSingleton<ChainRecipeBuilder>();
builder.Services.AddSingleton<SchemaObjectService>();
builder.Services.AddSingleton<AutoRefreshService>();
builder.Services.AddHostedService(p => p.GetRequiredService<AutoRefreshService>());
builder.Services.AddSingleton<GraphReadIndex>();
builder.Services.AddSingleton<StorageMaintenance>();
builder.Services.AddSingleton(new DiagnosticJournal(dataDirectory));
builder.Services.AddSingleton(new ValueSetStore(dataDirectory));
builder.Services.AddSingleton<AutomaticBackups>();
builder.Services.AddHostedService(p => p.GetRequiredService<AutomaticBackups>());
builder.Services.AddSingleton<PortableConfigurationService>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddHostedService(p => p.GetRequiredService<JobQueue>());
var app = builder.Build();

app.Use(
    async (context, next) =>
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; worker-src 'self' blob:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
        context.Response.Headers["Cache-Control"] = "no-store";
        if (origin.Length == 0 || context.Request.Host.Value != new Uri(origin).Authority)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { Code = "HOST_REJECTED", Message = "Local host required." });
            return;
        }
        if (
            (context.Request.Headers.TryGetValue("Origin", out var requestOrigin) && requestOrigin.ToString() != origin)
            || context.Request.Headers["Sec-Fetch-Site"] == "cross-site"
        )
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { Code = "ORIGIN_REJECTED", Message = "Same origin required." });
            return;
        }
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            var supplied = Encoding.UTF8.GetBytes(context.Request.Headers["X-Schemora-Token"].ToString());
            if (!CryptographicOperations.FixedTimeEquals(supplied, expectedToken))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(
                    new { Code = "SESSION_REQUIRED", Message = "Open the interface using the link from the current Schemora launch." }
                );
                return;
            }
        }
        var operationId = Guid.NewGuid().ToString("N");
        context.Response.Headers["X-Schemora-Operation"] = operationId;
        var journal = context.RequestServices.GetRequiredService<DiagnosticJournal>();
        try
        {
            await next();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (RecipeEngineException ex)
        {
            journal.Record(ex.Code, 422, operationId);
            context.Response.StatusCode = 422;
            await context.Response.WriteAsJsonAsync(
                new
                {
                    ex.Code,
                    ex.Message,
                    ex.Errors,
                    OperationId = operationId,
                }
            );
        }
        catch (AppException ex)
        {
            journal.Record(ex.Code, ex.Status, operationId);
            context.Response.StatusCode = ex.Status;
            await context.Response.WriteAsJsonAsync(
                new
                {
                    ex.Code,
                    ex.Message,
                    ex.Detail,
                    OperationId = operationId,
                }
            );
        }
        catch (BadHttpRequestException ex)
        {
            journal.Record("REQUEST_INVALID", 400, operationId);
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(
                new
                {
                    Code = "REQUEST_INVALID",
                    Message = "The request body or parameters could not be read.",
                    OperationId = operationId,
                    Detail = ex.Message,
                }
            );
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unreadable files, model errors and failing tools are input problems the user can act on; anything else is a
            // defect in Schemora and must not be presented as a bad file.
            var input = ErrorKinds.IsInputFailure(ex);
            var code = input ? "ANALYSIS_FAILED" : "INTERNAL_ERROR";
            var status = input ? 422 : 500;
            journal.Record(code, status, operationId, error: ex);
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(
                new
                {
                    Code = code,
                    Message = input
                        ? "The operation did not complete. Check the file format and tool availability."
                        : "Internal error in Schemora. The operation was not completed; report the operation number from the diagnostics journal.",
                    OperationId = operationId,
                }
            );
        }
    }
);

app.MapGet(
    "/api/health",
    () =>
        new
        {
            Status = "ok",
            Phase = 5,
            Version = typeof(Program).Assembly.GetName().Version?.ToString(3),
            // Includes the git commit of this build, e.g. 1.8.0+abc123def456.
            Build = typeof(Program)
                .Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()
                ?.InformationalVersion,
        }
);
app.MapGet("/api/diagnostics", (Toolchain tools, CancellationToken ct) => tools.Diagnose(ct));
app.MapPost(
    "/api/schema/import",
    (ImportRequest request, DacpacReader reader) => reader.Read(LocalFiles.Validate(request.Path, ".dacpac"))
);
app.MapPost(
    "/api/schema/build",
    (BuildRequest request, SqlProjectBuilder projectBuilder, CancellationToken ct) =>
        projectBuilder.Build(request.Path, request.TrustProject, ct)
);
app.MapDockerEndpoints();
app.MapWorkspaceEndpoints();
app.MapGraphEndpoints();
app.MapEnhancementEndpoints();
app.MapRecipeEndpoints();
app.MapChangeEndpoints();
app.MapReleaseEndpoints(dataDirectory);
app.MapStorageMaintenance();
app.MapManagedFiles();
app.MapDiagnosticEndpoints();
app.MapCSharpProjectEndpoints();
app.MapProductExperience(dataDirectory);
app.MapValueSets();
app.MapAutomaticBackups();
app.Map("/api/{**path}", () => Results.NotFound(new { Code = "ENDPOINT_NOT_FOUND", Message = "API endpoint not found." }));

var assembly = Assembly.GetExecutingAssembly();
var resources = assembly
    .GetManifestResourceNames()
    .Where(n => n.StartsWith("Schemora.Ui/", StringComparison.Ordinal))
    .ToDictionary(n => n["Schemora.Ui/".Length..].Replace('\\', '/'), n => n, StringComparer.Ordinal);
var contentTypes = new FileExtensionContentTypeProvider();
app.MapMethods(
    "/{**path}",
    ["GET", "HEAD"],
    (HttpContext context, string? path) =>
    {
        var requested = string.IsNullOrEmpty(path) ? "index.html" : path;
        if (!resources.TryGetValue(requested, out var resource))
            return Results.NotFound();
        contentTypes.TryGetContentType(requested, out var contentType);
        return Results.Stream(assembly.GetManifestResourceStream(resource)!, contentType ?? "application/octet-stream");
    }
);

try
{
    await app.StartAsync();
}
catch (Exception error) when (LaunchConfiguration.IsAddressInUse(error))
{
    Console.Error.WriteLine(launch.AddressInUseMessage());
    Environment.ExitCode = 1;
    await app.DisposeAsync();
    return;
}
origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
var launchUrl = $"{origin}/#token={token}";
localInstance.Publish(origin, token);
Console.WriteLine($"Schemora: {origin}");
if (sessionFile is not null)
    await File.WriteAllTextAsync(
        sessionFile,
        JsonSerializer.Serialize(
            new
            {
                origin,
                token,
                launchUrl,
                processId = Environment.ProcessId,
            }
        )
    );
if (!noBrowser)
{
    try
    {
        Process.Start(new ProcessStartInfo(launchUrl) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"Could not open the browser: {ex.Message}. Restart the application or use --session-file for headless mode."
        );
    }
}
try
{
    await app.WaitForShutdownAsync();
}
finally
{
    if (sessionFile is not null && File.Exists(sessionFile))
        File.Delete(sessionFile);
    await app.DisposeAsync();
}

public sealed record ImportRequest(string? Path);

public sealed record BuildRequest(string? Path, bool TrustProject);
