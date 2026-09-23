using System.Text.Json;

namespace Fabric.Policy.Core;

// Names match platform_fabric's generated inventory and dependency graph.
// The HTTP boundary returns the complete original manifest alongside these reviews;
// the caller must still verify artifact integrity and dependency extraction.
public sealed record EvaluationRequest(Guid WorkspaceId, Manifest Manifest, DependencyGraph Dependencies);
public sealed record Manifest(int SchemaVersion, string Repository, List<ManifestItem> Items,
    Dictionary<string, ExternalDependency> ExternalDependencies);
public sealed record ManifestItem(string LogicalId, string Type, string Name, Governance Governance);
public sealed record Governance(string Owner, string Classification);
public sealed record ExternalDependency(string Repository, string Owner, string Type, string ItemName,
    Dictionary<string, ExternalTarget> Environments);
public sealed record ExternalTarget(Guid WorkspaceId);
public sealed record DependencyGraph(int SchemaVersion, List<DependencyEdge> Edges);
public sealed record DependencyEdge(string Item, string Dependency);

public sealed record Finding(string Code, string Message, string? Dependency = null);
public sealed record ItemDecision(string LogicalId, string Name, bool Approved, IReadOnlyList<Finding> Findings);
public sealed record EvaluationResult(Guid DecisionId, DateTimeOffset EvaluatedAt, Guid WorkspaceId,
    string? Environment, string PolicyVersion, string PolicyDigest, string RequestDigest,
    IReadOnlyList<Finding> Findings, IReadOnlyList<ItemDecision> Items);

public static class ContractJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

public sealed class InvalidEvaluationException(string message) : Exception(message);
