/**
 * Everything the chat UI does over the wire, with no React and no assistant-ui in sight.
 *
 * This file is separate from {@link ./AgentCoreRuntime.ts} so it can be tested. The parts that are
 * easy to get wrong here — a server-sent event split across two reads, a session id that outlives
 * the call it names, a 404 for a call the host forgot — all fail in ways a browser shows as a chat
 * that simply stops working, with nothing in any log. The hook is a thin wrapper over this.
 */

/** The header that names the call, on the request and on the answer. */
export const SessionHeader = "X-AgentCore-Session";

/**
 * The header naming the stage the turn speaks in.
 *
 * It arrives with the response headers, before the first token, which is the only reason the stage
 * can be shown while the turn is still running: the `agentcore` block that carries `stage_after`
 * rides the *last* chunk, so it says where the machine ended up, never where it is.
 */
export const StageHeader = "X-AgentCore-Stage";

/** The prefix every server-sent event carries. */
const DataPrefix = "data: ";

/** The event that closes a stream. */
const DoneEvent = "[DONE]";

/** One message, in the only shape AgentCore's endpoint reads. */
export type WireMessage = {
  readonly role: string;
  readonly content: string;
};

/**
 * Who produced a message, when that is not simply "the agent".
 *
 * Nothing sends this yet. It is declared here so the browser already has somewhere to put a human
 * rep the day AgentCore can hand a conversation to one: the wire, the runtime and the message
 * metadata are the three places that would otherwise all need changing at once, under time
 * pressure, while a customer is waiting on the other end of a live handoff.
 *
 * The contract AgentCore would emit, beside the OpenAI shape and alongside `agentcore`:
 *
 *     "agentcore_speaker": { "kind": "human", "name": "Dana R.", "detail": "Support" }
 */
export type Speaker = {
  /** `agent` is the model, `human` a real person, `system` the host speaking for itself. */
  readonly kind: "agent" | "human" | "system";
  readonly name: string;
  /** A role, team, or anything else worth showing under the name. Optional. */
  readonly detail?: string;
};

/** One thing the host asked the browser to draw. */
export type RenderPart = {
  readonly name: string;
  readonly data: unknown;
};

/** Anything `JSON.parse` can produce. */
export type JsonValue =
  | string
  | number
  | boolean
  | null
  | readonly JsonValue[]
  | { readonly [key: string]: JsonValue };

/** A JSON object, which is the only shape a tool's arguments ever take. */
export type JsonObject = { readonly [key: string]: JsonValue };

/**
 * One tool the host ran, with its result once it has one.
 *
 * Both halves of a call arrive as separate chunks and are folded together here, because the screen
 * wants one row per tool that fills in, and not two rows that have to be matched up by whoever draws
 * them. A part with no `result` yet is a tool still running.
 *
 * This is a *report* and never a request. AgentCore owns the tool loop, so nothing the browser does
 * with this can make a tool run, and the OpenAI `tool_calls` field — which does mean "you run this"
 * — is deliberately not what carries it.
 */
export type ToolPart = {
  readonly callId: string;
  readonly name: string;
  readonly arguments: JsonObject;
  /** What the tool answered, in whatever shape it answered in. Absent while it is still running. */
  readonly result?: unknown;
  /** Whether the tool failed. Absent while it is still running. */
  readonly failed?: boolean;
};

/** Everything one turn has produced so far. */
export type TurnState = {
  readonly text: string;
  readonly data: readonly RenderPart[];
  /** Every tool this turn has called, in call order, each with its result once it has one. */
  readonly tools: readonly ToolPart[];
  /** The stage the pipeline is in: the turn's own stage, then the stage it moved to at the end. */
  readonly stage: string | null;
  /** Whether the stage the turn moved to ends the call. Only ever true on the final state. */
  readonly isTerminal: boolean;
  /** Who is speaking, when the host says. `null` means the agent, which is the only case today. */
  readonly speaker: Speaker | null;
};

/** The id of the open call, held for the life of the tab. */
export type Session = {
  current: string | null;
};

