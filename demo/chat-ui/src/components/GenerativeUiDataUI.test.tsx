import { Thread } from "@/components/assistant-ui/thread";
import {
  AssistantRuntimeProvider,
  useLocalRuntime,
  type ChatModelAdapter,
} from "@assistant-ui/react";
import { defaultGenerativeUILibrary } from "@assistant-ui/react-generative-ui";
import { act, cleanup, render, screen } from "@testing-library/react";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, describe, expect, test } from "vitest";
import { GENERATIVE_UI_PART, GenerativeUiDataUI } from "./GenerativeUiDataUI";

/**
 * The renderer for what `ui.draw` sends.
 *
 * The drawing never travels as a tool call: the server owns the tool loop and the reply carries
 * only text, so the tree arrives as a `data` part on its own SSE field. Nothing renders a data part
 * until a renderer is registered by name, which is what these tests hold in place.
 */

// Testing-library only registers its own cleanup when vitest runs with `globals: true`. It does
// not here, so each test would otherwise leave its DOM in the body and the next one would find two
// of everything.
afterEach(cleanup);

const CARD = {
  $type: "Card",
  title: "Q3 revenue",
  children: [
    { $type: "Fact", label: "Bookings", value: "$1.2M" },
    { $type: "Button", label: "Approve", $action: { type: "approve", id: 42 } },
  ],
};

/** Drives a thread that answers with one drawing, and reports what the caller sent back. */
function mount(options: { hold?: boolean } = {}) {
  const sent: string[] = [];
  let release: (() => void) | undefined;
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });

  const adapter: ChatModelAdapter = {
    async *run({ messages }) {
      sent.push(messages.at(-1)?.content.map((part) => ("text" in part ? part.text : "")).join("") ?? "");

      yield {
        content: [{ type: "data" as const, name: GENERATIVE_UI_PART, data: CARD }],
      };

      if (options.hold) {
        await held;
      }
    },
  };

  function Harness() {
    const runtime = useLocalRuntime(adapter);
    return (
      <AssistantRuntimeProvider runtime={runtime}>
        <GenerativeUiDataUI />
        <Thread />
      </AssistantRuntimeProvider>
    );
  }

  const view = render(<Harness />);
  return { view, sent, release: () => release?.() };
}

async function ask(view: ReturnType<typeof render>) {
  const box = view.container.querySelector("textarea") ?? view.container.querySelector("input");
  const { fireEvent } = await import("@testing-library/react");

  await act(async () => {
    fireEvent.change(box!, { target: { value: "show me revenue" } });
  });
  await act(async () => {
    fireEvent.keyDown(box!, { key: "Enter", code: "Enter" });
  });
}

describe("the drawing renderer", () => {
  test("draws a data part that would otherwise render as nothing", async () => {
    const { view } = mount();
    await ask(view);

    expect(await screen.findByText("Q3 revenue")).toBeTruthy();
    expect(screen.getByText("Bookings")).toBeTruthy();
    expect(screen.getByText("$1.2M")).toBeTruthy();
  });

  test("a click on an idle thread sends the payload back as the next user turn", async () => {
    const { view, sent } = mount();
    await ask(view);

    const button = await screen.findByRole("button", { name: "Approve" });
    const { fireEvent } = await import("@testing-library/react");
    await act(async () => {
      fireEvent.click(button);
    });

    // Wrapped in prose, not bare JSON: the wire layer flattens a user message to its text, and the
    // agent would otherwise read raw JSON as something the caller typed.
    const clicked = sent.find((text) => text.includes("clicked"));
    expect(clicked).toContain("approve");
    expect(clicked).toContain("42");
  });

  test("the controls are inert while a turn is still running", async () => {
    const { view } = mount({ hold: true });
    await ask(view);

    await screen.findByText("Q3 revenue");

    const drawing = view.container.querySelector("[data-agentcore-drawing]");
    expect(drawing?.hasAttribute("inert")).toBe(true);
  });

  test("an action type nobody registered still reaches the agent", async () => {
    // The agent names its own action types and reports them in its receipt. A fixed registry here
    // would render buttons that silently do nothing for every name it had not been told about.
    const registered = Object.keys(defaultGenerativeUILibrary);
    expect(registered).toContain("Button");

    const { view, sent } = mount();
    await ask(view);

    const button = await screen.findByRole("button", { name: "Approve" });
    const { fireEvent } = await import("@testing-library/react");
    await act(async () => {
      fireEvent.click(button);
    });

    expect(sent.some((text) => text.includes("approve"))).toBe(true);
  });
});

describe("the vocabulary the drawing model is taught", () => {
  test("names only components this app can actually render", () => {
    // The drift guard. The C# skill file is the model's whole vocabulary and the library is what
    // draws it; an upgrade that renames a component would otherwise show the caller a hole.
    // From demo/chat-ui, which is where vitest runs. `import.meta.url` is not a file URL under
    // happy-dom.
    //
    // This reaches outside demo/chat-ui into AgentCore's own source tree, so this test breaks if
    // chat-ui is copied into another repo, per the README's suggestion. There is no fix here short
    // of publishing the vocabulary as its own artifact both sides read.
    const vocabulary = readFileSync(
      resolve(process.cwd(), "../../src/AgentCore.Application/Tools/Drawing/vocabulary.md"),
      "utf8",
    );

    const taught = [...vocabulary.matchAll(/^- `([A-Z][A-Za-z]*)`/gm)].map((match) => match[1]);
    const renderable = new Set(Object.keys(defaultGenerativeUILibrary));

    expect(taught.length).toBe(27);
    expect(taught.filter((name) => !renderable.has(name))).toEqual([]);
    expect([...renderable].filter((name) => !taught.includes(name))).toEqual([]);
  });
});
