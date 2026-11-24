CREATE TABLE IF NOT EXISTS operations (
    id uuid PRIMARY KEY,
    actor_id uuid NOT NULL,
    state integer NOT NULL,
    outcome integer,
    created_at timestamptz NOT NULL DEFAULT now(),
    finished_at timestamptz
);

ALTER TABLE operations ADD COLUMN IF NOT EXISTS storage_domain text;
ALTER TABLE operations ADD COLUMN IF NOT EXISTS expires_at timestamptz;

ALTER TABLE operations ADD COLUMN IF NOT EXISTS error_message text;

ALTER TABLE operations ADD COLUMN IF NOT EXISTS step_deadline timestamptz;
ALTER TABLE operations ADD COLUMN IF NOT EXISTS transfer_deadline timestamptz;
