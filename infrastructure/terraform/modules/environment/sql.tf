# Cloud SQL for PostgreSQL 16: private IP only, TLS required, daily backups with point-in-time recovery.

resource "google_sql_database_instance" "main" {
  name                = local.name
  project             = var.project_id
  region              = var.region
  database_version    = "POSTGRES_16"
  deletion_protection = var.deletion_protection

  settings {
    tier              = var.sql_tier
    availability_type = var.sql_high_availability ? "REGIONAL" : "ZONAL"
    disk_autoresize   = true
    disk_type         = "PD_SSD"
    user_labels       = local.labels

    ip_configuration {
      ipv4_enabled                                  = false
      private_network                               = google_compute_network.main.id
      ssl_mode                                      = "ENCRYPTED_ONLY"
      enable_private_path_for_google_cloud_services = true
    }

    backup_configuration {
      enabled                        = true
      start_time                     = "02:00"
      point_in_time_recovery_enabled = true
      transaction_log_retention_days = 7

      backup_retention_settings {
        retained_backups = var.sql_backup_retention_days
        retention_unit   = "COUNT"
      }
    }

    maintenance_window {
      day          = 7
      hour         = 4
      update_track = "stable"
    }

    insights_config {
      query_insights_enabled  = true
      record_application_tags = true
    }
  }

  depends_on = [google_service_networking_connection.private_services]
}

resource "google_sql_database" "main" {
  name     = "fandom_score"
  project  = var.project_id
  instance = google_sql_database_instance.main.name
}

# No characters that need quoting in a Npgsql connection string.
resource "random_password" "db" {
  length  = 32
  special = false
}

resource "google_sql_user" "app" {
  name     = "fandom"
  project  = var.project_id
  instance = google_sql_database_instance.main.name
  password = random_password.db.result
}

# The only secret Terraform writes a value for. The others are set with gcloud (infrastructure/README.md).
resource "google_secret_manager_secret" "db" {
  secret_id = "ConnectionStrings__Database"
  project   = var.project_id
  labels    = local.labels

  replication {
    auto {}
  }

  depends_on = [google_project_service.apis]
}

resource "google_secret_manager_secret_version" "db" {
  secret = google_secret_manager_secret.db.id
  secret_data = join(";", [
    "Host=${google_sql_database_instance.main.private_ip_address}",
    "Port=5432",
    "Database=${google_sql_database.main.name}",
    "Username=${google_sql_user.app.name}",
    "Password=${random_password.db.result}",
    "SSL Mode=Require",
    "Maximum Pool Size=20",
  ])
}
