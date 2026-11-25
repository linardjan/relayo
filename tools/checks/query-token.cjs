const signalR = require('module').createRequire(require('path').join(__dirname, '../..') + '/web/widget/')('@microsoft/signalr')
const W = '00000000-0000-0000-0000-000000000001', O = 'http://localhost:5300'
;(async () => {
  const s = await (await fetch(`http://localhost:5200/api/widget/${W}/session`, { method: 'POST', headers: { Origin: O } })).json()
  const hub = new signalR.HubConnectionBuilder()
    .withUrl(`http://localhost:5200/hubs/widget?widgetId=${W}&access_token=${encodeURIComponent(s.visitorToken)}`,
      { transport: signalR.HttpTransportType.WebSockets, skipNegotiation: true, headers: { Origin: O } })
    .configureLogging(signalR.LogLevel.None).build()
  const got = []
  hub.on('message', (m) => got.push(m))
  await hub.start()
  await fetch(`http://localhost:5200/api/widget/${W}/messages`, { method: 'POST', headers: { Origin: O, 'X-Relayo-Visitor': s.visitorToken, 'content-type': 'application/json' }, body: JSON.stringify({ text: 'query token check' }) })
  await new Promise((r) => setTimeout(r, 1500))
  console.log(got.some((m) => m.text === 'query token check') && hub.state === 'Connected' ? 'PASS token in query string (browser path)' : 'FAIL token in query string')
  await hub.stop()
})()
