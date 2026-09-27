---
paths:
  - "tests/**"
---

# Tests

When you finish adding or changing tests, run the `test-cleanup-audit` skill on the diff before you
report done.

## Prove each new test fails when the code is wrong

A test that passes whatever the code does proves nothing. Before you report done, do this for
every test you add or change:

1. Break the production line the test guards: return the wrong value, skip the call, or flip
   the setting.
2. Run the test. It must fail.
3. Put the line back. Check with `git diff` that the file is byte-identical to before.

List each check in your report: the test, the line you broke, and the failure you saw. If a
test still passes, fix the test before you report done.

## Live-server tests skip without a word

`[PostgresFact]`, `[QdrantFact]`, and `[S3Fact]` skip when their variables are unset. A green
`dotnet test` then proves nothing about them. Name the variables to run them:

- `AGENTCORE_TEST_POSTGRES`: a connection string. The role must be allowed to create databases
  and roles.
- `AGENTCORE_TEST_QDRANT`: `host:port` of the **gRPC** port (6334), not 6333.
- `AGENTCORE_TEST_S3_ENDPOINT` and `AGENTCORE_TEST_S3_BUCKET`.

A full run against one Qdrant reports about 26 false `RpcException: Timeout expired` failures in
`KbShapedCorpusFixture`. The 30 s gRPC deadline expires under load. Re-run the failing class with
`--filter` before you blame your change. Run `pgrep -f testhost.dll` first: a second run against
the same Qdrant always times out.

## Conventions

- Drive time with `FakeTimeProvider` and `WaitForTimersAsync`. A `Task.Delay` settle loop is
  flaky.
- Mark a shared helper that asserts with `[AssertionMethod]` from `AgentCore.TestSupport`.
  SonarQube S2699 honours it by name.
