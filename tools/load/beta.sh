#!/usr/bin/env bash
# The on-sale load test on beta, start to finish (tools/load/README.md, "Running it on beta"): the manual steps
# there, run in order by one script, ending in one summary block to paste back. It checks what it needs, asks once,
# pre-scales the API, loads synthetic fans and a throwaway show per pass (http/setup.sql through db-shell.sh), runs
# the k6 Job in the cluster (http/k6-job.yaml) for each in-cluster peak and then once through Cloud Armor, checks
# the registrations in the database, samples the database connections and the HPA while each pass runs, cleans up
# every tag (http/cleanup.sql), deletes the Job, and puts the HPA floor back.
#
#   tools/load/beta.sh                 # asks once before touching beta, then runs everything
#   tools/load/beta.sh --yes           # no question
#   tools/load/beta.sh --dry-run       # prints every command it would run; runs nothing against beta
#   LEVELS="25" tools/load/beta.sh     # in-cluster peaks (default "25 50"; "" skips them); EDGE_PEAK=1 (0 skips)
#
# Beta has real testers: run it outside their hours, announced, with the dashboard open (infrastructure/OPERATIONS.md,
# "Dashboard"). Needs gcloud signed in, kubectl on the beta cluster (infrastructure/RUNBOOK.md, "Where things are"),
# gh (to see whether a deploy is running), and jq. k6 runs inside the cluster from the Job's image; nothing to install.
# What it leaves to you: the database CPU, read from Cloud Monitoring for the window each pass prints, and recording
# the results in the README.
set -euo pipefail

ENVIRONMENT=beta
YES=0
DRY_RUN=0
for arg in "$@"; do
  case "$arg" in
    --yes | -y) YES=1 ;;
    --dry-run | -n) DRY_RUN=1 ;;
    --help | -h) sed -n '2,/^[^#]/{/^#/p}' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    beta) ENVIRONMENT=beta ;;
    prod) echo "This procedure is written for beta (its hostnames, pod ceiling, and tier); prod has no recipe yet." >&2; exit 2 ;;
    *) echo "Unknown argument '$arg'. Usage: tools/load/beta.sh [beta] [--yes] [--dry-run]" >&2; exit 2 ;;
  esac
done

