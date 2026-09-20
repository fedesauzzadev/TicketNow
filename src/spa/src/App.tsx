import { BrowserRouter, Link, NavLink, Route, Routes } from 'react-router-dom';
import EventsPage from './pages/EventsPage';
import EventDetailPage from './pages/EventDetailPage';
import AdminPage from './pages/AdminPage';
import QueuePage from './pages/QueuePage';
import LoginPage from './pages/LoginPage';
import OpsPage from './pages/OpsPage';
import { useAuthStore } from './store/authStore';

export default function App() {
  const { displayName } = useAuthStore();
  return (
    <BrowserRouter>
      <header className="topbar">
        <Link to="/" className="brand">
          Ticket<span>Now</span>
        </Link>
        <nav>
          <NavLink to="/" end>
            Eventos
          </NavLink>
          <NavLink to="/admin">Admin</NavLink>
          <NavLink to="/ops">Ops</NavLink>
          <NavLink to="/login">{displayName ? displayName : 'Entrar'}</NavLink>
        </nav>
      </header>
      <main className="container">
        <Routes>
          <Route path="/" element={<EventsPage />} />
          <Route path="/events/:id" element={<EventDetailPage />} />
          <Route path="/queue/:onsaleId" element={<QueuePage />} />
          <Route path="/admin" element={<AdminPage />} />
          <Route path="/ops" element={<OpsPage />} />
          <Route path="/login" element={<LoginPage />} />
          <Route path="*" element={<p>Página no encontrada.</p>} />
        </Routes>
      </main>
    </BrowserRouter>
  );
}
