import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

// Em desenvolvimento o Vite repassa a API e o WebSocket para o Hub local (dotnet run, porta 5080).
const hub = process.env.HUB_URL ?? 'http://127.0.0.1:5080';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    proxy: {
      '/api': hub,
      '/ws': { target: hub, ws: true },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: false,
    // Um pacote só (≈160 kB gzip), servido pelo Hub na rede do Tailscale; dividir não compensa.
    chunkSizeWarningLimit: 600,
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
  },
});
