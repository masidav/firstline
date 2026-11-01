// On-sale spike through the full HTTP path (tools/load/README.md, "Full HTTP path"). Each iteration is one synthetic
// fan from setup.sql doing what the fan app's show screen does (apps/mobile/src/features/ticketing/ShowScreen.tsx):
// open the show (GET /events/{id}), load their eligibility and standing for the allocation, refresh it a few times
// while deciding, register (PUT /registration, some fans tapping twice), and load the eligibility again as the
// screen does after registering. Fans arrive at a rate that ramps up, holds, spikes to PEAK, and holds there.
//
// Every fan is used once: iteration i is fan i + 1, so FANS must cover the whole run (run.sh sizes it). Their
// user and session ids are derived from RUN like setup.sql does, and their access tokens are signed here with the
// key in SIGNING_KEY_FILE, as the API's SessionService signs them (HS256, sub and sid, 15 minutes).
//
// Environment:
//   BASE_URL           API origin, default http://127.0.0.1:5271 (the /v1 prefix is added here)
//   RUN, FANS          the run tag and fan count given to setup.sql
//   SIGNING_KEY_FILE   file holding the API's Auth:SigningKey (base64)
//   PEAK               peak fan arrivals per second (default 50); the base load is a quarter of it
//   HOLD               seconds at the peak (default 60)
//   REFRESHES          eligibility refreshes before registering (default 2), THINK seconds apart at most (default 2)
//   DOUBLE_TAP         share of fans who send the registration twice at once (default 0.1)
//   MAX_VUS            cap on concurrent fans (default 4000)
//   P95_MS             p95 latency threshold for every request (default 500)
import http from 'k6/http';
import crypto from 'k6/crypto';
import encoding from 'k6/encoding';
import exec from 'k6/execution';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';

const BASE = `${(__ENV.BASE_URL || 'http://127.0.0.1:5271').replace(/\/$/, '')}/v1`;
const RUN = __ENV.RUN || fail('RUN is required (the run tag given to setup.sql)');
const FANS = Number(__ENV.FANS || fail('FANS is required (the fan count given to setup.sql)'));
const PEAK = Number(__ENV.PEAK || 50);
const HOLD = Number(__ENV.HOLD || 60);
const REFRESHES = Number(__ENV.REFRESHES ?? 2);
const THINK = Number(__ENV.THINK ?? 2);
const DOUBLE_TAP = Number(__ENV.DOUBLE_TAP ?? 0.1);
const P95_MS = Number(__ENV.P95_MS || 500);
const ISSUER = __ENV.AUTH_ISSUER || 'fandom-score';
const AUDIENCE = __ENV.AUTH_AUDIENCE || 'fandom-score-api';
const KEY = encoding.b64decode(open(__ENV.SIGNING_KEY_FILE || fail('SIGNING_KEY_FILE is required')).trim(), 'std');

const EVENT_ID = uuid(`fandom-load:${RUN}:event`);
const ALLOCATION_ID = uuid(`fandom-load:${RUN}:allocation`);

