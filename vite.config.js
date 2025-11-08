import { defineConfig } from 'vite';
import { resolve } from 'path';

const rootDir = resolve(__dirname, 'PoPunkouterSoftware', 'wwwroot');
const outDir = resolve(__dirname, 'dist');

export default defineConfig({
  root: rootDir,
  publicDir: false,
  server: {
    port: 3000,
    open: '/index.html',
    fs: {
      strict: true,
      allow: [rootDir]
    }
  },
  build: {
    outDir,
    emptyOutDir: true,
    rollupOptions: {
      input: {
        main: resolve(rootDir, 'index.html'),
        team: resolve(rootDir, 'OurTeam.html'),
        webapps: resolve(rootDir, 'OurWebApps.html'),
        phoneapps: resolve(rootDir, 'OurPhoneApps.html'),
        contact: resolve(rootDir, 'Contact.html'),
        privacy: resolve(rootDir, 'PrivacyPolicy.html')
      }
    },
    minify: 'terser',
    cssMinify: true
  }
});
