SELECT pg_advisory_xact_lock(1437240812);
CREATE TABLE IF NOT EXISTS accounts (
    id uuid PRIMARY KEY,
    user_name text NOT NULL,
    normalized_user_name text NOT NULL UNIQUE,
    password_hash bytea NOT NULL,
    password_salt bytea NOT NULL,
    password_iterations integer NOT NULL,
    permissions text[] NOT NULL DEFAULT ARRAY['storage.download','storage.userdata.upload','game.user.edit','game.user.skin','game.user.cape','repository.offer'],
    suppressed boolean NOT NULL DEFAULT false
);
CREATE TABLE IF NOT EXISTS account_sessions (
    session_lookup text PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    expires_at timestamptz NOT NULL
);
CREATE TABLE IF NOT EXISTS account_operation_receipts (
    operation_id uuid PRIMARY KEY,
    outcome integer
);

\getenv admin_salt ADMIN_SALT
\getenv admin_hash ADMIN_HASH
INSERT INTO accounts (id, user_name, normalized_user_name, password_hash, password_salt, password_iterations, permissions)
VALUES (gen_random_uuid(), 'admin', 'ADMIN', decode(:'admin_hash', 'hex'), decode(:'admin_salt', 'hex'), 600000,
    ARRAY['system.user.edit','storage.download','storage.userdata.upload','game.user.edit','game.user.skin','game.user.cape','repository.offer'])
ON CONFLICT (normalized_user_name) DO NOTHING;
