output "cluster_name" {
  value = google_container_cluster.main.name
}

output "cluster_location" {
  value = google_container_cluster.main.location
}

output "image_repository" {
  description = "Prefix for image names: <repo>/api:<tag>, <repo>/worker:<tag>, <repo>/migrate:<tag>."
  value       = "${var.region}-docker.pkg.dev/${var.project_id}/${google_artifact_registry_repository.images.repository_id}"
}

output "api_ip_address" {
  description = "Static address of the API load balancer. Point the api_domain A record here; it is also the API's ForwardedHeaders__KnownProxies value."
  value       = google_compute_global_address.api.address
}

output "api_ip_address_name" {
  description = "Name the Gateway refers to (spec.addresses)."
  value       = google_compute_global_address.api.name
}

output "certificate_map" {
  description = "Certificate Manager map for the Gateway annotation networking.gke.io/certmap."
  value       = google_certificate_manager_certificate_map.api.name
}

output "certificate_dns_record" {
  description = "CNAME record to create so Certificate Manager can issue the certificate for api_domain."
  value = {
    name = google_certificate_manager_dns_authorization.api.dns_resource_record[0].name
    type = google_certificate_manager_dns_authorization.api.dns_resource_record[0].type
    data = google_certificate_manager_dns_authorization.api.dns_resource_record[0].data
  }
}

output "security_policy" {
  description = "Cloud Armor policy for GCPBackendPolicy.spec.default.securityPolicy."
  value       = google_compute_security_policy.api.name
}

output "sql_instance_connection_name" {
  value = google_sql_database_instance.main.connection_name
}

output "sql_private_ip" {
  value = google_sql_database_instance.main.private_ip_address
}

output "workload_service_accounts" {
  description = "Google service account per Kubernetes service account (annotation iam.gke.io/gcp-service-account)."
  value       = { for name, account in google_service_account.workload : name => account.email }
}

output "deploy_service_account" {
  description = "GitHub Actions environment variable GCP_DEPLOY_SA."
  value       = google_service_account.deploy.email
}

output "workload_identity_provider" {
  description = "GitHub Actions environment variable GCP_WIF_PROVIDER."
  value       = google_iam_workload_identity_pool_provider.github.name
}

output "buckets" {
  value = {
    quarantine = google_storage_bucket.quarantine.name
    archive    = google_storage_bucket.archive.name
  }
}
