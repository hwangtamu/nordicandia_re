import { defineConfig } from 'vite';
export default defineConfig({server:{port:5173,strictPort:true,proxy:{'/api/web':{target:'http://127.0.0.1:5080'}}},build:{chunkSizeWarningLimit:1500}});
