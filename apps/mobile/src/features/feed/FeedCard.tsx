import { router } from 'expo-router';
import { type ReactNode, useState } from 'react';
import { Image, Linking, Pressable, StyleSheet, Text, View } from 'react-native';

import { Body } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { fanName } from '../follows/follows';
import { FollowButton } from '../follows/FollowButton';
import { concertLine } from '../submissions/photoUpload';
import { saleLines } from '../ticketing/showInfo';
import { standingMessage } from '../ticketing/standing';
import { type FeedItem, formatWhen, stars, statSentence, ticketsLine } from './feedText';
import { ReactionBar } from './ReactionBar';
import { ReportForm } from './ReportForm';
import { SongControls } from '../spotify/SongControls';

export type FeedCardProps = {
  item: FeedItem;
  /** The signed-in fan's own item: no Follow or Report. */
  mine: boolean;
  following: boolean;
  onFollow: (handle: string, follow: boolean) => void;
};

/** One item of the home feed, by type. Unknown types (a newer API) render nothing. */
export function FeedCard(props: FeedCardProps) {
  const { item } = props;
  switch (item.type) {
    case 'photo':
      return item.photo ? <PhotoCard {...props} /> : null;
    case 'album_log':
      return item.log ? <LogCard {...props} /> : null;
    case 'grab_bag':
      return item.grab_bag ? <GrabBagCard {...props} /> : null;
    case 'concert':
      return item.concert ? <ConcertCard {...props} /> : null;
    case 'registration':
      return item.registration ? <RegistrationCard {...props} /> : null;
    case 'stat':
      return item.stat ? <StatCard {...props} /> : null;
    case 'prompt_answer':
      return item.prompt_answer ? <PromptAnswerCard {...props} /> : null;
    default:
      return null;
  }
}

/** Who posted it and, for other fans, a Follow toggle. */
function Byline({ item, mine, following, onFollow, children }: FeedCardProps & { children: ReactNode }) {
  const handle = item.user?.handle;
  return (
    <View style={styles.byline}>
      {handle ? (
        <Pressable accessibilityRole="link" accessibilityLabel={`Open ${fanName(item.user)}'s page`} onPress={() => router.push(`/fans/${handle}`)}
          style={styles.grow}>
          <Text style={styles.line}>
            {children}
            {item.user_level_name ? <Text style={styles.level}>{` · ${item.user_level_name}`}</Text> : null}
          </Text>
        </Pressable>
      ) : (
        <Text style={[styles.line, styles.grow]}>{children}</Text>
      )}
      {!mine && handle ? <FollowButton handle={handle} following={following} onToggle={onFollow} /> : null}
    </View>
  );
}

/** A Report link, or the report form once tapped; never on the fan's own posts. */
function Report({ item, mine }: { item: FeedItem; mine: boolean }) {
  const [reporting, setReporting] = useState(false);
  if (mine) return null;
  const who = fanName(item.user);
  return reporting ? (
    <ReportForm
      targetType={item.type === 'photo' ? 'photo' : item.type === 'prompt_answer' ? 'prompt_answer' : 'log'}
      targetId={item.id}
      onClose={() => setReporting(false)}
    />
  ) : (
    <Pressable accessibilityRole="button" accessibilityLabel={`Report ${who}'s post`} onPress={() => setReporting(true)} hitSlop={8}>
      <Text style={styles.report}>Report</Text>
    </Pressable>
  );
}

function PhotoCard(props: FeedCardProps) {
  const { item, mine } = props;
  const photo = item.photo!;
  const who = fanName(item.user);
  const what = photo.kind === 'merch' ? 'merch' : 'a concert photo';
  return (
    <View style={styles.card}>
      <Byline {...props}>
        <Text style={styles.strong}>{who}</Text> posted {what} · {item.artist?.name}
      </Byline>
      {photo.files[0] && (
        <Image
          source={{ uri: photo.files[0].url }}
          style={styles.photo}
          resizeMode="cover"
          accessibilityLabel={`${who}'s ${what} for ${item.artist?.name}`}
        />
      )}
      {photo.concert ? <Text style={styles.muted}>{concertLine(photo.concert)}</Text> : null}
      {photo.caption ? <Text style={styles.text}>{photo.caption}</Text> : null}
      <ReactionBar targetType="photo" targetId={item.id} initial={item.reactions ?? []} />
      <Report item={item} mine={mine} />
    </View>
  );
}

