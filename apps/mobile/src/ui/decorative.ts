/**
 * Props that hide a purely visual element from VoiceOver, TalkBack, and web screen readers (`aria-hidden`): album
 * covers next to the song's title, the avatar next to a fan's name, the bouncer's door, a progress line. Spread them
 * on the element; what it stands for is already read from the text beside it.
 */
export const decorative = { accessibilityElementsHidden: true, importantForAccessibility: 'no-hide-descendants' } as const;
