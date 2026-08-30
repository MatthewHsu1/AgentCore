import {
  useLocalRuntime,
  type ChatModelAdapter,
  type ChatModelRunResult,
  type MessageTiming,
  type ThreadMessage,
} from "@assistant-ui/react";
import { useRef } from "react";
import {
  runTurn,
  wireMessages,
  type Session,
  type ToolPart,
  type TurnState,
  type WireMessage,
} from "./transport.ts";

/**
 * The bridge between assistant-ui and AgentCore's OpenAI-compatible endpoint.
 *
 * AgentCore keeps the transcript on the server and names the call in the `X-AgentCore-Session`
 * header. assistant-ui, like every OpenAI client, is stateless and sends the whole message list
 * instead. Holding the session id here — in the browser, for the life of the tab — is what
 * reconciles the two, and it is what saves AgentCore from needing a server-side map from chat
 * thread to call.
 *
 * Everything that touches the wire lives in {@link ./transport.ts}, where it is tested. This file
 * is the React shape around it and nothing else.
 */

/**
 * Flattens one assistant-ui message into the single string the OpenAI shape carries.
 *
 * AgentCore's endpoint reads text and no other content part, so anything else in the message — an
 * image, a tool call — has nothing to map onto and is left out rather than sent as `[object
 * Object]`.
 */
function flatten(message: ThreadMessage): WireMessage {
  const text = message.content
    .filter((part): part is { type: "text"; text: string } => part.type === "text")
    .map((part) => part.text)
    .join("");

  return { role: message.role, content: text };
}

/**
 * Measures one turn, in the shape `message.metadata.timing` is read back in.
 *
 * `useLocalRuntime` does not time turns for you — assistant-ui ships `useStreamingTiming` for that,
 * but only external-store adapters call it. A chat model adapter reports its own timing instead,
 * which is what this does. The field semantics follow assistant-ui's own tracker: `streamStartTime`
 * is epoch milliseconds while `firstTokenTime` and `totalStreamTime` are milliseconds *since* it,
 * and the token count is the same length/4 estimate, so a reader cannot tell the two apart.
 */
function newTurnClock() {
  const startTime = Date.now();
  let firstTokenTime: number | undefined;
  let length = 0;
  let totalChunks = 0;
  let toolCallCount = 0;

  return {
    observe(state: TurnState) {
      // Growth, not arrival: the endpoint re-sends the whole text every frame, so an unchanged
      // length means nothing was added and must not count as a chunk.
      if (state.text.length > length) {
        firstTokenTime ??= Date.now() - startTime;
        length = state.text.length;
        totalChunks += 1;
      }
      // The wire now reports every tool the host ran, drawing tools included, so this is the count
      // itself rather than the drawings it used to be estimated from.
      toolCallCount = state.tools.length;
    },

    finish(): MessageTiming {
      const totalStreamTime = Date.now() - startTime;
      const tokenCount = Math.ceil(length / 4);

      return {
        streamStartTime: startTime,
        totalStreamTime,
        totalChunks,
        toolCallCount,
        ...(firstTokenTime !== undefined && { firstTokenTime }),
        ...(tokenCount > 0 && { tokenCount }),
        ...(totalStreamTime > 0 &&
          tokenCount > 0 && {
            tokensPerSecond: tokenCount / (totalStreamTime / 1000),
          }),
      };
    },
  };
}

/**
 * Turns one reported tool into the content part assistant-ui draws it as.
 *
 * `result` is left off entirely while the tool is still running, because that absence is what the
 * kit reads as "running" — an empty string there would draw a finished tool that answered nothing.
 */
function toolContent(tool: ToolPart) {
  return {
    type: "tool-call" as const,
    toolCallId: tool.callId,
    toolName: tool.name,
    args: tool.arguments,
    argsText: JSON.stringify(tool.arguments, null, 2),
    ...(tool.result !== undefined
      ? { result: tool.result, isError: tool.failed === true }
      : {}),
  };
}

/**
 * Binds assistant-ui to one AgentCore endpoint.
 *
 * @param endpoint The route the host mapped the text endpoint on.
 * @returns The runtime to hand to `AssistantRuntimeProvider`.
 */
export function useAgentCoreRuntime(endpoint: string) {
  // A ref and not state: changing the session must never re-render, and the value has to be the
  // current one by the time the next turn reads it rather than on the next paint.
  const session = useRef<Session>({ current: null });

  const adapter: ChatModelAdapter = {
    async *run({ messages, abortSignal }) {
      const turn = runTurn({
        endpoint,
        session: session.current,
        messages: wireMessages(messages.map(flatten)),
        abortSignal,
        fetch: (url, init) => fetch(url, init),
      });

      const clock = newTurnClock();
      let content: ChatModelRunResult["content"] = [];
      let stage: ChatModelRunResult["metadata"] = undefined;

      for await (const state of turn) {
        clock.observe(state);

        // `metadata.custom` is the one slot on a message that is the app's to define. Nothing on
        // screen reads the stage or `isTerminal` — the caller is never shown which stage answered
        // them — but both stay here because they cost one field each and the alternative is
        // re-plumbing the runtime the day something does want them.
        stage = {
          custom: {
            stage: state.stage,
            isTerminal: state.isTerminal,
            // Absent today. Carried anyway so a live handoff is a server change on its own.
            speaker: state.speaker,
          },
        };

        // Every yield replaces the message content rather than adding to it, so each one repeats
        // everything drawn so far. Drop the repeat and a later text-only yield erases the drawing.
        // Tools first, and then the words: the host runs every tool before it speaks, so this is
        // the order the turn actually happened in.
        content = [
          ...state.tools.map(toolContent),
          ...(state.text.length > 0
            ? [{ type: "text" as const, text: state.text }]
            : []),
          ...state.data.map((part) => ({
            type: "data" as const,
            name: part.name,
            data: part.data,
          })),
        ];
        yield { content, metadata: stage };
      }

      // A final yield carrying the same content, so the timing lands on the finished message
      // without blanking what was already drawn — `content` is optional on the result, but
      // omitting it here would make this yield the message's last word on its own content.
      yield { content, metadata: { ...stage, timing: clock.finish() } };
    },
  };

  return useLocalRuntime(adapter);
}