LEVELS=${LEVELS-"25 50"}             # in-cluster peaks, fans a second (README step 4: start at 25, then 50)
EDGE_PEAK=${EDGE_PEAK:-1}            # the Cloud Armor pass (README step 5: 1 passes; 2 and above trip the throttle)
HOLD=${HOLD:-60}                     # seconds at the peak (onsale.js)
TICKETS=${TICKETS:-2000}
CITY=${CITY:-us-ak-anchorage}        # the test show's city and the other hub: no real tester's hub (README step 1)
OTHER_CITY=${OTHER_CITY:-us-hi-honolulu}
TAG=${TAG:-b$(date -u +%m%d%H%M)}    # run tags are "$TAG" plus a letter per pass; new every run (ids derive from it)
EDGE_URL=${EDGE_URL:-https://api.beta.firstline-5730kl.com}   # k6-job.yaml's comment; terraform envs/beta api_domain
IN_CLUSTER_URL=http://api.fandomscore.svc.cluster.local         # k6-job.yaml's default
MIN_PODS=6                           # beta's ceiling (overlays/beta maxReplicas); README step 2
OVERLAY_MIN_PODS=2                   # the beta overlay's minReplicas, restored at the end (README step 6)
PROJECT='project-ef36a8a9-d958-4163-b4c'   # RUNBOOK.md, "Where things are"
CLUSTER=fandomscore-$ENVIRONMENT
NS=fandomscore
REPO=masidav/firstline
DASHBOARD='https://console.cloud.google.com/monitoring/dashboards/builder/59585b13-d112-474c-89b9-97c25deadfcc?project=project-ef36a8a9-d958-4163-b4c'

ROOT=$(cd "$(dirname "$0")/../.." && pwd)
cd "$ROOT"
DB_SHELL=infrastructure/k8s/db-shell.sh
JOB_FILE=tools/load/http/k6-job.yaml
SCRIPT_FILE=tools/load/http/onsale.js
SETUP_SQL=tools/load/http/setup.sql
CLEANUP_SQL=tools/load/http/cleanup.sql
RESULTS_DIR=tools/load/results
WORK=$(mktemp -d)
NOTES="$WORK/summary.txt"
: > "$NOTES"

STARTED_AT=$(date -u +%Y-%m-%dT%H:%M:%SZ)
LOAD_STARTED=0        # set once beta is changed; the exit trap then cleans up
CLEANED=0
CREATED_TAGS=()
WINDOWS=()            # "<tag> <start> <end>" per pass, for the log query afterwards
SAMPLER_PID=
SAMPLER_STOP=
HPA_SAMPLER_PID=
PRE_FAILED=0
PASS_FAILED=0

# ---------------------------------------------------------------------------------------------------- helpers
stage() { printf '\n== %s\n' "$*"; }
note() { printf '%s\n' "$*" | tee -a "$NOTES"; }
quiet_note() { printf '%s\n' "$*" >> "$NOTES"; }
die() { echo "ERROR: $*" >&2; exit 1; }
show_cmd() {   # the command as you would type it
  local out="+" a
  for a in "$@"; do
    if [[ $a =~ ^[A-Za-z0-9_./=:,@%+-]+$ ]]; then out="$out $a"; else out="$out '${a//\'/\'\\\'\'}'"; fi
  done
  printf '%s\n' "$out" >&2
}
run() { show_cmd "$@"; [ "$DRY_RUN" = 1 ] || "$@"; }         # a command for its effect
qry() { show_cmd "$@"; [ "$DRY_RUN" = 1 ] || "$@"; }         # a command for its output (empty in a dry run)
db() {  # db "<what produces the SQL>"; the SQL itself on stdin (README: ... | infrastructure/k8s/db-shell.sh beta)
  printf '+ %s | %s %s\n' "$1" "$DB_SHELL" "$ENVIRONMENT" >&2
  if [ "$DRY_RUN" = 1 ]; then cat > /dev/null; return 0; fi
  "$DB_SHELL" "$ENVIRONMENT"
}
db_sql() {  # one statement; output unaligned, no headers, so it can be parsed
  local sql=$1
  printf '\\pset tuples_only on\n\\pset format unaligned\n%s\n' "$sql" | db "echo \"$sql\""
}
# Fans the arrival-rate stages in onsale.js use (http/run.sh): ramp 1 -> peak/4 for 30 s, 30 s there, 10 s up to the
# peak, HOLD s there, 10 s down, plus 5 % and 100 for k6 starting iterations a little early.
fans_for() { awk -v p="$1" -v h="$HOLD" 'BEGIN { b = int(p / 4 + 0.5); if (b < 1) b = 1;
  printf "%d", ((1 + b) / 2 * 30 + b * 30 + (b + p) / 2 * 10 + p * h + p / 2 * 10) * 1.05 + 100 }'; }
check() {  # check "<message>" <command...>: in a dry run a failing check is reported, not fatal
  local message=$1; shift
  if "$@"; then return 0; fi
  if [ "$DRY_RUN" = 1 ]; then echo "  would stop here: $message" >&2; PRE_FAILED=1; return 0; fi
  die "$message"
}
is_int() { [[ $1 =~ ^[0-9]+$ ]]; }
matches() { [[ $1 =~ $2 ]]; }
ends_with() { [[ $1 == *"$2" ]]; }
gh_signed_in() { gh auth status >/dev/null 2>&1; }
manual_cleanup() {
  cat >&2 <<EOF
Finish by hand (README step 6):
  for tag in ${CREATED_TAGS[*]+"${CREATED_TAGS[*]}"}; do { printf '\\\\set run %s\\n' "\$tag"; cat $CLEANUP_SQL; } | $DB_SHELL $ENVIRONMENT; done
  kubectl -n $NS delete job/load-onsale configmap/load-onsale
  kubectl -n $NS patch hpa api -p '{"spec":{"minReplicas":$OVERLAY_MIN_PODS}}'
EOF
}

# ------------------------------------------------------------------------------------------------- the cluster
hpa_min() { local v; v=$(qry kubectl -n "$NS" get hpa api -o 'jsonpath={.spec.minReplicas}'); [ "$DRY_RUN" = 1 ] && v=$MIN_PODS; echo "$v"; }
api_ready() {  # README step 2: `kubectl -n fandomscore get pods -l app.kubernetes.io/component=api` shows 6 ready
  kubectl -n "$NS" get pods -l app.kubernetes.io/component=api --no-headers 2>/dev/null | awk '$2 == "1/1" && $3 == "Running" { n++ } END { print n + 0 }'
}
deploy_status() {  # README: `gh run list --workflow Deploy --limit 1`; the HPA floor is reset by every deploy
  qry gh run list --repo "$REPO" --workflow Deploy --limit 1 --json status --jq '.[0].status // "none"'
}
wait_for_no_deploy() {
  local status i
  for i in $(seq 1 60); do
    status=$(deploy_status)
    [ "$DRY_RUN" = 1 ] && status=completed
    case "$status" in
      completed | none) echo "Deploy: last run $status; none in progress."; return 0 ;;
      *) [ "$i" = 1 ] && echo "A Deploy run is $status. Waiting for it (it reapplies the overlay, so the HPA floor is checked again after)."; sleep 30 ;;
    esac
  done
  die "A Deploy run was still in progress after 30 minutes."
}
prescale() {  # README step 2 and infrastructure/README.md "On-sale traffic"
  LOAD_STARTED=1
  run kubectl -n "$NS" patch hpa api -p "{\"spec\":{\"minReplicas\":$MIN_PODS}}"
  show_cmd kubectl -n "$NS" get pods -l app.kubernetes.io/component=api
  [ "$DRY_RUN" = 1 ] && { echo "(waits until $MIN_PODS api pods are 1/1 Running, up to 15 minutes)"; return 0; }
  local i ready
  for i in $(seq 1 180); do
    ready=$(api_ready)
    [ "$ready" -ge "$MIN_PODS" ] && break
    [ "$((i % 6))" = 1 ] && echo "  $ready of $MIN_PODS api pods ready (Autopilot may be adding nodes)..."
    sleep 5
  done
  kubectl -n "$NS" get pods -l app.kubernetes.io/component=api
  [ "$ready" -ge "$MIN_PODS" ] || die "Only $ready of $MIN_PODS api pods ready after 15 minutes. Look at: kubectl -n $NS get events --sort-by=.lastTimestamp | tail -20"
  echo "$ready api pods ready."
}
ensure_floor() {  # README: before each pass, no deploy in progress and the HPA still shows 6
  wait_for_no_deploy
  local min; min=$(hpa_min)
  if [ "$min" != "$MIN_PODS" ]; then
    echo "The HPA floor is $min, not $MIN_PODS (a deploy reset it). Raising it again."
    prescale
  else
    echo "HPA minReplicas: $min."
  fi
}

