#!/usr/bin/env bash
# Turns one cloud environment off without destroying it: every workload goes to zero replicas and the Gateway is
# deleted (its load balancer is the other thing that costs money while idle). Data, config, secrets, certificates,
# DNS, and Cloud SQL stay. Turning it back on is an ordinary deploy (deploy.sh), which recreates the Gateway;
# allow about 15 minutes before the hostnames answer again.
#
#   infrastructure/k8s/shutdown.sh <beta|prod>
#
# Normally run by the Deploy workflow when infrastructure/k8s/overlays/<env>/state says "off" (set-state.sh).
# Needs kubectl pointed at the environment's cluster.
set -euo pipefail

environment="${1:?environment (beta, prod)}"
case "$environment" in
  beta|prod) ;;
  *) echo "Unknown environment '${environment}': beta or prod." >&2; exit 1 ;;
esac
namespace=fandomscore

echo "== Scaling ${environment} to zero"
kubectl scale deployment --all --replicas=0 -n "$namespace"
kubectl delete job migrate -n "$namespace" --ignore-not-found
kubectl wait pod --all -n "$namespace" --for=delete --timeout=5m || true
kubectl get deployment -n "$namespace"

echo "== Deleting the Gateway (load balancer)"
kubectl delete gateway api -n "$namespace" --ignore-not-found --wait --timeout=5m

echo "${environment} is off. Cloud SQL (fandomscore-${environment}) keeps running; the next deploy with state 'on' brings everything back."
