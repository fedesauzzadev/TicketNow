import { useEffect, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { api, type EventListItem } from '../api';

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
  const [page, setPage] = useState(1);
  const [items, setItems] = useState<EventListItem[]>([]);
  const [total, setTotal] = useState(0);
  const termRef = useRef(term);

  // Debounce de 400 ms: menos requests al borde durante la escritura.
  // Cambiar el término reinicia la paginación; si no cambió, no se toca nada
  // (si no, el timer del montaje borra los datos ya cargados).
  useEffect(() => {
    const timer = setTimeout(() => {
      const t = search.trim();
      if (t !== termRef.current) {
        termRef.current = t;
        setTerm(t);
        setPage(1);
        setItems([]);
        setTotal(0);
      }
    }, 400);
    return () => clearTimeout(timer);
  }, [search]);

  const { data, isLoading, isError } = useQuery({
    queryKey: ['events', term, page],
    queryFn: () => api.events(term ? { search: term, page } : { page }),
  });

  // La lista se deriva del DATO (cacheado o fresco), no del fetch: al volver
  // con caché tibia (staleTime 30 s) no hay refetch y el queryFn no correría,
  // dejando la lista vacía ("Sin resultados" fantasma).
  useEffect(() => {
    if (!data) return;
    setTotal(data.total);
    setItems((prev) =>
      page === 1
        ? data.items
        : [...prev, ...data.items.filter((i) => !prev.some((x) => x.id === i.id))],
    );
  }, [data, page]);

  if (isLoading && items.length === 0) return <p>Cargando eventos…</p>;
  if (isError && items.length === 0) return <p>No se pudo cargar el catálogo.</p>;

  const remaining = total - items.length;

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
      <p className="muted">
        {items.length} de {total} eventos
      </p>
      <div className="grid">
        {items.map((e) => (
          <Link key={e.id} to={`/events/${e.id}`} className="card">
            <span className={`badge badge-${e.status}`}>{statusLabel(e.status)}</span>{' '}
            {e.requiresQueue && e.status === 'onsale' && <span className="badge badge-queue">Con cola</span>}{' '}
            {e.availability === 'soldout' && <span className="badge badge-soldout">Agotado</span>}
            {(e.availability === 'low' || e.availability === 'medium') && e.status === 'onsale' && (
              <span className="badge badge-low">Pocas entradas</span>
            )}
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
      {items.length === 0 && <p>Sin resultados.</p>}
      {remaining > 0 && (
        <p>
          <button onClick={() => setPage((p) => p + 1)} disabled={isLoading}>
            {isLoading ? 'Cargando…' : `Mostrar más (${remaining} restantes)`}
          </button>
        </p>
      )}
    </>
  );
}
