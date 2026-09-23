namespace Fabric.Policy.Core;

public sealed class PolicyEvaluator(IPolicyStore store)
{
    public async ValueTask<EvaluationResult> EvaluateAsync(EvaluationRequest request, string requestDigest,
        CancellationToken cancellationToken = default)
    {
        RequestValidation.Validate(request);
        var snapshot = await store.GetSnapshotAsync(cancellationToken);
        var workspace = snapshot.Document.Workspaces.FirstOrDefault(w => w.WorkspaceId == request.WorkspaceId);
        var global = new List<Finding>();
        if (workspace is null)
            global.Add(new("workspace.unregistered", "The workspace is not registered in policy."));
        else if (workspace.Repository != request.Manifest.Repository)
            global.Add(new("workspace.repository", "The repository is not approved for this workspace."));

        var items = request.Manifest.Items.ToDictionary(i => i.LogicalId);
        var findings = items.Keys.ToDictionary(id => id, _ => new List<Finding>(global));
        var prerequisites = items.Keys.ToDictionary(id => id, _ => new HashSet<string>());
        var dependents = items.Keys.ToDictionary(id => id, _ => new HashSet<string>());
        var policy = workspace is null ? null : snapshot.Document.Environments[workspace.Environment];

        foreach (var item in items.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (policy is null) continue;
            var reasons = findings[item.LogicalId];
            if (!policy.AllowedItemTypes.Contains(item.Type))
                reasons.Add(new("item.type", $"Item type '{item.Type}' is not allowed in {workspace!.Environment}."));
            if (item.Governance is null || string.IsNullOrWhiteSpace(item.Governance.Owner) ||
                !policy.AllowedOwners.Contains(item.Governance.Owner))
                reasons.Add(new("item.owner", "An approved owner is required."));
            if (item.Governance?.Classification is null || !policy.AllowedClassifications.Contains(item.Governance.Classification))
                reasons.Add(new("item.classification", "Classification is missing or not allowed in this environment."));
            if (policy.RequireExplicitItemApproval && !policy.ApprovedItemIds.Contains(item.LogicalId))
                reasons.Add(new("item.explicitApproval", "The logical item ID requires explicit environment approval."));
        }

        foreach (var edge in request.Dependencies.Edges.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (items.ContainsKey(edge.Dependency))
            {
                prerequisites[edge.Item].Add(edge.Dependency);
                dependents[edge.Dependency].Add(edge.Item);
            }
            else if (!ExternalIsApproved(edge.Dependency, request.Manifest, workspace, snapshot.Document))
                findings[edge.Item].Add(new("dependency.unapproved", "Dependency is unresolved or lacks a matching external approval.", edge.Dependency));
        }

        // Kahn traversal avoids recursion even on adversarial graphs. Residual
        // nodes are cycle members OR dependents of a cycle; all must be denied.
        var remaining = prerequisites.ToDictionary(p => p.Key, p => p.Value.Count);
        var ready = new Queue<string>(remaining.Where(p => p.Value == 0).Select(p => p.Key));
        var visited = new HashSet<string>();
        while (ready.TryDequeue(out var id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            visited.Add(id);
            foreach (var dependency in prerequisites[id].Order(StringComparer.Ordinal))
                if (findings[dependency].Count > 0)
                    findings[id].Add(new("dependency.denied", "A prerequisite was denied.", dependency));
            foreach (var dependent in dependents[id])
                if (--remaining[dependent] == 0) ready.Enqueue(dependent);
        }
        foreach (var id in items.Keys.Where(id => !visited.Contains(id)))
            findings[id].Add(new("dependency.cycle", "Item belongs to or depends on a dependency cycle."));

        var decisions = items.Values.OrderBy(i => i.LogicalId, StringComparer.Ordinal)
            .Select(i => new ItemDecision(i.LogicalId, i.Name, findings[i.LogicalId].Count == 0, findings[i.LogicalId])).ToArray();
        return new(Guid.NewGuid(), DateTimeOffset.UtcNow, request.WorkspaceId, workspace?.Environment,
            snapshot.Document.Version, snapshot.Digest, requestDigest,
            global, decisions);
    }

    private static bool ExternalIsApproved(string dependency, Manifest manifest, WorkspacePolicy? workspace, PolicyDocument document)
    {
        if (workspace is null || !dependency.StartsWith("external:", StringComparison.Ordinal)) return false;
        var alias = dependency[9..];
        if (!manifest.ExternalDependencies.TryGetValue(alias, out var declaration) ||
            !declaration.Environments.TryGetValue(workspace.Environment, out var target)) return false;
        return document.ExternalDependencies.Any(a => a.ConsumerRepository == manifest.Repository && a.Alias == alias &&
            a.Environment == workspace.Environment && a.WorkspaceId == target.WorkspaceId &&
            a.Repository == declaration.Repository && a.Type == declaration.Type && a.ItemName == declaration.ItemName);
    }
}
