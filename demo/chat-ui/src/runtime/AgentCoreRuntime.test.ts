import { describe, expect, it } from "vitest";
import { sourceContent } from "./AgentCoreRuntime.ts";
import type { SourcePart } from "./transport.ts";

const document: SourcePart = {
  id: "card-42",
  sourceType: "document",
  title: "Spirit CT900 owner's manual, p.27",
  locator: "p.27",
  url: null,
  mediaType: "text/plain",
  origin: "knowledge",
  callId: "call-1",
};

describe("sourceContent", () => {
  it("maps a document source onto assistant-ui's document variant", () => {
    // A card has no URL, so the document variant is the only honest one: the url variant would
    // draw a link the caller could click, and there is nothing behind it.
    expect(sourceContent(document)).toEqual({
      type: "source",
      sourceType: "document",
      id: "card-42",
      title: "Spirit CT900 owner's manual, p.27",
      mediaType: "text/plain",
      parentId: "call-1",
      providerMetadata: { agentcore: { origin: "knowledge", locator: "p.27" } },
    });
  });

  it("maps a url source onto the url variant", () => {
    const part = sourceContent({
      ...document,
      sourceType: "url",
      url: "https://example.com/support",
      title: "Spirit Fitness support",
      origin: "web-search",
    });

    expect(part).toMatchObject({
      type: "source",
      sourceType: "url",
      url: "https://example.com/support",
      title: "Spirit Fitness support",
    });
  });

  it("falls back to the document variant when a url source arrives with no link", () => {
    // The host promises a link on a url source. A missing one is a host bug, and a url part with an
    // empty href renders a dead chip, so this degrades to the shape that needs no link.
    const part = sourceContent({ ...document, sourceType: "url", url: null });

    expect(part).toMatchObject({ type: "source", sourceType: "document" });
  });
});
