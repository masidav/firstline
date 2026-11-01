"""One line from a k6 --summary-export file (run.sh): summary.py <file> <label>, or summary.py <file> attempts."""
import json
import sys

metrics = json.load(open(sys.argv[1]))["metrics"]


def get(name, field, default=0.0):
    return metrics.get(name, {}).get(field, default)


if sys.argv[2] == "attempts":
    print(int(get("register_attempts", "count")))
    sys.exit()


def latency(name):
    m = metrics.get(name, {})
    return f"p50 {m.get('med', 0):.0f} / p95 {m.get('p(95)', 0):.0f} / p99 {m.get('p(99)', 0):.0f} ms"


print(f"peak {sys.argv[2]} fans/s: {get('http_reqs', 'count'):.0f} requests, {get('http_reqs', 'rate'):.0f}/s average; "
      f"{get('registered', 'count'):.0f} of {get('register_attempts', 'count'):.0f} registered; "
      f"max {get('vus_max', 'max', get('vus_max', 'value')):.0f} VUs")
print(f"  all requests  {latency('http_req_duration')}")
for name in ("event", "eligibility", "register"):
    print(f"  {name:<13} {latency('http_req_duration{name:' + name + '}')}")
print(f"  failed {get('http_req_failed', 'value') * 100:.2f}%, 5xx or no response {get('server_errors', 'count'):.0f}, "
      f"429 {get('rate_limited', 'count'):.0f}, wrong registrations {get('registration_wrong', 'count'):.0f}, "
      f"checks {get('checks', 'value') * 100:.2f}%, fans ran out {get('fans_exhausted', 'count'):.0f}")
