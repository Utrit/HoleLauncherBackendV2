#!/bin/sh
set -eu
umask 077
config=$(mktemp)
trap 'rm -f "$config"' EXIT
cat > "$config" <<EOF
security.protocol=SASL_PLAINTEXT
sasl.mechanism=PLAIN
sasl.jaas.config=org.apache.kafka.common.security.plain.PlainLoginModule required username="init" password="$KAFKA_INIT_PASSWORD";
request.timeout.ms=3000
default.api.timeout.ms=5000
EOF

topics() { /opt/kafka/bin/kafka-topics.sh --bootstrap-server kafka:9092 --command-config "$config" "$@"; }
acl() { /opt/kafka/bin/kafka-acls.sh --bootstrap-server kafka:9092 --command-config "$config" --add "$@"; }

attempt=0
until topics --list >/dev/null 2>&1; do
    attempt=$((attempt + 1))
    if [ "$attempt" -ge 15 ]; then
        topics --list
        exit 1
    fi
    sleep 1
done

access=holelauncher.access.requests.v1
status=holelauncher.orchestrator.status.v1
replies=holelauncher.api.replies.v1
for topic in "$access" "$status" "$replies"; do
    topics --create --if-not-exists --topic "$topic" --partitions 1 --replication-factor 1 \
        --config retention.ms=300000 --config segment.ms=60000
done

acl --allow-principal User:api --operation Write --topic "$access"
acl --allow-principal User:api --operation Read --topic "$replies"
acl --allow-principal User:access --operation Read --topic "$access"
acl --allow-principal User:access --operation Read --group holelauncher-access-v1
acl --allow-principal User:access --operation Write --topic "$status"
acl --allow-principal User:orchestrator --operation Read --topic "$status"
acl --allow-principal User:orchestrator --operation Read --group holelauncher-orchestrator-status-v1
acl --allow-principal User:access --allow-principal User:orchestrator --operation Write --topic "$replies"
acl --allow-principal User:api --allow-principal User:access --allow-principal User:orchestrator --operation IdempotentWrite --cluster

operations=holelauncher.orchestrator.operations.v1
execution=holelauncher.access.execute.v1
for topic in "$operations" "$execution"; do
    topics --create --if-not-exists --topic "$topic" --partitions 1 --replication-factor 1
done
acl --allow-principal User:access --operation Write --topic "$operations"
acl --allow-principal User:orchestrator --operation Read --topic "$operations"
acl --allow-principal User:orchestrator --operation Write --topic "$execution"
acl --allow-principal User:access --operation Read --topic "$execution"

storage_transfers=holelauncher.storage.transfers.v1
storage_decisions=holelauncher.storage.decisions.v1
storage_results=holelauncher.storage.results.v1
for topic in "$storage_transfers" "$storage_decisions" "$storage_results"; do
    topics --create --if-not-exists --topic "$topic" --partitions 1 --replication-factor 1
done
acl --allow-principal User:api --operation Write --topic "$storage_transfers"
acl --allow-principal User:storage --operation Write --topic "$storage_transfers"
acl --allow-principal User:orchestrator --operation Read --topic "$storage_transfers"
acl --allow-principal User:orchestrator --operation Read --topic "$storage_results"
acl --allow-principal User:orchestrator --operation Write --topic "$storage_decisions"
acl --allow-principal User:storage --operation Read --topic "$storage_decisions"
acl --allow-principal User:storage --operation Read --group holelauncher-storage-v1
acl --allow-principal User:storage --operation Write --topic "$storage_results"
acl --allow-principal User:storage --operation IdempotentWrite --cluster
