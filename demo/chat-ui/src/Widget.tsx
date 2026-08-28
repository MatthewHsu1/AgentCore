import { Thread } from "@/components/assistant-ui/thread";
import { LauncherBubble } from "@/components/elements/launcher-bubble";
import { TooltipProvider } from "@/components/ui/tooltip";
import { GenerativeUiDataUI } from "@/components/GenerativeUiDataUI";
import { AssistantRuntimeProvider } from "@assistant-ui/react";
import { XIcon } from "lucide-react";
import { useEffect, useState } from "react";
import { useAgentCoreRuntime } from "./runtime/AgentCoreRuntime";

/**
 * The embeddable form of the chat: a bubble on someone else's page that opens into a panel.
 *
 * It is a second page rather than a mode of the main one. The full app owns the whole viewport and
 * carries a sidebar and a thread list; a widget owns a corner, has no room for either, and has to
 * be able to render as *nothing but a bubble* so the host page shows through around it. Those are
 * different layouts, not one layout with a flag.
 *
 * Read {@link ../public/embed.js} next: this half only knows how big it wants to be, and says so.
 * The script on the host page is what actually resizes the frame.
 */

/** The size the frame should be, in CSS pixels, for each state. */
const SIZE = {
  closed: { width: 96, height: 96 },
  teaser: { width: 336, height: 420 },
  open: { width: 400, height: 620 },
} as const;

const endpoint =
  document.documentElement.dataset.agentcoreEndpoint || "/v1/chat/completions";

const PROMPTS = [
  "What treadmill fits a small room?",
  "How do I service my elliptical?",
  "Where is my order?",
] as const;

type Phase = "closed" | "teaser" | "open";

/**
 * Tells the host page how much room to give the frame.
 *
 * An iframe cannot resize itself — its size belongs to the document that embedded it. So the widget
 * posts what it needs and `embed.js` applies it. Without this the frame would be stuck at whatever
 * size it was created with, and either clip the open panel or leave a 400px invisible rectangle
 * over the host page swallowing clicks while the bubble is closed.
 */
function useFrameSize(phase: Phase) {
  useEffect(() => {
    const size = SIZE[phase];
    // "*" rather than a fixed origin: the widget is embedded on sites it cannot know the names of,
    // and the message carries no secret — only two numbers.
    window.parent?.postMessage(
      { source: "agentcore-widget", type: "resize", ...size },
      "*",
    );
  }, [phase]);
}

export function Widget() {
  const runtime = useAgentCoreRuntime(endpoint);
  const [phase, setPhase] = useState<Phase>("closed");
  const [pending, setPending] = useState<string | null>(null);

  useFrameSize(phase);

  // A prompt picked from the teaser has to wait for the thread to exist before it can be sent.
  useEffect(() => {
    if (phase !== "open" || !pending) return;

    runtime.thread.append({
      role: "user",
      content: [{ type: "text", text: pending }],
    });
    setPending(null);
  }, [phase, pending, runtime]);

  return (
    <AssistantRuntimeProvider runtime={runtime}>
      <GenerativeUiDataUI />
      <TooltipProvider>
        <div className="flex h-dvh w-full items-end justify-end p-3">
          {phase === "open" ? (
            <div className="bg-background border-border/60 relative flex h-full w-full flex-col overflow-hidden rounded-2xl border shadow-xl">
              <button
                type="button"
                onClick={() => setPhase("closed")}
                aria-label="Close chat"
                className="hover:bg-accent absolute end-2 top-2 z-10 rounded-full p-1.5"
              >
                <XIcon className="size-4" />
              </button>
              <Thread />
            </div>
          ) : (
            <LauncherBubble
              open={phase === "teaser"}
              unread={0}
              greeting="Hi — ask us anything about Spirit equipment."
              prompts={PROMPTS}
              onToggle={() =>
                setPhase(phase === "teaser" ? "closed" : "teaser")
              }
              onPick={(prompt) => {
                setPending(prompt);
                setPhase("open");
              }}
              onStart={() => setPhase("open")}
            />
          )}
        </div>
      </TooltipProvider>
    </AssistantRuntimeProvider>
  );
}
