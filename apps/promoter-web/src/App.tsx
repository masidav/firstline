import { Navigate, Outlet, Route, Routes, useLocation } from 'react-router-dom';

import { useSession } from './auth/SessionProvider';
import { AllocationPage } from './pages/AllocationPage';
import { GoogleCallbackPage } from './pages/GoogleCallbackPage';
import { MembersPage } from './pages/MembersPage';
import { NewShowPage } from './pages/NewShowPage';
import { OrganizationLayout } from './pages/OrganizationLayout';
import { OrganizationsPage } from './pages/OrganizationsPage';
import { ShowPage } from './pages/ShowPage';
import { ShowsPage } from './pages/ShowsPage';
import { SignInPage } from './pages/SignInPage';
import { Button, Loading } from './ui/components';

export function App() {
  return (
    <Routes>
      <Route path="/sign-in" element={<SignInPage />} />
      <Route path="/google-callback" element={<GoogleCallbackPage />} />
      <Route element={<RequireSignIn />}>
        <Route path="/" element={<OrganizationsPage />} />
        <Route path="/o/:organizationId" element={<OrganizationLayout />}>
          <Route index element={<ShowsPage />} />
          <Route path="shows/new" element={<NewShowPage />} />
          <Route path="shows/:eventId" element={<ShowPage />} />
          <Route path="shows/:eventId/allocations/:allocationId" element={<AllocationPage />} />
          <Route path="members" element={<MembersPage />} />
        </Route>
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}

/** Waits for the stored session, then shows the page or sends the promoter to sign in (and back afterwards). */
function RequireSignIn() {
  const { session, state } = useSession();
  const location = useLocation();
  switch (state.status) {
    case 'loading':
      return <main className="page"><Loading /></main>;
    case 'unreachable':
      return (
        <main className="page narrow">
          <h1>Can't reach the server</h1>
          <p className="muted">Check your connection, or that the API is running.</p>
          <Button onClick={() => void session.restore()}>Try again</Button>
        </main>
      );
    case 'signedOut':
      return <Navigate to={`/sign-in?next=${encodeURIComponent(location.pathname + location.search)}`} replace />;
    case 'signedIn':
      return <Outlet />;
  }
}
