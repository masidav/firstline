#!/usr/bin/env bash
# Runs industry_newsletter.py from cron. Settings (SMTP password, Anthropic key) come from
# ~/.config/industry-newsletter/env, which is outside the repo and never committed.
#   crontab:  0 8 * * 1,4  /home/glide/firstline/crawlers/newsletter_cron.sh
set -u
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
conf="${NEWSLETTER_ENV:-$HOME/.config/industry-newsletter/env}"
state_dir="$HOME/.local/state/industry-newsletter"
mkdir -p "$state_dir"
if [ -f "$conf" ]; then
  # shellcheck source=/dev/null
  set -a; . "$conf"; set +a
else
  echo "$(date -Is) no settings file at $conf; nothing sent" >&2
  exit 1
fi
echo "$(date -Is) run start"
cd "$here" && python3 industry_newsletter.py --state "$state_dir/newsletter.state.json" --out "$state_dir/last-email.txt" "$@"
rc=$?
echo "$(date -Is) run end rc=$rc"
exit $rc
