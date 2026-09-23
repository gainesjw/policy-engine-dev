using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Fabric.Policy.Core;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddConcurrencyLimiter("evaluation", limiter =>
    {
        limiter.PermitLimit = builder.Configuration.GetValue("Evaluation:ConcurrencyLimit", 64);
        limiter.QueueLimit = 0;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});
var policyPath = builder.Configuration["PolicyStore:Path"] ?? Path.Combine(AppContext.BaseDirectory, "policies", "policies.json");
// Eager construction validates policy before any requests can be accepted.
builder.Services.AddSingleton<IPolicyStore>(new JsonFilePolicyStore(policyPath));
builder.Services.AddSingleton<PolicyEvaluator>();

var apiKey = builder.Configuration["Authentication:ApiKey"];
if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(apiKey))
    throw new InvalidOperationException("Set Authentication__ApiKey outside Development. Use TLS at the ingress.");
var expectedKeyHash = string.IsNullOrWhiteSpace(apiKey) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));

var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/v1") && expectedKeyHash is not null)
    {
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers["X-Policy-Key"].ToString()));
        if (!CryptographicOperations.FixedTimeEquals(expectedKeyHash, suppliedHash))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }
    await next(context);
});
app.UseRateLimiter();
app.MapGet("/health/ready", async (IPolicyStore store, CancellationToken cancellationToken) =>
{
    var snapshot = await store.GetSnapshotAsync(cancellationToken);
    return Results.Ok(new { status = "ready", policyVersion = snapshot.Document.Version, policyDigest = snapshot.Digest });
});

app.MapPost("/v1/evaluations", async (JsonElement body, PolicyEvaluator evaluator,
    ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    try
    {
        var request = body.Deserialize<EvaluationRequest>(ContractJson.Options)
            ?? throw new InvalidEvaluationException("A JSON request object is required.");
        // Includes all incoming manifest fields, even fields not used by rules.
        // This is an exact JSON-text digest, not a canonical JSON signature.
        var requestDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body.GetRawText()))).ToLowerInvariant();
        var result = await evaluator.EvaluateAsync(request, requestDigest, cancellationToken);
        logger.LogInformation("Policy review {DecisionId}: workspace {WorkspaceId}, environment {Environment}, passing {Passing}, failing {Failing}, policy {PolicyDigest}, request {RequestDigest}",
            result.DecisionId, result.WorkspaceId, result.Environment, result.Items.Count(i => i.Approved), result.Items.Count(i => !i.Approved), result.PolicyDigest, result.RequestDigest);
        // Preserve the full submitted manifest, including fields not used by policy rules.
        // Reviews never filter artifacts or authorize a deployment.
        return Results.Ok(new
        {
            result.DecisionId, result.EvaluatedAt, result.WorkspaceId, result.Environment,
            result.PolicyVersion, result.PolicyDigest, result.RequestDigest,
            Manifest = body.EnumerateObject().Last(p => p.Name.Equals("manifest", StringComparison.OrdinalIgnoreCase)).Value,
            result.Findings, result.Items
        });
    }
    catch (InvalidEvaluationException exception)
    {
        return Results.Problem(statusCode: 400, title: "Invalid evaluation request", detail: exception.Message);
    }
    catch (JsonException)
    {
        return Results.Problem(statusCode: 400, title: "Request does not match the evaluation contract");
    }
}).RequireRateLimiting("evaluation");

app.Run();
