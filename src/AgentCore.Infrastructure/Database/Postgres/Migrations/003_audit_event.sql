-- Store 3 — the audit chain. One chain over the whole table, not one for each call: deleting a whole
-- call then breaks a link instead of leaving every remaining chain valid.
CREATE TABLE audit_event (
    chain_position  bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    call_id         text        NOT NULL,
    sequence        bigint      NOT NULL,
    kind            text        NOT NULL,
    occurred_at     timestamptz NOT NULL,
    turn_index      integer     NULL,
    amends_sequence bigint      NULL,
    payload         jsonb       NOT NULL DEFAULT '{}'::jsonb,
    previous_hash   char(64)    NOT NULL,
    hash            char(64)    NOT NULL,

    CONSTRAINT audit_event_call_sequence_unique UNIQUE (call_id, sequence),
    CONSTRAINT audit_event_previous_hash_unique UNIQUE (previous_hash),
    CONSTRAINT audit_event_hash_unique          UNIQUE (hash)
);

CREATE INDEX audit_event_call_id_idx ON audit_event (call_id, sequence);

-- No CHECK constraint on hash. PostgreSQL does not recompute it; AuditChain.Verify in C# and the
-- nightly head-hash anchor are the defences.

-- Refusals 1, 2 and 3: the writer may only insert and read.
--
-- The PUBLIC revoke is required and its absence was a real hole. A privilege held by PUBLIC is not
-- removed by revoking from the role: with a GRANT ALL ... TO PUBLIC present, the other two statements
-- ran clean and has_table_privilege('agentcore_writer','audit_event','UPDATE') still returned true.
REVOKE ALL ON audit_event FROM PUBLIC;
REVOKE ALL ON audit_event FROM agentcore_writer;
GRANT INSERT, SELECT ON audit_event TO agentcore_writer;