/** The part of `fetch` this module uses, so a test can hand it one that reaches no network. */
export type FetchLike = (url: string, init: RequestInit) => Promise<Response>;

/** What one turn needs to run. */
export type TurnOptions = {
  readonly endpoint: string;
  readonly session: Session;
  readonly messages: readonly WireMessage[];
  readonly abortSignal: AbortSignal;
  readonly fetch: FetchLike;
};

/** What the endpoint adds beside the OpenAI shape on the last chunk of a stream. */
type TurnInfo = {
  session?: string;
  stage_before?: string;
  stage_after?: string;
  is_terminal?: boolean;
};

/** One half of one tool call, as the endpoint writes it. */
type ToolFrame = {
  call_id?: string;
  name?: string;
  phase?: string;
  /** Typed rather than `unknown` because it comes off `JSON.parse` of a field the host writes as an object. */
  arguments?: JsonObject;
  /** The host writes the answer as JSON, so this is an object as often as it is a string. */
  result?: unknown;
  failed?: boolean;
};

/** One chunk of a streamed answer. */
type StreamChunk = {
  choices?: { delta?: { content?: string }; finish_reason?: string | null }[];
  agentcore?: TurnInfo;
  agentcore_tool?: ToolFrame;
  agentcore_data?: RenderPart;
  agentcore_speaker?: Speaker;
};

/** The body of one refusal. */
type WireError = {
  error?: { message?: string; code?: string };
};

/**
 * Drops everything the endpoint cannot read.
 *
 * AgentCore's endpoint reads text and no other content part, and answers a request with no user
 * text at all with a 400. A message left empty by that filter would be one of those, so it goes.
 */
export function wireMessages(
  messages: readonly { role: string; content: string }[],
): WireMessage[] {
  return messages
    .map((message) => ({ role: message.role, content: message.content }))
    .filter((message) => message.content.length > 0);
}

/**
 * Splits whatever has arrived into whole events and whatever is still incomplete.
 *
 * An event ends at a blank line and a read can end anywhere, so the tail of a read is very often
 * half an event. Returning it rather than parsing it is the whole job.
 *
 * @param buffer Everything read and not yet parsed.
 * @returns The complete events, and the remainder to prepend to the next read.
 */
export function splitEvents(buffer: string): { events: string[]; rest: string } {
  const parts = buffer.split("\n\n");
  const rest = parts.pop() ?? "";
  return { events: parts, rest };
}

/**
 * Reads one event.
 *
 * @param event One event, as it arrived.
 * @returns The chunk, or `null` for the terminator and for anything that is not a data line.
 */
export function readEvent(event: string): StreamChunk | null {
  const line = event.trim();
  if (!line.startsWith(DataPrefix)) {
    return null;
  }

  const payload = line.slice(DataPrefix.length);
  if (payload === DoneEvent) {
    return null;
  }

  return JSON.parse(payload) as StreamChunk;
}

/** Reads the failure the endpoint wrote, or falls back to the status line. */
async function failureOf(response: Response): Promise<{ message: string; code?: string }> {
  try {
    const body = (await response.json()) as WireError;
    if (body.error?.message) {
      return { message: body.error.message, code: body.error.code };
    }
  } catch {
    // A body that is not the documented shape tells us nothing the status line does not.
  }

  return { message: `the request failed with status ${response.status}.` };
}

/** Posts one turn. */
function post(options: TurnOptions, session: string | null): Promise<Response> {
  const headers: Record<string, string> = { "Content-Type": "application/json" };
  if (session) {
    headers[SessionHeader] = session;
  }

  return options.fetch(options.endpoint, {
    method: "POST",
    headers,
    body: JSON.stringify({ messages: options.messages, stream: true }),
    signal: options.abortSignal,
  });
}

/**
 * Folds one tool frame into the list the state carries.
 *
 * @param tools What the turn has called so far.
 * @param frame The half that just arrived.
 * @returns A new list. Never the one passed in: the state yielded before this is read after it, so
 * a list mutated in place would change under a consumer that already has it.
 */
