-- Fan levels go from 5 to 10, named (decided 2026-10-28): Roadie, Groupie, Street Team, Front Row, Backstage Pass,
-- Opening Act, Headliner, Gold Record, Platinum, Superstar. Each level spans a tenth of activity: below 0.1 is level 1
-- (Roadie), 0.9 and up is level 10 (Superstar). The names live in the API (FanLevelEndpoints.Names); the database
-- keeps the number. Stored levels are recomputed here.

ALTER TABLE fan_overall_scores DROP CONSTRAINT fan_overall_scores_level_check;
ALTER TABLE fan_overall_scores DROP CONSTRAINT fan_overall_scores_level_matches;

CREATE OR REPLACE FUNCTION fan_level_for(p_activity numeric) RETURNS smallint LANGUAGE sql IMMUTABLE AS $$
  SELECT least(10, 1 + floor(greatest(0, p_activity) * 10))::smallint;
$$;

UPDATE fan_overall_scores SET level = fan_level_for(activity);

ALTER TABLE fan_overall_scores
  ADD CONSTRAINT fan_overall_scores_level_check CHECK (level BETWEEN 1 AND 10),
  ADD CONSTRAINT fan_overall_scores_level_matches CHECK (level = fan_level_for(activity));
