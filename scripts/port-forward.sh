#!/usr/bin/env bash
# Forward the three services to localhost: AuthenticationCore :5194, RecordAuditTrails :5196, RecordSignatures :5197.
# Runs in the foreground until Ctrl-C.
. "$(dirname "$0")/lib.sh"
need kubectl

NS=part11; REL=part11
while [ $# -gt 0 ]; do
  case "$1" in
    -n|--namespace) NS="$2"; shift 2 ;;
    -r|--release) REL="$2"; shift 2 ;;
    -h|--help) sed -n '2,3p' "$0"; exit 0 ;;
    *) die "unknown option $1" ;;
  esac
done

pids=()
trap 'kill "${pids[@]}" 2>/dev/null' EXIT INT TERM
kubectl -n "$NS" port-forward "svc/$REL-authentication-core" 5194:8080 & pids+=($!)
kubectl -n "$NS" port-forward "svc/$REL-record-audit-trails" 5196:8080 & pids+=($!)
kubectl -n "$NS" port-forward "svc/$REL-record-signatures" 5197:8080 & pids+=($!)
wait
