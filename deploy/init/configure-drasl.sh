#!/bin/sh
set -eu

source_config=/drasl-source
runtime_config=/drasl-config
mkdir -p "$runtime_config"

for entry in "$source_config"/*; do
    [ -e "$entry" ] || continue
    [ "$entry" = "$source_config/config.toml" ] || cp -R "$entry" "$runtime_config/"
done

temp=$(mktemp "$runtime_config/config.XXXXXX")
trap 'rm -f "$temp"' EXIT
awk -v origin="$PUBLIC_BASE_URL" '
    BEGIN {
        if (origin !~ /^https?:\/\/(\[[0-9A-Fa-f:]+\]|[[:alnum:].-]+)(:[0-9]+)?\/?$/)
            exit 1
        sub(/\/$/, "", origin)
        # The local HTTP port is published on IPv4 loopback.
        if (origin ~ /^http:\/\/localhost(:|$)/) sub(/localhost/, "127.0.0.1", origin)
        domain = origin
        sub(/^https?:\/\//, "", domain)
        sub(/:[0-9]+$/, "", domain)
        gsub(/^\[|\]$/, "", domain)
        print "Domain = \"" tolower(domain) "\""
        print "BaseURL = \"" origin "/drasl\""
        top = 1
    }
    /^[[:space:]]*\[/ { top = 0 }
    top && /^[[:space:]]*(BaseURL|Domain)[[:space:]]*=/ { next }
    { print }
' "$source_config/config.toml" > "$temp"
chmod 644 "$temp"
mv -f "$temp" "$runtime_config/config.toml"
