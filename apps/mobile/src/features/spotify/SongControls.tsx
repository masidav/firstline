import { StyleSheet, View } from 'react-native';

import { space } from '../../ui/theme';
import { LikeButton } from './Library';
import { PlayButton, QueueButton, type SongProps } from './NowPlaying';

/** Play, Queue, and Like for one song, wherever a song appears (feed, bouncer door, featured artist, profiles, albums). */
export function SongControls(props: SongProps) {
  return (
    <View style={styles.controls}>
      <PlayButton {...props} />
      <QueueButton spotifyTrackId={props.spotifyTrackId} title={props.title} compact={props.compact} />
      <LikeButton kind="track" id={props.spotifyTrackId} title={props.title} compact={props.compact} />
    </View>
  );
}

const styles = StyleSheet.create({
  controls: { flexDirection: 'row', alignItems: 'center', gap: space.xs, flexWrap: 'wrap' },
});
