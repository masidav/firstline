# One Cloud Monitoring dashboard per environment: traffic and latency at the load balancer, the pods, the worker's
# job failures, the database, and recent errors. Output dashboard_url opens it.

locals {
  dashboard_namespace_filter = "resource.type=\"k8s_container\" resource.label.\"namespace_name\"=\"${local.namespace}\""
  dashboard_sql_filter       = "resource.type=\"cloudsql_database\" resource.label.\"database_id\"=\"${var.project_id}:${google_sql_database_instance.main.name}\""

  dashboard_tiles = [
    {
      xPos = 0, yPos = 0, width = 6, height = 4
      widget = {
        title = "Requests per second, by response class (load balancer)"
        xyChart = {
          dataSets = [{
            plotType       = "STACKED_AREA"
            legendTemplate = "$${metric.labels.response_code_class}xx"
            timeSeriesQuery = {
              timeSeriesFilter = {
                filter = "metric.type=\"loadbalancing.googleapis.com/https/request_count\" resource.type=\"https_lb_rule\""
                aggregation = {
                  alignmentPeriod    = "60s"
                  perSeriesAligner   = "ALIGN_RATE"
                  crossSeriesReducer = "REDUCE_SUM"
                  groupByFields      = ["metric.label.\"response_code_class\""]
                }
              }
            }
          }]
          yAxis = { scale = "LINEAR" }
        }
      }
    },
    {
      xPos = 6, yPos = 0, width = 6, height = 4
      widget = {
        title = "Latency p50 / p95 / p99 (load balancer, ms)"
        xyChart = {
          dataSets = [
            for p in [50, 95, 99] : {
              plotType       = "LINE"
              legendTemplate = "p${p}"
              timeSeriesQuery = {
                timeSeriesFilter = {
                  filter = "metric.type=\"loadbalancing.googleapis.com/https/total_latencies\" resource.type=\"https_lb_rule\""
                  aggregation = {
                    alignmentPeriod    = "60s"
                    perSeriesAligner   = "ALIGN_DELTA"
                    crossSeriesReducer = "REDUCE_PERCENTILE_${p}"
                  }
                }
              }
            }
          ]
          yAxis = { scale = "LINEAR" }
        }
      }
    },
    {
      xPos = 0, yPos = 4, width = 4, height = 4
      widget = {
        title = "Running containers, by workload"
        xyChart = {
          dataSets = [{
            plotType       = "LINE"
            legendTemplate = "$${resource.labels.container_name}"
            timeSeriesQuery = {
              timeSeriesFilter = {
                filter = "metric.type=\"kubernetes.io/container/uptime\" ${local.dashboard_namespace_filter}"
                aggregation = {
                  alignmentPeriod    = "60s"
                  perSeriesAligner   = "ALIGN_MEAN"
                  crossSeriesReducer = "REDUCE_COUNT"
                  groupByFields      = ["resource.label.\"container_name\""]
                }
              }
            }
          }]
          yAxis = { scale = "LINEAR" }
        }
      }
    },
    {
      xPos = 4, yPos = 4, width = 4, height = 4
      widget = {
        title = "CPU, share of request (pods)"
        xyChart = {
          dataSets = [{
            plotType       = "LINE"
            legendTemplate = "$${resource.labels.container_name}"
            timeSeriesQuery = {
              timeSeriesFilter = {
                filter = "metric.type=\"kubernetes.io/container/cpu/request_utilization\" ${local.dashboard_namespace_filter}"
                aggregation = {
                  alignmentPeriod    = "60s"
                  perSeriesAligner   = "ALIGN_MEAN"
                  crossSeriesReducer = "REDUCE_MEAN"
                  groupByFields      = ["resource.label.\"container_name\""]
                }
              }
            }
          }]
          yAxis = { scale = "LINEAR" }
        }
      }
    },
    {
      xPos = 8, yPos = 4, width = 4, height = 4
      widget = {
        title = "Memory, share of limit (pods)"
        xyChart = {
          dataSets = [{
            plotType       = "LINE"
            legendTemplate = "$${resource.labels.container_name}"
            timeSeriesQuery = {
              timeSeriesFilter = {
                filter = "metric.type=\"kubernetes.io/container/memory/limit_utilization\" ${local.dashboard_namespace_filter}"
                aggregation = {
                  alignmentPeriod    = "60s"
                  perSeriesAligner   = "ALIGN_MEAN"
                  crossSeriesReducer = "REDUCE_MAX"
                  groupByFields      = ["resource.label.\"container_name\""]
                }
              }
            }
          }]
          yAxis = { scale = "LINEAR" }
        }
      }
    },
    {
      xPos = 0, yPos = 8, width = 4, height = 4
      widget = {
        title = "Worker: failed job attempts and jobs given up (per 5 min)"
        xyChart = {
          dataSets = [
            {
              plotType       = "STACKED_BAR"
              legendTemplate = "failed attempt: $${metric.labels.kind}"
              timeSeriesQuery = {
                timeSeriesFilter = {
                  filter = "metric.type=\"logging.googleapis.com/user/${google_logging_metric.worker_job_failures.name}\" resource.type=\"k8s_container\""
                  aggregation = {
                    alignmentPeriod    = "300s"
                    perSeriesAligner   = "ALIGN_SUM"
                    crossSeriesReducer = "REDUCE_SUM"
                    groupByFields      = ["metric.label.\"kind\""]
                  }
                }
              }
            },
            {
              plotType       = "LINE"
              legendTemplate = "given up: $${metric.labels.kind}"
              timeSeriesQuery = {
                timeSeriesFilter = {
                  filter = "metric.type=\"logging.googleapis.com/user/${google_logging_metric.worker_jobs_given_up.name}\" resource.type=\"k8s_container\""
                  aggregation = {
                    alignmentPeriod    = "300s"
                    perSeriesAligner   = "ALIGN_SUM"
                    crossSeriesReducer = "REDUCE_SUM"
                    groupByFields      = ["metric.label.\"kind\""]
                  }
                }
              }
            },
          ]
          yAxis = { scale = "LINEAR" }
        }
      }
    },
    {
      xPos = 4, yPos = 8, width = 4, height = 4
      widget = {
        title = "Container restarts (per 10 min)"
        xyChart = {
          dataSets = [{
            plotType       = "STACKED_BAR"
            legendTemplate = "$${resource.labels.container_name}"
            timeSeriesQuery = {
              timeSeriesFilter = {
                filter = "metric.type=\"kubernetes.io/container/restart_count\" ${local.dashboard_namespace_filter}"
                aggregation = {
                  alignmentPeriod    = "600s"
                  perSeriesAligner   = "ALIGN_DELTA"
                  crossSeriesReducer = "REDUCE_SUM"
                  groupByFields      = ["resource.label.\"container_name\""]
                }
              }
            }
          }]
          yAxis = { scale = "LINEAR" }
        }
      }
    },
    {
      xPos = 8, yPos = 8, width = 4, height = 4
      widget = {
        title = "Health check: regions failing"
        xyChart = {
          dataSets = [{
            plotType = "LINE"
            timeSeriesQuery = {
              timeSeriesFilter = {
                filter = "metric.type=\"monitoring.googleapis.com/uptime_check/check_passed\" resource.type=\"uptime_url\" metric.label.\"check_id\"=\"${google_monitoring_uptime_check_config.api_health.uptime_check_id}\""
                aggregation = {
                  alignmentPeriod    = "60s"
                  perSeriesAligner   = "ALIGN_NEXT_OLDER"
                  crossSeriesReducer = "REDUCE_COUNT_FALSE"
                }
              }
            }
          }]
          yAxis = { scale = "LINEAR" }
        }
      }
    },
    {
      xPos = 0, yPos = 12, width = 6, height = 4
      widget = {
        title = "Cloud SQL: CPU, memory, disk (share of capacity)"
        xyChart = {
          dataSets = [
            for metric in ["cpu", "memory", "disk"] : {
              plotType       = "LINE"
              legendTemplate = metric
              timeSeriesQuery = {
                timeSeriesFilter = {
                  filter = "metric.type=\"cloudsql.googleapis.com/database/${metric}/utilization\" ${local.dashboard_sql_filter}"
                  aggregation = {
                    alignmentPeriod  = "60s"
                    perSeriesAligner = "ALIGN_MEAN"
                  }
                }
              }
            }
          ]
          yAxis = { scale = "LINEAR" }
        }
      }
    },
    {
      xPos = 6, yPos = 12, width = 6, height = 4
      widget = {
        title = "Cloud SQL: connections (each API pod holds up to 20)"
        xyChart = {
          dataSets = [{
            plotType = "LINE"
            timeSeriesQuery = {
              timeSeriesFilter = {
                filter = "metric.type=\"cloudsql.googleapis.com/database/postgresql/num_backends\" ${local.dashboard_sql_filter}"
                aggregation = {
                  alignmentPeriod    = "60s"
                  perSeriesAligner   = "ALIGN_MEAN"
                  crossSeriesReducer = "REDUCE_SUM"
                }
              }
            }
          }]
          yAxis = { scale = "LINEAR" }
        }
      }
    },
    {
      xPos = 0, yPos = 16, width = 12, height = 6
      widget = {
        title = "Errors (API and worker logs)"
        logsPanel = {
          filter        = "resource.type=\"k8s_container\" resource.labels.namespace_name=\"${local.namespace}\" (severity>=ERROR OR jsonPayload.LogLevel=\"Error\" OR jsonPayload.LogLevel=\"Critical\")"
          resourceNames = ["projects/${data.google_project.main.number}"]
        }
      }
    },
  ]
}

resource "google_monitoring_dashboard" "main" {
  project = var.project_id
  dashboard_json = jsonencode({
    displayName = "Fandom Score (${var.environment})"
    labels      = local.labels
    mosaicLayout = {
      columns = 12
      tiles   = local.dashboard_tiles
    }
  })

  depends_on = [google_project_service.apis]
}