# The samplers behind each pass: database connections every 10 s (README results: "connections sampled every 10 s
# from pg_stat_activity for the app user"), through one db-shell session fed a query at a time, and the HPA's CPU
# reading (`kubectl get hpa api`) every 10 s. Both stop when the stop file appears.
start_samplers() {
  local out=$1 hpa_out=$2
  SAMPLER_STOP="$WORK/stop-$(date +%s)"
  printf '+ echo "SELECT now()::time(0), count(*) FROM pg_stat_activity WHERE usename = current_user;" | %s %s   # every 10 s, one session, while the Job runs\n' "$DB_SHELL" "$ENVIRONMENT" >&2
  printf '+ kubectl -n %s get hpa api   # every 10 s while the Job runs\n' "$NS" >&2
  [ "$DRY_RUN" = 1 ] && return 0
  { printf '\\pset tuples_only on\n\\pset format unaligned\n'
    while [ ! -e "$SAMPLER_STOP" ]; do
      printf 'SELECT now()::time(0), count(*) FROM pg_stat_activity WHERE usename = current_user;\n'
      sleep 10
    done
  } | "$DB_SHELL" "$ENVIRONMENT" > "$out" 2> "$out.err" &
  SAMPLER_PID=$!
  { while [ ! -e "$SAMPLER_STOP" ]; do kubectl -n "$NS" get hpa api --no-headers 2>/dev/null || true; sleep 10; done; } > "$hpa_out" &
  HPA_SAMPLER_PID=$!
}
stop_samplers() {
  [ -n "$SAMPLER_STOP" ] && touch "$SAMPLER_STOP"
  local i
  if [ -n "$SAMPLER_PID" ]; then
    for i in $(seq 1 30); do kill -0 "$SAMPLER_PID" 2>/dev/null || break; sleep 2; done
    if kill -0 "$SAMPLER_PID" 2>/dev/null; then
      kill "$SAMPLER_PID" 2>/dev/null || true
      echo "The connection sampler's db-shell did not exit; a db-shell-* pod may be left: kubectl -n $NS get pods" >&2
    fi
    wait "$SAMPLER_PID" 2>/dev/null || true
  fi
  [ -n "$HPA_SAMPLER_PID" ] && { kill "$HPA_SAMPLER_PID" 2>/dev/null || true; wait "$HPA_SAMPLER_PID" 2>/dev/null || true; }
  SAMPLER_PID=; HPA_SAMPLER_PID=; SAMPLER_STOP=
}

