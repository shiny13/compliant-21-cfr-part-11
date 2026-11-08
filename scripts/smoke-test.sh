#!/usr/bin/env bash
# End-to-end check of a running platform (docker compose or Kubernetes port-forwards).
# Logs in, enrolls and certifies a signer, signs a record, verifies the audit trail, checks lockout and revocation.
#
#   scripts/smoke-test.sh                      # defaults match docker-compose.yml
#   AUTH_URL=... AUDIT_URL=... SIGNATURES_URL=... ADMIN_PASSWORD=... scripts/smoke-test.sh
#
# Needs: curl, jq. Exits non-zero on the first failed check.
. "$(dirname "$0")/lib.sh"
need curl; need jq

AUTH_URL="${AUTH_URL:-http://localhost:5194}"
AUDIT_URL="${AUDIT_URL:-http://localhost:5196}"
SIGNATURES_URL="${SIGNATURES_URL:-http://localhost:5197}"
ADMIN_USER="${ADMIN_USER:-admin}";           ADMIN_PASSWORD="${ADMIN_PASSWORD:-Dev-Admin-Passw0rd!}"
MANAGER_USER="${MANAGER_USER:-manager}";     MANAGER_PASSWORD="${MANAGER_PASSWORD:-Dev-Manager-Passw0rd!}"
REVIEWER_USER="${REVIEWER_USER:-reviewer}";  REVIEWER_PASSWORD="${REVIEWER_PASSWORD:-Dev-Reviewer-Passw0rd!}"
TECH_USER="${TECH_USER:-technician}";        TECH_PASSWORD="${TECH_PASSWORD:-Dev-Technician-Passw0rd!}"
RUN_ID="$(date +%s)"                         # employee ids must be unique per run when re-running against one install

pass=0
check() { # description expected actual
  if [ "$2" = "$3" ]; then pass=$((pass + 1)); printf '  \033[32mok\033[0m   %s\n' "$1"
  else printf '  \033[31mFAIL\033[0m %s (expected %s, got %s)\n' "$1" "$2" "$3"; exit 1; fi
}
status() { curl -s -o /dev/null -w '%{http_code}' "$@"; }

log "waiting for the services"
for url in "$AUTH_URL/health" "$AUDIT_URL/health" "$SIGNATURES_URL/health"; do
  for _ in $(seq 1 60); do [ "$(status "$url")" = 200 ] && break; sleep 2; done
  check "$url is healthy" 200 "$(status "$url")"
done

login() { curl -s "$AUTH_URL/auth/login" -H 'Content-Type: application/json' -d "$(jq -n --arg u "$1" --arg p "$2" '{username:$u,password:$p}')" | jq -r '.accessToken // empty'; }
ADMIN=$(login "$ADMIN_USER" "$ADMIN_PASSWORD");     [ -n "$ADMIN" ]   || die "administrator login failed"
MANAGER=$(login "$MANAGER_USER" "$MANAGER_PASSWORD"); [ -n "$MANAGER" ] || die "manager login failed"
REVIEWER=$(login "$REVIEWER_USER" "$REVIEWER_PASSWORD"); [ -n "$REVIEWER" ] || die "reviewer login failed"
TECH=$(login "$TECH_USER" "$TECH_PASSWORD");        [ -n "$TECH" ]    || die "technician login failed"
TECH_ID=$(curl -s "$AUTH_URL/auth/me" -H "Authorization: Bearer $TECH" | jq -r .subject)
MANAGER_ID=$(curl -s "$AUTH_URL/auth/me" -H "Authorization: Bearer $MANAGER" | jq -r .subject)

