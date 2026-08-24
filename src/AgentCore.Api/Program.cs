using AgentCore.Chat;
using AgentCore.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddAgentCoreHost();

var app = builder.Build();

app.MapAgentCoreHost();

app.MapAgentCoreChat();

app.Run();
