"use client";

import type { Speaker } from "@/runtime/transport";
import { useAuiState } from "@assistant-ui/react";
import { BotIcon, HeadsetIcon, ServerIcon } from "lucide-react";
import type { FC } from "react";

/**
 * The name on an answer, when it was not the model that wrote it.
 *
 * Nothing populates this yet — see `Speaker` in runtime/transport.ts for the contract AgentCore
 * would emit. Until it does, `useSpeaker` returns `null` and this renders nothing, which is exactly
 * what an app with one speaker should show.
 *
 * The shipped `elements-speaker-identity` is not used here. It takes an array of turns and renders
 * the whole conversation itself, bubbles and all, which suits the gallery demo and cannot be put
 * inside a `ThreadPrimitive.Messages` list that already owns its messages. This is the same idea at
 * the size the thread can actually use: one line, above one answer.
 */
export function useSpeaker(): Speaker | null {
  return useAuiState((s) => {
    if (s.message.role !== "assistant") return null;

    const custom = s.message.metadata?.custom as
      | { speaker?: unknown }
      | undefined;
    const speaker = custom?.speaker;

    if (
      typeof speaker === "object" &&
      speaker !== null &&
      typeof (speaker as Speaker).name === "string"
    ) {
      return speaker as Speaker;
    }

    // The stored object, not a fresh one: `useAuiState` compares by identity, and a new object per
    // call is an infinite render loop rather than a re-render.
    return null;
  });
}

const ICONS = {
  agent: BotIcon,
  human: HeadsetIcon,
  system: ServerIcon,
} as const;

export const MessageSpeaker: FC = () => {
  const speaker = useSpeaker();

  // The model is the default author. Labelling every answer "Assistant" is noise, so only a speaker
  // the caller would not otherwise assume earns a line.
  if (!speaker || speaker.kind === "agent") return null;

  const Icon = ICONS[speaker.kind] ?? ICONS.agent;

  return (
    <div
      data-slot="aui_message-speaker"
      className="text-muted-foreground mb-1 flex items-center gap-1.5 text-xs"
    >
      <Icon aria-hidden className="size-3.5 shrink-0" />
      <span className="font-medium">{speaker.name}</span>
      {speaker.detail && (
        <span className="text-foreground/40">· {speaker.detail}</span>
      )}
    </div>
  );
};
