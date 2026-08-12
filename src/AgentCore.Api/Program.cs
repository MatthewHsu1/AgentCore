using System.Text.Json.Nodes;
using AgentCore.Application.Audit;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Secrets;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.Infrastructure.Knowledge;
using AgentCore.Infrastructure.Llm;
using AgentCore.Infrastructure.Secrets;
using AgentCore.Infrastructure.Tools;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var documentPath = builder.Configuration["AgentCore:ConfigurationPath"] ?? "config/example.yaml";

ChainedSecretResolver secrets = new([new EnvironmentSecretResolver(), new FileSecretResolver()]);

// One client for the life of the process.
HttpClient toolClient = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) });

// D26: observability is OTLP to Grafana Cloud. The exporter reads OTEL_EXPORTER_OTLP_ENDPOINT and
// OTEL_EXPORTER_OTLP_HEADERS, so it is registered only when the endpoint is set: a host with no
// collector must not retry into a socket that answers nothing.
//
// T61 binds the free tier at 10,000 active metric series, and two settings break it. Keep
// OTEL_METRIC_EXPORT_INTERVAL at 60000 ms or above, and put no call id on a metric attribute. The
// library keeps its own half of that rule; see AgentCoreTelemetry.
var exportsOtlp = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("AgentCore.Api"))
    .WithTracing(tracing =>
    {
        // Item 7: the turn span carries gen_ai.* attributes, and the call id rides here rather than
        // on a metric, where cardinality is free.
        tracing.AddSource(AgentCoreTelemetry.ActivitySourceName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();

        if (exportsOtlp)
        {
            tracing.AddOtlpExporter();
        }
    })
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(AgentCoreTelemetry.MeterName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();

        if (exportsOtlp)
        {
            metrics.AddOtlpExporter();
        }
    });

// The composition root binds the guard evaluator and the turn loop while the host starts, so it
// cannot resolve a logger from the container. This factory is built once and lives as long as the
// process does.
var agentCoreLoggers = LoggerFactory.Create(logging => logging
    .AddConfiguration(builder.Configuration.GetSection("Logging"))
    .AddConsole());

// The audit chain of D23 belongs in PostgreSQL, and that adapter is not written. This one keeps the
// events in this process, so chain_check has something to read and no event is silently lost.
InMemoryAuditSink auditSink = new();

builder.Services.AddAgentCore(options =>
{
    options.ConfigurationPath = documentPath;
    options.SecretResolver = secrets;
    options.LoggerFactory = agentCoreLoggers;
    options.AuditSink = auditSink;

    options.UseChatClients(startup => OpenAiChatClientFactory
        .CreateAsync(startup.Configuration, secrets)
        .AsTask()
        .GetAwaiter()
        .GetResult());

    // providers.knowledge names the store. This release ships the file-system one, and it reads the
    // root the document sets.
    options.UseKnowledge(startup => new FileSystemKnowledgeStore(startup.Configuration.Providers?.Knowledge));

    // kind: http. Every header resolved above, so no tool call costs a lookup.
    options.AddToolFactory(startup => new HttpToolFactory(toolClient, startup.Secrets));

    // kind: binding. The document writes binds: CreateCase and knows nothing else, so the host owns
    // what that name does. This one records the request and opens no case: there is no case system
    // wired to this host yet, and a delegate that pretended otherwise would be a silent failure.
    options.Bind("CreateCase", (arguments, _) => ValueTask.FromResult<object?>(new JsonObject
    {
        ["opened"] = false,
        ["summary"] = arguments["summary"]?.DeepClone(),
        ["reason"] = "this host has no case system bound. Register a CreateCase delegate that opens one.",
    }));
});

var app = builder.Build();

app.MapGet("/health", () => Results.Ok("ok"));

app.MapChatCompletions();

app.Run();
