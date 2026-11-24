SELECT pg_advisory_lock(1437240813);
SELECT 'CREATE DATABASE orchestrator' WHERE NOT EXISTS (
    SELECT 1 FROM pg_database WHERE datname = 'orchestrator'
) \gexec
