import { styledGenerativeUILibrary } from "@/components/assistant-ui/generative-ui";
import {
  useAssistantDataUI,
  useAui,
  useAuiState,
  type DataMessagePartProps,
} from "@assistant-ui/react";
import { renderGenerativeUI } from "@assistant-ui/react-generative-ui";
import type { FC } from "react";

/** The data-part name the server publishes drawings under. It matches `PresentTool.RendererName`. */
export const GENERATIVE_UI_PART = "generative-ui";

/**
 * Draws one `generative-ui` data part, and turns a click on it into the next user turn.
 *
 * Module scope, not an inline arrow inside the registering component: `useAssistantDataUI` keys the
 * renderer by component identity, so a fresh function per render remounts the whole subtree and any
 * form state inside it goes with it.
 */
const GenerativeUiPart: FC<DataMessagePartProps> = ({ data }) => {
  const aui = useAui();
  const isRunning = useAuiState((state) => state.thread.isRunning);

  // Every action type the model invents is handled, rather than a fixed registry of names. The
  // agent chooses those names itself and reports them in its receipt, so a registry here would have
  // to be kept in step with something no code owns. An unhandled type would render a button that
  // silently does nothing.
  const dispatch = (action: { type: string }) => {
    // Appending mid-stream aborts the running turn rather than queueing behind it.
    if (aui.thread.getState().isRunning) return;

    // Wrapped in prose. The wire layer flattens a user message to its text, and bare JSON would
    // read to the agent as something the caller typed.
    aui.thread.append({
      role: "user",
      content: [{ type: "text", text: `the caller clicked: ${JSON.stringify(action)}` }],
    });
  };

  return (
    // The guard above stops the click landing; `inert` stops the caller believing it did, and takes
    // the controls out of the tab order rather than only ignoring the mouse.
    <div inert={isRunning} data-agentcore-drawing="" className={isRunning ? "opacity-60" : undefined}>
      {renderGenerativeUI(data, styledGenerativeUILibrary, { status: "done", dispatch })}
    </div>
  );
};

/** Registers the renderer for as long as it is mounted. Draws nothing itself. */
export function GenerativeUiDataUI() {
  useAssistantDataUI({ name: GENERATIVE_UI_PART, render: GenerativeUiPart });
  return null;
}
