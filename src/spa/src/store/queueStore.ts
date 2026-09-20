import { create } from 'zustand';
import { setAdmissionTokenReader } from '../api';

// Estado de la fila virtual + token de admisión (Fase 4).
interface QueueState {
  sessionId: string | null;
  onsaleId: string | null;
  admissionToken: string | null;
  position: number | null;
  etaSeconds: number | null;
  admitted: boolean;
  setQueue: (patch: Partial<QueueState>) => void;
  clear: () => void;
}

const initial: Omit<QueueState, 'setQueue' | 'clear'> = {
  sessionId: localStorage.getItem('tn-session') ?? null,
  onsaleId: localStorage.getItem('tn-onsale') ?? null,
  admissionToken: localStorage.getItem('tn-admission') ?? null,
  position: null,
  etaSeconds: null,
  admitted: false,
};

export const useQueueStore = create<QueueState>((set) => ({
  ...initial,
  setQueue: (patch) =>
    set((state) => {
      const next = { ...state, ...patch };
      if (patch.sessionId !== undefined)
        localStorage.setItem('tn-session', patch.sessionId ?? '');
      if (patch.onsaleId !== undefined) localStorage.setItem('tn-onsale', patch.onsaleId ?? '');
      if (patch.admissionToken !== undefined)
        localStorage.setItem('tn-admission', patch.admissionToken ?? '');
      return next;
    }),
  clear: () => {
    localStorage.removeItem('tn-session');
    localStorage.removeItem('tn-onsale');
    localStorage.removeItem('tn-admission');
    set({ ...initial, sessionId: null, onsaleId: null, admissionToken: null });
  },
}));

// Conecta el reader perezoso del api client (una vez al arrancar).
setAdmissionTokenReader(() => useQueueStore.getState().admissionToken);
