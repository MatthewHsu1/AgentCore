# AgentCore on Fly.io

## Setup

**1. Copy the two files into an empty repository.**

```bash
mkdir my-voice-agent && cd my-voice-agent
git init
cp /path/to/agentcore-flyio-starter/fly.toml .
cp /path/to/agentcore-flyio-starter/config.yaml .
```

**2. Edit `fly.toml`.** Change `app` to a name nobody else has taken, and `primary_region` to the
region nearest your callers. Fly's region list is at <https://fly.io/docs/reference/regions/>.

**3. Edit `config.yaml`.** The instructions under `agents:` are what the caller hears. Everything
else runs as shipped.

**4. Create the app.**

```bash
fly launch --no-deploy --copy-config --name my-voice-agent
```

`--copy-config` keeps the `fly.toml` you just edited. Without it, `fly launch` writes its own.

**5. Set the keys.**

```bash
fly secrets set OPENAI_API_KEY=sk-...
```

Fly stores these encrypted and injects them as environment variables. They never enter `fly.toml`
and never enter git.

**6. Deploy.**

```bash
fly deploy
```

**7. Check it.**

```bash
curl https://my-voice-agent.fly.dev/health     # -> "ok"
```

Open `https://my-voice-agent.fly.dev/chat` to talk to the agent by text before you point a phone
number at it.

**8. Point your call vendor at the socket.** The call WebSocket is:

```
wss://my-voice-agent.fly.dev/v1/call
```

## Routes

| Route | What it is |
|---|---|
| `/health` | Liveness. Returns `ok`. |
| `/chat` | A chat UI for testing the agent by text. |
| `/v1/chat/completions` | OpenAI-compatible text endpoint. |
| `/v1/call` | The call vendor's WebSocket. |

## Secrets

A document writes `${secret:some-name}` and never a value. AgentCore resolves the name in this
order, and stops at the first hit:

1. An environment variable — `some-name`, then `SOME_NAME`.
2. A file at `/run/secrets/some-name`.
3. The `AgentCore:Secrets` configuration section.

## Updating

Change one line in `fly.toml`:

```toml
image = "ghcr.io/example"
```

Then `fly deploy`. Your `config.yaml` and your secrets are untouched.


To roll back, put the old tag back and deploy again, or run `fly releases` and `fly deploy --image`
with the tag you want.

## A knowledge base

`config.yaml` here declares no knowledge provider, so the agent answers from its instructions alone.
Two ways to give it documents:

**A Fly volume.** Add to `fly.toml`:

```toml
[[mounts]]
  source = "kb"
  destination = "/app/kb"
```

and to `config.yaml`:

```yaml
providers:
  knowledge: { search: filesystem, documents: filesystem, root: /app/kb }
```

Create it with `fly volumes create kb --size 1`, then load files with `fly ssh sftp shell`. A volume
lives on one machine, so every machine that serves calls needs its own copy.

Delete the `[build] image` line from `fly.toml` and `fly deploy` builds this instead. The knowledge
base ships with the release, every machine has it, and updating AgentCore means bumping the `FROM`
line. This is the simpler of the two unless your documents change more often than your code.

## When it does not start

AgentCore validates the whole document at startup and refuses to run a bad one. The error names the
exact path and the check number:

```
The configuration document did not load. /policy/stages/1/to/1/when: the guard 'done' and the
guard 'wantsHuman' are both true at the same time. (check 5)
```

Read it with `fly logs`. A refusal to start is the design: a document that half-loads would put a
wrong agent on a real phone line.
