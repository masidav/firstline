import { render } from '@testing-library/react-native';
import { Platform } from 'react-native';

import { SpotifyEmbed } from './SpotifyEmbed';

const REAL_ID = '4Z8W4fKeB5YxbusRsdQVPb';

test('renders the Spotify player on the web for a real Spotify id', () => {
  const os = jest.replaceProperty(Platform, 'OS', 'web');
  try {
    const { toJSON } = render(<SpotifyEmbed kind="album" id={REAL_ID} title="In Rainbows" />);
    const json = JSON.stringify(toJSON());
    expect(json).toContain(`https://open.spotify.com/embed/album/${REAL_ID}`);
    expect(json).toContain('In Rainbows on Spotify');
  } finally {
    os.restore();
  }
});

test('renders nothing on phones or for made-up ids', () => {
  const os = jest.replaceProperty(Platform, 'OS', 'web');
  try {
    expect(render(<SpotifyEmbed kind="album" id="seed:glass-harbor-1" title="x" />).toJSON()).toBeNull();
    expect(render(<SpotifyEmbed kind="artist" id={null} title="x" />).toJSON()).toBeNull();
  } finally {
    os.restore();
  }
  const ios = jest.replaceProperty(Platform, 'OS', 'ios');
  try {
    expect(render(<SpotifyEmbed kind="album" id={REAL_ID} title="x" />).toJSON()).toBeNull();
  } finally {
    ios.restore();
  }
});
