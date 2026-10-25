import { StyleSheet, View } from 'react-native';

import { colors } from './theme';

/** A camera drawn with views (no icon font in the app): body, viewfinder bump, and lens. */
export function CameraIcon({ size = 26, color = colors.onAccent }: { size?: number; color?: string }) {
  const unit = size / 26;
  return (
    <View style={{ width: size, height: size * 0.8 }} accessibilityElementsHidden importantForAccessibility="no-hide-descendants">
      <View style={[styles.bump, { left: 6 * unit, width: 8 * unit, height: 4 * unit, backgroundColor: color, borderRadius: 1.5 * unit }]} />
      <View style={[styles.body, { top: 3 * unit, width: size, height: 17.5 * unit, borderColor: color, borderWidth: 2.2 * unit, borderRadius: 4 * unit }]}>
        <View style={{ width: 9 * unit, height: 9 * unit, borderRadius: 4.5 * unit, borderWidth: 2.2 * unit, borderColor: color }} />
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  bump: { position: 'absolute', top: 0 },
  body: { position: 'absolute', left: 0, alignItems: 'center', justifyContent: 'center' },
});
