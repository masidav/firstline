# Budget alert, uptime check on the health endpoint, and an alert when it fails. All go to alert_email.

resource "google_monitoring_notification_channel" "email" {
  display_name = "Fandom Score ${var.environment} alerts"
  project      = var.project_id
  type         = "email"

  labels = {
    email_address = var.alert_email
  }

  depends_on = [google_project_service.apis]
}

resource "google_billing_budget" "monthly" {
  billing_account = var.billing_account_id
  display_name    = "Fandom Score ${var.environment} monthly"

  budget_filter {
    projects               = ["projects/${data.google_project.main.number}"]
    credit_types_treatment = "INCLUDE_ALL_CREDITS"
  }

  amount {
    specified_amount {
      currency_code = "USD"
      units         = tostring(floor(var.budget_monthly_usd))
    }
  }

  threshold_rules {
    threshold_percent = 0.5
  }

  threshold_rules {
    threshold_percent = 0.9
  }

  threshold_rules {
    threshold_percent = 1.0
  }

  threshold_rules {
    threshold_percent = 1.0
    spend_basis       = "FORECASTED_SPEND"
  }

  all_updates_rule {
    monitoring_notification_channels = [google_monitoring_notification_channel.email.id]
    disable_default_iam_recipients   = false
  }

  depends_on = [google_project_service.apis]
}

resource "google_monitoring_uptime_check_config" "api_health" {
  display_name = "API health (${var.environment})"
  project      = var.project_id
  timeout      = "10s"
  period       = "60s"

  http_check {
    path           = "/v1/health"
    port           = 443
    use_ssl        = true
    validate_ssl   = true
    request_method = "GET"
  }

  monitored_resource {
    type = "uptime_url"
    labels = {
      project_id = var.project_id
      host       = var.api_domain
    }
  }

  # The api_down alert filters on this check's id. Changing the host replaces the check, and Google refuses to
  # delete a check an alert still references, so the new check must exist (and the alert move to it) first.
  lifecycle {
    create_before_destroy = true
  }

  depends_on = [google_project_service.apis]
}

resource "google_monitoring_alert_policy" "api_down" {
  display_name = "API down (${var.environment})"
  project      = var.project_id
  combiner     = "OR"

  conditions {
    display_name = "Health check failing from more than one region"
    condition_threshold {
      filter          = "resource.type = \"uptime_url\" AND metric.type = \"monitoring.googleapis.com/uptime_check/check_passed\" AND metric.labels.check_id = \"${google_monitoring_uptime_check_config.api_health.uptime_check_id}\""
      comparison      = "COMPARISON_GT"
      threshold_value = 1
      duration        = "300s"

      aggregations {
        alignment_period     = "300s"
        per_series_aligner   = "ALIGN_NEXT_OLDER"
        cross_series_reducer = "REDUCE_COUNT_FALSE"
        group_by_fields      = ["resource.label.*"]
      }

      trigger {
        count = 1
      }
    }
  }

  notification_channels = [google_monitoring_notification_channel.email.id]

  alert_strategy {
    auto_close = "1800s"
  }
}

# --- Worker and queue: the worker serves no traffic, so its failures are only visible in its logs. Two log-based
# metrics count them (workers/jobs/JobRunner.cs writes the messages), and alerts watch those. The worker image runs
# as two Deployments (worker, and redaction for the jobs that touch submission files); both are watched.

locals {
  worker_logs = <<-EOT
    resource.type="k8s_container"
    resource.labels.namespace_name="${local.namespace}"
    resource.labels.container_name=("worker" OR "redaction")
  EOT
}

# A job that used up its attempts (or had no handler, or kept exceeding its lease). It stays in the jobs table
# with status failed; workers/README.md "Operating" has the retry statement.
resource "google_logging_metric" "worker_jobs_given_up" {
  name        = "worker_jobs_given_up"
  project     = var.project_id
  description = "Jobs the worker gave up on (status failed in the jobs table), by kind."
  filter      = "${local.worker_logs} jsonPayload.Message:\"failed permanently\""

  metric_descriptor {
    metric_kind = "DELTA"
    value_type  = "INT64"
    unit        = "1"
    labels {
      key         = "kind"
      value_type  = "STRING"
      description = "Job kind (e.g. spotify.sync)."
    }
  }

  label_extractors = {
    kind = "EXTRACT(jsonPayload.State.Kind)"
  }

  depends_on = [google_project_service.apis]
}

