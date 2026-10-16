import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true, // the backend's Frontend:Url points here
    proxy: {
      // Same-origin /api calls go to the backend, so it needs no CORS.
      // secure: false because Node doesn't trust the Windows-store ASP.NET dev certificate.
      '/api': { target: 'https://localhost:5001', secure: false },
    },
  },
})
