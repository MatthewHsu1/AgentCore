using AgentCore.Chat;
using AgentCore.Hosting;

var builder = WebApplication.CreateBuilder(args);

await builder.AddAgentCoreHostAsync();

var app = builder.Build();

app.MapAgentCoreHost();

app.MapAgentCoreChat();

app.Run();
