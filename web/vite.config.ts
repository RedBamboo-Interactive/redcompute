import path from "path"
import { defineConfig } from "vite"
import react from "@vitejs/plugin-react"
import tailwindcss from "@tailwindcss/vite"

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"),
    },
  },
  server: {
    proxy: {
      "/status": "http://localhost:18800",
      "/jobs": "http://localhost:18800",
      "/activity": "http://localhost:18800",
      "/control": "http://localhost:18800",
      "/logs": "http://localhost:18800",
      "/discover": "http://localhost:18800",
      "/openapi.json": "http://localhost:18800",
      "/settings": "http://localhost:18800",
      "/tts": "http://localhost:18800",
      "/image-gen": "http://localhost:18800",
      "/music-gen": "http://localhost:18800",
      "/ws": { target: "ws://localhost:18800", ws: true },
    },
  },
})
