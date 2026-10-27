// Where the end-to-end stack runs. The database must exist with migrations applied and seed data loaded
// (tools/dev/seed.py --fake); run.sh does that locally, the e2e job in .github/workflows/ci.yml in CI.
const databaseUrl = new URL(process.env.E2E_DATABASE_URL ?? 'postgres://fandom:e2e@127.0.0.1:55480/fandom_e2e');

export const settings = {
  databaseUrl: databaseUrl.toString(),
  /** The same database as an Npgsql connection string, for the API. */
  npgsql:
    `Host=${databaseUrl.hostname};Port=${databaseUrl.port || 5432};Database=${databaseUrl.pathname.slice(1)};` +
    `Username=${decodeURIComponent(databaseUrl.username)};Password=${decodeURIComponent(databaseUrl.password)}`,
  apiPort: 5271,
  appPort: 8081,
  get apiUrl() {
    return `http://127.0.0.1:${this.apiPort}/v1`;
  },
  get appUrl() {
    return `http://127.0.0.1:${this.appPort}`;
  },
};
