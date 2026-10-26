-- One fan registers (each transaction takes the next fan, so every fan registers once per show).
WITH pick AS MATERIALIZED (SELECT (nextval('load_seq') - 1) % current_setting('load.fans')::int + 1 AS n)
SELECT register_for_allocation(f.id, current_setting('load.allocation')::uuid) FROM pick JOIN load_fans f ON f.n = pick.n;
