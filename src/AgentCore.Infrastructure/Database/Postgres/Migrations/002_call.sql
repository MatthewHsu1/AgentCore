-- Store 0 — what a call is, apart from its words. One row per thread in a caller's list.
--
-- This table comes first because call_message hangs off it. A call exists before it has words, and
-- deleting it takes the words with it: one statement, one transaction, no ordering to get wrong.
--
-- audit_event is deliberately NOT a child. 005 refuses every DELETE on it, so a cascade could not
-- run even if one were declared. The trail outlives the conversation on purpose, and relates to a
-- call by call_id convention alone.
CREATE TABLE call (
    call_id     text        PRIMARY KEY,
    title       text        NULL,
    status      text        NOT NULL DEFAULT 'regular',
    external_id text        NULL,
    custom      jsonb       NULL,

    -- What the SESSION of a call holds and nothing else does: the stage, whether the machine
    -- finished, and the slots the writers filled. Not the words — those are call_message — and not
    -- any number that addresses them, because a fact with two homes gets one chance to disagree.
    -- Written in the same batch as the turn's words, so a crash cannot land one without the other.
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
