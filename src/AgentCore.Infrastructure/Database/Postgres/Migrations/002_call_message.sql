-- Store 1 — the transcript. One row per message, never one JSON blob per call.
CREATE TABLE call_message (
    call_id    text        NOT NULL,
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

-- UPDATE amends a reply the caller cut short. DELETE erases one caller, and sweeps the retention
-- window. Neither is a hole: this store holds words, and words stay erasable.
GRANT SELECT, INSERT, UPDATE, DELETE ON call_message TO agentcore_writer;
