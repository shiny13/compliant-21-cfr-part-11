{{/* Non-sensitive settings as a YAML map of environment variables. */}}
{{- define "authentication-core.configData" -}}
AuthenticationCore__Issuer: {{ .Values.authentication.issuer | quote }}
AuthenticationCore__Audience: {{ .Values.authentication.audience | quote }}
AuthenticationCore__AccessTokenTtl: {{ .Values.authentication.accessTokenTtl | quote }}
AuthService__RefreshTokenTtl: {{ .Values.authentication.refreshTokenTtl | quote }}
AuthService__MaxFailedAttempts: {{ .Values.authentication.maxFailedAttempts | quote }}
AuthService__LockoutDuration: {{ .Values.authentication.lockoutDuration | quote }}
{{- if .Values.signingKey.existingSecret }}
AuthService__SigningKeyPemPath: {{ printf "/etc/authentication-core/%s" .Values.signingKey.secretKey | quote }}
{{- end }}
{{- range $key, $value := .Values.config }}
{{ $key }}: {{ $value | quote }}
{{- end }}
{{- end -}}

{{/* Sensitive settings as a YAML map of environment variables (used only when secret.existingSecret is empty). */}}
{{- define "authentication-core.secretData" -}}
{{- if .Values.feedServiceKey }}
AuthService__RevocationFeedServiceKey: {{ .Values.feedServiceKey | quote }}
{{- end }}
{{- range $i, $user := .Values.seedUsers }}
{{- if $user.id }}
AuthService__SeedUsers__{{ $i }}__Id: {{ $user.id | quote }}
{{- end }}
AuthService__SeedUsers__{{ $i }}__Username: {{ required "seedUsers[].username is required" $user.username | quote }}
AuthService__SeedUsers__{{ $i }}__Password: {{ required "seedUsers[].password is required" $user.password | quote }}
{{- range $j, $role := $user.roles }}
AuthService__SeedUsers__{{ $i }}__Roles__{{ $j }}: {{ $role | quote }}
{{- end }}
{{- end }}
{{- end -}}

{{- define "authentication-core.volumes" -}}
{{- if .Values.signingKey.existingSecret }}
- name: signing-key
  secret:
    secretName: {{ .Values.signingKey.existingSecret }}
    defaultMode: 288
    items:
      - key: {{ .Values.signingKey.secretKey }}
        path: {{ .Values.signingKey.secretKey }}
{{- end }}
{{- end -}}

{{- define "authentication-core.volumeMounts" -}}
{{- if .Values.signingKey.existingSecret }}
- name: signing-key
  mountPath: /etc/authentication-core
  readOnly: true
{{- end }}
{{- end -}}

{{/* Refuse a configuration that silently breaks token revocation and refresh rotation. */}}
{{- define "authentication-core.validate" -}}
{{- if and (gt (int .Values.replicaCount) 1) (not .Values.acknowledgeInMemoryStores) -}}
{{- fail "authentication-core keeps users, refresh tokens and revocations in memory per pod. Use replicaCount: 1, or set acknowledgeInMemoryStores: true once the stores are durable and shared." -}}
{{- end -}}
{{- if and .Values.autoscaling.enabled (not .Values.acknowledgeInMemoryStores) -}}
{{- fail "authentication-core cannot be autoscaled while its stores are in memory (see acknowledgeInMemoryStores)." -}}
{{- end -}}
{{- end -}}
