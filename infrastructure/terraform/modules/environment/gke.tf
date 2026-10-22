# GKE Autopilot: regional, private nodes, Workload Identity, Secret Manager add-on, Gateway API.

# Nodes run as a dedicated service account with only what logging, monitoring, and image pulls need.
resource "google_service_account" "gke_node" {
  account_id   = "${local.name}-node"
  project      = var.project_id
  display_name = "GKE node service account (${var.environment})"
}

resource "google_project_iam_member" "gke_node" {
  for_each = toset([
    "roles/logging.logWriter",
    "roles/monitoring.metricWriter",
    "roles/monitoring.viewer",
    "roles/stackdriver.resourceMetadata.writer",
    "roles/autoscaling.metricsWriter",
  ])

  project = var.project_id
  role    = each.value
  member  = google_service_account.gke_node.member
}

resource "google_artifact_registry_repository_iam_member" "gke_node_pull" {
  project    = var.project_id
  location   = google_artifact_registry_repository.images.location
  repository = google_artifact_registry_repository.images.name
  role       = "roles/artifactregistry.reader"
  member     = google_service_account.gke_node.member
}

resource "google_container_cluster" "main" {
  name     = local.name
  project  = var.project_id
  location = var.region

  enable_autopilot    = true
  network             = google_compute_network.main.id
  subnetwork          = google_compute_subnetwork.main.id
  deletion_protection = var.deletion_protection
  resource_labels     = local.labels

  release_channel {
    channel = "REGULAR"
  }

  ip_allocation_policy {
    cluster_secondary_range_name  = "pods"
    services_secondary_range_name = "services"
  }

  # Nodes have no public addresses; the control plane endpoint stays reachable for kubectl (IAM-authenticated).
  private_cluster_config {
    enable_private_nodes    = true
    enable_private_endpoint = false
  }

  cluster_autoscaling {
    auto_provisioning_defaults {
      service_account = google_service_account.gke_node.email
    }
  }

  # Secret Manager secrets mounted as files (infrastructure/k8s/base/secrets.yaml).
  secret_manager_config {
    enabled = true
  }

  # Gateway API for the external load balancer (infrastructure/k8s/base/gateway.yaml).
  gateway_api_config {
    channel = "CHANNEL_STANDARD"
  }

  maintenance_policy {
    daily_maintenance_window {
      start_time = "03:00"
    }
  }

  depends_on = [google_project_service.apis]
}
