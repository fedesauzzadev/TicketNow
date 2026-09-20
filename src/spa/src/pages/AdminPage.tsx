import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../api';

/** Panel de organizador (FR-50/51). Sin auth hasta Fase 6 — anotado en docs/06-seguridad.md. */
export default function AdminPage() {
  const queryClient = useQueryClient();
  const { data: venues } = useQuery({ queryKey: ['venues'], queryFn: api.venues });

  const [title, setTitle] = useState('');
  const [artist, setArtist] = useState('');
  const [venueId, setVenueId] = useState('');
  const [startsAt, setStartsAt] = useState('');
  const [opensAt, setOpensAt] = useState('');
  const [zonesText, setZonesText] = useState('General|5000|70\nVIP|1000|150');
  const [message, setMessage] = useState('');

  const create = useMutation({
    mutationFn: async () => {
      const zones = zonesText
        .split('\n')
        .map((line) => line.trim())
        .filter((line) => line.length > 0)
        .map((line) => {
          const [name, capacity, price] = line.split('|');
          return { name: name.trim(), capacity: Number(capacity), price: Number(price) };
        });
      const created = await api.createEvent({
        title,
        artist,
        venueId,
        startsAt: new Date(startsAt).toISOString(),
        zones,
      });
      if (opensAt) {
        await api.configureOnsale(created.id, { opensAt: new Date(opensAt).toISOString() });
      }
      return created;
    },
    onSuccess: (created) => {
      setMessage(`Evento creado: ${created.id}`);
      void queryClient.invalidateQueries({ queryKey: ['events'] });
    },
    onError: (e) => setMessage(`Error: ${(e as Error).message}`),
  });

  return (
    <section>
      <h2>Nuevo evento</h2>
      <form
        className="form"
        onSubmit={(e) => {
          e.preventDefault();
          create.mutate();
        }}
      >
        <label>
          Título
          <input value={title} onChange={(e) => setTitle(e.target.value)} required />
        </label>
        <label>
          Artista
          <input value={artist} onChange={(e) => setArtist(e.target.value)} required />
        </label>
        <label>
          Venue
          <select value={venueId} onChange={(e) => setVenueId(e.target.value)} required>
            <option value="">— elegir —</option>
            {venues?.map((v) => (
              <option key={v.id} value={v.id}>
                {v.name} ({v.city})
              </option>
            ))}
          </select>
        </label>
        <label>
          Fecha del evento
          <input type="datetime-local" value={startsAt} onChange={(e) => setStartsAt(e.target.value)} required />
        </label>
        <label>
          Apertura de venta (opcional)
          <input type="datetime-local" value={opensAt} onChange={(e) => setOpensAt(e.target.value)} />
        </label>
        <label>
          Zonas (una por línea: nombre|capacidad|precio)
          <textarea rows={3} value={zonesText} onChange={(e) => setZonesText(e.target.value)} />
        </label>
        <button type="submit" disabled={create.isPending}>
          {create.isPending ? 'Creando…' : 'Crear evento'}
        </button>
      </form>
      {message && <p>{message}</p>}
    </section>
  );
}
