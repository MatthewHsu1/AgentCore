import { useRef } from "react";
import {
  useLocalRuntime,
  type ChatModelAdapter,
  type ThreadMessage,
} from "@assistant-ui/react";
import { runTurn, wireMessages, type Session, type WireMessage } from "./transport.ts";

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

      for await (const text of turn) {
        yield { content: [{ type: "text" as const, text }] };
      }
    },
  };

  return useLocalRuntime(adapter);
}