function LogCard(props: FeedCardProps) {
  const { item, mine } = props;
  const log = item.log!;
  const who = fanName(item.user);
  return (
    <View style={styles.card}>
      <Byline {...props}>
        <Text style={styles.strong}>{who}</Text> {log.is_relisten ? 'relistened to' : 'logged'}
        {log.visibility === 'followers' ? ' · followers only' : ''}
      </Byline>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={`${who} logged ${log.album.title} by ${item.artist?.name}`}
        onPress={() => router.push(`/album/${log.album.id}`)}
        style={styles.row}
      >
        {log.album.cover_url ? <Image source={{ uri: log.album.cover_url }} style={styles.cover} /> : <View style={styles.cover} />}
        <View style={styles.rowText}>
          <Text style={styles.strong} numberOfLines={1}>
            {log.album.title}
          </Text>
          <Text style={styles.muted}>
            {item.artist?.name}
            {log.rating != null ? ` · ${stars(log.rating)}` : ''}
          </Text>
        </View>
      </Pressable>
      {log.review ? <Text style={styles.text}>{log.review}</Text> : null}
      <ReactionBar targetType="log" targetId={item.id} initial={item.reactions ?? []} />
      <Report item={item} mine={mine} />
    </View>
  );
}

function GrabBagCard(props: FeedCardProps) {
  const { item } = props;
  const song = item.grab_bag!;
  const who = fanName(item.user);
  return (
    <View style={[styles.card, styles.grabBag]}>
      <Byline {...props}>
        <Text style={styles.strong}>{who}</Text>'s grab bag · song {song.position}
      </Byline>
      <View style={styles.row}>
        {song.cover_url ? <Image source={{ uri: song.cover_url }} style={styles.cover} accessibilityIgnoresInvertColors /> : <View style={styles.cover} />}
        <View style={styles.rowText}>
          <Text style={styles.strong} numberOfLines={2}>
            {song.title}
          </Text>
          <Text style={styles.muted} numberOfLines={1}>
            {song.artist.name}
            {song.album_title ? ` · ${song.album_title}` : ''}
          </Text>
        </View>
      </View>
      <View style={styles.songActions}>
        <SongControls spotifyTrackId={song.spotify_track_id} spotifyUrl={song.spotify_url} title={song.title} />
        <Pressable
          accessibilityRole="link"
          accessibilityLabel={`Open ${song.title} on Spotify`}
          onPress={() => void Linking.openURL(song.spotify_url)}
          hitSlop={8}
        >
          <Text style={styles.link}>Open in Spotify</Text>
        </Pressable>
      </View>
    </View>
  );
}

function ConcertCard({ item }: FeedCardProps) {
  const show = item.concert!;
  const where = [show.event.venue, show.event.city].filter(Boolean).join(', ');
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`Open ${show.event.name}`}
      onPress={() => router.push(`/show/${show.event.id}`)}
      style={[styles.card, styles.concert]}
    >
      <Text style={styles.kicker}>Concert</Text>
      <Text style={styles.title}>{show.event.name}</Text>
      <Text style={styles.muted}>
        {item.artist?.name} · {formatWhen(show.event.starts_at)}
        {where ? ` · ${where}` : ''}
      </Text>
      <Text style={[styles.strong, (show.registered || show.allocation?.status === 'open') && styles.accent]}>{ticketsLine(show)}</Text>
      {!show.allocation && saleLines(show.event)[0] ? <Text style={styles.muted}>{saleLines(show.event)[0]}</Text> : null}
    </Pressable>
  );
}

