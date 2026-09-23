namespace Fabric.Policy.Core;

public static class RequestValidation
{
    public static bool IsLogicalId(string? value) => Guid.TryParseExact(value, "D", out var id) &&
        id != Guid.Empty && id.ToString("D") == value;

    public static void Validate(EvaluationRequest request)
    {
        static void Require(bool valid, string message)
        {
            if (!valid) throw new InvalidEvaluationException(message);
        }
        Require(request.WorkspaceId != Guid.Empty, "workspaceId must be a nonempty GUID.");
        var manifest = request.Manifest;
        Require(manifest is not null && manifest.SchemaVersion == 1 && !string.IsNullOrWhiteSpace(manifest.Repository),
            "A schemaVersion 1 manifest with repository is required.");
        Require(manifest!.Items is { Count: > 0 and <= 1000 }, "Manifest must contain 1–1000 items.");
        Require(manifest.ExternalDependencies is not null && manifest.ExternalDependencies.Count <= 1000,
            "externalDependencies must be an object with at most 1000 entries (can be empty).");
        var ids = new HashSet<string>();
        var names = new HashSet<(string, string)>();
        foreach (var item in manifest.Items!)
        {
            Require(item is not null && IsLogicalId(item.LogicalId) && !string.IsNullOrWhiteSpace(item.Type) &&
                !string.IsNullOrWhiteSpace(item.Name), "Every item needs a canonical lowercase logical GUID, type and name.");
            Require(ids.Add(item!.LogicalId) && names.Add((item.Type, item.Name)), "Duplicate item identity or type/name.");
            // Missing governance is a policy denial, not a transport error.
        }
        foreach (var (alias, external) in manifest.ExternalDependencies!)
        {
            Require(!string.IsNullOrWhiteSpace(alias) && external is not null &&
                !string.IsNullOrWhiteSpace(external.Repository) && !string.IsNullOrWhiteSpace(external.Owner) &&
                !string.IsNullOrWhiteSpace(external.Type) && !string.IsNullOrWhiteSpace(external.ItemName) &&
                external.Environments is not null, "Invalid external dependency declaration.");
            Require(external!.Environments!.All(e => e.Key is "dev" or "test" or "prod" &&
                e.Value is not null && e.Value.WorkspaceId != Guid.Empty), "Invalid external dependency target.");
        }
        Require(request.Dependencies is not null && request.Dependencies.SchemaVersion == 1 &&
            request.Dependencies.Edges is { Count: <= 10000 }, "A schemaVersion 1 dependency graph with at most 10000 edges is required.");
        foreach (var edge in request.Dependencies!.Edges)
            Require(edge is not null && edge.Item is not null && ids.Contains(edge.Item) &&
                !string.IsNullOrWhiteSpace(edge.Dependency), "Every edge needs a known item and a dependency.");
    }
}
