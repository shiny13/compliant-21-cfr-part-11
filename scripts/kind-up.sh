#!/usr/bin/env bash
# Local Kubernetes for development: create a kind cluster, build the images, load them, and deploy with development values.
#   scripts/kind-up.sh
#   scripts/k8s-smoke-test.sh
#   scripts/kind-down.sh
. "$(dirname "$0")/lib.sh"
need kind; need kubectl; need docker; need helm

CLUSTER="${CLUSTER:-part11}"
kind get clusters 2>/dev/null | grep -qx "$CLUSTER" || { log "creating kind cluster '$CLUSTER'"; kind create cluster --name "$CLUSTER"; }
"$(dirname "$0")/build-images.sh" --tag local
for s in "${SERVICES[@]}"; do kind load docker-image "$(image_ref "$s" local)" --name "$CLUSTER"; done
"$(dirname "$0")/deploy.sh" --dev --tag local -n part11 -r part11