const base = Math.max(1, Math.round(PEAK / 4));
export const options = {
  scenarios: {
    onsale: {
      executor: 'ramping-arrival-rate',
      startRate: 1,
      timeUnit: '1s',
      preAllocatedVUs: Math.min(Number(__ENV.MAX_VUS || 4000), PEAK * 4),
      maxVUs: Number(__ENV.MAX_VUS || 4000),
      stages: [
        { target: base, duration: '30s' }, // the page goes live, fans trickle in
        { target: base, duration: '30s' },
        { target: PEAK, duration: '10s' }, // the on-sale notification lands
        { target: PEAK, duration: `${HOLD}s` },
        { target: 0, duration: '10s' },
      ],
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: [`p(95)<${P95_MS}`],
    'http_req_duration{name:event}': [`p(95)<${P95_MS}`],
    'http_req_duration{name:eligibility}': [`p(95)<${P95_MS}`],
    'http_req_duration{name:register}': [`p(95)<${P95_MS}`],
    server_errors: ['count==0'],
    rate_limited: ['count==0'],
    registration_wrong: ['count==0'],
    fans_exhausted: ['count==0'],
    checks: ['rate==1'],
  },
  summaryTrendStats: ['avg', 'med', 'p(95)', 'p(99)', 'max'],
};

/** Fans who reached the registration step, and fans whose registration came back created. */
const registerAttempts = new Counter('register_attempts');
const registered = new Counter('registered');
/** A registration that wasn't created exactly once, or wasn't on the fan's eligibility afterwards. */
const registrationWrong = new Counter('registration_wrong');
const serverErrors = new Counter('server_errors');
const rateLimited = new Counter('rate_limited');
const fansExhausted = new Counter('fans_exhausted');

// 409 and 403 are answers, not failures, for the purposes of http_req_failed; checks below decide what's right.
http.setResponseCallback(http.expectedStatuses(200, 201));

export default function () {
  const n = exec.scenario.iterationInTest + 1;
  if (n > FANS) {
    fansExhausted.add(1);
    return;
  }

  const auth = `Bearer ${token(n)}`; // valid for 15 minutes, longer than one fan's visit
  const params = (name) => ({
    headers: { Authorization: auth, 'Content-Type': 'application/json' },
    tags: { name },
  });
  const eligibility = () => watch(http.get(`${BASE}/allocations/${ALLOCATION_ID}/eligibility?plus_one=false`, params('eligibility')));

  const show = watch(http.get(`${BASE}/events/${EVENT_ID}`, params('event')));
  check(show, { 'show loads': (r) => r.status === 200 && r.json('allocations.0.id') === ALLOCATION_ID });

  let before = eligibility();
  for (let i = 0; i < REFRESHES; i++) {
    sleep(Math.random() * THINK);
    before = eligibility();
  }
  check(before, { 'eligible before registering': (r) => r.status === 200 && r.json('eligible') === true && r.json('registration') === null });

  registerAttempts.add(1);
  const url = `${BASE}/allocations/${ALLOCATION_ID}/registration`;
  const body = JSON.stringify({ plus_one: false });
  const puts = Math.random() < DOUBLE_TAP
    ? http.batch([['PUT', url, body, params('register')], ['PUT', url, body, params('register')]])
    : [http.put(url, body, params('register'))];
  puts.forEach(watch);
  // A second tap gets the same registration back: 201 if it raced the first, 200 if it came after.
  const ids = new Set(puts.filter((r) => r.status === 200 || r.status === 201).map((r) => r.json('id')));
  const created = puts.some((r) => r.status === 201) && puts.every((r) => r.status === 200 || r.status === 201) && ids.size === 1;
  if (created) registered.add(1);

  const after = eligibility();
  const ok = check(after, {
    'registered exactly once': (r) =>
      created && r.status === 200 && r.json('registration.status') === 'registered' && ids.has(r.json('registration.id')),
    'standing shown after registering': (r) => r.status === 200 && r.json('standing.rank') >= 1,
  });
  if (!ok) registrationWrong.add(1);
}

function watch(response) {
  if (response.status === 0 || response.status >= 500) serverErrors.add(1);
  if (response.status === 429) rateLimited.add(1);
  return response;
}

// Postgres md5(text)::uuid, so the ids match setup.sql.
function uuid(text) {
  const h = crypto.md5(text, 'hex');
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

function token(n) {
  const now = Math.floor(Date.now() / 1000);
  const header = encoding.b64encode(JSON.stringify({ alg: 'HS256', typ: 'JWT' }), 'rawurl');
  const claims = encoding.b64encode(JSON.stringify({
    sub: uuid(`fandom-load:${RUN}:user:${n}`),
    sid: uuid(`fandom-load:${RUN}:session:${n}`),
    iss: ISSUER,
    aud: AUDIENCE,
    iat: now,
    nbf: now,
    exp: now + 900,
  }), 'rawurl');
  return `${header}.${claims}.${crypto.hmac('sha256', KEY, `${header}.${claims}`, 'base64rawurl')}`;
}

function fail(message) {
  throw new Error(message);
}
