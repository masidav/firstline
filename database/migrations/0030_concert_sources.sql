-- Concert data from outside sources (decided 2026-10-26: Ticketmaster, SeatGeek, Bandsintown, and Songkick, starting
-- with Ticketmaster). The worker links each tracked artist to the source's own artist id, then loads its upcoming
-- shows with sale dates and presales into events. Staff-entered shows are untouched; a synced show that has an
-- allocation keeps the time and city staff locked (the source's time is kept beside it).

-- An artist's id at a source. external_id null: looked and found no match (checked again later). matched_by:
-- 'spotify_link' when the source lists the artist's Spotify page (certain), 'staff' when staff set it.
CREATE TABLE artist_sources (
  artist_id   uuid NOT NULL REFERENCES artists ON DELETE CASCADE,
  source      text NOT NULL CHECK (source IN ('ticketmaster')),
  external_id text CHECK (external_id <> ''),
  matched_by  text CHECK (matched_by IN ('spotify_link', 'staff')),
  checked_at  timestamptz NOT NULL DEFAULT now(),   -- last match attempt
  synced_at   timestamptz,                          -- last successful load of shows
  PRIMARY KEY (artist_id, source),
  CONSTRAINT artist_sources_match CHECK ((external_id IS NULL) = (matched_by IS NULL))
);
CREATE UNIQUE INDEX artist_sources_external_uidx ON artist_sources (source, external_id) WHERE external_id IS NOT NULL;

-- What a source says about a show. status: the source's own (scheduled for staff-entered shows).
ALTER TABLE events
  ADD COLUMN ticket_url            text CHECK (ticket_url ~ '^https://'),
  ADD COLUMN status                text NOT NULL DEFAULT 'scheduled'
    CONSTRAINT events_status_check
    CHECK (status IN ('scheduled', 'onsale', 'offsale', 'cancelled', 'postponed', 'rescheduled')),
  ADD COLUMN region                text,                -- state or province code from the source
  ADD COLUMN time_tba              boolean NOT NULL DEFAULT false,   -- only the date is known (starts_at is local midnight)
  ADD COLUMN public_sale_starts_at timestamptz,
  ADD COLUMN public_sale_ends_at   timestamptz,
  ADD COLUMN source_starts_at      timestamptz,         -- the source's time, even when an allocation locked starts_at
  ADD COLUMN source_synced_at      timestamptz;

CREATE TABLE event_presales (
  event_id  uuid NOT NULL REFERENCES events ON DELETE CASCADE,
  name      text NOT NULL CHECK (name <> ''),
  starts_at timestamptz NOT NULL,
  ends_at   timestamptz,
  PRIMARY KEY (event_id, name, starts_at)
);

-- The hub city for a source's city, state or province, and country, or null (a suburb venue has no hub city yet).
CREATE FUNCTION hub_city_for(p_city text, p_region text, p_country text) RETURNS text LANGUAGE sql STABLE AS $$
  SELECT id FROM cities
   WHERE active AND lower(name) = lower(btrim(p_city)) AND region = upper(p_region) AND country = upper(p_country)
   LIMIT 1;
$$;

-- New artists are picked up by the worker's hourly concerts.schedule (never-checked artists first), so no trigger.
