import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { resolve } from 'node:path';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  base: '/',
  resolve: { preserveSymlinks: true },
  publicDir: 'public',
  build: {
    outDir: resolve(import.meta.dirname, '../wwwroot'),
    emptyOutDir: true,
    sourcemap: false,
    assetsInlineLimit: 4096,
    rollupOptions: {
      output: {
        manualChunks: {
          'react-vendor': ['react', 'react-dom', 'react-router-dom'],
          icons: ['lucide-react'],
          translations: ['./src/i18n.js']
        }
      }
    }
  },
  server: {
    strictPort: true,
    proxy: { '/api': { target: process.env.DSS_API_TARGET || 'http://127.0.0.1:5089', changeOrigin: true } }
  }
});
