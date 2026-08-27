# AgentCore

A .NET library for building voice and chat agents. You bring the host; AgentCore brings the turn
loop, the vendor seams, and the configuration document that drives them.

## Install

```
dotnet add package AgentCore.Hosting
```

`AgentCore.Hosting` brings the rest of the set with it.

## Use

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddAgentCoreHost();

var app = builder.Build();
app.MapAgentCoreHost();
app.Run();
```

AgentCore reads `config/example.yaml` unless `AgentCore__ConfigurationPath` names another document.
A document it cannot find is a startup failure, never a silent default.

[`demo/AgentCore.Demo/config/example.yaml`](https://github.com/MatthewHsu1/AgentCore/blob/main/demo/AgentCore.Demo/config/example.yaml)
is the annotated tour of every key.

## The packages

| Package | Holds |
| --- | --- |
| `AgentCore.Domain` | Pure domain records. Zero dependencies. |
| `AgentCore.Application` | Orchestration, configuration compilation, and every port interface. |
| `AgentCore.Infrastructure` | Outbound adapters: OpenAI, Zilliz, Telnyx call control, Postgres, B2, Git. |
| `AgentCore.AspNetCore` | Inbound adapters as `Map*` extensions: Telnyx Conversation Relay, Telnyx webhooks, chat completions. |
| `AgentCore.Hosting` | The batteries-included host: every vendor seam bound and every route mapped, in two calls. |

They share one version and ship as a set. Mixing versions across them is unsupported.

## The demo

[`demo/`](https://github.com/MatthewHsu1/AgentCore/tree/main/demo) holds a working host and a React
chat UI. Neither is published. It is how you watch the agent work, and the starting point you copy
when writing a UI of your own.

```
export OPENAI_API_KEY=sk-...
cd demo/AgentCore.Demo && dotnet run --launch-profile demo
```

The `demo` profile runs
[`config/demo.yaml`](https://github.com/MatthewHsu1/AgentCore/blob/main/demo/AgentCore.Demo/config/demo.yaml),
the smallest document that holds a real conversation: it needs only an OpenAI key. The `example`
profile runs the annotated reference instead, which names tools and vendors this repository cannot
give you a key for, and will not start.

Node is required to build the demo's UI.

## Licence

MIT
