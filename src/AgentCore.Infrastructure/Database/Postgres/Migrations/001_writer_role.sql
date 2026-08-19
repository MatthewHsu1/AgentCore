-- The role the running system connects as. Store 1 and store 3 share it.
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
