# Public entry point: a static address for the Gateway, a Google-managed certificate, and Cloud Armor.

resource "google_compute_global_address" "api" {
  name    = "${local.name}-api"
  project = var.project_id
}

resource "google_certificate_manager_dns_authorization" "api" {
  name    = "${local.name}-api"
  project = var.project_id
  domain  = var.api_domain

  depends_on = [google_project_service.apis]
}

resource "google_certificate_manager_certificate" "api" {
  name    = "${local.name}-api"
  project = var.project_id
  labels  = local.labels

  managed {
    domains            = [var.api_domain]
    dns_authorizations = [google_certificate_manager_dns_authorization.api.id]
  }
}

# The Gateway refers to this map by name (annotation networking.gke.io/certmap).
resource "google_certificate_manager_certificate_map" "api" {
  name    = "${local.name}-api"
  project = var.project_id
  labels  = local.labels
}

resource "google_certificate_manager_certificate_map_entry" "api" {
  name         = "${local.name}-api"
  project      = var.project_id
  map          = google_certificate_manager_certificate_map.api.name
  certificates = [google_certificate_manager_certificate.api.id]
  hostname     = var.api_domain
}

# Attached to the API backend by GCPBackendPolicy (infrastructure/k8s/base/gateway.yaml).
resource "google_compute_security_policy" "api" {
  name    = "${local.name}-api"
  project = var.project_id
  type    = "CLOUD_ARMOR"

  adaptive_protection_config {
    layer_7_ddos_defense_config {
      enable = true
    }
  }

  rule {
    priority    = 1000
    action      = "deny(403)"
    description = "SQL injection (preconfigured WAF rules)"
    match {
      expr {
        expression = "evaluatePreconfiguredWaf('sqli-v33-stable', {'sensitivity': 1})"
      }
    }
  }

  rule {
    priority    = 1010
    action      = "deny(403)"
    description = "Cross-site scripting (preconfigured WAF rules)"
    match {
      expr {
        expression = "evaluatePreconfiguredWaf('xss-v33-stable', {'sensitivity': 1})"
      }
    }
  }

  rule {
    priority    = 2000
    action      = "throttle"
    description = "Per-client rate limit at the edge; the API keeps its own stricter sign-in limit"
    match {
      versioned_expr = "SRC_IPS_V1"
      config {
        src_ip_ranges = ["*"]
      }
    }
    rate_limit_options {
      conform_action = "allow"
      exceed_action  = "deny(429)"
      enforce_on_key = "IP"
      rate_limit_threshold {
        count        = var.edge_rate_limit_per_minute
        interval_sec = 60
      }
    }
  }

  rule {
    priority    = 2147483647
    action      = "allow"
    description = "Default allow"
    match {
      versioned_expr = "SRC_IPS_V1"
      config {
        src_ip_ranges = ["*"]
      }
    }
  }

  depends_on = [google_project_service.apis]
}
