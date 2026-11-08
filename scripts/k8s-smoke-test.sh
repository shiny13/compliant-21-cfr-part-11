#!/usr/bin/env bash
# Run scripts/smoke-test.sh against a Kubernetes release: port-forwards the services and reads the account passwords
# from the Secret. Needs the accounts admin, manager, reviewer and technician
# (scripts/generate-secrets.sh --demo-users, or the --dev values).
. "$(dirname "$0")/lib.sh"
need kubectl; need jq

NS=part11; REL=part11
while [ $# -gt 0 ]; do
  case "$1" in
    -n|--namespace) NS="$2"; shift 2 ;;
    -r|--release) REL="$2"; shift 2 ;;
    -h|--help) sed -n '2,5p' "$0"; exit 0 ;;
    *) die "unknown option $1" ;;
  esac
done

secret="$REL-auth"
if kubectl -n "$NS" get secret "$secret" >/dev/null 2>&1; then
  pw_of() { # username -> password, looked up in the Secret
    local i=0 v
    while v=$(kubectl -n "$NS" get secret "$secret" -o "jsonpath={.data.AuthService__SeedUsers__${i}__Username}" 2>/dev/null | base64 -d) && [ -n "$v" ]; do
      if [ "$v" = "$1" ]; then kubectl -n "$NS" get secret "$secret" -o "jsonpath={.data.AuthService__SeedUsers__${i}__Password}" | base64 -d; return; fi
      i=$((i + 1))
    done
  }
  export ADMIN_PASSWORD="$(pw_of admin)" MANAGER_PASSWORD="$(pw_of manager)" REVIEWER_PASSWORD="$(pw_of reviewer)" TECH_PASSWORD="$(pw_of technician)"
else
  warn "no Secret $secret: using the development passwords"
fi

"$(dirname "$0")/port-forward.sh" -n "$NS" -r "$REL" >/dev/null 2>&1 &
PF=$!
trap 'kill $PF 2>/dev/null' EXIT
sleep 3
"$(dirname "$0")/smoke-test.sh"
