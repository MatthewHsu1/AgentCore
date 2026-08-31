-- Store 1 — the words of a call. One row per message, never one JSON blob per call.
--
-- A child of call. The row must exist before a word may be written against it, which is what stops
-- a call accumulating words no listing can reach.
CREATE TABLE call_message (
    call_id    text        NOT NULL REFERENCES call(call_id) ON DELETE CASCADE,
    ordinal    integer     NOT NULL,
    turn_index integer     NOT NULL,
    role       text        NOT NULL,
    content    jsonb       NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),

    PRIMARY KEY (call_id, ordinal)
);

CREATE INDEX call_message_updated_at_idx ON call_message (updated_at);
CREATE INDEX call_message_turn_idx       ON call_message (call_id, turn_index);
CREATE INDEX call_message_retention_idx  ON call_message (call_id, updated_at DESC);

-- UPDATE amends a reply the caller cut short. DELETE erases one caller. Neither is a hole: this
-- table holds words, and words stay erasable. Retention deletes the call itself and reaches these
-- rows through the cascade, so it needs no privilege of its own here.
GRANT SELECT, INSERT, UPDATE, DELETE ON call_message TO agentcore_writer;
