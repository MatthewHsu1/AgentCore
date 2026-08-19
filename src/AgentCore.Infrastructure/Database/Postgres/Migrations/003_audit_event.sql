-- Store 3 — the append-only record of what happened on a call.
CREATE TABLE audit_event (
    write_position  bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    call_id         text        NOT NULL,
    sequence        bigint      NOT NULL,
    kind            text        NOT NULL,
    occurred_at     timestamptz NOT NULL,
    turn_index      integer     NULL,
    amends_sequence bigint      NULL,
    payload         jsonb       NOT NULL DEFAULT '{}'::jsonb,

    CONSTRAINT audit_event_call_sequence_unique UNIQUE (call_id, sequence)
);

CREATE INDEX audit_event_call_id_idx ON audit_event (call_id, sequence);

-- Refusals 1, 2 and 3: the writer may only insert and read.
--
-- The PUBLIC revoke is required and its absence was a real hole. A privilege held by PUBLIC is not
-- removed by revoking from the role: with a GRANT ALL ... TO PUBLIC present, the other two statements
-- ran clean and has_table_privilege('agentcore_writer','audit_event','UPDATE') still returned true.
REVOKE ALL ON audit_event FROM PUBLIC;
REVOKE ALL ON audit_event FROM agentcore_writer;
GRANT INSERT, SELECT ON audit_event TO agentcore_writer;
