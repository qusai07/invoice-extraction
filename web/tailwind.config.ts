import type { Config } from "tailwindcss";

const config: Config = {
  content: ["./src/**/*.{ts,tsx}"],
  theme: {
    extend: {
      fontFamily: {
        sans: ["Segoe UI", "Tahoma", "system-ui", "sans-serif"],
      },
      colors: {
        brand: { 50: "#eef4fb", 100: "#d6e4f5", 500: "#1f4e79", 600: "#1a4268", 700: "#143350" },
      },
    },
  },
  plugins: [],
};

export default config;
