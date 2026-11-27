// Запросы к БД стенда (MS SQL) для проверок: значения строки через «|», строки через перевод строки,
// как раньше у psql -At. Подключение — из .env в корне репозитория.
//
//   node tools/checks/db.cjs "SELECT TOP 5 number, status FROM tickets ORDER BY number DESC"
const fs = require('fs')
const path = require('path')
const mssql = require('mssql')

const env = Object.fromEntries(
  fs.readFileSync(path.join(__dirname, '../../.env'), 'utf8').split('\n')
    .filter((l) => /^[A-Z0-9_]+=/.test(l))
    .map((l) => { const i = l.indexOf('='); return [l.slice(0, i), l.slice(i + 1).replace(/^"|"$/g, '')] }),
)

let pool = null
function connect() {
  pool ??= (env.MSSQL_AUTH || 'sql').toLowerCase() === 'windows' ? connectWindows() : new mssql.ConnectionPool({
    server: env.MSSQL_HOST || 'localhost',
    port: Number(env.MSSQL_PORT || 1433),
    database: env.MSSQL_DB,
    user: env.MSSQL_USER,
    password: env.MSSQL_PASSWORD,
    options: { encrypt: true, trustServerCertificate: env.MSSQL_TRUST_SERVER_CERTIFICATE === 'true' },
  }).connect()
  return pool
}

// MSSQL_AUTH=windows: вход под текущей учёткой Windows. Драйвер tedious так не умеет, нужен msnodesqlv8
// (ODBC Driver 18 for SQL Server). Ставится только на Windows и не входит в зависимости:
//   npm install --no-save msnodesqlv8   (в tools/checks)
function connectWindows() {
  let driver
  try {
    driver = require('mssql/msnodesqlv8')
  } catch {
    throw new Error('MSSQL_AUTH=windows needs msnodesqlv8: run "npm install --no-save msnodesqlv8" in tools/checks')
  }
  const trust = env.MSSQL_TRUST_SERVER_CERTIFICATE === 'true' ? 'yes' : 'no'
  return new driver.ConnectionPool({
    connectionString: `Driver={ODBC Driver 18 for SQL Server};Server=${env.MSSQL_HOST || 'localhost'},${env.MSSQL_PORT || 1433};`
      + `Database=${env.MSSQL_DB};Trusted_Connection=yes;Encrypt=yes;TrustServerCertificate=${trust};`,
  }).connect()
}

const text = (v) => (v === null || v === undefined ? '' : v === true ? 't' : v === false ? 'f' : v instanceof Date ? v.toISOString() : String(v))

/** Выполняет запрос и возвращает строки в виде «a|b\nc|d». */
async function sql(query) {
  const result = await (await connect()).request().query(query)
  const rows = result.recordset ?? []
  // mssql отдаёт uniqueidentifier в верхнем регистре, а API — в нижнем: приводим к виду API.
  const guids = new Set(Object.values(rows.columns ?? {}).filter((c) => c.type === mssql.UniqueIdentifier).map((c) => c.index))
  return rows.map((row) => Object.values(row).map((v, i) => (guids.has(i) && v ? String(v).toLowerCase() : text(v))).join('|')).join('\n')
}

async function close() {
  if (pool) await (await pool).close()
  pool = null
}

module.exports = { sql, close, env }

if (require.main === module) {
  sql(process.argv.slice(2).join(' '))
    .then((out) => console.log(out))
    .catch((e) => { console.error(e.message); process.exitCode = 1 })
    .finally(close)
}