export function foldTool(
  tools: readonly ToolPart[],
  frame: ToolFrame,
): readonly ToolPart[] {
  const callId = frame.call_id;
  if (!callId) {
    return tools;
  }

  const name = frame.name ?? callId;

  if (frame.phase !== "result") {
    return [...tools, { callId, name, arguments: frame.arguments ?? {} }];
  }

  const answered: ToolPart = {
    callId,
    name,
    arguments: {},
    result: frame.result ?? "",
    failed: frame.failed ?? false,
  };

  const index = tools.findIndex((tool) => tool.callId === callId);
  if (index < 0) {
    // A result with no call before it should not happen. Showing the answer with no question still
    // beats showing nothing and leaving the caller wondering what the wait was for.
    return [...tools, answered];
  }

  const merged = [...tools];
  merged[index] = { ...tools[index], ...answered, arguments: tools[index].arguments };
  return merged;
}

/**
 * Runs one turn and yields the reply as it grows.
 *
 * Each yield is the whole reply so far rather than the newest piece, because that is what the
 * runtime above renders.
 *
 * @param options What the turn needs.
 * @returns The reply, yielded once per piece that carries text or something to draw.
 * @throws Error The host refused the turn, or answered with no body.
 */
export async function* runTurn(options: TurnOptions): AsyncGenerator<TurnState> {
  const { session } = options;

  let response = await post(options, session.current);

  // The store does not survive a restart of the host, so an id from before one names a call that is
  // gone. Starting a new call is what the caller wanted; failing the turn over an id they never saw
  // is not.
  if (response.status === 404) {
    const failure = await failureOf(response);
    if (failure.code !== "session_not_found") {
      throw new Error(failure.message);
    }

    session.current = null;
    response = await post(options, null);
  }

  if (!response.ok) {
    throw new Error((await failureOf(response)).message);
  }

  // The answer names the call whether it started one or continued one, and the header arrives
  // before the first token does.
  const named = response.headers.get(SessionHeader);
  if (named) {
    session.current = named;
  }

  if (!response.body) {
    throw new Error("the host answered with no body, so there is nothing to read.");
  }

  // Named before the first token, so the very first yield already knows the stage.
  let stage = response.headers.get(StageHeader);
  let isTerminal = false;
  let speaker: Speaker | null = null;

  const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
  let pending = "";
  let text = "";
  let data: RenderPart[] = [];
  let tools: readonly ToolPart[] = [];

  try {
    for (; ;) {
      const { done, value } = await reader.read();
      if (done) {
        break;
      }

      pending += value;

      const { events, rest } = splitEvents(pending);
      pending = rest;

      for (const event of events) {
        const chunk = readEvent(event);
        if (!chunk) {
          continue;
        }

        // The last chunk of a finished call carries is_terminal. Holding on to the id past that
        // point would answer the next message with a 409, so the call is let go and the next turn
        // opens a new one.
        if (chunk.agentcore?.is_terminal) {
          session.current = null;
        }

        // The last chunk is the first place that knows where the machine moved to.
        // Sticky: a handoff names the speaker once, and every chunk after it belongs to them.
        speaker = chunk.agentcore_speaker ?? speaker;

        const info = chunk.agentcore;
        if (info) {
          stage = info.stage_after ?? stage;
          isTerminal = info.is_terminal ?? isTerminal;
          yield { text, data, tools, stage, isTerminal, speaker };
        }

        const rendered = chunk.agentcore_data;
        if (rendered) {
          // A new array each time: the yielded state is read after the yield, so the consumer must
          // never see a list this loop keeps changing underneath it.
          data = [...data, rendered];
          yield { text, data, tools, stage, isTerminal, speaker };
        }

        const tool = chunk.agentcore_tool;
        if (tool) {
          tools = foldTool(tools, tool);
          yield { text, data, tools, stage, isTerminal, speaker };
        }

        const delta = chunk.choices?.[0]?.delta?.content;
        if (delta) {
          text += delta;
          yield { text, data, tools, stage, isTerminal, speaker };
        }
      }
    }
  } finally {
    reader.cancel().catch(() => {
      // The turn is over either way, and a reader that will not close is not worth a failure.
    });
  }
}
