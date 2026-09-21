DO $$
BEGIN
    CREATE ROLE agentcore_writer NOLOGIN;
EXCEPTION
    WHEN duplicate_object OR unique_violation THEN
        NULL;
    WHEN insufficient_privilege THEN
        RAISE EXCEPTION 'agentcore_writer does not exist and this role may not create it. Either CREATE ROLE agentcore_writer NOLOGIN as a role that may, or grant CREATEROLE to the migrating role.';
END $$;

CREATE SCHEMA IF NOT EXISTS agentcore;

GRANT USAGE ON SCHEMA agentcore TO agentcore_writer;

GRANT SELECT ON agentcore.schema_migration TO agentcore_writer;

CREATE TABLE agentcore.conversation (
    conversation_id      text        PRIMARY KEY,
    title        text        NULL,
    status       text        NOT NULL DEFAULT 'regular',
    external_id  text        NULL,
    custom       jsonb       NULL,
    state        jsonb       NULL,
    next_ordinal integer     NOT NULL DEFAULT 0,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT conversation_status_check CHECK (status IN ('regular', 'archived'))
);

CREATE TABLE agentcore.conversation_principal (
    conversation_id       text        NOT NULL REFERENCES agentcore.conversation(conversation_id) ON DELETE CASCADE,
    principal_key text        NOT NULL,
    role          text        NOT NULL,
    attached_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (principal_key, conversation_id)
);

CREATE INDEX conversation_principal_conversation_idx ON agentcore.conversation_principal (conversation_id);

GRANT SELECT, INSERT, UPDATE, DELETE ON agentcore.conversation, agentcore.conversation_principal TO agentcore_writer;

CREATE TABLE agentcore.conversation_message (
    conversation_id    text        NOT NULL REFERENCES agentcore.conversation(conversation_id) ON DELETE CASCADE,
    ordinal    integer     NOT NULL,
    turn_index integer     NOT NULL,
    role       text        NOT NULL,
    content    jsonb       NOT NULL,
    message_id text        NOT NULL,
    covers_up_to integer NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (conversation_id, ordinal),
    CONSTRAINT conversation_message_message_id_unique UNIQUE (conversation_id, message_id)
);

CREATE INDEX conversation_message_updated_at_idx ON agentcore.conversation_message (updated_at);

CREATE INDEX conversation_message_turn_idx       ON agentcore.conversation_message (conversation_id, turn_index);

CREATE INDEX conversation_message_retention_idx  ON agentcore.conversation_message (conversation_id, updated_at DESC);

CREATE INDEX conversation_message_summary_idx    ON agentcore.conversation_message (conversation_id, ordinal DESC) WHERE covers_up_to IS NOT NULL;

GRANT SELECT, INSERT, UPDATE, DELETE ON agentcore.conversation_message TO agentcore_writer;

CREATE TABLE agentcore.response_continuation (
    store_id        text        PRIMARY KEY,
    conversation_id text        NOT NULL REFERENCES agentcore.conversation(conversation_id) ON DELETE CASCADE,
    envelope        jsonb       NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX response_continuation_conversation_idx ON agentcore.response_continuation (conversation_id);

CREATE INDEX response_continuation_retention_idx ON agentcore.response_continuation (updated_at);

REVOKE ALL ON agentcore.response_continuation FROM PUBLIC;

REVOKE ALL ON agentcore.response_continuation FROM agentcore_writer;

GRANT SELECT, INSERT, UPDATE, DELETE ON agentcore.response_continuation TO agentcore_writer;

CREATE TABLE agentcore.audit_event (
    write_position  bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    conversation_id         text        NOT NULL,
    event_id        uuid        NOT NULL,
    sequence        bigint      NOT NULL,
    kind            text        NOT NULL,
    occurred_at     timestamptz NOT NULL,
    turn_index      integer     NULL,
    amends_event_id uuid        NULL,
    payload         jsonb       NOT NULL DEFAULT '{}'::jsonb,

    CONSTRAINT audit_event_conversation_sequence_unique UNIQUE (conversation_id, sequence),
    CONSTRAINT audit_event_conversation_event_unique UNIQUE (conversation_id, event_id)
);

REVOKE ALL ON agentcore.audit_event FROM PUBLIC;

REVOKE ALL ON agentcore.audit_event FROM agentcore_writer;

GRANT INSERT, SELECT ON agentcore.audit_event TO agentcore_writer;

CREATE FUNCTION agentcore.agentcore_audit_refuse() RETURNS trigger LANGUAGE plpgsql AS $$

BEGIN
    RAISE EXCEPTION 'audit_event is append-only. A correction is a new event that names the old one.';
END $$;

CREATE TRIGGER audit_event_no_update
    BEFORE UPDATE ON agentcore.audit_event FOR EACH ROW EXECUTE FUNCTION agentcore.agentcore_audit_refuse();

CREATE TRIGGER audit_event_no_delete
    BEFORE DELETE ON agentcore.audit_event FOR EACH ROW EXECUTE FUNCTION agentcore.agentcore_audit_refuse();
    
CREATE TRIGGER audit_event_no_truncate
    BEFORE TRUNCATE ON agentcore.audit_event FOR EACH STATEMENT EXECUTE FUNCTION agentcore.agentcore_audit_refuse();
