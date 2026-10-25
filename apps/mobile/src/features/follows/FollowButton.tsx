import { Button } from '../../ui/components';

/** Follow / Following toggle for one fan. The parent owns the state so every card of the same fan agrees. */
export function FollowButton({
  handle,
  following,
  busy,
  onToggle,
}: {
  handle: string;
  following: boolean;
  busy?: boolean;
  onToggle: (handle: string, follow: boolean) => void;
}) {
  return (
    <Button
      label={following ? 'Following' : 'Follow'}
      accessibilityLabel={following ? `Unfollow @${handle}` : `Follow @${handle}`}
      kind="secondary"
      busy={busy}
      onPress={() => onToggle(handle, !following)}
    />
  );
}
