import { AssistantRuntimeProvider, useRemoteThreadListRuntime } from "@assistant-ui/react";
import { Thread } from "@/components/assistant-ui/thread";
import { ThreadListSidebar } from "@/components/assistant-ui/threadlist-sidebar";
import { SidebarProvider } from "@/components/ui/sidebar";
import { TooltipProvider } from "@/components/ui/tooltip";
import { useAgentCoreRuntime } from "./runtime/AgentCoreRuntime";
import { localThreadListAdapter } from "./runtime/LocalThreadListAdapter";

/**
 * The route the text endpoint answers on.
 *
 * It is read from the page rather than compiled in, because the host can move the endpoint —
 * `MapAgentCoreHost` takes a pattern — and a rebuilt bundle should not be the price of that. The
 * default is the one `MapChatCompletions` uses when a host names none.
 */
const endpoint =
  document.documentElement.dataset.agentcoreEndpoint || "/v1/chat/completions";

/**
 * The application: assistant-ui's thread and thread list, over AgentCore's endpoint.
 *
 * This is their `templates/default` composition with the Next.js parts left out — the `"use client"`
 * directive, the breadcrumb header that links to their docs, and the AI SDK transport. Our runtime
 * hook and our thread list adapter stand where `useChatRuntime` stood.
 *
 * `TooltipProvider` is not decoration. The vendored action bar renders `Tooltip` for every control
 * it shows, and those throw without a provider above them.
 */
export function App() {
  // `useRemoteThreadListRuntime` calls `runtimeHook` once per thread, which is what makes the
  // session id inside `useAgentCoreRuntime` belong to one conversation rather than to the whole
  // tab. The adapter beside it is the stub — see runtime/LocalThreadListAdapter.ts.
  const runtime = useRemoteThreadListRuntime({
    runtimeHook: () => useAgentCoreRuntime(endpoint),
    adapter: localThreadListAdapter(),
  });

  return (
    <AssistantRuntimeProvider runtime={runtime}>
      <TooltipProvider>
        <SidebarProvider>
          <div className="flex h-dvh w-full">
            <ThreadListSidebar />
            <div className="flex-1 overflow-hidden">
              <Thread />
            </div>
          </div>
        </SidebarProvider>
      </TooltipProvider>
    </AssistantRuntimeProvider>
  );
}
