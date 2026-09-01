-- The whole AgentCore schema. One migration, applied to a database that has none of it.
--
-- Order inside the file is the order the objects need: the role first, because every grant names it;
-- call before call_message, because the words are a child of the call they belong to; audit_event
-- last, with the triggers that refuse to let it change.


-- The role the running system connects as. Store 0, store 1 and store 3 share it.
--
-- NOLOGIN, no password: a deployment owns credentials, not this migration. Make the process's login
-- role a member of it:  GRANT agentcore_writer TO <login role>;
--
-- The handler is not decoration. Roles are cluster-wide but the advisory lock that guards this
-- migration is per-database, so two databases of one cluster can migrate at the same moment. That
-- race surfaces as either error depending on where it lands: duplicate_object from the existence
-- check, unique_violation from pg_authid's index.
DO $$
BEGIN
    CREATE ROLE agentcore_writer NOLOGIN;
EXCEPTION
    WHEN duplicate_object OR unique_violation THEN
        NULL;
    WHEN insufficient_privilege THEN
        RAISE EXCEPTION 'agentcore_writer does not exist and this role may not create it. Either CREATE ROLE agentcore_writer NOLOGIN as a role that may, or grant CREATEROLE to the migrating role.';
END $$;

-- The tables land in the first schema of the migrating role's search_path, so the grant follows them
-- there rather than naming public.
DO $$
BEGIN
    EXECUTE format('GRANT USAGE ON SCHEMA %I TO agentcore_writer', current_schema());
END $$;

-- The running system starts as this role and asks the ledger what is already applied, so it reads
-- the ledger and never writes it. Only a privileged first run migrates.
GRANT SELECT ON agentcore_schema_migration TO agentcore_writer;


-- Store 0 — what a call is, apart from its words. One row per thread in a caller's list.
--
-- This table comes first because call_message hangs off it. A call exists before it has words, and
-- deleting it takes the words with it: one statement, one transaction, no ordering to get wrong.
--
-- audit_event is deliberately NOT a child. The triggers at the foot of this file refuse every DELETE
-- on it, so a cascade could not run even if one were declared. The trail outlives the conversation
-- on purpose, and relates to a call by call_id convention alone.
CREATE TABLE call (
    call_id     text        PRIMARY KEY,
    title       text        NULL,
    status      text        NOT NULL DEFAULT 'regular',
    external_id text        NULL,
    custom      jsonb       NULL,

    -- What the SESSION of a call holds and nothing else does: the stage, whether the machine
    -- finished, the slots the writers filled, and the two marks that say how far the call has got.
    -- Not the words — those are call_message. Written in the same batch as the turn's words, so a
    -- crash cannot land one without the other.
    --
    -- The marks are here and nowhere else on purpose. An edit deletes the rows it replaces, so the
    -- largest ordinal and turn index still in call_message are smaller than the largest ever issued,
    -- and a session that recomputed from the surviving rows would hand the next turn a place a
    -- deleted row already stood in.
    state       jsonb       NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT call_status_check CHECK (status IN ('regular', 'archived'))
);

-- Who may see a call. principal_key is opaque: this schema never joins it to a user table, and the
-- consumer's identity model stays the consumer's. A call carries as many keys as are attached, so a
-- tenant key can be attached when the call starts and a person's key when identity is learned.
CREATE TABLE call_principal (
    call_id       text        NOT NULL REFERENCES call(call_id) ON DELETE CASCADE,
    principal_key text        NOT NULL,
    role          text        NOT NULL,
    attached_at   timestamptz NOT NULL DEFAULT now(),

    PRIMARY KEY (principal_key, call_id)
);

-- The listing filters by principal, so principal leads the primary key. This index serves the other
-- direction, which reading and detaching one call's claims use.
CREATE INDEX call_principal_call_idx ON call_principal (call_id);

-- No index on title. Searching the list is the consumer's, and a consumer who wants substring match
-- adds their own GIN index over pg_trgm rather than paying for one nobody asked for.
GRANT SELECT, INSERT, UPDATE, DELETE ON call, call_principal TO agentcore_writer;


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

    -- The handle an edit names its parent by. A client that edits an earlier message tells the host
    -- which message the new one hangs off, and the host turns that name into an ordinal to know
    -- where to cut. Position cannot answer it: the browser never learns an ordinal, and an ordinal
    -- moves meaning under it the moment anything is deleted.
    --
    -- Every row carries one. The client names the message it sent; the host names its own replies,
    -- because the message an edit anchors on is usually a reply and no client can have named one
    -- before it existed.
    message_id text        NOT NULL,

    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),

    PRIMARY KEY (call_id, ordinal),

    -- What the id -> ordinal lookup an edit does reads, and what stops two rows of one call
    -- answering to one name.
    CONSTRAINT call_message_message_id_unique UNIQUE (call_id, message_id)
);

CREATE INDEX call_message_updated_at_idx ON call_message (updated_at);
CREATE INDEX call_message_turn_idx       ON call_message (call_id, turn_index);
CREATE INDEX call_message_retention_idx  ON call_message (call_id, updated_at DESC);

-- UPDATE amends a reply the caller cut short. DELETE erases one caller, and withdraws the tail of a
-- call an edit replaced. Neither is a hole: this table holds words, and words stay erasable.
-- Retention deletes the call itself and reaches these rows through the cascade, so it needs no
-- privilege of its own here.
GRANT SELECT, INSERT, UPDATE, DELETE ON call_message TO agentcore_writer;


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

-- Refusals 4, 5 and 6: the table owner is refused as well.
--
-- Know what these stop. They stop the writer role and they stop an accident. They do not stop an
-- owner who means it: ALTER TABLE ... DISABLE TRIGGER USER leaves no trace, and
-- SET session_replication_role = replica bypasses all three in one statement with no DDL.
CREATE FUNCTION agentcore_audit_refuse() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'audit_event is append-only. A correction is a new event that names the old one.';
END $$;

CREATE TRIGGER audit_event_no_update
    BEFORE UPDATE ON audit_event FOR EACH ROW EXECUTE FUNCTION agentcore_audit_refuse();
CREATE TRIGGER audit_event_no_delete
    BEFORE DELETE ON audit_event FOR EACH ROW EXECUTE FUNCTION agentcore_audit_refuse();
CREATE TRIGGER audit_event_no_truncate
    BEFORE TRUNCATE ON audit_event FOR EACH STATEMENT EXECUTE FUNCTION agentcore_audit_refuse();
