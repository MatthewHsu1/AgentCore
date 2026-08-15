using System.Text.Json.Nodes;
using AgentCore.Application.Audit;
using AgentCore.Application.Secrets;
using AgentCore.AspNetCore.Call;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.AspNetCore.Vendors.TelnyxRelay;
using AgentCore.Infrastructure.Evaluation.OpenAiModeration;
using AgentCore.Infrastructure.Knowledge.FileStore;
using AgentCore.Infrastructure.Knowledge.VectorData.Zilliz;
using AgentCore.Infrastructure.Llm.OpenAI;
using AgentCore.Infrastructure.Secrets;
using AgentCore.Infrastructure.Telemetry.Grafana;
using AgentCore.Infrastructure.Tools;

var builder = WebApplication.CreateBuilder(args);

var documentPath = builder.Configuration["AgentCore:ConfigurationPath"] ?? "config/example.yaml";

ChainedSecretResolver secrets = new([new EnvironmentSecretResolver(), new FileSecretResolver()]);

// The composition root binds the guard evaluator and the turn loop while the host starts, so it
// cannot resolve a logger from the container. This factory is built once and lives as long as the
// process does. The telemetry seam below adds its own provider to it, so a line reaches the console
// and the collector both.
var agentCoreLoggers = LoggerFactory.Create(logging => logging
    .AddConfiguration(builder.Configuration.GetSection("Logging"))
    .AddConsole());

// One outbound HTTP pipeline for the life of the process. It holds the connection lifetime, the
// deadline, and the retry, so no vendor adapter holds a policy of its own. It is built here rather
// than registered, because AddAgentCoreAsync builds every adapter before builder.Build() runs and
// there is no provider to resolve a client from at that moment. Registering it is what closes it
// when the host stops.
AgentCoreHttpClients httpClients = new(loggers: agentCoreLoggers);
builder.Services.AddSingleton(httpClients);

// The audit chain of D23 belongs in PostgreSQL, and that adapter is not written. This one keeps the
// events in this process, so chain_check has something to read and no event is silently lost.
InMemoryAuditSink auditSink = new();

await builder.Services.AddAgentCoreAsync(options =>
{
    options.ConfigurationPath = documentPath;
    options.SecretResolver = secrets;
    options.LoggerFactory = agentCoreLoggers;
    options.AuditSink = auditSink;

    // The host lists the vendors it supports, once. providers.llm[].kind picks the adapter for each
    // entry, so a document that changes vendors changes no code here.
    options.UseChatClients(new OpenAiChatClientAdapter());

    // The host lists the knowledge vendors it supports, once, exactly as the llm seam above.
    // providers.knowledge.search and providers.knowledge.documents pick the adapter for each port,
    // so a document that changes stores changes no code here. Registering zilliz costs nothing until
    // a document names it: an adapter no field names is never asked to build anything, so this host
    // still starts with no cluster and no key.
    options.UseKnowledgeStores(new FileSystemKnowledgeAdapter(), new ZillizKnowledgeAdapter(httpClients));

    // The host lists the moderation vendors it supports, once, exactly as the two seams above.
    // providers.moderation.kind picks the adapter, so a document that changes vendors changes no code
    // here, and a document that names none moderates nothing and reads no key. Moderation reads what
    // the caller said BEFORE the model runs: a flagged turn speaks refusalReply and never reaches the
    // model. That is the owner's decision of 2026-08-13, and it departs from section 11 item 11.
    options.UseModeration(new OpenAiModerationAdapter(httpClients));

    // The host lists the telemetry vendors it supports, once, exactly as the three seams above.
    // providers.telemetry.kind picks the adapter, so a document that changes collectors changes no
    // code here. A document that names none exports nothing and reads no key: spans and measurements
    // are still written, where they cost almost nothing with nobody listening, and log lines keep
    // going to the console alone. D26 makes Grafana Cloud the destination, and that vendor's basic
    // credential resolves through the chain above under the two grafana-cloud-* names.
    options.UseTelemetry(new GrafanaOtlpTelemetryAdapter());

    // The host lists the call transports it supports, once, exactly as the seams above.
    // providers.call.kind picks one, and app.MapCall() below maps its socket. This vendor carries
    // text rather than audio — D28 buys recognition, turn detection, synthesis, and interruption
    // inside the relay — so providers.speech.kind must name it too, and AddAgentCoreAsync refuses
    // a document where the two disagree.
    options.UseCall(new TelnyxRelayCallAdapter());

    // The speech vendor. Bundled today, so this opens nothing and reads no key: it is a name, and
    // the socket is the channel. A SIP transport is what makes this seam do real work.
    options.UseSpeech(new TelnyxRelaySpeechAdapter());

    // kind: http. Every header resolved above, so no tool call costs a lookup.
    options.AddToolFactory(startup => new HttpToolFactory(httpClients.CreateClient(HttpToolFactory.HttpClientName), startup.Secrets));

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

// The relay socket is the inbound path of a real call, and a dead peer must not hold a session for
// the shipped two-minute default. This sets the twenty-second keep-alive numbers a call needs.
builder.Services.AddAgentCoreWebSockets();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok("ok"));

// The WebSocket middleware is the host's job, not the library's, so it runs before any endpoint
// that upgrades a request. The no-argument overload is required: the overload that takes a
// WebSocketOptions instance ignores the AddAgentCoreWebSockets registration entirely.
app.UseWebSockets();

app.MapChatCompletions();

// One inbound call route, and this line names no vendor. It reads providers.call, picks the
// transport the document names, and hands it the socket limits from the same block. Changing
// vendors is a document edit.
app.MapCall();

app.Run();
