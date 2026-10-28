variable "project_id" {
  type = string
}

variable "region" {
  type    = string
  default = "us-central1"
}

variable "api_domain" {
  type = string
}

variable "billing_account_id" {
  type = string
}

variable "budget_monthly_usd" {
  type    = number
  default = 1000
}

variable "alert_email" {
  type = string
}

variable "github_repository" {
  type    = string
  default = "masidav/firstline"
}

# Hostnames for the fan web app and the promoter dashboard (must match infrastructure/k8s/overlays/prod/gateway.yaml).
variable "web_domains" {
  type    = map(string)
  default = {}
}
