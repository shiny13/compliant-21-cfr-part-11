{{/* Names, labels and small helpers shared by all service charts. */}}

{{- define "part11-common.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "part11-common.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{/* Name another service of this platform gets when installed in the same release (umbrella chart). */}}
{{- define "part11-common.siblingName" -}}
{{- $chart := .chart -}}
{{- if contains $chart .root.Release.Name -}}
{{- .root.Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .root.Release.Name $chart | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}

{{/* Base URL of a sibling service: the explicit value if given, otherwise the in-cluster service of the same release. */}}
{{- define "part11-common.dependencyUrl" -}}
{{- if .value -}}
{{- .value | trimSuffix "/" -}}
{{- else -}}
{{- printf "http://%s:%v" (include "part11-common.siblingName" (dict "root" .root "chart" .chart)) (default 8080 .port) -}}
{{- end -}}
{{- end -}}

{{- define "part11-common.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "part11-common.selectorLabels" -}}
app.kubernetes.io/name: {{ include "part11-common.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "part11-common.labels" -}}
helm.sh/chart: {{ include "part11-common.chart" . }}
{{ include "part11-common.selectorLabels" . }}
app.kubernetes.io/part-of: part11-platform
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "part11-common.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "part11-common.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{- define "part11-common.configName" -}}
{{- printf "%s-config" (include "part11-common.fullname" .) -}}
{{- end -}}

{{/* The Secret holding sensitive environment variables: an existing one, or the one this chart creates. */}}
{{- define "part11-common.secretName" -}}
{{- if .Values.secret.existingSecret -}}
{{- .Values.secret.existingSecret -}}
{{- else -}}
{{- printf "%s-secret" (include "part11-common.fullname" .) -}}
{{- end -}}
{{- end -}}

{{- define "part11-common.image" -}}
{{- printf "%s:%s" .Values.image.repository (default .Chart.AppVersion .Values.image.tag) -}}
{{- end -}}
