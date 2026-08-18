using AgentCore.Hosting;

var builder = WebApplication.CreateBuilder(args);

await builder.AddAgentCoreHostAsync();

var app = builder.Build();

app.MapAgentCoreHost();

app.Run();
