import type { components } from '../../api/client';

export type FlagReason = components['schemas']['FanFlag']['reason'];
export type FlagStatus = components['schemas']['FanFlag']['status'];
export type AppealStatus = components['schemas']['FraudAppeal']['status'];

/** What a review is about, in words a fan can read without feeling accused. */
export const REASON_LABELS: Record<FlagReason, string> = {
  streaming_farm: 'unusual listening patterns',
  bot: 'automated activity',
  shared_device: 'accounts sharing a device',
  duplicate_account: 'a possible second account',
  other: 'something else',
};

export const FLAG_REASONS = Object.keys(REASON_LABELS) as FlagReason[];

export const reasonLabel = (reason: string) => REASON_LABELS[reason as FlagReason] ?? reason;

/** The fan's view of a review's status. */
export const FAN_STATUS: Record<FlagStatus, string> = {
  open: "We're taking a look. Until we're done, you can't register for fan tickets. Everything else works as usual.",
  upheld: "We've finished the review and the hold stays in place for now, so you can't register for fan tickets.",
  cleared: 'All sorted. This review is closed and nothing is on hold.',
};

export const FAN_APPEAL_STATUS: Record<AppealStatus, string> = {
  pending: "Your appeal is with our team. We'll let you know when they've decided.",
  granted: 'Your appeal was granted and the hold is lifted.',
  denied: "Your appeal wasn't granted.",
};

export const STATUS_LABELS: Record<FlagStatus, string> = { open: 'Open', upheld: 'Upheld', cleared: 'Cleared' };

export const dateLabel = (iso: string) => new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });

export const dateTimeLabel = (iso: string) =>
  new Date(iso).toLocaleString(undefined, { month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit' });

/** Staff-only JSON (evidence, audit inputs), indented for reading. */
export const prettyJson = (value: unknown) => JSON.stringify(value, null, 2);
