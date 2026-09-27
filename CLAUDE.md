AgentCore is a .NET 10 library. It ships as NuGet packages, so every dependency lands on every consumer.

Rules for C#, comments, tests, and the Voice folder live in `.claude/rules/`. Each one loads when you open a file it covers.

## Build and test

- `dotnet build` fails on any warning. Fix the cause. Ask the owner before you add a `NoWarn`, a `#pragma warning disable`, or an `.editorconfig` severity change.
- `dotnet test` skips the Postgres, Qdrant, and S3 tests without a word when their `AGENTCORE_TEST_*` variables are unset. A green run does not cover them. See `.claude/rules/tests.md`.

## Before you add an interface, wrapper, helper, or package

Read `.claude/reference/reuse-before-you-build.md` first.

## Agent skills

### Issue tracker

Issues and specs live as GitHub issues in MatthewHsu1/AgentCore, used through the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

The five default labels: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: one `CONTEXT.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.