function RegistrationCard({ item }: FeedCardProps) {
  const card = item.registration!;
  const where = [card.event.venue, card.event.city].filter(Boolean).join(', ');
  const message = card.standing ? standingMessage(card.standing) : null;
  const status = card.registration.status;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`Open your registration for ${card.event.name}`}
      onPress={() => router.push(`/show/${card.event.id}`)}
      style={[styles.card, styles.registration]}
    >
      <Text style={styles.kicker}>Your fan-ticket registration</Text>
      <Text style={styles.title}>{card.event.name}</Text>
      <Text style={styles.muted}>
        {item.artist?.name} · {formatWhen(card.event.starts_at)}
        {where ? ` · ${where}` : ''}
      </Text>
      {message ? (
        <>
          <Text style={[styles.strong, card.standing?.likely && styles.accent]}>{message.headline}</Text>
          <Body muted>{message.detail}</Body>
        </>
      ) : (
        <Text style={[styles.strong, status === 'selected' && styles.accent]}>
          {status === 'selected'
            ? card.registration.reservation_status === 'delivered'
              ? 'Your ticket is ready. Open Tickets to show it at the door.'
              : card.registration.seats === 2
                ? 'You got tickets for you and your +1! They arrive shortly before the show.'
                : 'You got a ticket! It arrives shortly before the show.'
            : status === 'returned'
              ? 'You returned your seat.'
              : status === 'not_selected'
                ? "You weren't selected this time."
                : 'Registration has closed. The draw is next.'}
        </Text>
      )}
    </Pressable>
  );
}

function PromptAnswerCard(props: FeedCardProps) {
  const { item, mine } = props;
  const answer = item.prompt_answer!;
  return (
    <View style={[styles.card, styles.promptCard]}>
      <Byline {...props}>
        <Text style={styles.strong}>{fanName(item.user)}</Text> answered the prompt
        {answer.visibility === 'followers' ? ' · followers only' : ''}
      </Byline>
      <Text style={styles.muted}>{answer.prompt_text}</Text>
      <Text style={styles.answer}>“{answer.body}”</Text>
      <Pressable accessibilityRole="button" accessibilityLabel={`See all answers to: ${answer.prompt_text}`}
        onPress={() => router.push(`/prompts/${answer.prompt_id}`)} hitSlop={8}>
        <Text style={styles.link}>See all answers</Text>
      </Pressable>
      <ReactionBar targetType="prompt_answer" targetId={item.id} initial={item.reactions ?? []} />
      <Report item={item} mine={mine} />
    </View>
  );
}

function StatCard(props: FeedCardProps) {
  const { item } = props;
  const who = fanName(item.user);
  return (
    <View style={[styles.card, styles.stat]}>
      <Byline {...props}>{statSentence(who, item.stat!, item.artist?.name)}</Byline>
    </View>
  );
}

const styles = StyleSheet.create({
  level: { color: colors.accent, fontWeight: '700' },
  card: { gap: space.sm, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: colors.border },
  grabBag: { backgroundColor: colors.surface },
  concert: { borderColor: colors.accent },
  registration: { borderColor: colors.accent, backgroundColor: colors.surface },
  stat: { backgroundColor: colors.surface },
  promptCard: { borderColor: '#e57373' },
  answer: { color: colors.text, fontSize: 18, fontWeight: '700', lineHeight: 24 },
  byline: { flexDirection: 'row', alignItems: 'center', gap: space.sm },
  grow: { flex: 1 },
  kicker: { color: colors.muted, fontSize: 13, fontWeight: '600', textTransform: 'uppercase', letterSpacing: 0.5 },
  title: { color: colors.text, fontSize: 18, fontWeight: '700' },
  line: { color: colors.text, fontSize: 15 },
  strong: { color: colors.text, fontSize: 15, fontWeight: '700' },
  accent: { color: colors.accent },
  text: { color: colors.text, fontSize: 15, lineHeight: 21 },
  muted: { color: colors.muted, fontSize: 14 },
  link: { color: colors.accent, fontSize: 15, fontWeight: '600' },
  photo: { width: '100%', aspectRatio: 4 / 3, borderRadius: 8, backgroundColor: colors.surface },
  row: { flexDirection: 'row', gap: space.md, alignItems: 'center' },
  songActions: { flexDirection: 'row', gap: space.md, alignItems: 'center' },
  cover: { width: 56, height: 56, borderRadius: 6, backgroundColor: colors.surface },
  rowText: { flex: 1, gap: 2 },
  report: { color: colors.muted, fontSize: 13, textDecorationLine: 'underline', alignSelf: 'flex-end' },
});
