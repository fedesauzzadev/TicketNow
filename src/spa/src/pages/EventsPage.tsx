import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { api } from '../api';

function statusLabel(status: string): string {
  switch (status) {
    case 'onsale':
      return 'En venta';
    case 'announced':
      return 'Próximamente';
    case 'onsale_closed':
      return 'Venta cerrada';
    case 'finished':
      return 'Finalizado';
    default:
      return status;
  }
}

export default function EventsPage() {
  const [search, setSearch] = useState('');
  const [term, setTerm] = useState('');

  // Debounce de 400 ms: menos requests al borde durante la escritura.
  useEffect(() => {
    const timer = setTimeout(() => setTerm(search.trim()), 400);
    return () => clearTimeout(timer);
  }, [search]);

  const { data, isLoading, isError } = useQuery({
    queryKey: ['events', term],
    queryFn: () => api.events(term ? { search: term } : undefined),
  });

  if (isLoading) return <p>Cargando eventos…</p>;
  if (isError || !data) return <p>No se pudo cargar el catálogo.</p>;

  return (
    <>
      <div className="toolbar">
        <input
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder="Buscar artista o evento…"
          aria-label="Buscar eventos"
        />
      </div>
      <div className="grid">
        {data.items.map((e) => (
          <Link key={e.id} to={`/events/${e.id}`} className="card">
            <span className={`badge badge-${e.status}`}>{statusLabel(e.status)}</span>
            <h3>{e.title}</h3>
            <p className="muted">{e.artist}</p>
            <p>
              {e.venue} · {e.city}
            </p>
            <p>{new Date(e.startsAt).toLocaleString('es-AR')}</p>
            {e.priceFrom != null && <p className="price">desde ${e.priceFrom}</p>}
            {e.status === 'announced' && e.onsaleOpensAt && (
              <p className="muted">Venta abre: {new Date(e.onsaleOpensAt).toLocaleString('es-AR')}</p>
            )}
          </Link>
        ))}
      </div>
      {data.items.length === 0 && <p>Sin resultados.</p>}
    </>
  );
}
