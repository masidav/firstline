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
  default = 150
}

variable "alert_email" {
  type = string
}

variable "github_repository" {
  type    = string
  default = "masidav/firstline"
}
