import { createBrowserRouter } from 'react-router';
import { Layout } from './components/Layout';
import { LoginPage } from './features/auth/LoginPage';
import { RequireAuth } from './features/auth/RequireAuth';
import { DashboardPage } from './features/dashboard/DashboardPage';
import { LibraryPage } from './features/library/LibraryPage';
import { OfferEditorPage } from './features/offers/OfferEditorPage';
import { OffersPage } from './features/offers/OffersPage';
import { PlaylistEditorPage } from './features/playlists/PlaylistEditorPage';
import { PlaylistsPage } from './features/playlists/PlaylistsPage';
import { SettingsPage } from './features/settings/SettingsPage';
import { StoreDetailPage } from './features/stores/StoreDetailPage';
import { StoresPage } from './features/stores/StoresPage';
import { NotFoundPage } from './components/NotFoundPage';

export const router = createBrowserRouter([
  { path: '/entrar', element: <LoginPage /> },
  {
    element: (
      <RequireAuth>
        <Layout />
      </RequireAuth>
    ),
    children: [
      { index: true, element: <DashboardPage /> },
      { path: 'biblioteca', element: <LibraryPage /> },
      { path: 'playlists', element: <PlaylistsPage /> },
      { path: 'playlists/:id', element: <PlaylistEditorPage /> },
      { path: 'ofertas', element: <OffersPage /> },
      { path: 'ofertas/:id', element: <OfferEditorPage /> },
      { path: 'lojas', element: <StoresPage /> },
      { path: 'lojas/:id', element: <StoreDetailPage /> },
      { path: 'configuracoes', element: <SettingsPage /> },
      { path: '*', element: <NotFoundPage /> },
    ],
  },
]);
