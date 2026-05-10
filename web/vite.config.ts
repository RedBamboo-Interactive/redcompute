import path from "path"
import fs from "fs"
import { defineConfig } from "vite"
import react from "@vitejs/plugin-react"
import tailwindcss from "@tailwindcss/vite"

const pkg = JSON.parse(fs.readFileSync(path.resolve(__dirname, "package.json"), "utf-8"))

export default defineConfig({
  plugins: [react(), tailwindcss()],
  define: {
    __APP_VERSION__: JSON.stringify(pkg.version),
  },
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"),
      "react": path.resolve(__dirname, "node_modules/react"),
      "react-dom": path.resolve(__dirname, "node_modules/react-dom"),
      "react/jsx-runtime": path.resolve(__dirname, "node_modules/react/jsx-runtime"),
      "react/jsx-dev-runtime": path.resolve(__dirname, "node_modules/react/jsx-dev-runtime"),
      "lucide-react": path.resolve(__dirname, "node_modules/lucide-react"),
    },
    dedupe: ["react", "react-dom", "@base-ui/react", "lucide-react"],
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
      "/tunnel": "http://localhost:18800",
      "/tts": "http://localhost:18800",
      "/image-gen": "http://localhost:18800",
      "/music-gen": "http://localhost:18800",
      "/ws": { target: "ws://localhost:18800", ws: true },
    },
  },
})
