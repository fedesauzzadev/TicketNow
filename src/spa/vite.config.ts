import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Dev local: `npm run dev` sirve en :5173 y proxea /api y /hubs al edge-cache (:8090).
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:8090',
      '/hubs': {
        target: 'http://localhost:8090',
        ws: true,
      },
    },
  },
  preview: { port: 4173 },
});
