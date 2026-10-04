-- A response id no longer stores a session snapshot: conversation.state is the one snapshot, written
-- with the words in the same commit. response_continuation shrinks to the one fact worth keeping, which
-- conversation a response id continues, and when that row was written for the retention sweep.
--
-- The rows whose store_id names a conversation were the old same-id pointer at the conversation store's own state; the
-- conversation table already answers for those ids, so the pointer rows go.
DELETE FROM agentcore.response_continuation rc
 WHERE EXISTS (
     SELECT 1 FROM agentcore.conversation c WHERE c.conversation_id = rc.store_id
 );

ALTER TABLE agentcore.response_continuation
    DROP COLUMN envelope,
    DROP COLUMN updated_at;

-- Replaces response_continuation_retention_idx, dropped along with updated_at above: a response id is
-- written once and never rewritten, so the sweep ages a row out from when it was created.
CREATE INDEX response_continuation_retention_idx ON agentcore.response_continuation (created_at);
