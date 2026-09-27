'use strict';

// Opt-in local fixture for the real WinUI/WebView capture path. Synthetic pixels
// only, no external requests, storage, camera or microphone access.
const http = require('node:http');
const server = http.createServer((request, response) => {
    response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
    response.end(`<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1">
<style>html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#ff0000}
#right{position:absolute;left:50%;top:0;width:50%;height:100%;background:#0000ff}
#moving{position:absolute;top:25%;width:12%;height:50%;background:#ffff00}
#label{position:absolute;color:white;top:5px;left:5px;font:16px monospace}</style></head>
<body><div id="right"></div><div id="moving"></div><div id="label"></div><script>
function frame(t){document.querySelector('#moving').style.left=(10+65*(.5+.5*Math.sin(t/500)))+'%';
document.querySelector('#label').textContent=innerWidth+' x '+innerHeight+' / '+Math.floor(t/250);
requestAnimationFrame(frame)}requestAnimationFrame(frame);
</script></body></html>`);
});
server.listen(19091, '127.0.0.1', () => console.log('Synthetic website fixture: http://127.0.0.1:19091/'));
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => server.close(() => process.exit(0)));
