import { build } from 'esbuild';
import { mkdir, copyFile } from 'node:fs/promises';
await mkdir('dist', { recursive: true });
await build({ entryPoints: ['src/main.tsx'], bundle: true, minify: true, outfile: 'dist/app.js', target: 'chrome120', define: { 'process.env.NODE_ENV': '"production"' } });
await copyFile('index.html', 'dist/index.html');
