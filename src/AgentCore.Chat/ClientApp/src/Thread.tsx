import {
  ComposerPrimitive,
  MessagePrimitive,
  ThreadPrimitive,
} from "@assistant-ui/react";

/**
 * The chat window.
 *
 * assistant-ui ships behaviour as unstyled primitives and leaves the markup to the app, so this
 * file is the markup and index.css is the look. The class names are our own.
 *
 * The @assistant-ui/styles package is deliberately not used. It is generated from Tailwind and its
 * theme layer does not survive the packaging: the sheet references 122 custom properties and
 * defines 80, and the missing ones include `--spacing`, which nearly every width, height, and pad
 * in it is a multiple of. Every one of those declarations is therefore invalid and dropped. Its
 * `.aui-root` is also the floating-bubble class — `position: fixed`, pinned to a corner, with
 * `overflow: clip` — so putting it on a full-page thread collapses the whole UI to nothing.
 */
export function Thread() {
  return (
    <ThreadPrimitive.Root className="chat-root">
      <ThreadPrimitive.Viewport className="chat-viewport" autoScroll>
        <ThreadPrimitive.Empty>
          <div className="chat-welcome">
            <p>How can I help you?</p>
          </div>
        </ThreadPrimitive.Empty>

        <ThreadPrimitive.Messages components={{ UserMessage, AssistantMessage }} />
      </ThreadPrimitive.Viewport>

      <Composer />
    </ThreadPrimitive.Root>
  );
}

/** One thing the caller said. */
function UserMessage() {
  return (
    <MessagePrimitive.Root className="chat-message chat-message-user">
      <div className="chat-bubble">
        <MessagePrimitive.Parts />
      </div>
    </MessagePrimitive.Root>
  );
}

/** One thing the agent said. */
function AssistantMessage() {
  return (
    <MessagePrimitive.Root className="chat-message chat-message-assistant">
      <div className="chat-bubble">
        <MessagePrimitive.Parts />
      </div>
    </MessagePrimitive.Root>
  );
}

/**
 * The box the caller types in.
 *
 * It sits outside the viewport rather than inside it, so the transcript scrolls under a composer
 * that stays put instead of scrolling away with the messages.
 */
function Composer() {
  return (
    <ComposerPrimitive.Root className="chat-composer">
      <ComposerPrimitive.Input
        className="chat-input"
        placeholder="Send a message"
        rows={1}
        autoFocus
        submitOnEnter
      />

      {/* One button in two states: the turn is either waiting to be sent or already running. */}
      <ThreadPrimitive.If running={false}>
        <ComposerPrimitive.Send className="chat-button" aria-label="Send">
          Send
        </ComposerPrimitive.Send>
      </ThreadPrimitive.If>
      <ThreadPrimitive.If running>
        <ComposerPrimitive.Cancel className="chat-button" aria-label="Stop">
          Stop
        </ComposerPrimitive.Cancel>
      </ThreadPrimitive.If>
    </ComposerPrimitive.Root>
  );
}
