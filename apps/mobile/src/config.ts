// Build-time settings (EXPO_PUBLIC_* are inlined into the bundle, so never put secrets here). See .env.example.
export const config = {
  apiUrl: process.env.EXPO_PUBLIC_API_URL ?? 'http://localhost:5271/v1',
  devSignIn: process.env.EXPO_PUBLIC_DEV_SIGN_IN === 'true',
};
