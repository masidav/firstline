import { fireEvent, render, screen } from '@testing-library/react-native';

import { Avatar, initial } from './Avatar';

test('the placeholder letter comes from the display name, else the handle; deleted fans get a question mark', () => {
  expect(initial({ handle: 'ana', display_name: 'Élodie' })).toBe('É');
  expect(initial({ handle: 'ben', display_name: null })).toBe('B');
  expect(initial({ handle: 'ben', display_name: '  ' })).toBe('B');
  expect(initial({ handle: null, deleted: true })).toBe('?');
  expect(initial(null)).toBe('?');
});

test('a fan with a picture shows it; without one, their initial', () => {
  render(<Avatar user={{ handle: 'ana', display_name: 'Ana', avatar_url: 'https://api.test/v1/avatars/1' }} size={40} />);
  const image = screen.getByLabelText("Ana's picture");
  expect(image.props.source).toEqual({ uri: 'https://api.test/v1/avatars/1' });
  expect(image).toHaveStyle({ width: 40, height: 40, borderRadius: 20 });

  render(<Avatar user={{ handle: 'ben', display_name: null, avatar_url: null }} />);
  expect(screen.getByLabelText("@ben's picture")).toBeTruthy();
  expect(screen.getByText('B')).toBeTruthy();
});

test('a picture that fails to load falls back to the initial', () => {
  render(<Avatar user={{ handle: 'ana', display_name: 'Ana', avatar_url: 'https://api.test/v1/avatars/gone' }} />);
  fireEvent(screen.getByLabelText("Ana's picture"), 'error');
  expect(screen.getByText('A')).toBeTruthy();
});
