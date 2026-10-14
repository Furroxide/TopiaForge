/** Vite's `?raw` imports, which the configuration tests use to read wrangler.toml. */
declare module "*?raw" {
  const text: string;
  export default text;
}