# k6-job.yaml with this pass's env values (README step 4: "Set RUN, FANS, and PEAK in the file").
render_job() {
  awk -v base_url="$1" -v run="$2" -v fans="$3" -v peak="$4" -v hold="$5" '
    /^ *- name: (BASE_URL|RUN|FANS|PEAK|HOLD)$/ { key = $3; print; next }
    key != "" && /^ *value: / {
      v = (key == "BASE_URL") ? base_url : (key == "RUN") ? run : (key == "FANS") ? fans : (key == "PEAK") ? peak : hold
      sub(/value: .*/, "value: \"" v "\""); key = ""
    }
    { print }' "$JOB_FILE"
}

# One k6 summary, compacted: each threshold with its result, then the lines that matter from the totals.
k6_digest() {
  awk '
    /█ THRESHOLDS/ { sec = "t"; next }
    /█ TOTAL RESULTS/ { sec = "r"; next }
    sec == "t" {   # a metric name, then its threshold line(s); "{name:event}" belongs to the metric above it
      line = $0; gsub(/^[[:space:]]+|[[:space:]]+$/, "", line)
      if (line ~ /^(✓|✗)/) { print "    " name ": " line } else if (line ~ /^\{/) { name = parent line } else if (line != "") { name = line; parent = line }
    }
    sec == "r" {
      line = $0; gsub(/^[[:space:]]+|[[:space:]]+$/, "", line)
      if (line ~ /^[a-z_]+\.+:/) { rparent = line; sub(/\.+:.*/, "", rparent) }
      if (line ~ /^\{/) { sub(/^\{ */, "{", line); sub(/ *\}/, "}", line); line = rparent line }
      else if (line !~ /^(checks_succeeded|checks_failed|✓|✗|register_attempts|registered|registration_wrong|server_errors|rate_limited|fans_exhausted|http_req_duration|http_req_failed|http_reqs|dropped_iterations|iteration|vus_max)/) next
      gsub(/\.\.+:/, ":", line); gsub(/[[:space:]]+/, " ", line); print "    " line
    }' "$1"
}

# ----------------------------------------------------------------------------------------------------- a pass
pass() {  # pass <letter> <peak> <base_url> <label>
  local letter=$1 peak=$2 base_url=$3 label=$4
  local tag="$TAG$letter" fans log start end outcome attempts rows digest conns hpa_peak
  fans=$(fans_for "$peak")
  log="$WORK/k6-$tag.log"
  stage "Pass $letter: $label, tag $tag, PEAK=$peak fans/s, HOLD=$HOLD s, FANS=$fans"
  ensure_floor

  echo "-- Test fans and an isolated allocation (README step 3; the allocation opens 3 s after this)"
  CREATED_TAGS+=("$tag")
  local setup_out
  setup_out=$({ printf '\\set run %s\n\\set fans %s\n\\set tickets %s\n\\set city %s\n\\set other_city %s\n' \
                  "$tag" "$fans" "$TICKETS" "$CITY" "$OTHER_CITY"; cat "$SETUP_SQL"; } \
              | db "{ printf '\\\\set run $tag\\n\\\\set fans $fans\\n\\\\set tickets $TICKETS\\n\\\\set city $CITY\\n\\\\set other_city $OTHER_CITY\\n'; cat $SETUP_SQL; }")
  if [ "$DRY_RUN" != 1 ]; then
    printf '%s\n' "$setup_out" | grep -E '^event=|^(INSERT|UPDATE|BEGIN|COMMIT)' | tail -3
    printf '%s\n' "$setup_out" | grep -q '^event=' || die "setup.sql did not print its event and allocation ids; its output: $setup_out"
  fi

  echo "-- The k6 Job (README step 4), right after setup"
  render_job "$base_url" "$tag" "$fans" "$peak" "$HOLD" > "$WORK/k6-job-$tag.yaml"
  echo "  $JOB_FILE with BASE_URL=$base_url RUN=$tag FANS=$fans PEAK=$peak HOLD=$HOLD -> $WORK/k6-job-$tag.yaml"
  start=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  run kubectl -n "$NS" apply -f "$WORK/k6-job-$tag.yaml"
  start_samplers "$WORK/connections-$tag.txt" "$WORK/hpa-$tag.txt"
  show_cmd kubectl -n "$NS" logs -f job/load-onsale
  if [ "$DRY_RUN" = 1 ]; then
    echo "(waits for the Job's pod, follows k6's output, then reads the Job's status: succeeded or failed)"
    end="<end>"; outcome="dry run"; attempts=0; digest="    (k6 summary)"; conns="n/a"; hpa_peak="n/a"
  else
    local i phase=
    for i in $(seq 1 120); do   # Autopilot may have to add a node for the Job's 2 CPUs
      phase=$(kubectl -n "$NS" get pods -l job-name=load-onsale -o 'jsonpath={.items[0].status.phase}' 2>/dev/null || true)
      case "$phase" in Running | Succeeded | Failed) break ;; esac
      [ "$((i % 6))" = 1 ] && echo "  Job pod: ${phase:-not yet scheduled}..."
      sleep 5
    done
    case "$phase" in Running | Succeeded | Failed) ;; *) stop_samplers; die "The load-onsale pod did not start in 10 minutes (${phase:-no pod}): kubectl -n $NS describe job/load-onsale" ;; esac
    kubectl -n "$NS" logs -f job/load-onsale 2>&1 | tee "$log" || true
    for i in $(seq 1 24); do
      outcome=$(kubectl -n "$NS" get job load-onsale -o json | jq -r 'if (.status.succeeded // 0) > 0 then "passed" elif (.status.failed // 0) > 0 then "FAILED" else "" end')
      [ -n "$outcome" ] && break
      sleep 5
    done
    [ -n "$outcome" ] || outcome="unknown (Job neither succeeded nor failed 2 minutes after its log ended)"
    end=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    kubectl -n "$NS" logs job/load-onsale > "$log" 2>/dev/null || true   # the complete output, in case -f dropped
    stop_samplers
    digest=$(k6_digest "$log")
    [ -n "$digest" ] || digest=$(tail -n 40 "$log" | sed 's/^/    /')
    attempts=$(sed -nE 's/^[[:space:]]*register_attempts\.+:[[:space:]]*([0-9]+).*/\1/p' "$log" | head -1)
    attempts=${attempts:-0}
    conns=$(awk -F'|' '/^[0-9:]+\|[0-9]+$/ { n++; if ($2 + 0 > m) m = $2 + 0 } END { if (n) printf "peak %d (%d samples, one of them this sampler)", m, n; else print "no samples (see the sampler error output)" }' "$WORK/connections-$tag.txt")
    hpa_peak=$(sed -nE 's/.*cpu: *([0-9]+)%\/.*/\1/p' "$WORK/hpa-$tag.txt" | sort -n | tail -1)
    hpa_peak=${hpa_peak:+$hpa_peak % of the 250m request}; hpa_peak=${hpa_peak:-no reading}
    cp "$log" "$RESULTS_FILE_BASE-$tag.k6.log"
  fi
  WINDOWS+=("$tag $start $end")

  echo "-- Every fan who tried is registered once (README step 4)"
  rows=$(db_sql "SELECT count(*), count(DISTINCT user_id) FROM allocation_registrations WHERE allocation_id = md5('fandom-load:$tag:allocation')::uuid;")
  local verdict="(dry run)"
  if [ "$DRY_RUN" != 1 ]; then
    rows=$(printf '%s\n' "$rows" | grep -E '^[0-9]+\|[0-9]+$' | tail -1 || true)
    local total distinct; total=${rows%%|*}; distinct=${rows##*|}
    if [ -z "$rows" ]; then verdict="UNREADABLE (no count line from db-shell)"
    elif [ "$total" = "$attempts" ] && [ "$distinct" = "$total" ]; then verdict="OK, every fan registered once"
    else verdict="MISMATCH"; fi
    echo "database: $total registrations, $distinct fans, $attempts k6 attempts -> $verdict"
  fi

  echo "-- Delete the Job before the next pass (README step 4)"
  run kubectl -n "$NS" delete job/load-onsale --ignore-not-found

  [ "$outcome" = passed ] || [ "$DRY_RUN" = 1 ] || PASS_FAILED=1
  quiet_note "Pass $letter: $label, tag $tag, PEAK=$peak fans/s, HOLD=$HOLD s, FANS=$fans, $start to $end"
  quiet_note "  k6: $outcome (the Job fails if any threshold did)"
  quiet_note "$digest"
  quiet_note "  database: ${rows:-?} registrations|distinct fans; k6 register_attempts $attempts -> $verdict"
  quiet_note "  app connections (pg_stat_activity, every 10 s): $conns"
  quiet_note "  API HPA cpu (kubectl get hpa api, every 10 s): $hpa_peak"
  quiet_note "  DB CPU peak: ____ (Cloud Monitoring cloudsql.googleapis.com/database/cpu/utilization for $start to $end; the dashboard's Cloud SQL CPU tile)"
}

