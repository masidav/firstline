import { ApiError } from './client';

/** A short, user-facing message for any failed call. */
export function errorMessage(error: unknown): string {
  if (!(error instanceof ApiError)) {
    return "Can't reach the server. Check your connection and try again.";
  }
  switch (error.code) {
    case 'handle_taken':
      return 'That handle is taken. Try another.';
    case 'email_in_use':
      return 'Another account already uses this email. Sign in the way you did before.';
    case 'rate_limited':
      return 'Too many attempts. Wait a minute and try again.';
    case 'artist_add_limit':
      return "You've added as many new artists as allowed today. Try again tomorrow.";
    case 'spotify_unavailable':
      return "Spotify isn't responding right now. Try again in a moment.";
    case 'unauthorized':
      return 'Your session has ended. Sign in again.';
    default:
      return error.problem.errors?.[0]?.message ?? error.message;
  }
}
