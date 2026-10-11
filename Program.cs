using System.Collections.Concurrent;

// Portfolio demonstration only. The actor header simulates a verified identity;
// a real deployment must replace it with validated OIDC tokens and durable storage.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<PortalStore>();
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/tenants/{tenant}/tasks", (string tenant, HttpRequest request, PortalStore store) =>
{
    if (!store.CanAccess(request.Headers["X-Actor-Id"].ToString(), tenant)) return Results.StatusCode(403);
    return Results.Ok(store.Tasks.Where(t => t.Tenant == tenant));
});

app.MapPost("/tenants/{tenant}/tasks/{taskId}/sync", (string tenant, string taskId,
    HttpRequest request, PortalStore store) =>
{
    var actor = request.Headers["X-Actor-Id"].ToString();
    if (!store.CanAccess(actor, tenant)) return Results.StatusCode(403);
    var key = request.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(key)) return Results.BadRequest(new { error = "Idempotency-Key required" });
    var task = store.Tasks.SingleOrDefault(t => t.Tenant == tenant && t.Id == taskId);
    if (task is null) return Results.NotFound();
    var result = store.Sync(tenant, task, key);
    return result.Conflict ? Results.Conflict(new { error = "Key already used for a different task" })
        : Results.Ok(new { task = result.TaskId, vendorStatus = result.Status, replayed = result.Replayed });
});

app.MapGet("/tenants/{tenant}/reconciliation", (string tenant, HttpRequest request, PortalStore store) =>
{
    if (!store.CanAccess(request.Headers["X-Actor-Id"].ToString(), tenant)) return Results.StatusCode(403);
    return Results.Ok(store.Reconcile(tenant));
});

app.Run();

record WorkTask(string Id, string Tenant, string Engagement, string Status);
record SyncResult(string TaskId, string Status, bool Replayed, bool Conflict = false);
record Drift(string TaskId, string LocalStatus, string? VendorStatus);

sealed class PortalStore
{
    private readonly Dictionary<string, HashSet<string>> _memberships = new()
    {
        ["alice"] = ["alpha"], ["bob"] = ["beta"], ["admin"] = ["alpha", "beta"]
    };
    public IReadOnlyList<WorkTask> Tasks { get; } =
    [
        new("A-101", "alpha", "tax-2026", "ready"),
        new("A-102", "alpha", "audit-2026", "draft"),
        new("B-201", "beta", "tax-2026", "ready")
    ];

    // The vendor is a synthetic downstream system with tenant-prefixed accounts.
    private readonly ConcurrentDictionary<string, string> _vendor = new();
    private readonly Dictionary<string, (string TaskId, string Status)> _receipts = new();
    private readonly object _syncGate = new();

    public bool CanAccess(string actor, string tenant) =>
        _memberships.TryGetValue(actor, out var tenants) && tenants.Contains(tenant);

    public SyncResult Sync(string tenant, WorkTask task, string key)
    {
        lock (_syncGate)
        {
            var receiptKey = $"{tenant}:{key}";
            if (_receipts.TryGetValue(receiptKey, out var receipt))
                return receipt.TaskId == task.Id
                    ? new(task.Id, receipt.Status, true)
                    : new(task.Id, receipt.Status, true, true);

            var vendorAccountTask = $"acct-{tenant}:{task.Id}";
            _vendor[vendorAccountTask] = task.Status;
            _receipts[receiptKey] = (task.Id, task.Status);
            return new(task.Id, task.Status, false);
        }
    }

    public IReadOnlyList<Drift> Reconcile(string tenant) => Tasks
        .Where(t => t.Tenant == tenant)
        .Select(t => new Drift(t.Id, t.Status,
            _vendor.TryGetValue($"acct-{tenant}:{t.Id}", out var status) ? status : null))
        .Where(d => d.LocalStatus != d.VendorStatus)
        .ToList();
}
