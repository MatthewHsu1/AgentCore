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

/** The prefix every server-sent event carries. */
const DataPrefix = "data: ";

/** The event that closes a stream. */
const DoneEvent = "[DONE]";

/** One message, in the only shape AgentCore's endpoint reads. */
export type WireMessage = {
  readonly role: string;
  readonly content: string;
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
  stage_after?: string;
  is_terminal?: boolean;
};

/** One chunk of a streamed answer. */
type StreamChunk = {
  choices?: { delta?: { content?: string }; finish_reason?: string | null }[];
  agentcore?: TurnInfo;
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
 * Runs one turn and yields the reply as it grows.
 *
 * Each yield is the whole reply so far rather than the newest piece, because that is what the
 * runtime above renders.
 *
 * @param options What the turn needs.
 * @returns The reply, yielded once per piece that carries text.
 * @throws Error The host refused the turn, or answered with no body.
 */
export async function* runTurn(options: TurnOptions): AsyncGenerator<string> {
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

  const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
  let pending = "";
  let text = "";

  try {
    for (;;) {
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

        const delta = chunk.choices?.[0]?.delta?.content;
        if (delta) {
          text += delta;
          yield text;
        }
      }
    }
  } finally {
    reader.cancel().catch(() => {
      // The turn is over either way, and a reader that will not close is not worth a failure.
    });
  }
}
