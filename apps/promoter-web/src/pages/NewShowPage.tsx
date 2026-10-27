import { useCallback, useState } from 'react';
import { useNavigate } from 'react-router-dom';

import { ApiError, type Artist, errorMessage, type HubCity, unwrap } from '../api/client';
import { useSession } from '../auth/SessionProvider';
import { zonedToIso } from '../lib/time';
import { Button, Card, ErrorText, Field } from '../ui/components';
import { Picker } from '../ui/Picker';
import { useOrganization } from './OrganizationLayout';

/** A new show: a tracked artist, a hub city (the time is entered in that city's time zone), a venue, a time. */
export function NewShowPage() {
  const { organization } = useOrganization();
  const { session } = useSession();
  const navigate = useNavigate();
  const [artist, setArtist] = useState<Artist | null>(null);
  const [city, setCity] = useState<HubCity | null>(null);
  const [name, setName] = useState('');
  const [venue, setVenue] = useState('');
  const [startsAt, setStartsAt] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  const searchArtists = useCallback(
    async (q: string) => unwrap(await session.api.GET('/artists', { params: { query: { q, status: 'all', limit: 8, sort: 'name' } } })).items,
    [session],
  );
  const searchCities = useCallback(async (q: string) => unwrap(await session.api.GET('/cities', { params: { query: { q, limit: 8 } } })).items, [session]);

  async function submit() {
    if (!artist || !city || !startsAt) {
      setError('Choose the artist, the city, and the date and time.');
      return;
    }
    setBusy(true);
    setError(undefined);
    try {
      const created = unwrap(
        await session.api.POST('/organizations/{organizationId}/events', {
          params: { path: { organizationId: organization.id } },
          body: {
            artist_id: artist.id, name: name.trim() || `${artist.name}${venue.trim() ? ` at ${venue.trim()}` : ''}`,
            venue: venue.trim() || null, city_id: city.id, starts_at: zonedToIso(startsAt, city.time_zone),
          },
        }),
      );
      navigate(`/o/${organization.id}/shows/${created.event.id}`);
    } catch (e) {
      setError(e instanceof ApiError ? errorMessage(e) : errorMessage(e));
      setBusy(false);
    }
  }

  return (
    <Card title="New show">
      <form className="grid" onSubmit={(e) => { e.preventDefault(); void submit(); }}>
        <Picker label="Artist" placeholder="Search artists on Fandom Score" search={searchArtists} describe={(a) => a.name} keyOf={(a) => a.id} value={artist} onChange={setArtist} />
        <Picker label="City" placeholder="Search cities" search={searchCities} describe={(c) => `${c.name}, ${c.region}`} keyOf={(c) => c.id} value={city} onChange={setCity} />
        <Field label="Venue" value={venue} onChange={(e) => setVenue(e.target.value)} />
        <Field label="Show name" value={name} onChange={(e) => setName(e.target.value)} placeholder="Artist at venue" />
        <Field
          label="Starts"
          type="datetime-local"
          value={startsAt}
          onChange={(e) => setStartsAt(e.target.value)}
          hint={city ? `In ${city.name} time (${city.time_zone}).` : 'Choose the city first: times are in the show city’s time zone.'}
        />
        <div className="actions">
          <Button type="submit" busy={busy}>Create show</Button>
          <Button kind="secondary" onClick={() => navigate(`/o/${organization.id}`)}>Cancel</Button>
        </div>
        <ErrorText>{error}</ErrorText>
      </form>
    </Card>
  );
}