# ---------------------------------------------------------------------------------------------- cleanup, exit
cleanup() {  # README step 6
  [ "$CLEANED" = 1 ] && return 0
  CLEANED=1
  stage "Cleanup (README step 6)"
  local failed=0 tag out
  for tag in ${CREATED_TAGS[@]+"${CREATED_TAGS[@]}"}; do
    out=$({ printf '\\set run %s\n' "$tag"; cat "$CLEANUP_SQL"; } | db "{ printf '\\\\set run $tag\\n'; cat $CLEANUP_SQL; }") || { failed=1; out="cleanup.sql FAILED for $tag"; }
    if [ "$DRY_RUN" = 1 ]; then quiet_note "  $tag: (cleanup.sql: live_registrations 0, fans_left 0 expected)"; continue; fi
    out=$(printf '%s\n' "$out" | awk '/^ *[0-9]+ *\| *[0-9]+ *$/ { gsub(/ /, ""); split($0, a, "|"); printf "live_registrations %s, fans_left %s", a[1], a[2] }')
    [ "${out:-}" = "live_registrations 0, fans_left 0" ] || failed=1
    note "  $tag: ${out:-no live_registrations/fans_left line from cleanup.sql}"
  done
  run kubectl -n "$NS" delete job/load-onsale configmap/load-onsale --ignore-not-found || failed=1
  run kubectl -n "$NS" patch hpa api -p "{\"spec\":{\"minReplicas\":$OVERLAY_MIN_PODS}}" || failed=1
  if [ "$failed" = 1 ]; then note "  CLEANUP INCOMPLETE: see the commands printed above and finish by hand."; manual_cleanup; return 1; fi
  note "  job and configmap deleted; HPA minReplicas back to $OVERLAY_MIN_PODS"
}
on_exit() {
  local status=$?
  set +e
  trap - EXIT
  stop_samplers
  if [ "$LOAD_STARTED" = 1 ] && [ "$CLEANED" = 0 ]; then
    [ "$status" = 0 ] || echo "Stopped with status $status; cleaning up what was created." >&2
    cleanup || status=1
  fi
  if [ "$status" != 0 ] && [ "$LOAD_STARTED" = 1 ]; then
    echo >&2
    echo "The run did not finish. What was recorded so far ($NOTES):" >&2
    cat "$NOTES" >&2
  fi
  exit "$status"
}
trap on_exit EXIT

