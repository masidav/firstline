# Public DNS for the environment's hostnames, when the zone is in Cloud DNS (var.dns_zone_name). Cloud Domains
# creates the zone with the registration and points the registrar at it, so the zone is read here, not managed;
# the records are managed. Without a zone name, create the records at the DNS provider from the outputs
# api_ip_address, certificate_dns_record, and web_certificate_dns_records.

locals {
  dns_hostnames = var.dns_zone_name == null ? {} : merge({ api = var.api_domain }, var.web_domains)
  dns_authorizations = var.dns_zone_name == null ? {} : merge(
    { api = google_certificate_manager_dns_authorization.api },
    google_certificate_manager_dns_authorization.web,
  )
}

data "google_dns_managed_zone" "public" {
  count = var.dns_zone_name == null ? 0 : 1

  name    = var.dns_zone_name
  project = var.project_id

  depends_on = [google_project_service.apis]
}

# Every hostname points at the Gateway's static address.
resource "google_dns_record_set" "hosts" {
  for_each = local.dns_hostnames

  project      = var.project_id
  managed_zone = data.google_dns_managed_zone.public[0].name
  name         = "${each.value}."
  type         = "A"
  ttl          = 300
  rrdatas      = [google_compute_global_address.api.address]

  lifecycle {
    precondition {
      condition     = endswith("${each.value}.", ".${data.google_dns_managed_zone.public[0].dns_name}")
      error_message = "${each.value} is not under the Cloud DNS zone ${data.google_dns_managed_zone.public[0].dns_name}"
    }
  }
}

# The CNAMEs that let Certificate Manager issue each certificate.
resource "google_dns_record_set" "certificate_authorizations" {
  for_each = local.dns_authorizations

  project      = var.project_id
  managed_zone = data.google_dns_managed_zone.public[0].name
  name         = each.value.dns_resource_record[0].name
  type         = each.value.dns_resource_record[0].type
  ttl          = 300
  rrdatas      = [each.value.dns_resource_record[0].data]
}
