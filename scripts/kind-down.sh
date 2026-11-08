#!/usr/bin/env bash
# Delete the local kind cluster created by kind-up.sh.
. "$(dirname "$0")/lib.sh"
need kind
kind delete cluster --name "${CLUSTER:-part11}"
