import { router } from 'expo-router';
import { type ReactNode, useState } from 'react';
import { Image, Linking, Pressable, StyleSheet, Text, View } from 'react-native';

import { Avatar } from '../../ui/Avatar';
import { Body } from '../../ui/components';
import { decorative } from '../../ui/decorative';
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

/**
 * Who posted it (their picture and name) and, for other fans, a Follow toggle. `text` is the sentence as a screen
 * reader should say it (the children are the same sentence with styling); the name opens the fan's page.
 */
function Byline({ item, mine, following, onFollow, text, children }: FeedCardProps & { text: string; children: ReactNode }) {
  const handle = item.user?.handle;
  const level = item.user_level_name ? `, ${item.user_level_name}` : '';
  return (
    <View style={styles.byline}>
      {item.user ? (
        <View {...decorative}>
          <Avatar user={item.user} size={32} />
        </View>
      ) : null}
      {handle ? (
        <Pressable accessibilityRole="link" accessibilityLabel={`${text}${level}`} accessibilityHint={`Opens ${fanName(item.user)}'s page`}
          onPress={() => router.push(`/fans/${handle}`)} style={styles.grow}>
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
      <Byline {...props} text={`${who} posted ${what}, ${item.artist?.name}`}>
        <Text style={styles.strong}>{who}</Text> posted {what} · {item.artist?.name}
      </Byline>
      {photo.files[0] && (
        <Image
          source={{ uri: photo.files[0].url }}
          style={styles.photo}
          resizeMode="cover"
          accessible
          accessibilityRole="image"
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
  const verb = log.is_relisten ? 'relistened to' : 'logged';
  const onlyFollowers = log.visibility === 'followers';
  return (
    <View style={styles.card}>
      <Byline {...props} text={`${who} ${verb}${onlyFollowers ? ', followers only' : ''}`}>
        <Text style={styles.strong}>{who}</Text> {verb}
        {onlyFollowers ? ' · followers only' : ''}
      </Byline>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={`${log.album.title} by ${item.artist?.name}${log.rating != null ? `, rated ${stars(log.rating).replace('★ ', '')} stars` : ''}`}
        accessibilityHint="Opens the album"
        onPress={() => router.push(`/album/${log.album.id}`)}
        style={styles.row}
      >
        {log.album.cover_url ? <Image source={{ uri: log.album.cover_url }} style={styles.cover} {...decorative} /> : <View style={styles.cover} />}
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
      <Byline {...props} text={`${who}'s grab bag, song ${song.position}`}>
        <Text style={styles.strong}>{who}</Text>'s grab bag · song {song.position}
      </Byline>
      <View style={styles.row}>
        {song.cover_url ? <Image source={{ uri: song.cover_url }} style={styles.cover} accessibilityIgnoresInvertColors {...decorative} /> : <View style={styles.cover} />}
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

/** "Geese · Nov 19, 2026, 8:00 PM · Metro, Chicago": the line under a show's name (commas instead of dots when read aloud). */
function showLine(artistName: string | null | undefined, event: { starts_at: string; venue?: string | null; city?: string | null }, separator: string): string {
  const where = [event.venue, event.city].filter(Boolean).join(', ');
  return [artistName, formatWhen(event.starts_at), where].filter(Boolean).join(separator);
}

function ConcertCard({ item }: FeedCardProps) {
  const show = item.concert!;
  const sale = !show.allocation ? saleLines(show.event)[0] : undefined;
  const tickets = ticketsLine(show);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`Concert: ${show.event.name}. ${showLine(item.artist?.name, show.event, ', ')}. ${tickets}${sale ? `. ${sale}` : ''}`}
      accessibilityHint="Opens the show"
      onPress={() => router.push(`/show/${show.event.id}`)}
      style={[styles.card, styles.concert]}
    >
      <Text style={styles.kicker}>Concert</Text>
      <Text style={styles.title}>{show.event.name}</Text>
      <Text style={styles.muted}>{showLine(item.artist?.name, show.event, ' · ')}</Text>
      <Text style={[styles.strong, (show.registered || show.allocation?.status === 'open') && styles.accent]}>{tickets}</Text>
      {sale ? <Text style={styles.muted}>{sale}</Text> : null}
    </Pressable>
  );
}

/** What the registration's status means for the fan, once there's no live standing to show. */
function registrationOutcome(registration: NonNullable<FeedItem['registration']>['registration']): string {
  switch (registration.status) {
    case 'selected':
      return registration.reservation_status === 'delivered'
        ? 'Your ticket is ready. Open Tickets to show it at the door.'
        : registration.seats === 2
          ? 'You got tickets for you and your +1! They arrive shortly before the show.'
          : 'You got a ticket! It arrives shortly before the show.';
    case 'returned':
      return 'You returned your seat.';
    case 'not_selected':
      return "You weren't selected this time.";
    default:
      return 'Registration has closed. The draw is next.';
  }
}

function RegistrationCard({ item }: FeedCardProps) {
  const card = item.registration!;
  const message = card.standing ? standingMessage(card.standing) : null;
  const headline = message ? message.headline : registrationOutcome(card.registration);
  const good = message ? card.standing?.likely : card.registration.status === 'selected';
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`Your fan-ticket registration: ${card.event.name}. ${showLine(item.artist?.name, card.event, ', ')}. ${headline}${message ? ` ${message.detail}` : ''}`}
      accessibilityHint="Opens the show"
      onPress={() => router.push(`/show/${card.event.id}`)}
      style={[styles.card, styles.registration]}
    >
      <Text style={styles.kicker}>Your fan-ticket registration</Text>
      <Text style={styles.title}>{card.event.name}</Text>
      <Text style={styles.muted}>{showLine(item.artist?.name, card.event, ' · ')}</Text>
      <Text style={[styles.strong, good && styles.accent]}>{headline}</Text>
      {message ? <Body muted>{message.detail}</Body> : null}
    </Pressable>
  );
}

function PromptAnswerCard(props: FeedCardProps) {
  const { item, mine } = props;
  const answer = item.prompt_answer!;
  const onlyFollowers = answer.visibility === 'followers';
  return (
    <View style={[styles.card, styles.promptCard]}>
      <Byline {...props} text={`${fanName(item.user)} answered the prompt${onlyFollowers ? ', followers only' : ''}`}>
        <Text style={styles.strong}>{fanName(item.user)}</Text> answered the prompt
        {onlyFollowers ? ' · followers only' : ''}
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
  const sentence = statSentence(fanName(item.user), item.stat!, item.artist?.name);
  return (
    <View style={[styles.card, styles.stat]}>
      <Byline {...props} text={sentence.replace(' · ', ', ')}>
        {sentence}
      </Byline>
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
