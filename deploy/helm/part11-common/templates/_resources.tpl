{{/*
ConfigMap of non-sensitive settings. Expects a dict: root (chart context) and data (map of ENV_NAME -> value).
ASP.NET Core reads "Section__Key" environment variables as configuration "Section:Key".
*/}}
{{- define "part11-common.configmap" -}}
apiVersion: v1
kind: ConfigMap
metadata:
  name: {{ include "part11-common.configName" .root }}
  labels:
    {{- include "part11-common.labels" .root | nindent 4 }}
data:
  {{- range $key, $value := .data }}
  {{ $key }}: {{ $value | toString | quote }}
  {{- end }}
{{- end -}}

{{/*
Secret of sensitive settings, created only when no existing Secret is named. Expects root and data.
In production prefer secret.existingSecret (see scripts/generate-secrets.sh) so values never pass through Helm.
*/}}
{{- define "part11-common.secret" -}}
{{- if not .root.Values.secret.existingSecret }}
apiVersion: v1
kind: Secret
metadata:
  name: {{ include "part11-common.secretName" .root }}
  labels:
    {{- include "part11-common.labels" .root | nindent 4 }}
type: Opaque
stringData:
  {{- range $key, $value := .data }}
  {{ $key }}: {{ $value | toString | quote }}
  {{- end }}
{{- end }}
{{- end -}}

{{- define "part11-common.serviceaccount" -}}
{{- if .Values.serviceAccount.create }}
apiVersion: v1
kind: ServiceAccount
metadata:
  name: {{ include "part11-common.serviceAccountName" . }}
  labels:
    {{- include "part11-common.labels" . | nindent 4 }}
  {{- with .Values.serviceAccount.annotations }}
  annotations:
    {{- toYaml . | nindent 4 }}
  {{- end }}
automountServiceAccountToken: false
{{- end }}
{{- end -}}

{{- define "part11-common.service" -}}
apiVersion: v1
kind: Service
metadata:
  name: {{ include "part11-common.fullname" . }}
  labels:
    {{- include "part11-common.labels" . | nindent 4 }}
  {{- with .Values.service.annotations }}
  annotations:
    {{- toYaml . | nindent 4 }}
  {{- end }}
spec:
  type: {{ .Values.service.type }}
  ports:
    - name: http
      port: {{ .Values.service.port }}
      targetPort: http
      protocol: TCP
  selector:
    {{- include "part11-common.selectorLabels" . | nindent 4 }}
{{- end -}}

{{- define "part11-common.ingress" -}}
{{- if .Values.ingress.enabled }}
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: {{ include "part11-common.fullname" . }}
  labels:
    {{- include "part11-common.labels" . | nindent 4 }}
  {{- with .Values.ingress.annotations }}
  annotations:
    {{- toYaml . | nindent 4 }}
  {{- end }}
spec:
  {{- with .Values.ingress.className }}
  ingressClassName: {{ . }}
  {{- end }}
  {{- with .Values.ingress.tls }}
  tls:
    {{- toYaml . | nindent 4 }}
  {{- end }}
  rules:
    {{- range .Values.ingress.hosts }}
    - host: {{ .host | quote }}
      http:
        paths:
          {{- range .paths }}
          - path: {{ .path }}
            pathType: {{ default "Prefix" .pathType }}
            backend:
              service:
                name: {{ include "part11-common.fullname" $ }}
                port:
                  name: http
          {{- end }}
    {{- end }}
{{- end }}
{{- end -}}

{{- define "part11-common.hpa" -}}
{{- if .Values.autoscaling.enabled }}
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: {{ include "part11-common.fullname" . }}
  labels:
    {{- include "part11-common.labels" . | nindent 4 }}
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: {{ include "part11-common.fullname" . }}
  minReplicas: {{ .Values.autoscaling.minReplicas }}
  maxReplicas: {{ .Values.autoscaling.maxReplicas }}
  metrics:
    - type: Resource
      resource:
        name: cpu
        target:
          type: Utilization
          averageUtilization: {{ .Values.autoscaling.targetCPUUtilizationPercentage }}
{{- end }}
{{- end -}}

{{- define "part11-common.pdb" -}}
{{- if and .Values.podDisruptionBudget.enabled (or .Values.autoscaling.enabled (gt (int .Values.replicaCount) 1)) }}
apiVersion: policy/v1
kind: PodDisruptionBudget
metadata:
  name: {{ include "part11-common.fullname" . }}
  labels:
    {{- include "part11-common.labels" . | nindent 4 }}
spec:
  minAvailable: {{ .Values.podDisruptionBudget.minAvailable }}
  selector:
    matchLabels:
      {{- include "part11-common.selectorLabels" . | nindent 6 }}
{{- end }}
{{- end -}}

{{/* Allows ingress to the pods only from the same namespace (and any extra rules given). */}}
{{- define "part11-common.networkpolicy" -}}
{{- if .Values.networkPolicy.enabled }}
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: {{ include "part11-common.fullname" . }}
  labels:
    {{- include "part11-common.labels" . | nindent 4 }}
spec:
  podSelector:
    matchLabels:
      {{- include "part11-common.selectorLabels" . | nindent 6 }}
  policyTypes:
    - Ingress
  ingress:
    - from:
        - podSelector: {}
      ports:
        - port: http
    {{- with .Values.networkPolicy.extraIngress }}
    {{- toYaml . | nindent 4 }}
    {{- end }}
{{- end }}
{{- end -}}

{{/* `helm test`: the service answers its health probe. */}}
{{- define "part11-common.testpod" -}}
apiVersion: v1
kind: Pod
metadata:
  name: {{ include "part11-common.fullname" . }}-test
  labels:
    {{- include "part11-common.labels" . | nindent 4 }}
  annotations:
    "helm.sh/hook": test
    "helm.sh/hook-delete-policy": before-hook-creation,hook-succeeded
spec:
  restartPolicy: Never
  containers:
    - name: health
      image: {{ .Values.tests.image }}
      command: ["wget", "-qO-", "http://{{ include "part11-common.fullname" . }}:{{ .Values.service.port }}/health"]
{{- end -}}