# ============================================================================================== 0. prerequisites
stage "Prerequisites"
[ "$DRY_RUN" = 1 ] && echo "DRY RUN: nothing is run against $ENVIRONMENT; commands are printed with a leading +."
for tool in gcloud kubectl gh jq awk; do check "$tool is not installed (needed: gcloud, kubectl, gh, jq, awk)" command -v "$tool" >/dev/null; done
for f in "$DB_SHELL" "$JOB_FILE" "$SCRIPT_FILE" "$SETUP_SQL" "$CLEANUP_SQL"; do check "$f is missing" test -f "$f"; done
check "$DB_SHELL is not executable" test -x "$DB_SHELL"
check "TAG must be letters and digits, at most 12 (it names rows and ids): '$TAG'" matches "$TAG" '^[A-Za-z0-9]{1,12}$'
check "HOLD must be a number of seconds: '$HOLD'" is_int "$HOLD"
check "EDGE_PEAK must be a number (0 skips the Cloud Armor pass): '$EDGE_PEAK'" is_int "$EDGE_PEAK"
check "TICKETS must be a number: '$TICKETS'" is_int "$TICKETS"
for level in $LEVELS; do check "LEVELS must be numbers (fans a second): '$LEVELS'" is_int "$level"; done
echo "environment: $ENVIRONMENT (project $PROJECT, cluster $CLUSTER, namespace $NS)"
account=$(gcloud auth list --filter=status:ACTIVE --format='value(account)' 2>/dev/null | head -1 || true)
check "gcloud is not signed in: gcloud auth login --update-adc" test -n "$account"
echo "gcloud account: ${account:-none}"
project=$(gcloud config get-value project 2>/dev/null || true)
check "gcloud project is '${project:-unset}', not $PROJECT: gcloud config set project $PROJECT" test "$project" = "$PROJECT"
context=$(kubectl config current-context 2>/dev/null || true)
check "kubectl points at '${context:-nothing}', not the $ENVIRONMENT cluster ($CLUSTER): gcloud container clusters get-credentials $CLUSTER --region us-central1 --project $PROJECT" \
  ends_with "$context" "$CLUSTER"
