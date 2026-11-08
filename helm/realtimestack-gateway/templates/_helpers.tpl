{{- define "realtimestack-gateway.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- define "realtimestack-gateway.fullname" -}}
{{- if .Values.fullnameOverride }}{{ .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}{{ else }}{{ .Release.Name | trunc 63 | trimSuffix "-" }}{{ end -}}
{{- end -}}
{{- define "realtimestack-gateway.labels" -}}
app.kubernetes.io/name: {{ include "realtimestack-gateway.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
{{- end -}}
{{- define "realtimestack-gateway.selectorLabels" -}}
app.kubernetes.io/name: {{ include "realtimestack-gateway.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}
{{- define "realtimestack-gateway.serviceAccountName" -}}
{{- if .Values.serviceAccount.create }}{{ default (include "realtimestack-gateway.fullname" .) .Values.serviceAccount.name }}{{ else }}{{ required "serviceAccount.name is required when create=false" .Values.serviceAccount.name }}{{ end -}}
{{- end -}}
{{- define "realtimestack-gateway.redisEndpoint" -}}
{{- if and (eq .Values.redis.mode "managed") (eq .Values.topology "ha") -}}{{ printf "%s:%v,sentinelUser=default" .Values.redis.managedReleaseName .Values.redis.sentinelPort }}{{- else if eq .Values.redis.mode "managed" -}}{{ printf "%s-master:%v" .Values.redis.managedReleaseName .Values.redis.port }}{{- else -}}{{ required "redis.externalEndpoint is required in external mode" .Values.redis.externalEndpoint }}{{- end -}}
{{- end -}}
