"use client";

import { RegenerateMenu } from "@/components/elements/regenerate-menu";
import { useAui, useAuiState } from "@assistant-ui/react";
import { useActionBarReload } from "@assistant-ui/core/react";
import { useState, type FC } from "react";

/**
 * The ways to ask again.
 *
 * Only the first is a regenerate in assistant-ui's sense. The other two cannot be: the runtime
 * replays the same request, and AgentCore takes no per-turn steering, so "shorter" has nowhere to
 * live on a reload. They are sent as a new instruction instead, which is a turn the agent can
 * actually act on — and which keeps the answer being complained about in view.
 */
const OPTIONS = [
  { id: "again", label: "Try again", detail: "Same question, fresh answer" },
  { id: "shorter", label: "Shorter", detail: "Ask for the short version" },
  { id: "detail", label: "More detail", detail: "Ask it to go deeper" },
] as const;

const FOLLOW_UP: Record<string, string> = {
  shorter: "That was too long. Answer again, much shorter.",
  detail: "Go deeper on that answer. Add the detail you left out.",
};

export const Regenerate: FC = () => {
  const aui = useAui();
  const { disabled, reload } = useActionBarReload();
  const isRunning = useAuiState((s) => s.thread.isRunning);
  const [open, setOpen] = useState(false);

  const pick = (id: string) => {
    setOpen(false);

    // A turn is already in flight: appending would abort it, and reloading is refused anyway.
    if (isRunning) return;

    if (id === "again") {
      if (!disabled) reload();
      return;
    }

    const text = FOLLOW_UP[id];
    if (text) {
      aui.thread.append({ role: "user", content: [{ type: "text", text }] });
    }
  };

  return (
    <RegenerateMenu
      className="max-w-none"
      options={OPTIONS}
      open={open}
      currentId="again"
      onOpenChange={setOpen}
      onPick={pick}
    />
  );
};
