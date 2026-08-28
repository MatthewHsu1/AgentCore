"use client";

import {
  ConversationSearch,
  type SearchHit,
} from "@/components/elements/conversation-search";
import { useAuiState } from "@assistant-ui/react";
import { useMemo, useState, type FC } from "react";

/** How much of the line around a match to show on each side. */
const CONTEXT = 40;

/** The flattened text of one message, since only text is searchable. */
function textOf(parts: readonly { type: string }[]): string {
  return parts
    .filter((p): p is { type: "text"; text: string } => p.type === "text")
    .map((p) => p.text)
    .join("");
}

/**
 * Search over the messages of the open thread.
 *
 * The whole thread is already in memory — assistant-ui holds it to render it — so this is a scan,
 * not a query. That also means it finds only what the browser has: a thread whose history has not
 * been loaded is not searched, and cannot be.
 *
 * Matching is first-hit-per-message. A second match inside one long answer is not worth a second
 * row that looks almost identical to the first.
 */
export const ThreadMessageSearch: FC = () => {
  const messages = useAuiState((s) => s.thread.messages);
  const [query, setQuery] = useState("");
  const [activeIndex, setActiveIndex] = useState(0);

  const hits = useMemo<SearchHit[]>(() => {
    const needle = query.trim().toLowerCase();
    if (needle.length < 2) return [];

    const found: SearchHit[] = [];

    messages.forEach((message, position) => {
      const text = textOf(message.content);
      const at = text.toLowerCase().indexOf(needle);
      if (at < 0) return;

      found.push({
        id: message.id,
        before: text.slice(Math.max(0, at - CONTEXT), at),
        match: text.slice(at, at + needle.length),
        after: text.slice(at + needle.length, at + needle.length + CONTEXT),
        position,
      });
    });

    return found;
  }, [messages, query]);

  const step = (direction: number) => {
    if (hits.length === 0) return;
    // Wrap both ways, so stepping past either end lands on the other rather than sticking.
    setActiveIndex((i) => (i + direction + hits.length) % hits.length);
  };

  return (
    <ConversationSearch
      className="max-w-none"
      query={query}
      hits={hits}
      activeIndex={hits.length === 0 ? 0 : Math.min(activeIndex, hits.length - 1)}
      onQueryChange={(next) => {
        setQuery(next);
        setActiveIndex(0);
      }}
      onStep={step}
    />
  );
};
