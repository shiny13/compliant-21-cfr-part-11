#!/usr/bin/env bash
# Create the Kubernetes Secrets the platform chart expects, with freshly generated random values.
# Existing Secrets are NEVER overwritten (rotating a database password or the signing key needs a plan; see deploy/README.md).
#
#   scripts/generate-secrets.sh -n part11 -r part11                  # one administrator: admin
#   scripts/generate-secrets.sh -n part11 -r part11 --demo-users     # + manager, reviewer, technician
#   scripts/generate-secrets.sh -n part11 -r part11 --user alice:Reviewer --user bob:LabManager
#
# Secrets created (for release <r>):
#   <r>-postgres          POSTGRES_PASSWORD, AUDIT_DB_PASSWORD, SIGNATURES_DB_PASSWORD
#   <r>-auth-signing-key  signing-key.pem (RSA 2048, signs access tokens)
#   <r>-auth              revocation feed key + user accounts (with fixed ids)
#   <r>-audit             RecordAuditTrails connection string + feed key
#   <r>-signatures        RecordSignatures connection string + HMAC seal key + feed key
# Generated user passwords are printed ONCE, here. Store them in your password manager.
. "$(dirname "$0")/lib.sh"
need kubectl; need openssl

NS=part11; REL=part11; USERS=(admin:Administrator); DEMO=0; CUSTOM=0; SEAL_ID=key-1
while [ $# -gt 0 ]; do
  case "$1" in
    -n|--namespace) NS="$2"; shift 2 ;;
    -r|--release) REL="$2"; shift 2 ;;
    --demo-users) DEMO=1; shift ;;
    --user) [ "$CUSTOM" = 0 ] && USERS=() && CUSTOM=1; USERS+=("$2"); shift 2 ;;
    --seal-key-id) SEAL_ID="$2"; shift 2 ;;
    -h|--help) sed -n '2,19p' "$0"; exit 0 ;;
    *) die "unknown option $1" ;;
  esac
done
[ "$DEMO" = 1 ] && USERS+=(manager:LabManager reviewer:Reviewer technician:LabTechnician)

kubectl get namespace "$NS" >/dev/null 2>&1 || kubectl create namespace "$NS" >/dev/null
exists() { kubectl -n "$NS" get secret "$1" >/dev/null 2>&1; }
label() { kubectl -n "$NS" label secret "$1" app.kubernetes.io/part-of=part11-platform app.kubernetes.io/instance="$REL" --overwrite >/dev/null; }

# PostgreSQL
PG_SECRET="$REL-postgres"
if exists "$PG_SECRET"; then
  warn "$PG_SECRET already exists: kept"
  AUDIT_PW="$(kubectl -n "$NS" get secret "$PG_SECRET" -o jsonpath='{.data.AUDIT_DB_PASSWORD}' | base64 -d)"
  SIG_PW="$(kubectl -n "$NS" get secret "$PG_SECRET" -o jsonpath='{.data.SIGNATURES_DB_PASSWORD}' | base64 -d)"
else
  AUDIT_PW="$(random_secret 24)"; SIG_PW="$(random_secret 24)"
  kubectl -n "$NS" create secret generic "$PG_SECRET" \
    --from-literal=POSTGRES_PASSWORD="$(random_secret 24)" \
    --from-literal=AUDIT_DB_PASSWORD="$AUDIT_PW" --from-literal=SIGNATURES_DB_PASSWORD="$SIG_PW" >/dev/null
  label "$PG_SECRET"; log "created $PG_SECRET"
fi

# Feed key shared by AuthenticationCore and the API services. If the auth secret exists, reuse its key.
if exists "$REL-auth"; then
  FEED_KEY="$(kubectl -n "$NS" get secret "$REL-auth" -o jsonpath='{.data.AuthService__RevocationFeedServiceKey}' | base64 -d)"
else
  FEED_KEY="$(random_secret 32)"
fi

# AuthenticationCore signing key
if exists "$REL-auth-signing-key"; then warn "$REL-auth-signing-key already exists: kept"
else
  tmp="$(mktemp)"; trap 'rm -f "$tmp"' EXIT
  openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "$tmp" 2>/dev/null
  kubectl -n "$NS" create secret generic "$REL-auth-signing-key" --from-file=signing-key.pem="$tmp" >/dev/null
  label "$REL-auth-signing-key"; log "created $REL-auth-signing-key"
fi

# AuthenticationCore accounts
if exists "$REL-auth"; then warn "$REL-auth already exists: kept (users and feed key unchanged)"
else
  args=(--from-literal=AuthService__RevocationFeedServiceKey="$FEED_KEY")
  i=0; printed=""
  for entry in "${USERS[@]}"; do
    name="${entry%%:*}"; role="${entry#*:}"; pw="$(random_secret 18)"
    args+=(--from-literal="AuthService__SeedUsers__${i}__Id=$(new_uuid)" --from-literal="AuthService__SeedUsers__${i}__Username=$name"
           --from-literal="AuthService__SeedUsers__${i}__Password=$pw" --from-literal="AuthService__SeedUsers__${i}__Roles__0=$role")
    printed+=$(printf '    %-12s %-14s %s\n' "$name" "$role" "$pw")$'\n'
    i=$((i + 1))
  done
  kubectl -n "$NS" create secret generic "$REL-auth" "${args[@]}" >/dev/null
  label "$REL-auth"; log "created $REL-auth"
  printf '\n  Accounts (shown once):\n%s\n' "$printed"
fi

# RecordAuditTrails
if exists "$REL-audit"; then warn "$REL-audit already exists: kept"
else
  kubectl -n "$NS" create secret generic "$REL-audit" \
    --from-literal=ConnectionStrings__AuditDb="Host=$REL-postgres;Port=5432;Database=record_audit_trails;Username=audit_app;Password=$AUDIT_PW;SSL Mode=Prefer" \
    --from-literal=AuthenticationCore__Revocation__FeedServiceKey="$FEED_KEY" >/dev/null
  label "$REL-audit"; log "created $REL-audit"
fi

# RecordSignatures (HMAC seal key: 48 random bytes, base64)
if exists "$REL-signatures"; then warn "$REL-signatures already exists: kept"
else
  kubectl -n "$NS" create secret generic "$REL-signatures" \
    --from-literal=ConnectionStrings__SignaturesDb="Host=$REL-postgres;Port=5432;Database=record_signatures;Username=signatures_app;Password=$SIG_PW;SSL Mode=Prefer" \
    --from-literal="Signatures__SealKeys__$SEAL_ID=$(openssl rand -base64 48 | tr -d '\n')" \
    --from-literal=AuthenticationCore__Revocation__FeedServiceKey="$FEED_KEY" >/dev/null
  label "$REL-signatures"; log "created $REL-signatures"
  warn "BACK UP the seal key ($REL-signatures / Signatures__SealKeys__$SEAL_ID) and keep it as long as the signatures must verify."
fi
log "secrets ready in namespace '$NS'"
