# One environment: network, GKE Autopilot cluster, Cloud SQL, registry, secrets, buckets, edge, CI/CD identity.
# See docs/adr/0002-hosting-gcp-gke.md and infrastructure/README.md.

data "google_project" "main" {
  project_id = var.project_id
}

locals {
  name = "fandomscore-${var.environment}"
  labels = {
    app         = "fandomscore"
    environment = var.environment
    managed_by  = "terraform"
  }

  # Kubernetes namespace and service accounts the manifests use (infrastructure/k8s/base).
  namespace = "fandomscore"
  workloads = ["api", "worker", "migrate", "redaction"]

  # Which workload may read which secret. The app reads them as files named after the configuration key.
  secret_readers = {
    api       = ["ConnectionStrings__Database", "Auth__SigningKey", "Auth__Apple__PrivateKey", "Spotify__ClientSecret", "Spotify__TokenKey"]
    worker    = ["ConnectionStrings__Database", "Spotify__ClientSecret", "Spotify__TokenKey", "Ticketmaster__ApiKey"]
    migrate   = ["ConnectionStrings__Database"]
    redaction = []
  }
  secret_bindings = {
    for pair in flatten([
      for workload, secrets in local.secret_readers : [
        for secret in secrets : { workload = workload, secret = secret }
      ]
    ]) : "${pair.workload}/${pair.secret}" => pair
  }
}

resource "google_project_service" "apis" {
  for_each = toset([
    "artifactregistry.googleapis.com",
    "billingbudgets.googleapis.com",
    "certificatemanager.googleapis.com",
    "cloudresourcemanager.googleapis.com",
    "compute.googleapis.com",
    "container.googleapis.com",
    "containerscanning.googleapis.com",
    "iam.googleapis.com",
    "iamcredentials.googleapis.com",
    "logging.googleapis.com",
    "monitoring.googleapis.com",
    "secretmanager.googleapis.com",
    "servicenetworking.googleapis.com",
    "sqladmin.googleapis.com",
    "sts.googleapis.com",
  ])

  project            = var.project_id
  service            = each.value
  disable_on_destroy = false
}
