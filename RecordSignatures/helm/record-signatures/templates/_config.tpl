{{- define "record-signatures.configData" -}}
Database__MigrateOnStartup: "false"
Swagger__Enabled: {{ .Values.swaggerEnabled | quote }}
AuthenticationCore__Issuer: {{ .Values.authentication.issuer | quote }}
AuthenticationCore__Audience: {{ .Values.authentication.audience | quote }}
{{- $auth := include "part11-common.dependencyUrl" (dict "root" . "value" .Values.authentication.baseUrl "chart" "authentication-core") }}
AuthenticationCore__JwksUrl: {{ printf "%s/.well-known/jwks.json" $auth | quote }}
AuthenticationCore__JwksRefreshInterval: {{ .Values.authentication.jwksRefreshInterval | quote }}
AuthenticationCore__Revocation__FeedUrl: {{ printf "%s/auth/revocations" $auth | quote }}
AuthenticationCore__Revocation__PollInterval: {{ .Values.authentication.revocationPollInterval | quote }}
Signatures__AuthServiceBaseUrl: {{ $auth | quote }}
Signatures__AuditTrailBaseUrl: {{ include "part11-common.dependencyUrl" (dict "root" . "value" .Values.auditTrail.baseUrl "chart" "record-audit-trails") | quote }}
Signatures__OrganizationName: {{ .Values.signatures.organizationName | quote }}
Signatures__MaxFailedAttempts: {{ .Values.signatures.maxFailedAttempts | quote }}
Signatures__FailureWindow: {{ .Values.signatures.failureWindow | quote }}
Signatures__LockoutDuration: {{ .Values.signatures.lockoutDuration | quote }}
Signatures__ReviewIntervalMonths: {{ .Values.signatures.reviewIntervalMonths | quote }}
Signatures__MaxCommentLength: {{ .Values.signatures.maxCommentLength | quote }}
Signatures__HttpTimeout: {{ .Values.signatures.httpTimeout | quote }}
{{- if .Values.signatures.alertWebhookUrl }}
Signatures__AlertWebhookUrl: {{ .Values.signatures.alertWebhookUrl | quote }}
{{- end }}
Signatures__ActiveSealKeyId: {{ required "signatures.activeSealKeyId is required: the API refuses to start without an active seal key" .Values.signatures.activeSealKeyId | quote }}
{{- range $key, $value := .Values.config }}
{{ $key }}: {{ $value | quote }}
{{- end }}
{{- end -}}

{{/* Used only when secret.existingSecret is empty. */}}
{{- define "record-signatures.secretData" -}}
{{- with .Values.database }}
ConnectionStrings__SignaturesDb: {{ printf "Host=%s;Port=%v;Database=%s;Username=%s;Password=%s;SSL Mode=%s" (default (include "part11-common.siblingName" (dict "root" $ "chart" "postgres")) .host) .port .name .username (required "database.password is required (or set secret.existingSecret)" .password) .sslMode | quote }}
{{- end }}
{{- range $id, $key := .Values.signatures.sealKeys }}
Signatures__SealKeys__{{ $id }}: {{ $key | quote }}
{{- end }}
{{- if .Values.authentication.feedServiceKey }}
AuthenticationCore__Revocation__FeedServiceKey: {{ .Values.authentication.feedServiceKey | quote }}
{{- end }}
{{- end -}}
