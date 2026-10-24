import { configure } from '@testing-library/react-native';

// findBy* and waitFor give up after 1 s by default. On a busy machine (CI, or the full suite in parallel) a screen's
// first render can take longer, which made tests fail at random. Five seconds still fails fast on a real bug.
configure({ asyncUtilTimeout: 5000 });