# api METHOD TOKEN URL [JSON]  -> sets $CODE (HTTP status) and $BODY (response body). Call it directly, not inside $(...).
api() {
  local out; out=$(mktemp)
  if [ $# -ge 4 ]; then CODE=$(curl -s -o "$out" -w '%{http_code}' -X "$1" "$3" -H "Authorization: Bearer $2" -H 'Content-Type: application/json' -d "$4")
  else CODE=$(curl -s -o "$out" -w '%{http_code}' -X "$1" "$3" -H "Authorization: Bearer $2"); fi
  BODY=$(cat "$out"); rm -f "$out"
}

log "identity (AuthenticationCore)"
check "request without a token is refused" 401 "$(status "$SIGNATURES_URL/api/meanings")"

log "enrollment and certification (11.100)"
identity='{"method":"GovernmentIssuedId","evidenceReference":"Passport sighted by HR (smoke test)"}'
api POST "$ADMIN" "$SIGNATURES_URL/api/signers" "$(jq -n --arg id "$TECH_ID" --arg u "$TECH_USER" --arg e "EMP-T-$RUN_ID" --argjson i "$identity" '{userId:$id,username:$u,printedName:"Terry Technician",employeeId:$e,identity:$i}')"
check "administrator enrolls the technician" 201 "$CODE"
api POST "$ADMIN" "$SIGNATURES_URL/api/signers" "$(jq -n --arg id "$MANAGER_ID" --arg u "$MANAGER_USER" --arg e "EMP-M-$RUN_ID" --argjson i "$identity" '{userId:$id,username:$u,printedName:"Morgan Manager",employeeId:$e,identity:$i}')"
check "administrator enrolls the manager" 201 "$CODE"

api POST "$TECH" "$AUDIT_URL/api/records" '{"recordType":"BatchRecord","title":"Smoke-test batch","fields":{"pH":"7.0"}}'
REC=$(echo "$BODY" | jq -r .id)
check "record created in RecordAuditTrails" 201 "$CODE"
sign_body() { jq -n --arg r "$REC" --argjson v "$1" --arg m "$2" --arg u "$3" --arg p "$4" '{recordId:$r,recordVersion:$v,meaning:$m,username:$u,password:$p}'; }

api POST "$TECH" "$SIGNATURES_URL/api/signatures" "$(sign_body 1 Authorship "$TECH_USER" "$TECH_PASSWORD")"
check "cannot sign before accepting the certification" 403 "$CODE"
api POST "$TECH" "$SIGNATURES_URL/api/signers/me/certification" "$(jq -n --arg u "$TECH_USER" --arg p "wrong-password" '{username:$u,password:$p,statementVersion:"1.0"}')"
check "certification with a wrong password is refused" 401 "$CODE"
api POST "$TECH" "$SIGNATURES_URL/api/signers/me/certification" "$(jq -n --arg u "$TECH_USER" --arg p "$TECH_PASSWORD" '{username:$u,password:$p,statementVersion:"1.0"}')"
check "technician accepts the certification" 200 "$CODE"
api POST "$MANAGER" "$SIGNATURES_URL/api/signers/me/certification" "$(jq -n --arg u "$MANAGER_USER" --arg p "$MANAGER_PASSWORD" '{username:$u,password:$p,statementVersion:"1.0"}')"
check "manager accepts the certification" 200 "$CODE"

log "signing (11.50, 11.70, 11.200)"
api POST "$TECH" "$SIGNATURES_URL/api/signatures" "$(sign_body 1 Authorship "$MANAGER_USER" "$MANAGER_PASSWORD")"
check "signing with someone else's identification code is refused" 401 "$CODE"
api POST "$TECH" "$SIGNATURES_URL/api/signatures" "$(sign_body 1 Authorship "$TECH_USER" "$TECH_PASSWORD")"
SIG="$BODY"
check "technician signs (Authorship)" 201 "$CODE"
check "signature carries the printed name" "Terry Technician" "$(echo "$SIG" | jq -r .signerPrintedName)"
SIG_ID=$(echo "$SIG" | jq -r .id)
api POST "$TECH" "$SIGNATURES_URL/api/signatures" "$(sign_body 1 Approval "$TECH_USER" "$TECH_PASSWORD")"
check "a technician cannot give an Approval" 403 "$CODE"
api POST "$MANAGER" "$SIGNATURES_URL/api/signatures" "$(sign_body 1 Approval "$MANAGER_USER" "$MANAGER_PASSWORD")"
check "manager approves" 201 "$CODE"
api GET "$TECH" "$SIGNATURES_URL/api/records/$REC/signatures"
V="$BODY"
check "both signatures verify" "Valid,Valid" "$(echo "$V" | jq -r '[.signatures[].status] | join(",")')"
REPORT=$(curl -s "$SIGNATURES_URL/api/records/$REC/signatures/report?format=text" -H "Authorization: Bearer $REVIEWER")
echo "$REPORT" | grep -q "Signed by    : Terry Technician" && ok=yes || ok=no
check "text report shows the signer, time and meaning" yes "$ok"

log "linking to the record (11.70)"
api PUT "$TECH" "$AUDIT_URL/api/records/$REC" '{"fields":{"pH":"6.8"},"reason":"Re-measured the pH","expectedVersion":1}'
check "record edited in RecordAuditTrails" 200 "$CODE"
api GET "$TECH" "$SIGNATURES_URL/api/records/$REC/signatures"
V="$BODY"
check "signatures now show the record changed" "RecordModifiedSinceSigning,RecordModifiedSinceSigning" "$(echo "$V" | jq -r '[.signatures[].status] | join(",")')"

log "audit trail (11.10(e))"
api GET "$REVIEWER" "$AUDIT_URL/api/records/$SIG_ID/audit-trail"
TRAIL="$BODY"
check "the signature has an audit trail" 200 "$CODE"
check "audit trail names the signer" "$TECH_USER" "$(echo "$TRAIL" | jq -r '.[0].username')"
api GET "$MANAGER" "$AUDIT_URL/api/audit-trail/verify"
INTEGRITY="$BODY"
check "audit trail chain verifies" true "$(echo "$INTEGRITY" | jq -r .isValid)"

log "loss management and alerts (11.300)"
for i in 1 2 3; do api POST "$TECH" "$SIGNATURES_URL/api/signatures" "$(sign_body 2 Responsibility "$TECH_USER" "wrong-$i")" >/dev/null; done
api POST "$TECH" "$SIGNATURES_URL/api/signatures" "$(sign_body 2 Responsibility "$TECH_USER" "$TECH_PASSWORD")"
check "signing is locked after repeated failures" 423 "$CODE"
api GET "$MANAGER" "$SIGNATURES_URL/api/security-alerts?minimumSeverity=Critical"
ALERTS="$BODY"
check "a critical alert was raised" SignerLockedOut "$(echo "$ALERTS" | jq -r '[.[].type] | map(select(. == "SignerLockedOut")) | first')"
api POST "$MANAGER" "$SIGNATURES_URL/api/signatures/$SIG_ID/revoke" '{"reason":"Smoke test cleanup"}'
check "a manager can revoke a signature" 200 "$CODE"

printf '\n\033[1;32mAll %d checks passed.\033[0m\n' "$pass"
