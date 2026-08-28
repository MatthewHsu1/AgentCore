"use client";

import { DraftRestore } from "@/components/elements/draft-restore";
import { useAui, useAuiState } from "@assistant-ui/react";
import { useEffect, useState, type FC } from "react";

const KEY = "agentcore.composer-draft";

/** A draft is only worth keeping if there is enough of it to have been worth typing. */
const MIN_LENGTH = 8;

type Draft = { text: string; savedAt: number };

function read(): Draft | null {
  try {
    const raw = localStorage.getItem(KEY);
    if (!raw) return null;

    const parsed: unknown = JSON.parse(raw);
    if (
      typeof parsed === "object" &&
      parsed !== null &&
      typeof (parsed as Draft).text === "string" &&
      typeof (parsed as Draft).savedAt === "number"
    ) {
      return parsed as Draft;
    }
  } catch {
    // A private window, cleared site data, or a half-written value. No draft is a fine answer.
  }
  return null;
}

function write(draft: Draft | null) {
  try {
    if (draft) localStorage.setItem(KEY, JSON.stringify(draft));
    else localStorage.removeItem(KEY);
  } catch {
    // Storage can be full or blocked outright. Losing a draft must never break the composer.
  }
}

/** "3 minutes ago", roughly. Precision here would be false: the draft is not a log entry. */
function ago(savedAt: number): string {
  const seconds = Math.max(0, Math.round((Date.now() - savedAt) / 1000));
  if (seconds < 60) return "just now";

  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} min ago`;

  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;

  return `${Math.round(hours / 24)}d ago`;
}

/**
 * Keeps what the caller typed but never sent, and offers it back after a reload.
 *
 * The composer is cleared on send, so this saves only while there is text in it and clears the
 * moment there is not — which covers both sending and deleting, without either needing an event.
 *
 * Only the draft found *at mount* is offered. Re-offering the text as it is being typed would put a
 * banner over the composer the whole time the composer is in use.
 */
export const ComposerDraft: FC = () => {
  const aui = useAui();
  const text = useAuiState((s) => s.composer.text);
  const [found, setFound] = useState<Draft | null>(null);
  const [dismissed, setDismissed] = useState(false);

  useEffect(() => {
    const draft = read();
    if (draft && draft.text.trim().length >= MIN_LENGTH) setFound(draft);
  }, []);

  useEffect(() => {
    if (text.trim().length >= MIN_LENGTH) write({ text, savedAt: Date.now() });
    else if (text.length === 0) write(null);
  }, [text]);

  // Whatever the caller is typing now beats whatever they typed before.
  if (dismissed || !found || text.length > 0) return null;

  return (
    <DraftRestore
      className="mx-auto max-w-(--thread-max-width)"
      draft={found.text}
      savedAt={ago(found.savedAt)}
      onRestore={() => {
        aui.composer.setText(found.text);
        setDismissed(true);
      }}
      onDiscard={() => {
        write(null);
        setDismissed(true);
      }}
    />
  );
};
