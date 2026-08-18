import { AssistantRuntimeProvider } from "@assistant-ui/react";
import { useAgentCoreRuntime } from "./runtime/AgentCoreRuntime";
import { Thread } from "./Thread";

/**
 * The route the text endpoint answers on.
 *
 * It is read from the page rather than compiled in, because the host can move the endpoint —
 * `MapAgentCoreHost` takes a pattern — and a rebuilt bundle should not be the price of that. The
 * default is the one `MapChatCompletions` uses when a host names none.
 */
const endpoint =
  document.documentElement.dataset.agentcoreEndpoint || "/v1/chat/completions";

export function App() {
  const runtime = useAgentCoreRuntime(endpoint);

  return (
    <AssistantRuntimeProvider runtime={runtime}>
      <Thread />
    </AssistantRuntimeProvider>
  );
}
