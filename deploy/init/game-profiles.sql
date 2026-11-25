CREATE TABLE IF NOT EXISTS game_profiles (
    account_id uuid PRIMARY KEY,
    game_name text CHECK (game_name ~ '^[A-Za-z0-9_]{3,16}$'),
    skin_file_id uuid,
    cape_file_id uuid
);
CREATE UNIQUE INDEX IF NOT EXISTS game_profiles_game_name_key ON game_profiles (lower(game_name));

CREATE TABLE IF NOT EXISTS game_name_history (
    sequence bigint GENERATED ALWAYS AS IDENTITY,
    operation_id uuid PRIMARY KEY,
    account_id uuid NOT NULL,
    game_name text NOT NULL CHECK (game_name ~ '^[A-Za-z0-9_]{3,16}$'),
    provision boolean NOT NULL,
    state text NOT NULL DEFAULT 'pending' CHECK (state IN ('pending','complete','reverted')),
    outcome integer,
    error_message text,
    reported boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS game_name_history_latest ON game_name_history (account_id, sequence DESC) WHERE state = 'complete';

CREATE TABLE IF NOT EXISTS game_skin_models (
    account_id uuid PRIMARY KEY REFERENCES game_profiles(account_id) ON DELETE CASCADE,
    skin_model text NOT NULL CHECK (skin_model IN ('classic', 'slim'))
);

CREATE TABLE IF NOT EXISTS game_texture_history (
    sequence bigint GENERATED ALWAYS AS IDENTITY,
    operation_id uuid PRIMARY KEY,
    account_id uuid NOT NULL,
    file_id uuid NOT NULL,
    is_skin boolean NOT NULL,
    skin_model text NOT NULL CHECK (skin_model IN ('classic', 'slim')),
    state text NOT NULL DEFAULT 'pending' CHECK (state IN ('pending','complete','reverting','reverted')),
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    outcome integer,
    error_message text,
    reported boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS game_texture_history_latest
    ON game_texture_history (account_id, is_skin, sequence DESC) WHERE state = 'complete';
