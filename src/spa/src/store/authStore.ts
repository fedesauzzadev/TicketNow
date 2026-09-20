import { create } from 'zustand';

// Identidad del fan (Fase 6): JWT emitido por el gateway (/api/auth/token).
interface AuthState {
  userToken: string | null;
  userId: string | null;
  displayName: string | null;
  login: (userId: string, displayName: string, token: string) => void;
  logout: () => void;
}

export const useAuthStore = create<AuthState>((set) => ({
  userToken: localStorage.getItem('tn-user-token'),
  userId: localStorage.getItem('tn-user-id'),
  displayName: localStorage.getItem('tn-user-name'),
  login: (userId, displayName, token) => {
    localStorage.setItem('tn-user-token', token);
    localStorage.setItem('tn-user-id', userId);
    localStorage.setItem('tn-user-name', displayName);
    set({ userToken: token, userId, displayName });
  },
  logout: () => {
    localStorage.removeItem('tn-user-token');
    localStorage.removeItem('tn-user-id');
    localStorage.removeItem('tn-user-name');
    set({ userToken: null, userId: null, displayName: null });
  },
}));
