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