# Every failed attempt (the job retries with backoff). A burst usually means a provider (Spotify) is down.
resource "google_logging_metric" "worker_job_failures" {
  name        = "worker_job_failures"
  project     = var.project_id
  description = "Failed job attempts (retried with backoff), by kind."
  filter      = "${local.worker_logs} jsonPayload.Message:\"failed on attempt\""

  metric_descriptor {
    metric_kind = "DELTA"
    value_type  = "INT64"
    unit        = "1"
    labels {
      key         = "kind"
      value_type  = "STRING"
      description = "Job kind (e.g. spotify.sync)."
    }
  }

  label_extractors = {
    kind = "EXTRACT(jsonPayload.State.Kind)"
  }

  depends_on = [google_project_service.apis]
}

# The polling loop itself failed: usually the database is unreachable. The loop retries every poll interval.
resource "google_logging_metric" "worker_loop_errors" {
  name        = "worker_loop_errors"
  project     = var.project_id
  description = "Job loop errors (the worker could not reach the queue)."
  filter      = "${local.worker_logs} jsonPayload.Message:\"Job loop error\""

  metric_descriptor {
    metric_kind = "DELTA"
    value_type  = "INT64"
    unit        = "1"
  }

  depends_on = [google_project_service.apis]
}

resource "google_monitoring_alert_policy" "worker_jobs_given_up" {
  display_name = "Worker gave up on a job (${var.environment})"
  project      = var.project_id
  combiner     = "OR"

  conditions {
    display_name = "A job reached status failed in the last 10 minutes"
    condition_threshold {
      filter          = "resource.type = \"k8s_container\" AND metric.type = \"logging.googleapis.com/user/${google_logging_metric.worker_jobs_given_up.name}\""
      comparison      = "COMPARISON_GT"
      threshold_value = 0
      duration        = "0s"

      aggregations {
        alignment_period     = "600s"
        per_series_aligner   = "ALIGN_SUM"
        cross_series_reducer = "REDUCE_SUM"
        group_by_fields      = ["metric.label.kind"]
      }

      trigger {
        count = 1
      }
    }
  }

  documentation {
    content   = "A background job failed on every attempt and stays in the jobs table with status failed. Find it and its last_error, fix the cause, and retry it: workers/README.md (Operating) and infrastructure/RUNBOOK.md."
    mime_type = "text/markdown"
  }

  notification_channels = [google_monitoring_notification_channel.email.id]

  alert_strategy {
    auto_close = "86400s"
  }
}

resource "google_monitoring_alert_policy" "worker_queue_unreachable" {
  display_name = "Worker cannot reach the queue (${var.environment})"
  project      = var.project_id
  combiner     = "OR"

  conditions {
    display_name = "More than 20 job loop errors in 5 minutes"
    condition_threshold {
      filter          = "resource.type = \"k8s_container\" AND metric.type = \"logging.googleapis.com/user/${google_logging_metric.worker_loop_errors.name}\""
      comparison      = "COMPARISON_GT"
      threshold_value = 20
      duration        = "0s"

      aggregations {
        alignment_period     = "300s"
        per_series_aligner   = "ALIGN_SUM"
        cross_series_reducer = "REDUCE_SUM"
      }

      trigger {
        count = 1
      }
    }
  }

  documentation {
    content   = "The worker's polling loop keeps failing, which almost always means the database is unreachable or its connection string or server CA is wrong. Jobs are not running. See infrastructure/RUNBOOK.md."
    mime_type = "text/markdown"
  }

  notification_channels = [google_monitoring_notification_channel.email.id]

  alert_strategy {
    auto_close = "1800s"
  }
}

# --- Pods: a container that keeps restarting (crash loop, failed liveness probe, out of memory).
resource "google_monitoring_alert_policy" "container_restarts" {
  display_name = "Pods restarting (${var.environment})"
  project      = var.project_id
  combiner     = "OR"

  conditions {
    display_name = "A container restarted more than twice in 10 minutes"
    condition_threshold {
      filter          = "resource.type = \"k8s_container\" AND resource.labels.namespace_name = \"${local.namespace}\" AND metric.type = \"kubernetes.io/container/restart_count\""
      comparison      = "COMPARISON_GT"
      threshold_value = 2
      duration        = "0s"

      aggregations {
        alignment_period     = "600s"
        per_series_aligner   = "ALIGN_DELTA"
        cross_series_reducer = "REDUCE_SUM"
        group_by_fields      = ["resource.label.container_name"]
      }

      trigger {
        count = 1
      }
    }
  }

  documentation {
    content   = "kubectl describe the pod and read its previous logs (kubectl logs --previous). Common causes: a secret without a version, the liveness probe failing (a stuck job loop), or the memory limit. See infrastructure/RUNBOOK.md."
    mime_type = "text/markdown"
  }

  notification_channels = [google_monitoring_notification_channel.email.id]

  alert_strategy {
    auto_close = "1800s"
  }
}

