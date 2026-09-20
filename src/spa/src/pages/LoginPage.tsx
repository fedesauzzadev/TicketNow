import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { api } from '../api';
import { useAuthStore } from '../store/authStore';

/** Identidad mock didáctica (Fase 6): email → JWT de usuario del gateway. */
export default function LoginPage() {
  const navigate = useNavigate();
  const { userId, displayName, login, logout } = useAuthStore();
  const [email, setEmail] = useState('');
  const [name, setName] = useState('');
  const [error, setError] = useState('');
  const [working, setWorking] = useState(false);

  async function doLogin(e: React.FormEvent): Promise<void> {
    e.preventDefault();
    setError('');
    setWorking(true);
    try {
      const res = await api.login(email, name || undefined);
      login(res.userId, res.displayName, res.token);
      navigate('/');
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setWorking(false);
    }
  }

  if (userId) {
    return (
      <section>
        <h2>Sesión</h2>
        <p>
          Conectado como <strong>{displayName}</strong> (<code>{userId}</code>)
        </p>
        <button onClick={logout}>Cerrar sesión</button>
        <p>
          <Link to="/">Volver a eventos</Link>
        </p>
      </section>
    );
  }

  return (
    <section>
      <h2>Entrar</h2>
      <p className="muted">Login mock: cualquier email vale (en producción esto es un IdP).</p>
      <form onSubmit={doLogin} className="buy">
        <label>
          Email
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required />
        </label>
        <label>
          Nombre (opcional)
          <input value={name} onChange={(e) => setName(e.target.value)} />
        </label>
        <button type="submit" disabled={working}>
          {working ? 'Entrando…' : 'Entrar'}
        </button>
        {error && <p className="error">{error}</p>}
      </form>
    </section>
  );
}
