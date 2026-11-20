{{- define "realtimestack-gateway.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "realtimestack-gateway.accountsName" -}}
{{- default (printf "%s-accounts" (include "realtimestack-gateway.fullname" .)) .Values.accounts.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- define "realtimestack-gateway.accountsSelector" -}}
app.kubernetes.io/name: {{ include "realtimestack-gateway.accountsName" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/component: accounts
{{- end -}}
{{- define "realtimestack-gateway.hostCheckInEnv" -}}
- name: POD_NAMESPACE
  valueFrom: { fieldRef: { fieldPath: metadata.namespace } }
- name: POD_NAME
  valueFrom: { fieldRef: { fieldPath: metadata.name } }
- name: POD_UID
  valueFrom: { fieldRef: { fieldPath: metadata.uid } }
- name: NODE_NAME
  valueFrom: { fieldRef: { fieldPath: spec.nodeName } }
{{- if or (eq .component "accounts") (not .root.Values.licensing.serverUrl) }}
- name: CORMIER_LICENSE_SERVER_URL
  value: {{ .root.Values.managedInstallation.licensingOrigin | quote }}
{{- end }}
- name: CORMIER_LICENSE_INSTALLATION_ID
  value: {{ .root.Values.managedInstallation.installationId | quote }}
- name: CORMIER_LICENSE_INSTANCE_ID
  value: {{ printf "%s:$(POD_UID)" .component | quote }}
- name: CORMIER_LICENSE_AGENT_TOKEN_FILE
  value: /secrets/check-in/agent-token
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
