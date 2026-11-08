#!/usr/bin/env bash
# Uninstall ONLY record-signatures.   RecordSignatures/scripts/undeploy.sh -n NAMESPACE -r RELEASE
. "$(dirname "$0")/../../scripts/lib.sh"
need helm
NS=part11; REL=record-signatures
while [ $# -gt 0 ]; do
  case "$1" in
    -n|--namespace) NS="$2"; shift 2 ;;
    -r|--release) REL="$2"; shift 2 ;;
    *) die "unknown option $1" ;;
  esac
done
helm uninstall "$REL" -n "$NS"
