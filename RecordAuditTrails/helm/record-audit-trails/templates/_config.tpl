{{- define "record-audit-trails.configData" -}}
Database__MigrateOnStartup: "false"
Swagger__Enabled: {{ .Values.swaggerEnabled | quote }}
AuthenticationCore__Issuer: {{ .Values.authentication.issuer | quote }}
AuthenticationCore__Audience: {{ .Values.authentication.audience | quote }}
{{- $auth := include "part11-common.dependencyUrl" (dict "root" . "value" .Values.authentication.baseUrl "chart" "authentication-core") }}
AuthenticationCore__JwksUrl: {{ printf "%s/.well-known/jwks.json" $auth | quote }}
AuthenticationCore__JwksRefreshInterval: {{ .Values.authentication.jwksRefreshInterval | quote }}
AuthenticationCore__Revocation__FeedUrl: {{ printf "%s/auth/revocations" $auth | quote }}
AuthenticationCore__Revocation__PollInterval: {{ .Values.authentication.revocationPollInterval | quote }}
Retention__DefaultYears: {{ .Values.retention.defaultYears | quote }}
{{- range $type, $years := .Values.retention.byRecordType }}
Retention__ByRecordType__{{ $type }}: {{ $years | quote }}
{{- end }}
{{- range $key, $value := .Values.config }}
{{ $key }}: {{ $value | quote }}
{{- end }}
{{- end -}}

{{/* Used only when secret.existingSecret is empty. */}}
{{- define "record-audit-trails.secretData" -}}
{{- with .Values.database }}
ConnectionStrings__AuditDb: {{ printf "Host=%s;Port=%v;Database=%s;Username=%s;Password=%s;SSL Mode=%s" (default (include "part11-common.siblingName" (dict "root" $ "chart" "postgres")) .host) .port .name .username (required "database.password is required (or set secret.existingSecret)" .password) .sslMode | quote }}
{{- end }}
{{- if .Values.authentication.feedServiceKey }}
AuthenticationCore__Revocation__FeedServiceKey: {{ .Values.authentication.feedServiceKey | quote }}
{{- end }}
{{- end -}}
