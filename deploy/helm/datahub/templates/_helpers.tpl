{{/* Resource name for one app: <release>-<app>, e.g. datahub-web. */}}
{{- define "datahub.name" -}}
{{- printf "%s-%s" .root.Release.Name .app | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "datahub.selectorLabels" -}}
app.kubernetes.io/name: datahub
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/component: {{ .app }}
{{- end -}}

{{- define "datahub.labels" -}}
{{ include "datahub.selectorLabels" . }}
app.kubernetes.io/version: {{ include "datahub.tag" .root | quote }}
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .root.Chart.Name .root.Chart.Version }}
{{- end -}}

{{- define "datahub.tag" -}}
{{- .Values.image.tag | default .Chart.AppVersion -}}
{{- end -}}

{{/* Image for one app: [<registry>/]datahub-<app>:<tag> */}}
{{- define "datahub.image" -}}
{{- $v := .root.Values.image -}}
{{- if $v.registry }}{{ $v.registry | trimSuffix "/" }}/{{ end }}datahub-{{ .app }}:{{ include "datahub.tag" .root }}
{{- end -}}

{{/* Environment shared by every container: the environment name and service identity for telemetry. */}}
{{- define "datahub.commonEnv" -}}
- name: ASPNETCORE_ENVIRONMENT
  value: {{ .root.Values.environment | quote }}
- name: DOTNET_ENVIRONMENT
  value: {{ .root.Values.environment | quote }}
{{- end -}}

{{/* Secret keys as environment variables; optional, so a key that isn't set is simply left out. */}}
{{- define "datahub.secretEnv" -}}
{{- $secret := .root.Values.secrets.existingSecret -}}
{{- range .keys }}
- name: {{ . }}
  valueFrom:
    secretKeyRef:
      name: {{ $secret }}
      key: {{ . }}
      optional: true
{{- end }}
{{- end -}}
