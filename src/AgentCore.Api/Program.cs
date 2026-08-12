using System.Text.Json.Nodes;
using AgentCore.Application.Secrets;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.Infrastructure.Knowledge;
using AgentCore.Infrastructure.Llm;
using AgentCore.Infrastructure.Secrets;
using AgentCore.Infrastructure.Tools;

var builder = WebApplication.CreateBuilder(args);

var documentPath = builder.Configuration["AgentCore:ConfigurationPath"] ?? "config/example.yaml";

ChainedSecretResolver secrets = new([new EnvironmentSecretResolver(), new FileSecretResolver()]);

// One client for the life of the process.
HttpClient toolClient = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) });

builder.Services.AddAgentCore(options =>
{
    options.ConfigurationPath = documentPath;
    options.SecretResolver = secrets;

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
