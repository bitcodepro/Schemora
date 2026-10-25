using System.Collections.Concurrent;
using System.Threading.Channels;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record JobInfo(
    string Id,
    string WorkspaceId,
    string Kind,
    string Status,
    string Stage,
    double Progress,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    string? Error = null,
    object? Result = null
);

public sealed class JobQueue(CatalogStore store, WorkspaceScanner scanner, RecipeService recipes, DiagnosticJournal? journal = null)
    : BackgroundService
{
    private sealed record Work(
        JobInfo Job,
        Workspace Workspace,
        string Mode,
        string? DatabaseId,
        bool TrustProjects,
        CancellationTokenSource Cancel
    );

    private readonly Channel<Work> channel = Channel.CreateBounded<Work>(
        new BoundedChannelOptions(16) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait }
    );
    private readonly ConcurrentDictionary<string, JobInfo> jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> cancellations = new();
    private readonly ConcurrentDictionary<string, string> active = new();

    public JobInfo Enqueue(Workspace workspace, string mode, string? databaseId, bool trustProjects)
    {
        if (mode is not ("quick" or "full" or "rebuild"))
            throw new AppException("SCAN_MODE_INVALID", "Выберите quick, full или rebuild.");
        if (mode == "rebuild" && !trustProjects)
            throw new AppException("PROJECT_TRUST_REQUIRED", "Подтвердите доверие перед сборкой проектов.", 403);
        var job = new JobInfo(Guid.NewGuid().ToString("N"), workspace.Id, "scan:" + mode, "queued", "В очереди", 0, DateTimeOffset.UtcNow);
        if (!active.TryAdd(workspace.Id, job.Id))
            throw new AppException("WORKSPACE_BUSY", "Сканирование этой рабочей области уже выполняется.", 409);
        var cancel = new CancellationTokenSource();
        cancellations[job.Id] = cancel;
        Save(job);
        if (!channel.Writer.TryWrite(new(job, workspace, mode, databaseId, trustProjects, cancel)))
        {
            active.TryRemove(workspace.Id, out _);
            cancellations.TryRemove(job.Id, out _);
            cancel.Dispose();
            Save(job with { Status = "failed", Error = "Очередь заполнена", FinishedAt = DateTimeOffset.UtcNow });
            throw new AppException("QUEUE_FULL", "Очередь локальных задач заполнена.", 429);
        }
        return job;
    }

    public bool HasActiveJobs => !active.IsEmpty || !cancellations.IsEmpty;

    public bool IsActive(string workspaceId) => active.ContainsKey(workspaceId);

    public JobInfo? Get(string id) => jobs.TryGetValue(id, out var job) ? job : store.Get<JobInfo>("Jobs", id);

    public IReadOnlyList<JobInfo> List() => store.Recent<JobInfo>("Jobs", 100);

    public bool Cancel(string id)
    {
        if (!cancellations.TryGetValue(id, out var cancellation))
            return false;
        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void Save(JobInfo job)
    {
        var previous = jobs.GetValueOrDefault(job.Id);
        jobs[job.Id] = job;
        store.Put("Jobs", job.Id, job.WorkspaceId, job);
        if (previous is null || previous.Status != job.Status || previous.Stage != job.Stage)
            journal?.Record(
                "JOB_" + job.Status.ToUpperInvariant(),
                job.Status == "failed" ? 422 : 200,
                jobId: job.Id,
                progress: job.Progress
            );
        foreach (var old in jobs.Values.Where(j => j.Status is not ("running" or "queued")).OrderByDescending(j => j.CreatedAt).Skip(100))
            jobs.TryRemove(old.Id, out _);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var old in store.All<JobInfo>("Jobs").Where(j => j.Status is "queued" or "running"))
            Save(
                old with
                {
                    Status = "failed",
                    Stage = "Прервано предыдущим завершением приложения",
                    Error = "Запустите задачу повторно",
                    FinishedAt = DateTimeOffset.UtcNow,
                }
            );
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var work in channel.Reader.ReadAllAsync(stoppingToken))
            {
                var job = work.Job;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, work.Cancel.Token);
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    job = job with { Status = "running", Stage = "Запуск", StartedAt = DateTimeOffset.UtcNow };
                    Save(job);
                    var result = await scanner.Scan(
                        work.Workspace,
                        work.Mode,
                        work.DatabaseId,
                        work.TrustProjects,
                        (stage, progress) =>
                        {
                            job = job with { Stage = stage, Progress = progress };
                            Save(job);
                        },
                        linked.Token
                    );
                    job = job with { Stage = "Проверка зависимых рецептов", Progress = 98 };
                    Save(job);
                    var recipesChecked = await recipes.RefreshWorkspace(work.Workspace.Id, linked.Token);
                    result = result with { RecipesChecked = recipesChecked };
                    job = job with
                    {
                        Status = result.Partial ? "partial" : "completed",
                        Stage = "Готово",
                        Progress = 100,
                        FinishedAt = DateTimeOffset.UtcNow,
                        Result = result,
                    };
                }
                catch (OperationCanceledException)
                {
                    job = job with { Status = "cancelled", Stage = "Отменено", FinishedAt = DateTimeOffset.UtcNow };
                }
                catch (Exception ex)
                {
                    journal?.Record(
                        ex is AppException appError ? appError.Code : "JOB_EXECUTION_FAILED",
                        422,
                        jobId: job.Id,
                        error: ex is AppException ? null : ex
                    );
                    job = job with { Status = "failed", Stage = "Ошибка", Error = ex.Message, FinishedAt = DateTimeOffset.UtcNow };
                }
                finally
                {
                    active.TryRemove(work.Workspace.Id, out _);
                    cancellations.TryRemove(job.Id, out _);
                    work.Cancel.Dispose();
                    // Publish terminal state only after the workspace can accept the next job.
                    Save(job);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var pair in cancellations)
            {
                pair.Value.Cancel();
                pair.Value.Dispose();
                if (Get(pair.Key) is { } job)
                    Save(job with { Status = "cancelled", Stage = "Приложение остановлено", FinishedAt = DateTimeOffset.UtcNow });
            }
        }
    }
}
