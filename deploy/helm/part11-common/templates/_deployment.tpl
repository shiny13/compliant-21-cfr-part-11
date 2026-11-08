{{/*
Deployment. Expects a dict:
  root          chart context
  migrate       true to run `Database__MigrateOnly` in an init container (services with a database)
  volumes       list of extra volumes            (optional)
  volumeMounts  list of extra volume mounts      (optional)
The pod runs as a non-root user with a read-only root filesystem; /tmp is an emptyDir.
*/}}
{{- define "part11-common.deployment" -}}
{{- $root := .root -}}
{{- $values := $root.Values -}}
apiVersion: apps/v1
kind: Deployment
metadata:
  name: {{ include "part11-common.fullname" $root }}
  labels:
    {{- include "part11-common.labels" $root | nindent 4 }}
spec:
  {{- if not $values.autoscaling.enabled }}
  replicas: {{ $values.replicaCount }}
  {{- end }}
  revisionHistoryLimit: {{ $values.revisionHistoryLimit }}
  selector:
    matchLabels:
      {{- include "part11-common.selectorLabels" $root | nindent 6 }}
  template:
    metadata:
      annotations:
        checksum/config: {{ include (print $root.Template.BasePath "/configmap.yaml") $root | sha256sum }}
        {{- if not $values.secret.existingSecret }}
        checksum/secret: {{ include (print $root.Template.BasePath "/secret.yaml") $root | sha256sum }}
        {{- end }}
        {{- with $values.podAnnotations }}
        {{- toYaml . | nindent 8 }}
        {{- end }}
      labels:
        {{- include "part11-common.selectorLabels" $root | nindent 8 }}
    spec:
      {{- with $values.imagePullSecrets }}
      imagePullSecrets:
        {{- toYaml . | nindent 8 }}
      {{- end }}
      serviceAccountName: {{ include "part11-common.serviceAccountName" $root }}
      automountServiceAccountToken: false
      securityContext:
        {{- toYaml $values.podSecurityContext | nindent 8 }}
      {{- if and .migrate $values.migrations.enabled }}
      initContainers:
        - name: migrate
          image: {{ include "part11-common.image" $root }}
          imagePullPolicy: {{ $values.image.pullPolicy }}
          env:
            - name: Database__MigrateOnStartup
              value: "true"
            - name: Database__MigrateOnly
              value: "true"
          envFrom:
            - configMapRef:
                name: {{ include "part11-common.configName" $root }}
            - secretRef:
                name: {{ include "part11-common.secretName" $root }}
          securityContext:
            {{- toYaml $values.containerSecurityContext | nindent 12 }}
          resources:
            {{- toYaml $values.migrations.resources | nindent 12 }}
          volumeMounts:
            - name: tmp
              mountPath: /tmp
      {{- end }}
      containers:
        - name: {{ $root.Chart.Name }}
          image: {{ include "part11-common.image" $root }}
          imagePullPolicy: {{ $values.image.pullPolicy }}
          ports:
            - name: http
              containerPort: 8080
              protocol: TCP
          envFrom:
            - configMapRef:
                name: {{ include "part11-common.configName" $root }}
            - secretRef:
                name: {{ include "part11-common.secretName" $root }}
          {{- with $values.extraEnv }}
          env:
            {{- toYaml . | nindent 12 }}
          {{- end }}
          startupProbe:
            httpGet: { path: /health, port: http }
            periodSeconds: 3
            failureThreshold: 40
          livenessProbe:
            httpGet: { path: /health, port: http }
            periodSeconds: 15
            timeoutSeconds: 3
          readinessProbe:
            httpGet: { path: /health, port: http }
            periodSeconds: 5
            timeoutSeconds: 3
          securityContext:
            {{- toYaml $values.containerSecurityContext | nindent 12 }}
          resources:
            {{- toYaml $values.resources | nindent 12 }}
          volumeMounts:
            - name: tmp
              mountPath: /tmp
            {{- with .volumeMounts }}
            {{- toYaml . | nindent 12 }}
            {{- end }}
      volumes:
        - name: tmp
          emptyDir: {}
        {{- with .volumes }}
        {{- toYaml . | nindent 8 }}
        {{- end }}
      {{- with $values.nodeSelector }}
      nodeSelector:
        {{- toYaml . | nindent 8 }}
      {{- end }}
      {{- with $values.affinity }}
      affinity:
        {{- toYaml . | nindent 8 }}
      {{- end }}
      {{- with $values.tolerations }}
      tolerations:
        {{- toYaml . | nindent 8 }}
      {{- end }}
      {{- with $values.topologySpreadConstraints }}
      topologySpreadConstraints:
        {{- toYaml . | nindent 8 }}
      {{- end }}
{{- end -}}
