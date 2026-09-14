# AgentCore

A .NET library for building voice and chat agents. The agent is a configuration document; the host is two calls.

## 1. Describe the agent

`config/helper.yaml`:

```yaml
apiVersion: agentcore/v1
name: helper

agents:
  defaults:
    model: { ref: reply }
    instructions: |
      You are a helpful assistant. Answer plainly and briefly.
  items:
    - id: helper
      instructions: |
        Answer whatever the person asks.
      tools: []

policy:
  initial: talk
  stages:
    - id: talk
      agent: helper
      terminal: true

providers:
  llm:
    - { kind: openai, model: gpt-5.6-luna, as: reply }
  call: { kind: telnyx-relay }
  speech:
    stt: { kind: telnyx-relay }
    tts: { kind: telnyx-relay }
```

`agents` holds the instructions, `policy` holds the stages, `providers` binds the vendors. No C# changes to add a stage, a tool, or a model.
[`config/example.yaml`](https://github.com/MatthewHsu1/AgentCore/blob/main/config/example.yaml)
is the annotated tour of every key.

## 2. Wire up the host

```csharp
using AgentCore.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.AddAgentCoreHost();

var app = builder.Build();
app.MapAgentCoreHost();
app.Run();
```

`AddAgentCoreHost` loads the document (`config/example.yaml` unless `AgentCore__ConfigurationPath` names another) and binds every vendor seam. `MapAgentCoreHost` serves the health check, the OpenAI-compatible Responses endpoint, and the call socket.
A document it cannot find is a startup failure, never a silent default.

## Install

```
dotnet add package AgentCore.Hosting
```

`AgentCore.Hosting` brings the rest of the set with it.

## 3. Talk to it

Over the OpenAI-compatible endpoint (`/v1/responses`), the document above holds this conversation:

```
User: Hello

Assistant: How can I help you today!
```

## The packages

| Package | Holds |
| --- | --- |
| `AgentCore.Domain` | Pure domain records. Zero dependencies. |
| `AgentCore.Application` | Orchestration, configuration compilation, and every port interface. |
| `AgentCore.Infrastructure` | Outbound adapters: OpenAI, Zilliz, Telnyx call control, Postgres, B2, Git. |
| `AgentCore.AspNetCore` | Inbound adapters as `Map*` extensions: Telnyx Conversation Relay, Telnyx webhooks, Responses. |
| `AgentCore.Hosting` | The batteries-included host: every vendor seam bound and every route mapped, in two calls. |

They share one version and ship as a set. Mixing versions across them is unsupported.

## Licence

MIT
