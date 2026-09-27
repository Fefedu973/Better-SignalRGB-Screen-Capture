// The SignalRGB effect is distributed as one HTML file. Keep its embedded halo
// identical to the source used by the webpage and native output preview.
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '..');
const sourcePath = path.join(root, 'Better-SignalRGB-Screen-Capture/Services/WebOutput/ContourHalo.js');
const effectPath = path.join(root, 'Better-SignalRGB-Screen-Capture-Effect.html');
const begin = '    // BEGIN GENERATED CONTOUR HALO';
const end = '    // END GENERATED CONTOUR HALO';

function sync(check = false) {
    const source = fs.readFileSync(sourcePath, 'utf8').replace(/\r\n/g, '\n').trimEnd();
    if (/<\/script/i.test(source)) throw new Error('The shared halo script must be safe to embed in HTML.');
    const effect = fs.readFileSync(effectPath, 'utf8').replace(/\r\n/g, '\n');
    const start = effect.indexOf(begin), finish = effect.indexOf(end);
    if (start < 0 || finish < start || effect.indexOf(begin, start + begin.length) >= 0 ||
        effect.indexOf(end, finish + end.length) >= 0) throw new Error('Expected one generated halo block.');
    const updated = effect.slice(0, start) + begin + '\n' + source + '\n' + effect.slice(finish);
    if (check && updated !== effect) throw new Error('SignalRGB halo is stale. Run node scripts/Sync-ContourHalo.cjs.');
    if (!check && updated !== effect) fs.writeFileSync(effectPath, updated);
}

module.exports = sync;
if (require.main === module) {
    if (process.argv.slice(2).some(argument => argument !== '--check')) throw new Error('Usage: Sync-ContourHalo.cjs [--check]');
    sync(process.argv.includes('--check'));
    console.log('PASS: shared contour halo matches the standalone SignalRGB effect.');
}