# --- Database: sustained CPU or memory pressure (the tier is too small, or a query needs an index).
resource "google_monitoring_alert_policy" "sql_pressure" {
  display_name = "Cloud SQL under pressure (${var.environment})"
  project      = var.project_id
  combiner     = "OR"

  conditions {
    display_name = "CPU above 80 % for 15 minutes"
    condition_threshold {
      filter          = "resource.type = \"cloudsql_database\" AND resource.labels.database_id = \"${var.project_id}:${google_sql_database_instance.main.name}\" AND metric.type = \"cloudsql.googleapis.com/database/cpu/utilization\""
      comparison      = "COMPARISON_GT"
      threshold_value = 0.8
      duration        = "900s"

      aggregations {
        alignment_period   = "300s"
        per_series_aligner = "ALIGN_MEAN"
      }

      trigger {
        count = 1
      }
    }
  }

  conditions {
    display_name = "Memory above 90 % for 15 minutes"
    condition_threshold {
      filter          = "resource.type = \"cloudsql_database\" AND resource.labels.database_id = \"${var.project_id}:${google_sql_database_instance.main.name}\" AND metric.type = \"cloudsql.googleapis.com/database/memory/utilization\""
      comparison      = "COMPARISON_GT"
      threshold_value = 0.9
      duration        = "900s"

      aggregations {
        alignment_period   = "300s"
        per_series_aligner = "ALIGN_MEAN"
      }

      trigger {
        count = 1
      }
    }
  }

  documentation {
    content   = "Check Query Insights for the top queries, and the connection count (each API pod holds up to 20). A bigger tier restarts the instance: do it in the maintenance window unless it is urgent. See infrastructure/RUNBOOK.md."
    mime_type = "text/markdown"
  }

  notification_channels = [google_monitoring_notification_channel.email.id]

  alert_strategy {
    auto_close = "1800s"
  }
}

# --- API errors as seen by the load balancer: 5xx answers (including 502/503 when no backend is healthy).
resource "google_monitoring_alert_policy" "api_errors" {
  display_name = "API returning errors (${var.environment})"
  project      = var.project_id
  combiner     = "OR"

  conditions {
    display_name = "More than 5 % of responses are 5xx over 5 minutes"
    condition_threshold {
      filter             = "resource.type = \"https_lb_rule\" AND metric.type = \"loadbalancing.googleapis.com/https/request_count\" AND metric.labels.response_code_class = 500"
      denominator_filter = "resource.type = \"https_lb_rule\" AND metric.type = \"loadbalancing.googleapis.com/https/request_count\""
      comparison         = "COMPARISON_GT"
      threshold_value    = 0.05
      duration           = "300s"

      aggregations {
        alignment_period     = "300s"
        per_series_aligner   = "ALIGN_SUM"
        cross_series_reducer = "REDUCE_SUM"
      }

      denominator_aggregations {
        alignment_period     = "300s"
        per_series_aligner   = "ALIGN_SUM"
        cross_series_reducer = "REDUCE_SUM"
      }

      trigger {
        count = 1
      }
    }
  }

  documentation {
    content   = "Look at the API logs for the exceptions (severity ERROR in namespace fandomscore), and at the load balancer logs for 502/503 (no healthy backend: pods failing readiness). A bad deploy is rolled back with deploy.sh <env> <previous-sha>. See infrastructure/RUNBOOK.md."
    mime_type = "text/markdown"
  }

  notification_channels = [google_monitoring_notification_channel.email.id]

  alert_strategy {
    auto_close = "1800s"
  }
}

# Verified proof must be redacted and archived before the quarantine bucket deletes it at 30 days (storage.tf). The
# redaction deployment's hourly redaction.sweep logs "Redaction overdue: ..." while any verified file is still in quarantine 25 days
# after upload (workers/image-redaction/RedactionSweepJob.cs); this alert emails at most once every 6 hours.
resource "google_monitoring_alert_policy" "redaction_overdue" {
  display_name = "Proof photos about to be lost (${var.environment})"
  project      = var.project_id
  combiner     = "OR"

  conditions {
    display_name = "Verified files still in quarantine after 25 days"
    condition_matched_log {
      filter = "resource.type = \"k8s_container\" AND resource.labels.container_name = \"redaction\" AND jsonPayload.Message =~ \"^Redaction overdue\""
    }
  }

  documentation {
    content   = "Verified proof is still unarchived 25 days after upload; the quarantine bucket deletes it at 30. Check the staff Photo redaction screen (GET /v1/admin/redactions) and failed submissions.redact jobs."
    mime_type = "text/markdown"
  }

  notification_channels = [google_monitoring_notification_channel.email.id]

  alert_strategy {
    notification_rate_limit {
      period = "21600s"
    }
    auto_close = "86400s"
  }

  depends_on = [google_project_service.apis]
}
