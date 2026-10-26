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

# Verified proof must be redacted and archived before the quarantine bucket deletes it at 30 days (storage.tf). The
# worker's hourly redaction.sweep logs "Redaction overdue: ..." while any verified file is still in quarantine 25 days
# after upload (workers/image-redaction/RedactionSweepJob.cs); this alert emails at most once every 6 hours.
resource "google_monitoring_alert_policy" "redaction_overdue" {
  display_name = "Proof photos about to be lost (${var.environment})"
  project      = var.project_id
  combiner     = "OR"

  conditions {
    display_name = "Verified files still in quarantine after 25 days"
    condition_matched_log {
      filter = "resource.type = \"k8s_container\" AND resource.labels.container_name = \"worker\" AND jsonPayload.Message =~ \"^Redaction overdue\""
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
