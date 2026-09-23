using System.Text.Json;
using Fabric.Policy.Core;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var store = new JsonFilePolicyStore(Path.Combine(root, "policies/policies.json"));
var evaluator = new PolicyEvaluator(store);
EvaluationRequest Sample() => JsonSerializer.Deserialize<EvaluationRequest>(
    File.ReadAllText(Path.Combine(root, "examples/evaluation.json")), ContractJson.Options)!;
Task<EvaluationResult> Evaluate(EvaluationRequest request) => evaluator.EvaluateAsync(request, "test-request-digest").AsTask();
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static bool Has(EvaluationResult result, string code) => result.Items.Any(i => i.Findings.Any(f => f.Code == code));
var passed = 0;
async Task Spec(string name, Func<Task> test)
{
    await test();
    Console.WriteLine($"PASS {name}");
    passed++;
}

await Spec("dev approves the complete example", async () =>
{
    var result = await Evaluate(Sample());
    Check(result.Items.All(i => i.Approved) && result.Environment == "dev", "Expected dev approval");
    Check(result.PolicyDigest.Length == 64 && result.PolicyVersion.StartsWith("sample-"), "Missing policy identity");
});
await Spec("reviews return independent results without a deployment verdict", async () =>
{
    var request = Sample();
    var report = request.Manifest.Items[1];
    request.Manifest.Items[1] = report with { Governance = report.Governance with { Owner = "unapproved" } };
    var result = await Evaluate(request);
    Check(result.Items.Single(i => i.LogicalId == request.Manifest.Items[0].LogicalId).Approved, "Independent prerequisite should pass");
    Check(!result.Items.Single(i => i.LogicalId == report.LogicalId).Approved, "Invalid owner should fail");
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, ContractJson.Options));
    Check(!json.RootElement.TryGetProperty("approved", out _), "Review must not decide deployment");
});
await Spec("test uses its own external workspace", async () =>
{
    Check((await Evaluate(Sample() with { WorkspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222") })).Items.All(i => i.Approved), "Expected test approval");
});
await Spec("production requires explicit item and external approvals", async () =>
{
    var result = await Evaluate(Sample() with { WorkspaceId = Guid.Parse("33333333-3333-3333-3333-333333333333") });
    Check(!result.Items.All(i => i.Approved) && Has(result, "item.explicitApproval") && Has(result, "dependency.unapproved"), "Expected prod denial");
});
await Spec("unknown workspace denies every item", async () =>
{
    var result = await Evaluate(Sample() with { WorkspaceId = Guid.NewGuid() });
    Check(!result.Items.All(i => i.Approved) && result.Items.All(i => !i.Approved) && Has(result, "workspace.unregistered"), "Unknown workspace allowed");
});
await Spec("repository cannot select another repository's workspace", async () =>
{
    var result = await Evaluate(Sample() with { WorkspaceId = Guid.Parse("44444444-4444-4444-4444-444444444444") });
    Check(!result.Items.All(i => i.Approved) && Has(result, "workspace.repository"), "Repository mismatch allowed");
});
await Spec("denied prerequisite blocks transitive dependents", async () =>
{
    var request = Sample();
    var model = request.Manifest.Items[0];
    request.Manifest.Items[0] = model with { Governance = model.Governance with { Classification = "restricted" } };
    var lastId = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    request.Manifest.Items.Add(new(lastId, "Report", "Downstream", new("analytics-dev", "internal")));
    request.Dependencies.Edges.Add(new(lastId, request.Manifest.Items[1].LogicalId));
    var result = await Evaluate(request);
    Check(result.Items.All(i => !i.Approved) && result.Items.Single(i => i.LogicalId == lastId).Findings.Any(f => f.Code == "dependency.denied"), "Transitive denial failed");
});
await Spec("confidential item approved in dev is denied in test", async () =>
{
    var request = Sample();
    var model = request.Manifest.Items[0];
    request.Manifest.Items[0] = model with { Governance = model.Governance with { Classification = "confidential" } };
    Check((await Evaluate(request)).Items.All(i => i.Approved), "Dev classification rule failed");
    Check(!(await Evaluate(request with { WorkspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222") })).Items.All(i => i.Approved), "Test classification rule failed");
});
await Spec("cross-environment external substitution is denied", async () =>
{
    var request = Sample();
    var external = request.Manifest.ExternalDependencies["engineering-lakehouse"];
    external.Environments["dev"] = external.Environments["test"];
    var result = await Evaluate(request);
    Check(!result.Items.All(i => i.Approved) && Has(result, "dependency.unapproved"), "Cross-environment dependency allowed");
});
await Spec("unknown dependency is denied", async () =>
{
    var request = Sample();
    request.Dependencies.Edges.Add(new(request.Manifest.Items[0].LogicalId, "missing"));
    Check(!(await Evaluate(request)).Items.All(i => i.Approved), "Missing dependency allowed");
});
await Spec("cycles and their descendants are denied; independent items can pass", async () =>
{
    var request = Sample();
    request.Dependencies.Edges.Add(new(request.Manifest.Items[0].LogicalId, request.Manifest.Items[1].LogicalId));
    request.Manifest.Items.Add(new("cccccccc-cccc-cccc-cccc-cccccccccccc", "Report", "Independent", new("analytics-dev", "internal")));
    var result = await Evaluate(request);
    Check(!result.Items.All(i => i.Approved) && result.Items.Count(i => i.Approved) == 1 && Has(result, "dependency.cycle"), "Cycle handling failed");
});
await Spec("missing governance and unsupported item type are denied", async () =>
{
    var request = Sample();
    request.Manifest.Items[0] = request.Manifest.Items[0] with { Governance = null!, Type = "Unknown" };
    var result = await Evaluate(request);
    Check(!result.Items.All(i => i.Approved) && Has(result, "item.owner") && Has(result, "item.classification") && Has(result, "item.type"), "Missing metadata allowed");
});
await Spec("duplicate edges do not change the result", async () =>
{
    var request = Sample();
    request.Dependencies.Edges.AddRange(request.Dependencies.Edges.ToArray());
    Check((await Evaluate(request)).Items.All(i => i.Approved), "Duplicate edge broke evaluation");
});
await Spec("malformed and oversized contracts reject before evaluation", async () =>
{
    var mutations = new Action<EvaluationRequest>[] {
        r => r.Manifest.Items.Clear(),
        r => r.Manifest.Items.Add(r.Manifest.Items[0]),
        r => r.Manifest.Items[0] = null!,
        r => r.Dependencies.Edges.Add(new("unknown-item", "missing")),
        r => r.Dependencies.Edges.Add(null!),
        r => r.Dependencies.Edges.AddRange(Enumerable.Repeat(r.Dependencies.Edges[0], 10001))
    };
    foreach (var mutate in mutations)
    {
        var request = Sample();
        mutate(request);
        try { await Evaluate(request); throw new Exception("Malformed request accepted"); }
        catch (InvalidEvaluationException) { }
    }
    foreach (var request in new[] { Sample() with { Manifest = null! }, Sample() with { Dependencies = null! }, Sample() with { WorkspaceId = Guid.Empty } })
    {
        try { await Evaluate(request); throw new Exception("Missing field accepted"); }
        catch (InvalidEvaluationException) { }
    }
});
await Spec("invalid policies fail startup and snapshots do not change on file edits", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"policy-spec-{Guid.NewGuid():N}.json");
    try
    {
        File.Copy(Path.Combine(root, "policies/policies.json"), path);
        var cached = new JsonFilePolicyStore(path);
        var before = await cached.GetSnapshotAsync();
        File.WriteAllText(path, "{\"schemaVersion\":99}");
        Check(ReferenceEquals(before, await cached.GetSnapshotAsync()), "Snapshot changed without a restart");
        try { _ = new JsonFilePolicyStore(path); throw new Exception("Invalid policy accepted"); }
        catch (InvalidDataException) { }
    }
    finally { File.Delete(path); }
});
await Spec("parallel evaluations have no shared mutable decision state", async () =>
{
    var results = await Task.WhenAll(Enumerable.Range(0, 256).Select(i => Task.Run(async () =>
        await Evaluate(i % 2 == 0 ? Sample() : Sample() with { WorkspaceId = Guid.NewGuid() }))));
    Check(results.Where((r, i) => r.Items.All(item => item.Approved) != (i % 2 == 0)).Count() == 0, "Decisions leaked across requests");
    Check(results.Select(r => r.DecisionId).Distinct().Count() == 256 && results.Select(r => r.PolicyDigest).Distinct().Count() == 1, "Snapshot or decision identity failed");
});
Console.WriteLine($"{passed} specifications passed.");
