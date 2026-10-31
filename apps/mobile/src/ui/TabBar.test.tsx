import { fireEvent, render, screen } from '@testing-library/react-native';
import { Text } from 'react-native';

import { TabBar } from './TabBar';

type Props = Parameters<typeof TabBar>[0];

/** The props React Navigation hands a custom tab bar, reduced to what TabBar reads. */
function props(index: number, unread?: number) {
  const routes = [
    { key: 'home-1', name: 'home' },
    { key: 'tickets-1', name: 'tickets' },
    { key: 'profile-1', name: 'profile' },
  ];
  const descriptors = {
    'home-1': { options: { title: 'Feed' } },
    'tickets-1': { options: { title: 'Tickets' } },
    'profile-1': {
      options: {
        title: 'Profile',
        tabBarBadge: unread,
        tabBarAccessibilityLabel: unread ? `Profile, ${unread} unread alerts` : 'Profile',
        ...(unread && { tabBarActiveTintColor: 'red', tabBarInactiveTintColor: 'red' }),
      },
    },
  };
  const navigation = { emit: jest.fn(() => ({ defaultPrevented: false })), navigate: jest.fn() };
  const value = { state: { index, routes }, descriptors, navigation, insets: { top: 0, right: 0, bottom: 0, left: 0 } };
  return value as unknown as Props & { navigation: typeof navigation };
}

test('tabs are buttons named by their title; the selected one is marked', () => {
  const p = props(0);
  render(<TabBar {...p} />);

  expect(screen.getByRole('button', { name: 'Feed' }).props.accessibilityState).toEqual({ selected: true });
  expect(screen.getByRole('button', { name: 'Tickets' }).props.accessibilityState).toEqual({ selected: false });
  expect(screen.getByRole('button', { name: 'Profile' })).toBeTruthy();
});

test('pressing another tab navigates to it; pressing the current one does not', () => {
  const p = props(0);
  render(<TabBar {...p} />);

  fireEvent.press(screen.getByRole('button', { name: 'Tickets' }));
  expect(p.navigation.emit).toHaveBeenCalledWith({ type: 'tabPress', target: 'tickets-1', canPreventDefault: true });
  expect(p.navigation.navigate).toHaveBeenCalledWith('tickets', undefined);

  fireEvent.press(screen.getByRole('button', { name: 'Feed' }));
  expect(p.navigation.navigate).toHaveBeenCalledTimes(1);
});

test('unread alerts turn the Profile tab red with the count, capped at 99+', () => {
  const { rerender } = render(<TabBar {...props(0, 3)} />);
  expect(screen.getByRole('button', { name: 'Profile, 3 unread alerts' })).toBeTruthy();
  expect(screen.getByText('3')).toBeTruthy();
  expect(screen.getByText('Profile')).toHaveStyle({ color: 'red' });
  expect(screen.getByText('Tickets')).not.toHaveStyle({ color: 'red' });

  rerender(<TabBar {...props(0, 120)} />);
  expect(screen.getByText('99+')).toBeTruthy();

  rerender(<TabBar {...props(0)} />);
  expect(screen.queryByText(/^\d+\+?$/)).toBeNull();
  expect(screen.getByText('Profile')).not.toHaveStyle({ color: 'red' });
});

test('a view above the tabs (the now-playing bar) is rendered', () => {
  const { Text } = jest.requireActual('react-native');
  render(<TabBar {...props(0)} above={<Text>Now playing</Text>} />);

  expect(screen.getByText('Now playing')).toBeTruthy();
});

test("a tab's icon (the Profile tab's picture) renders with its label", () => {
  const p = props(0);
  (p.descriptors as Record<string, { options: Record<string, unknown> }>)['profile-1']!.options.tabBarIcon = ({ size }: { size: number }) => (
    <Text>{`picture ${size}`}</Text>
  );
  render(<TabBar {...p} />);
  expect(screen.getByText('picture 22')).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Profile' })).toBeTruthy();
});
