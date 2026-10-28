#!/usr/bin/env bash
# SQL against an environment's database, from inside its cluster (Cloud SQL has only a private address).
#
#   infrastructure/k8s/db-shell.sh beta              # interactive psql
#   infrastructure/k8s/db-shell.sh beta < file.sql   # run a file, stop at the first error
#
# A temporary pod runs as the migrate identity and mounts the connection string and the server CA from Secret
# Manager exactly as the migrate Job does, so the password never passes through this machine or the pod spec.
# It connects as the application's database user: everything it changes is a real change. Needs kubectl pointed at
# the environment's cluster (gcloud container clusters get-credentials fandomscore-<env> ...).
set -euo pipefail

env="${1:?usage: db-shell.sh beta|prod [< file.sql]}"
case "$env" in beta | prod) ;; *) echo "Unknown environment: $env" >&2; exit 2 ;; esac
context="$(kubectl config current-context)"
case "$context" in
  *"fandomscore-$env") ;;
  *) echo "kubectl points at $context, not the $env cluster (fandomscore-$env)." >&2; exit 2 ;;
esac

# Inside the pod: split the Npgsql connection string into libpq variables, then psql with the CA verified.
read -r -d '' overrides <<'JSON' || true
{
  "spec": {
    "serviceAccountName": "migrate",
    "securityContext": {"runAsUser": 70, "runAsGroup": 70, "runAsNonRoot": true, "seccompProfile": {"type": "RuntimeDefault"}},
    "containers": [{
      "name": "db-shell",
      "image": "postgres:16-alpine",
      "stdin": true,
      "stdinOnce": true,
      "tty": TTY,
      "command": ["sh", "-c", "c=$(cat /secrets/ConnectionStrings__Database); get() { printf '%s' \"$c\" | tr ';' '\\n' | sed -n \"s/^$1=//p\"; }; export PGHOST=$(get Host) PGDATABASE=$(get Database) PGUSER=$(get Username) PGPASSWORD=$(get Password); exec psql 'sslmode=verify-ca sslrootcert=/secrets/Database__ServerCa' -v ON_ERROR_STOP=1"],
      "volumeMounts": [{"name": "secrets", "mountPath": "/secrets", "readOnly": true}],
      "resources": {"requests": {"cpu": "100m", "memory": "128Mi"}, "limits": {"cpu": "100m", "memory": "128Mi"}},
      "securityContext": {"allowPrivilegeEscalation": false, "capabilities": {"drop": ["ALL"]}}
    }],
    "volumes": [{"name": "secrets", "csi": {"driver": "secrets-store-gke.csi.k8s.io", "readOnly": true,
      "volumeAttributes": {"secretProviderClass": "migrate-secrets"}}}]
  }
}
JSON

if [ -t 0 ]; then tty=true; flags=-it; else tty=false; flags=-i; fi
exec kubectl -n fandomscore run "db-shell-$(date +%s)" --rm "$flags" --restart=Never --quiet \
  --image=postgres:16-alpine --overrides "${overrides/TTY/$tty}"
