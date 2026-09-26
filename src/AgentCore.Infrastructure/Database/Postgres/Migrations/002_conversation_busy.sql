-- The busy mark of a conversation while one of its turns runs: a lease that lapses at busy_until.
-- No foreign key to conversation: the first turn of a conversation marks it before its row exists.
-- UNLOGGED: a mark is written twice per turn, once before the model call, so its commit must not wait for a WAL
-- flush (measured 0.3-1 ms unlogged against 60-630 ms logged on a loaded disk). A crash of the server empties the
-- table, which frees every conversation exactly as lapsed leases would; the store's turn check stays the backstop.
CREATE UNLOGGED TABLE agentcore.conversation_busy (
    conversation_id text        PRIMARY KEY,
    holder          text        NOT NULL,
    busy_until      timestamptz NOT NULL
);

REVOKE ALL ON agentcore.conversation_busy FROM PUBLIC;

GRANT SELECT, INSERT, UPDATE, DELETE ON agentcore.conversation_busy TO agentcore_writer;
