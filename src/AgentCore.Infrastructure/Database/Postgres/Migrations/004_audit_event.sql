-- Store 3 — the append-only record of what happened on a call.
--
-- Two numbers, two owners, and the split is the point. event_id is IDENTITY and the CALL allocates
-- it: an amendment has to name the event it corrects the instant that event is raised, so waiting
-- for a store is not available. sequence is ORDER and the STORE allocates it: a per-call counter
-- held by a session restarts at zero on the second session of one call, which is what a chat page
-- reload is, and two app instances have no counter to share at all.
--
-- Nothing sorts by event_id. UUID v7 is random within a millisecond and one turn raises three events
-- inside one, so id order is not time order. Read the chain by sequence.
CREATE TABLE audit_event (
    write_position  bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    call_id         text        NOT NULL,
    event_id        uuid        NOT NULL,
    sequence        bigint      NOT NULL,
    kind            text        NOT NULL,
    occurred_at     timestamptz NOT NULL,
    turn_index      integer     NULL,
    amends_event_id uuid        NULL,
    payload         jsonb       NOT NULL DEFAULT '{}'::jsonb,

    CONSTRAINT audit_event_call_sequence_unique UNIQUE (call_id, sequence),

    -- What makes a replayed batch a no-op instead of a duplicate. QueuedAuditSink logs and drops a
    -- batch today rather than retrying it, so nothing exercises this yet — but the day a retry is
    -- added, it must be safe by construction, not by care, and this constraint is what makes it so.
    CONSTRAINT audit_event_call_event_unique UNIQUE (call_id, event_id)
);

-- No index for reading one call's chain is declared here, and that is not an omission:
-- audit_event_call_sequence_unique is (call_id, sequence), so the constraint's own index already
-- serves every "this call, in order" read. A second index on the same two columns in the same order
-- would only cost every insert a second write.

-- Refusals 1, 2 and 3: the writer may only insert and read.
--
-- The PUBLIC revoke is required and its absence was a real hole. A privilege held by PUBLIC is not
-- removed by revoking from the role: with a GRANT ALL ... TO PUBLIC present, the other two statements
-- ran clean and has_table_privilege('agentcore_writer','audit_event','UPDATE') still returned true.
REVOKE ALL ON audit_event FROM PUBLIC;
REVOKE ALL ON audit_event FROM agentcore_writer;
GRANT INSERT, SELECT ON audit_event TO agentcore_writer;
