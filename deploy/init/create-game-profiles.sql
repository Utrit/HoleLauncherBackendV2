SELECT 'CREATE DATABASE game_profiles' WHERE NOT EXISTS (
    SELECT FROM pg_database WHERE datname = 'game_profiles'
)\gexec