echo "kubectl context: ${context:-none}"
check "gh is not signed in (needed to see whether a Deploy run is in progress): gh auth login" gh_signed_in
image=$(sed -nE 's/^ *image: *(.*)$/\1/p' "$JOB_FILE" | head -1)
echo "k6: runs in the cluster from $image (the Job in $JOB_FILE); nothing to install locally"
mkdir -p "$RESULTS_DIR"
RESULTS_FILE="$RESULTS_DIR/beta-$(date -u +%Y-%m-%d).txt"
[ -e "$RESULTS_FILE" ] && RESULTS_FILE="$RESULTS_DIR/beta-$(date -u +%Y-%m-%d-%H%M).txt"
RESULTS_FILE_BASE=${RESULTS_FILE%.txt}
echo "results: $RESULTS_FILE (and the k6 logs beside it; the directory is not committed)"
tier=$(sed -nE 's/^ *sql_tier *= *"([^"]*)".*/\1/p' infrastructure/terraform/envs/beta/main.tf | head -1)

stage "Checks on $ENVIRONMENT before anything changes"
echo "-- The cluster answers (README step 2: the HPA)"
show_cmd kubectl -n "$NS" get hpa api
[ "$DRY_RUN" = 1 ] || kubectl -n "$NS" get hpa api || die "kubectl cannot read the api HPA in $NS on $CLUSTER (is the cluster reachable and the environment on?)"
echo "-- No leftover Job or configmap from an earlier run"
show_cmd kubectl -n "$NS" get job/load-onsale configmap/load-onsale --ignore-not-found
if [ "$DRY_RUN" != 1 ]; then
  leftovers=$(kubectl -n "$NS" get job/load-onsale configmap/load-onsale --ignore-not-found --no-headers 2>/dev/null || true)
  [ -z "$leftovers" ] || die "load-onsale already exists in the cluster (an earlier run that did not clean up):
$leftovers
Run: kubectl -n $NS delete job/load-onsale configmap/load-onsale"
fi
echo "-- No Deploy run in progress (README: every deploy resets the HPA floor)"
wait_for_no_deploy
echo "-- The test show's cities are nobody's hub (README step 1; expects no rows)"
hubs=$(db_sql "SELECT city_id, count(*) FROM user_hub_cities WHERE city_id IN ('$CITY', '$OTHER_CITY') GROUP BY 1;")
if [ "$DRY_RUN" != 1 ]; then
  hubs=$(printf '%s\n' "$hubs" | grep -E '^[^|]+\|[0-9]+$' || true)
  [ -z "$hubs" ] || die "A real tester has one of the test cities as a hub, so the test show would appear in their feed:
$hubs
Pick two other city ids: CITY=... OTHER_CITY=... tools/load/beta.sh"
  echo "no rows: nobody has $CITY or $OTHER_CITY as a hub"
fi
echo "-- The database's connection limit (README step 2: $MIN_PODS pods can open $((MIN_PODS * 24)))"
max_conn=$(db_sql "SHOW max_connections;")
[ "$DRY_RUN" = 1 ] && max_conn="?"
max_conn=$(printf '%s\n' "$max_conn" | grep -E '^[0-9?]+$' | tail -1 || true)
echo "max_connections: ${max_conn:-unknown}"

# ============================================================================================== 1. the plan
stage "Plan"
plan_letters=(a b c d e f g h)
n=0
for level in $LEVELS; do
  echo "  pass ${plan_letters[$n]}: in cluster ($IN_CLUSTER_URL), PEAK=$level fans/s, HOLD=$HOLD s, FANS=$(fans_for "$level"), tag $TAG${plan_letters[$n]}"
  n=$((n + 1))
done
if [ "$EDGE_PEAK" != 0 ]; then
  echo "  pass ${plan_letters[$n]}: through Cloud Armor ($EDGE_URL), PEAK=$EDGE_PEAK fans/s, HOLD=$HOLD s, FANS=$(fans_for "$EDGE_PEAK"), tag $TAG${plan_letters[$n]}"
  n=$((n + 1))
fi
[ "$n" -gt 0 ] || die "Nothing to run: LEVELS is empty and EDGE_PEAK is 0."
cat <<EOF
  before: HPA minReplicas $MIN_PODS and $MIN_PODS api pods ready (Autopilot: up to 15 minutes)
  each pass: setup.sql (synthetic fans, a 'Load Test <tag> (ignore)' artist, one show, one allocation of $TICKETS),
             the k6 Job, about $((80 + HOLD)) s of load plus scheduling, the registrations check, delete the Job
  after: cleanup.sql per tag, delete the Job and configmap, HPA minReplicas back to $OVERLAY_MIN_PODS
  Beta has real testers: outside their hours, announced, with the dashboard open: $DASHBOARD
