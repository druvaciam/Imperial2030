#!/bin/sh
# Starts the game, or updates it to a newly copied image. Run from anywhere, with sudo:
#
#   sudo sh start.sh          port 8080 the first time; afterwards whatever port it already uses
#   sudo sh start.sh 9000     run on port 9000 (remembered for next time)
#
# Needs Docker with the compose plugin. Everything else - the database, its password, the login key -
# is set up here on the first run and kept in .env next to this script.
set -e
cd "$(dirname "$0")"

DEFAULT_PORT=8080

if ! command -v docker >/dev/null 2>&1; then
    echo "Docker is not installed. Install it first, e.g.:  curl -fsSL https://get.docker.com | sh" >&2
    exit 1
fi
if docker compose version >/dev/null 2>&1; then
    COMPOSE="docker compose"
elif command -v docker-compose >/dev/null 2>&1; then
    COMPOSE="docker-compose"
else
    echo "Docker is installed but its compose plugin is not (package docker-compose-plugin or docker-compose-v2)." >&2
    exit 1
fi

# First run: generate the secrets. Never regenerated - the database is locked with this password.
if [ ! -f .env ]; then
    password="$(head -c 16 /dev/urandom | od -An -tx1 | tr -d ' \n')Aa1!"
    key="$(head -c 32 /dev/urandom | base64 | tr -d '\n')"
    printf 'PORT=%s\nMSSQL_SA_PASSWORD=%s\nJWT_KEY=%s\n' "$DEFAULT_PORT" "$password" "$key" > .env
    chmod 600 .env
    echo "Created .env with new secrets (keep this file)."
fi

# The port: from the command line if given, otherwise the one already in .env.
if [ -n "$1" ]; then
    case "$1" in
        ''|*[!0-9]*) echo "Port must be a number, got: $1" >&2; exit 1 ;;
    esac
    if [ "$1" -lt 1 ] || [ "$1" -gt 65535 ]; then
        echo "Port must be between 1 and 65535, got: $1" >&2
        exit 1
    fi
    if grep -q '^PORT=' .env; then
        sed -i "s/^PORT=.*/PORT=$1/" .env
    else
        echo "PORT=$1" >> .env
    fi
fi
PORT="$(grep '^PORT=' .env | cut -d= -f2)"
PORT="${PORT:-$DEFAULT_PORT}"   # same fallback docker-compose.yml uses

# A freshly copied image replaces the running one; without a new file the current image is reused.
if [ -f imperial2030-sql-app.tar ]; then
    docker load -i imperial2030-sql-app.tar
fi

$COMPOSE up -d --remove-orphans
docker image prune -f >/dev/null   # drop the image an update replaced

address="$(hostname -I 2>/dev/null | cut -d' ' -f1)"
echo
echo "Running on port $PORT:  http://${address:-<this-server>}:$PORT"
