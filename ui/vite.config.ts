import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// In `dev`, the Vite server proxies API + WebSocket calls to the running Host on :5199.
// In production the Host serves the built `dist/`, so the app talks to its own origin.
export default defineConfig({
  plugins: [react()],
  build: { outDir: 'dist', emptyOutDir: true },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5199',
      '/ws': { target: 'ws://localhost:5199', ws: true },
    },
  },
})