EOF
[ "$PRE_FAILED" = 0 ] || echo "DRY RUN: a real run would have stopped at the checks marked above." >&2
if [ "$DRY_RUN" = 1 ]; then
  echo "(prompt: Run this on $ENVIRONMENT now? [y/N])"
elif [ "$YES" != 1 ]; then
  read -r -p "Run this on $ENVIRONMENT now? [y/N] " answer
  case "$answer" in y | Y | yes | YES) ;; *) echo "Cancelled; nothing was changed."; exit 1 ;; esac
fi

# ============================================================================================== 2. pre-scale
stage "Pre-scale the API to $MIN_PODS pods (README step 2)"
prescale
note "Beta on-sale load test, $STARTED_AT, tools/load/beta.sh: Cloud SQL ${tier:-?}, max_connections ${max_conn:-?}, HPA minReplicas $MIN_PODS ($MIN_PODS api pods of 250m request), HOLD=$HOLD"

# ============================================================================================== 3. the passes
stage "The k6 script as a configmap (README step 4)"
run kubectl -n "$NS" create configmap load-onsale --from-file="$SCRIPT_FILE"
n=0
for level in $LEVELS; do
  pass "${plan_letters[$n]}" "$level" "$IN_CLUSTER_URL" "in cluster"
  n=$((n + 1))
done
if [ "$EDGE_PEAK" != 0 ]; then
  echo
  echo "The edge pass (README step 5): all requests come from the cluster's one egress address, which Cloud Armor"
  echo "throttles at 600 a minute; PEAK=1 should pass with no 429, PEAK=2 and above is expected to trip rate_limited."
  pass "${plan_letters[$n]}" "$EDGE_PEAK" "$EDGE_URL" "through Cloud Armor"
fi

# ============================================================================================== 4. cleanup
cleanup || true

# ============================================================================================== 5. API errors
stage "API error log lines in each pass window (infrastructure/OPERATIONS.md, \"Logs\")"
echo "(Cloud Logging may still be ingesting the last minute; re-run the query later if a count looks low)"
for window in ${WINDOWS[@]+"${WINDOWS[@]}"}; do
  read -r tag start end <<< "$window"
  filter="resource.type=\"k8s_container\" resource.labels.namespace_name=\"$NS\" resource.labels.container_name=\"api\" (severity>=ERROR OR jsonPayload.LogLevel=\"Error\") timestamp>=\"$start\" timestamp<=\"$end\""
  if [ "$DRY_RUN" = 1 ]; then
    show_cmd gcloud logging read "$filter" --project "$PROJECT" --freshness 12h --limit 5000 --format 'value(jsonPayload.Message)'
    continue
  fi
  show_cmd gcloud logging read "$filter" --project "$PROJECT" --freshness 12h --limit 5000 --format 'value(jsonPayload.Message)'
  errors=$(gcloud logging read "$filter" --project "$PROJECT" --freshness 12h --limit 5000 --format 'value(jsonPayload.Message)' 2>/dev/null) || errors="__failed__"
  if [ "$errors" = "__failed__" ]; then
    quiet_note "  $tag: API error lines: query failed; run it by hand: gcloud logging read '$filter' --project $PROJECT --freshness 12h --limit 5000"
    continue
  fi
  count=$(printf '%s' "$errors" | grep -c . || true)
  note "  $tag ($start to $end): $count API error lines"
  [ "$count" = 0 ] || printf '%s\n' "$errors" | cut -c1-110 | sort | uniq -c | sort -rn | head -3 | sed 's/^/      /' | tee -a "$NOTES"
done

# ============================================================================================== 6. summary
stage "Summary"
echo "Fill in each pass's DB CPU peak from Cloud Monitoring (the dashboard's Cloud SQL CPU tile, or Metrics Explorer,"
echo "cloudsql.googleapis.com/database/cpu/utilization, 1-minute points) for the window the pass shows, then paste:"
echo "  $DASHBOARD"
[ "$PASS_FAILED" = 0 ] || quiet_note "At least one pass FAILED its k6 thresholds (see above); the data was still cleaned up."
if [ "$DRY_RUN" = 1 ]; then
  echo "(dry run: the summary would be written to $RESULTS_FILE and printed between the markers below)"
else
  cp "$NOTES" "$RESULTS_FILE"
  echo "Written to $RESULTS_FILE (k6 logs beside it; rendered Job manifests and samples in $WORK)"
fi
echo
echo "----- paste from here -----"
cat "$NOTES"
echo "----- to here -----"
