import assert from "node:assert/strict";
import { test } from "vitest";
import {
  SessionHeader,
  readEvent,
  runTurn,
  splitEvents,
  wireMessages,
  type FetchLike,
  type Session,
} from "./transport.ts";

/**
 * What the browser does over the wire.
 *
 * No network and no browser: every test drives `runTurn` with a fetch of its own, so the failures
 * these cover — a half-read event, a session id kept past the end of its call, a 404 for a call the
 * host forgot — are reproduced exactly rather than waited for.
 */

// -------------------------------------------------------------------------------------------------
// Fakes.
// -------------------------------------------------------------------------------------------------

/** One recorded request. */
type Sent = { session: string | null; body: unknown };

/** Builds a response whose body arrives in exactly the pieces given. */
function streaming(pieces: string[], headers: Record<string, string> = {}): Response {
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      const encoder = new TextEncoder();
      for (const piece of pieces) {
        controller.enqueue(encoder.encode(piece));
      }
      controller.close();
    },
  });

  return new Response(body, { status: 200, headers });
}

/** Builds one refusal in the shape the endpoint writes. */
function refusal(status: number, message: string, code?: string): Response {
  return new Response(JSON.stringify({ error: { message, code } }), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

/** A fetch that answers from a script and records what it was asked. */
function scripted(responses: Response[]): { fetch: FetchLike; sent: Sent[] } {
  const sent: Sent[] = [];
  let index = 0;

  const fetch: FetchLike = (_url, init) => {
    const headers = (init.headers ?? {}) as Record<string, string>;
    sent.push({
      session: headers[SessionHeader] ?? null,
      body: JSON.parse(String(init.body)),
    });

    const response = responses[index++];
    assert.ok(response, `the test scripted ${responses.length} answers and got one more request.`);
    return Promise.resolve(response);
  };

  return { fetch, sent };
}

/** One data event. */
function event(payload: unknown): string {
  return `data: ${JSON.stringify(payload)}\n\n`;
}

/** One chunk that carries text. */
function delta(text: string): string {
  return event({ choices: [{ delta: { content: text }, finish_reason: null }] });
}

/** Runs one turn to the end and collects every yield. */
async function collect(
  responses: Response[],
  session: Session,
  messages: { role: string; content: string }[] = [{ role: "user", content: "hi" }],
): Promise<{ yields: string[]; sent: Sent[] }> {
  const { fetch, sent } = scripted(responses);
  const yields: string[] = [];

  for await (const state of runTurn({
    endpoint: "/v1/chat/completions",
    session,
    messages: wireMessages(messages),
    abortSignal: new AbortController().signal,
    fetch,
  })) {
    yields.push(state.text);
  }

  return { yields, sent };
}

// -------------------------------------------------------------------------------------------------
// The event reader. These are the pieces that a chunk boundary can break.
// -------------------------------------------------------------------------------------------------

test("splitEvents keeps an incomplete event back for the next read", () => {
  const { events, rest } = splitEvents("data: one\n\ndata: tw");

  assert.deepEqual(events, ["data: one"]);
  assert.equal(rest, "data: tw");
});

test("splitEvents returns nothing when no event is complete yet", () => {
  const { events, rest } = splitEvents("data: par");

  assert.deepEqual(events, []);
  assert.equal(rest, "data: par");
});

test("readEvent ignores the terminator", () => {
  assert.equal(readEvent("data: [DONE]"), null);
});

test("readEvent ignores a line that is not data", () => {
  assert.equal(readEvent(": keep-alive"), null);
});

// -------------------------------------------------------------------------------------------------
// The request body.
// -------------------------------------------------------------------------------------------------

test("wireMessages drops a message left with no text", () => {
  const wire = wireMessages([
    { role: "user", content: "hello" },
    { role: "assistant", content: "" },
  ]);

  assert.deepEqual(wire, [{ role: "user", content: "hello" }]);
});

// -------------------------------------------------------------------------------------------------
// The session, which is the whole reason this file exists.
// -------------------------------------------------------------------------------------------------

test("the first turn names no session and keeps the one the host answers with", async () => {
  const session: Session = { current: null };

  const { sent } = await collect(
    [streaming([delta("hi")], { [SessionHeader]: "call-1" })],
    session,
  );

  assert.equal(sent[0]!.session, null, "a first turn must not name a call that does not exist.");
  assert.equal(session.current, "call-1");
});

test("the next turn sends the session back", async () => {
  const session: Session = { current: "call-1" };

  const { sent } = await collect([streaming([delta("again")])], session);

  assert.equal(sent[0]!.session, "call-1");
});

test("a terminal turn lets the call go, so the next one opens a new call", async () => {
  // Holding the id past the end of a call answers the next message with a 409, and the UI would
  // show a chat that refuses everything from then on.
  const session: Session = { current: null };

  await collect(
    [
      streaming(
        [
          delta("goodbye"),
          event({ choices: [{ delta: {}, finish_reason: "stop" }], agentcore: { is_terminal: true } }),
        ],
        { [SessionHeader]: "call-1" },
      ),
    ],
    session,
  );

  assert.equal(session.current, null);
});

test("a non-terminal turn keeps the call", async () => {
  const session: Session = { current: null };

  await collect(
    [
      streaming(
        [
          delta("still here"),
          event({ choices: [{ delta: {}, finish_reason: "stop" }], agentcore: { is_terminal: false } }),
        ],
        { [SessionHeader]: "call-1" },
      ),
    ],
    session,
  );

  assert.equal(session.current, "call-1");
});

test("a call the host has forgotten is started again rather than failing the turn", async () => {
  // The default session store does not survive a restart, so this is what every open tab sees the
  // first time somebody redeploys.
  const session: Session = { current: "gone" };

  const { yields, sent } = await collect(
    [
      refusal(404, "no call named 'gone' is open on this host.", "session_not_found"),
      streaming([delta("fresh start")], { [SessionHeader]: "call-2" }),
    ],
    session,
  );

  assert.equal(sent.length, 2);
  assert.equal(sent[0]!.session, "gone");
  assert.equal(sent[1]!.session, null, "the retry must not name the call that is gone.");
  assert.equal(session.current, "call-2");
  assert.deepEqual(yields, ["fresh start"]);
});

test("a 404 that is not a lost session is not retried", async () => {
  const session: Session = { current: "call-1" };

  await assert.rejects(
    () => collect([refusal(404, "the route is not mapped here.")], session),
    /the route is not mapped here/,
  );
});

test("a refused turn surfaces what the host said", async () => {
  const session: Session = { current: "call-1" };

  await assert.rejects(
    () => collect([refusal(409, "this call is finished.", "turn_refused")], session),
    /this call is finished/,
  );
});

// -------------------------------------------------------------------------------------------------
// The reply.
// -------------------------------------------------------------------------------------------------

test("the reply grows with each piece", async () => {
  const session: Session = { current: null };

  const { yields } = await collect(
    [streaming([delta("Hel"), delta("lo "), delta("there")])],
    session,
  );

  assert.deepEqual(yields, ["Hel", "Hello ", "Hello there"]);
});

test("an event split across two reads is still read once, whole", async () => {
  // The regression this catches loses whole words silently, and only under load: the split happens
  // wherever the network happened to break the stream.
  const session: Session = { current: null };
  const whole = delta("split me");
  const at = Math.floor(whole.length / 2);

  const { yields } = await collect(
    [streaming([whole.slice(0, at), whole.slice(at)])],
    session,
  );

  assert.deepEqual(yields, ["split me"]);
});

test("two events arriving in one read are both read", async () => {
  const session: Session = { current: null };

  const { yields } = await collect([streaming([delta("one") + delta("two")])], session);

  assert.deepEqual(yields, ["one", "onetwo"]);
});

test("the terminator ends the stream without becoming text", async () => {
  const session: Session = { current: null };

  const { yields } = await collect([streaming([delta("done"), "data: [DONE]\n\n"])], session);

  assert.deepEqual(yields, ["done"]);
});

test("a chunk that carries no text yields nothing", async () => {
  const session: Session = { current: null };

  const { yields } = await collect(
    [streaming([event({ choices: [{ delta: { role: "assistant" } }] }), delta("after")])],
    session,
  );

  assert.deepEqual(yields, ["after"]);
});

test("runTurn yields data parts alongside the text", async () => {
  const events = [
    'data: {"choices":[{"delta":{"content":"here it is"}}]}\n\n',
    'data: {"agentcore_data":{"name":"chart","data":{"title":"Q3"}}}\n\n',
    "data: [DONE]\n\n",
  ];

  const session: Session = { current: null };
  const states = [];
  for await (const state of runTurn({
    endpoint: "/v1/chat/completions",
    session,
    messages: [{ role: "user", content: "chart it" }],
    abortSignal: new AbortController().signal,
    fetch: scripted([streaming(events)]).fetch,
  })) {
    states.push(state);
  }

  const last = states[states.length - 1];
  assert.equal(last.text, "here it is");
  assert.deepEqual(last.data, [{ name: "chart", data: { title: "Q3" } }]);
});

test("a data part survives a later text-only yield", async () => {
  // The runtime replaces message content on every yield, so a state that forgot the drawing would
  // blank it from the screen the moment the model spoke again.
  const events = [
    'data: {"agentcore_data":{"name":"chart","data":{"title":"Q3"}}}\n\n',
    'data: {"choices":[{"delta":{"content":"and that is why"}}]}\n\n',
    "data: [DONE]\n\n",
  ];

  const session: Session = { current: null };
  const states = [];
  for await (const state of runTurn({
    endpoint: "/v1/chat/completions",
    session,
    messages: [{ role: "user", content: "chart it" }],
    abortSignal: new AbortController().signal,
    fetch: scripted([streaming(events)]).fetch,
  })) {
    states.push(state);
  }

  const last = states[states.length - 1];
  assert.equal(last.text, "and that is why");
  assert.deepEqual(last.data, [{ name: "chart", data: { title: "Q3" } }]);
});

// -------------------------------------------------------------------------------------------------
// Tool calls. The host runs the tool, so both halves arrive as facts and neither asks for anything.
// -------------------------------------------------------------------------------------------------

/** Runs one turn over the events given and answers every state it yielded. */
async function states(events: string[]) {
  const collected = [];
  for await (const state of runTurn({
    endpoint: "/v1/chat/completions",
    session: { current: null },
    messages: [{ role: "user", content: "look it up" }],
    abortSignal: new AbortController().signal,
    fetch: scripted([streaming(events)]).fetch,
  })) {
    collected.push(state);
  }

  return collected;
}

test("runTurn yields a tool call before its result arrives", async () => {
  // The point of showing a tool at all is showing it while it runs, so the call half must reach the
  // screen on its own rather than waiting to be paired with a result.
  const collected = await states([
    'data: {"agentcore_tool":{"call_id":"c1","name":"read_records","phase":"call","arguments":{"what":"revenue"}}}\n\n',
    "data: [DONE]\n\n",
  ]);

  const last = collected[collected.length - 1];
  assert.deepEqual(last.tools, [
    { callId: "c1", name: "read_records", arguments: { what: "revenue" } },
  ]);
});

test("runTurn keeps a tool answer that is an object as an object", async () => {
  // The host writes the answer as JSON rather than as text, so that the quotes inside it are not
  // escaped and the panel can lay it out. A result read as a string would print those escapes raw.
  const collected = await states([
    'data: {"agentcore_tool":{"call_id":"c1","name":"read_records","phase":"call","arguments":{}}}\n\n',
    'data: {"agentcore_tool":{"call_id":"c1","name":"read_records","phase":"result","result":{"entities":["it\'s"]},"failed":false}}\n\n',
    "data: [DONE]\n\n",
  ]);

  const last = collected[collected.length - 1];
  assert.deepEqual(last.tools[0].result, { entities: ["it's"] });
});

test("runTurn folds a tool result onto the call it answers", async () => {
  const collected = await states([
    'data: {"agentcore_tool":{"call_id":"c1","name":"read_records","phase":"call","arguments":{"what":"revenue"}}}\n\n',
    'data: {"agentcore_tool":{"call_id":"c1","name":"read_records","phase":"result","result":"42 rows","failed":false}}\n\n',
    'data: {"choices":[{"delta":{"content":"there are 42."}}]}\n\n',
    "data: [DONE]\n\n",
  ]);

  const last = collected[collected.length - 1];
  assert.equal(last.text, "there are 42.");
  assert.deepEqual(last.tools, [
    {
      callId: "c1",
      name: "read_records",
      arguments: { what: "revenue" },
      result: "42 rows",
      failed: false,
    },
  ]);
});

test("runTurn keeps a failed tool marked as failed", async () => {
  const collected = await states([
    'data: {"agentcore_tool":{"call_id":"c1","name":"read_records","phase":"call","arguments":{}}}\n\n',
    'data: {"agentcore_tool":{"call_id":"c1","name":"read_records","phase":"result","result":"the table is gone","failed":true}}\n\n',
    "data: [DONE]\n\n",
  ]);

  const last = collected[collected.length - 1];
  assert.equal(last.tools[0]?.failed, true);
  assert.equal(last.tools[0]?.result, "the table is gone");
});

test("runTurn keeps two tool calls apart and pairs each with its own result", async () => {
  const collected = await states([
    'data: {"agentcore_tool":{"call_id":"c1","name":"read_records","phase":"call","arguments":{}}}\n\n',
    'data: {"agentcore_tool":{"call_id":"c2","name":"aggregate_records","phase":"call","arguments":{}}}\n\n',
    'data: {"agentcore_tool":{"call_id":"c2","name":"aggregate_records","phase":"result","result":"second","failed":false}}\n\n',
    'data: {"agentcore_tool":{"call_id":"c1","name":"read_records","phase":"result","result":"first","failed":false}}\n\n',
    "data: [DONE]\n\n",
  ]);

  const last = collected[collected.length - 1];
  assert.deepEqual(
    last.tools.map((tool) => [tool.callId, tool.result]),
    [
      ["c1", "first"],
      ["c2", "second"],
    ],
  );
});

test("a tool survives a later text-only yield", async () => {
  // Same rule as a drawing: the runtime replaces message content on every yield, so a state that
  // forgot the tool would blank it the moment the model spoke.
  const collected = await states([
    'data: {"agentcore_tool":{"call_id":"c1","name":"read_records","phase":"call","arguments":{}}}\n\n',
    'data: {"choices":[{"delta":{"content":"one moment"}}]}\n\n',
    "data: [DONE]\n\n",
  ]);

  const last = collected[collected.length - 1];
  assert.equal(last.text, "one moment");
  assert.equal(last.tools.length, 1);
});

test("a result for a call that never arrived is kept rather than dropped", async () => {
  // It should not happen. If it ever does, showing the answer with no question beats showing
  // nothing at all and leaving the caller wondering what the wait was for.
  const collected = await states([
    'data: {"agentcore_tool":{"call_id":"c9","name":"read_records","phase":"result","result":"orphan","failed":false}}\n\n',
    "data: [DONE]\n\n",
  ]);

  const last = collected[collected.length - 1];
  assert.deepEqual(last.tools, [
    { callId: "c9", name: "read_records", arguments: {}, result: "orphan", failed: false },
  ]);
});

test("a turn that calls no tool yields an empty tool list", async () => {
  const collected = await states([
    'data: {"choices":[{"delta":{"content":"hello"}}]}\n\n',
    "data: [DONE]\n\n",
  ]);

  assert.deepEqual(collected[collected.length - 1].tools, []);
});
