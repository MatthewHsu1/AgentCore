// The voice host. Section 6 gives the final shape:
//   AddAgentCore + MapTelnyxMedia + MapTelnyxWebhooks + MapChatCompletions.
// None of those exist yet, so this host only starts and answers a health probe.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok("ok"));

app.Run();
