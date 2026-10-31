-- Per-environment settings, and the first one: how often trivia and prompts come out (requested 2026-10-31 for beta,
-- so demos aren't stuck waiting for Tuesday or Thursday). 'weekly' is the product decision of 2026-10-27 (trivia on
-- Tuesdays, prompts on Thursdays, UTC) and the default; 'daily' makes every day its own release day, as before
-- migration 0034. Every rule that depends on the cadence goes through trivia_week_day() and prompt_week_day(), so
-- flipping the setting flips the whole feature: the API's "current" question and prompt, the triggers that only accept
-- answers in the current period, and staff scheduling. The deploy writes the environment's settings from
-- infrastructure/k8s/overlays/<env>/settings (deploy.sh); prod has none and stays weekly.

CREATE TABLE app_settings (
  key        text PRIMARY KEY CHECK (key ~ '^[a-z][a-z0-9_]*$'),
  value      text NOT NULL CHECK (value ~ '^[a-z0-9_.:-]*$'),
  updated_at timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT app_settings_engagement_cadence CHECK (key <> 'engagement_cadence' OR value IN ('weekly', 'daily'))
);
INSERT INTO app_settings (key, value) VALUES ('engagement_cadence', 'weekly');

CREATE FUNCTION app_setting(p_key text) RETURNS text LANGUAGE sql STABLE AS $$
  SELECT value FROM app_settings WHERE key = p_key;
$$;

-- The current release day on or before p_on: the day itself when daily, else the latest Tuesday (trivia) or
-- Thursday (prompts).
CREATE OR REPLACE FUNCTION trivia_week_day(p_on date DEFAULT (now() AT TIME ZONE 'UTC')::date) RETURNS date
LANGUAGE sql STABLE AS $$
  SELECT CASE WHEN app_setting('engagement_cadence') = 'daily' THEN p_on
              ELSE p_on - ((extract(isodow FROM p_on)::int - 2 + 7) % 7) END;
$$;
CREATE OR REPLACE FUNCTION prompt_week_day(p_on date DEFAULT (now() AT TIME ZONE 'UTC')::date) RETURNS date
LANGUAGE sql STABLE AS $$
  SELECT CASE WHEN app_setting('engagement_cadence') = 'daily' THEN p_on
              ELSE p_on - ((extract(isodow FROM p_on)::int - 4 + 7) % 7) END;
$$;

-- The release day after p_day (the API's next_question_on and next_prompt_on).
CREATE FUNCTION engagement_period_days() RETURNS integer LANGUAGE sql STABLE AS $$
  SELECT CASE WHEN app_setting('engagement_cadence') = 'daily' THEN 1 ELSE 7 END;
$$;
CREATE FUNCTION trivia_next_day(p_day date DEFAULT trivia_week_day()) RETURNS date LANGUAGE sql STABLE AS $$
  SELECT p_day + engagement_period_days();
$$;
CREATE FUNCTION prompt_next_day(p_day date DEFAULT prompt_week_day()) RETURNS date LANGUAGE sql STABLE AS $$
  SELECT p_day + engagement_period_days();
$$;
