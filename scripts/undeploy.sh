#!/usr/bin/env bash
# Remove the release. PersistentVolumeClaims (database data) and Secrets are KEPT unless --purge is given.
#
#   scripts/undeploy.sh -n part11 -r part11
#   scripts/undeploy.sh -n part11 -r part11 --purge     # also delete data and secrets (irreversible; records and signatures are lost)
. "$(dirname "$0")/lib.sh"
need helm

NS=part11; REL=part11; PURGE=0
while [ $# -gt 0 ]; do
  case "$1" in
    -n|--namespace) NS="$2"; shift 2 ;;
    -r|--release) REL="$2"; shift 2 ;;
    --purge) PURGE=1; shift ;;
    -h|--help) sed -n '2,6p' "$0"; exit 0 ;;
    *) die "unknown option $1" ;;
  esac
done

helm uninstall "$REL" -n "$NS"
if [ "$PURGE" = 1 ]; then
  need kubectl
  warn "deleting data and secrets for release $REL in $NS"
  kubectl -n "$NS" delete pvc -l app.kubernetes.io/instance="$REL" --ignore-not-found
  kubectl -n "$NS" delete secret -l app.kubernetes.io/instance="$REL" --ignore-not-found
else
  log "kept PersistentVolumeClaims and Secrets (use --purge to delete them)"
fi
