namespace Schemora.Host;

public sealed record DockerSchemaRequest(string? Path, bool AllowIsolatedDocker = false);

public static class DockerEndpoints
{
    public static void MapDockerEndpoints(this WebApplication app)
    {
        app.MapGet("/api/schema/docker/status", (DockerSchemaReader reader) => new { reader.RecoveryDiagnostics });
        app.MapPost(
            "/api/schema/docker",
            (DockerSchemaRequest request, DockerSchemaReader reader, CancellationToken ct) =>
                reader.Read(request.Path, request.AllowIsolatedDocker, ct)
        );
    }
}
