using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fabric.Policy.Core;

public sealed record PolicyDocument(int SchemaVersion, string Version,
    ImmutableArray<WorkspacePolicy> Workspaces, ImmutableDictionary<string, EnvironmentPolicy> Environments,
    ImmutableArray<ExternalApproval> ExternalDependencies);
public sealed record WorkspacePolicy(Guid WorkspaceId, string Repository, string Environment);
public sealed record EnvironmentPolicy(ImmutableHashSet<string> AllowedItemTypes,
    ImmutableHashSet<string> AllowedClassifications, ImmutableHashSet<string> AllowedOwners,
    [property: JsonRequired] bool RequireExplicitItemApproval, ImmutableHashSet<string> ApprovedItemIds);
public sealed record ExternalApproval(string ConsumerRepository, string Alias, string Environment,
    Guid WorkspaceId, string Repository, string Type, string ItemName);
public sealed record PolicySnapshot(PolicyDocument Document, string Digest);

// Implement a cached Blob/Table adapter here later. One evaluation uses one
// immutable snapshot, including its workspace registry and external approvals.
public interface IPolicyStore
{
    ValueTask<PolicySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public sealed class JsonFilePolicyStore : IPolicyStore
{
    private readonly PolicySnapshot snapshot;

    public JsonFilePolicyStore(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var options = new JsonSerializerOptions(ContractJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var document = JsonSerializer.Deserialize<PolicyDocument>(bytes, options)
            ?? throw new InvalidDataException("Policy document is empty.");
        Validate(document);
        snapshot = new(document, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    public ValueTask<PolicySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(snapshot);
    }

    private static void Validate(PolicyDocument document)
    {
        static void Require(bool valid, string message)
        {
            if (!valid) throw new InvalidDataException(message);
        }
        Require(document.SchemaVersion == 1 && !string.IsNullOrWhiteSpace(document.Version), "Invalid policy version.");
        Require(document.Environments is not null &&
            document.Environments.Keys.ToHashSet().SetEquals(["dev", "test", "prod"]), "Policies must define dev, test and prod.");
        Require(!document.Workspaces.IsDefaultOrEmpty, "Workspace registry is required.");
        var ids = new HashSet<Guid>();
        var targets = new HashSet<(string, string)>();
        foreach (var workspace in document.Workspaces)
        {
            Require(workspace is not null && workspace.WorkspaceId != Guid.Empty &&
                !string.IsNullOrWhiteSpace(workspace.Repository) && workspace.Environment is not null &&
                document.Environments!.ContainsKey(workspace.Environment), "Invalid workspace registration.");
            Require(ids.Add(workspace!.WorkspaceId) && targets.Add((workspace.Repository, workspace.Environment!)),
                "Duplicate workspace ID or repository/environment target.");
        }
        foreach (var policy in document.Environments!.Values)
        {
            Require(policy is not null && policy.AllowedItemTypes is { Count: > 0 } &&
                policy.AllowedClassifications is { Count: > 0 } && policy.AllowedOwners is { Count: > 0 } &&
                policy.ApprovedItemIds is not null, "Policy allowlists are required.");
            Require(policy!.AllowedItemTypes.All(x => x is "Lakehouse" or "Notebook" or "SemanticModel" or "Report") &&
                policy.AllowedClassifications.All(x => x is "public" or "internal" or "confidential" or "restricted") &&
                policy.AllowedOwners.All(x => !string.IsNullOrWhiteSpace(x)) &&
                policy.ApprovedItemIds!.All(RequestValidation.IsLogicalId), "Invalid policy allowlist value.");
        }
        Require(!document.ExternalDependencies.IsDefault, "External approval list is required (can be empty).");
        var externalKeys = new HashSet<(string, string, string)>();
        foreach (var external in document.ExternalDependencies)
        {
            Require(external is not null && !string.IsNullOrWhiteSpace(external.ConsumerRepository) &&
                !string.IsNullOrWhiteSpace(external.Alias) && !string.IsNullOrWhiteSpace(external.ItemName) &&
                external.Type == "Lakehouse" &&
                document.Workspaces.Any(w => w.WorkspaceId == external.WorkspaceId &&
                    w.Repository == external.Repository && w.Environment == external.Environment) &&
                document.Workspaces.Any(w => w.Repository == external.ConsumerRepository && w.Environment == external.Environment),
                "External approval must reference registered workspaces in the same environment.");
            Require(externalKeys.Add((external!.ConsumerRepository, external.Alias, external.Environment)), "Duplicate external approval.");
        }
    }
}
