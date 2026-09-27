const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');

const root = path.resolve(__dirname, '..');
const defaults = { version:1, enabled:false, webEnabled:false, pictureMode:'Standard', hue:0, brightness:0,
    saturation:0, blur:false, ambilight:true, ambilightFullscreen:false, hideSources:false,
    ambilightBlur:30, ambilightSpread:10, ambilightSaturation:3, ambilightIntensity:100,
    interpolation:'smooth', frameRate:30, ambilightStyle:'Classic', ambilightCutoff:0,
    screenX:0, screenY:0, screenWidth:320, screenHeight:200 };
function makeState(fixture, settings = {}, width = 320, height = 200) {
    return { version:1, settings:{...defaults,...settings}, canvasWidth:320, canvasHeight:200,
        outputWidth:width, outputHeight:height, sources:fixture.sources.map((source,zIndex) => {
            const g=source.geometry;
            return {id:source.id,type:'Monitor',websiteUrl:'',canvasX:g.x,canvasY:g.y,canvasWidth:g.width,
                canvasHeight:g.height,rotation:g.rotation,opacity:g.opacity,isMirroredHorizontally:g.mirrorX,
                isMirroredVertically:g.mirrorY,cropLeftPct:g.left,cropRightPct:g.right,cropTopPct:g.top,
                cropBottomPct:g.bottom,cropRotation:g.cropRotation,zIndex,outerStyle:source.outer,cropStyle:source.crop};
        }) };
}

module.exports = async function checkWebOutput(browser, fixtures) {
    const assets=path.join(root,'Better-SignalRGB-Screen-Capture','Services','WebOutput');
    const html=fs.readFileSync(path.join(assets,'StreamingCanvasPage.html'),'utf8')
        .replace('<!--CONTOUR_HALO_SCRIPT-->',()=>`<script>${fs.readFileSync(path.join(assets,'ContourHalo.js'),'utf8')}</script>`)
        .replace('<!--WEB_OUTPUT_SCRIPT-->',()=>`<script>${fs.readFileSync(path.join(assets,'StreamingCanvasPage.js'),'utf8')}</script>`);
    const streams=new Set();
    let connections=0,stateVersion=1,state=makeState(fixtures[0]);
    let currentFrames=[{bytes:Buffer.from(fixtures[0].composite,'base64')}];
    function part(response,type,bytes,id,version=stateVersion) {
        const header=Buffer.from(`--frame\r\nContent-Type: ${type}\r\nContent-Length: ${bytes.length}\r\nX-State-Version: ${version}\r\n${id?`X-Source-Id: ${id}\r\n`:''}\r\n`);
        // Exercise fragmented boundaries, headers and payloads.
        response.write(header.subarray(0,7));response.write(header.subarray(7));
        response.write(bytes.subarray(0,13));response.write(bytes.subarray(13));response.write('\r\n');
    }
    function sendState(response){part(response,'application/json',Buffer.from(JSON.stringify(state)));}
    function sendFrames(response){for(const frame of currentFrames)part(response,'image/jpeg',frame.bytes,frame.id);}
    function update(nextState,frames){
        state=nextState;stateVersion++;if(frames!==undefined)currentFrames=frames;
        for(const response of streams){sendState(response);if(frames!==undefined)sendFrames(response);}
        return stateVersion;
    }
    const server=http.createServer((request,response)=>{
        if(request.url!=='/web-stream'){response.writeHead(200,{'Content-Type':'text/html; charset=utf-8'});response.end(html);return;}
        connections++;streams.add(response);
        response.writeHead(200,{'Content-Type':'multipart/x-mixed-replace; boundary=frame','Cache-Control':'no-store'});
        sendState(response);sendFrames(response);response.on('close',()=>streams.delete(response));
    });
    await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
    const page=await browser.newPage({viewport:{width:320,height:200}});
    const reference=await browser.newPage({viewport:{width:320,height:200}});
    const errors=[];page.on('pageerror',error=>errors.push(error.message));
    await page.addInitScript(()=>{
        const decode=window.createImageBitmap.bind(window);
        window.decodeDelay=0;window.startedDecodes=0;
        window.createImageBitmap=async(...args)=>{
            window.startedDecodes++;
            if(window.decodeDelay)await new Promise(resolve=>setTimeout(resolve,window.decodeDelay));
            return decode(...args);
        };
    });
    async function waitForState(version,drawsAfter){
        await page.waitForFunction(({version,drawsAfter})=>{
            const d=window.webOutputDiagnostics;
            return d&&d.stateVersion>=version&&d.activeDecoders===0&&d.pendingCount===0&&
                (drawsAfter===undefined||d.renderCount>drawsAfter);
        },{version,drawsAfter},{timeout:5000});
    }
    async function change(nextState,frames){
        const before=await page.evaluate(()=>window.webOutputDiagnostics.renderCount);
        const decodes=await page.evaluate(()=>window.startedDecodes);
        const version=update(nextState,frames);
        if(frames?.length)await page.waitForFunction(count=>window.startedDecodes>=count,decodes+frames.length,{timeout:5000});
        await waitForState(version,before);
        await page.waitForTimeout(50); // A decoded image can still be waiting for the next capped render tick.
        return version;
    }
    async function stats(screenshot){
        return page.evaluate(async encoded=>{
            let image=document.getElementById('frame');
            if(encoded){image=new Image();image.src='data:image/png;base64,'+encoded;await image.decode();}
            const c=document.createElement('canvas');c.width=320;c.height=200;
            const ctx=c.getContext('2d');ctx.fillStyle='#000';ctx.fillRect(0,0,320,200);ctx.drawImage(image,0,0,320,200);
            const bytes=ctx.getImageData(0,0,320,200).data;let lit=0,colored=0;
            for(let i=0;i<bytes.length;i+=4){
                if(Math.max(bytes[i],bytes[i+1],bytes[i+2])>5)lit++;
                if(Math.max(bytes[i],bytes[i+1],bytes[i+2])-Math.min(bytes[i],bytes[i+1],bytes[i+2])>4)colored++;
            }return{lit,colored};
        },screenshot?.toString('base64'));
    }
    async function compare(name,tolerance=2){
        const expected=await reference.screenshot(),actual=await page.screenshot();
        const result=await page.evaluate(async({expected,actual})=>{
            async function pixels(encoded){
                const img=new Image();img.src='data:image/png;base64,'+encoded;await img.decode();
                const c=document.createElement('canvas');c.width=320;c.height=200;
                const ctx=c.getContext('2d');ctx.drawImage(img,0,0,320,200);return ctx.getImageData(0,0,320,200).data;
            }
            const a=await pixels(actual),b=await pixels(expected);let sum=0,outliers=0;
            for(let i=0;i<a.length;i+=4){let peak=0;for(let k=0;k<3;k++){const d=Math.abs(a[i+k]-b[i+k]);sum+=d;peak=Math.max(peak,d);}if(peak>30)outliers++;}
            return{mean:sum/(320*200*3),outliers:outliers/(320*200)};
        },{expected:expected.toString('base64'),actual:actual.toString('base64')});
        assert.ok(result.mean<tolerance&&result.outliers<.025,`${name}: web/SignalRGB difference ${JSON.stringify(result)}`);
    }
    async function setReference(fixture,settings,replaceSources=false){
        await reference.evaluate(({fixture,settings,replaceSources})=>{
            const send=event=>onCanvasApiEvent({sender:'BetterSignalRGBScreenCapture',event});
            window.screenX=window.screenY=0;window.screenW=320;window.screenH=200;
            send('config:'+JSON.stringify({...settings,enabled:true}));
            if(replaceSources){send('reset');for(const source of fixture.sources){
                send(`header:${source.id}.jpg:image/jpeg:1:outer:${source.outer}|crop:${source.crop}`);
                send(`data:${source.id}:0:${source.jpeg}`);send(`end:${source.id}`);
            }}
        },{fixture,settings,replaceSources});
        // Old plugin has no diagnostics hook; tiny synthetic images need two decoders
        // and one 30fps render tick, including on CI.
        await reference.waitForTimeout(100);
    }
    try{
        await page.goto(`http://127.0.0.1:${server.address().port}/`,{waitUntil:'domcontentloaded'});
        await page.waitForFunction(()=>window.startedDecodes>=1);
        await waitForState(1,0);await page.waitForTimeout(50);
        assert.ok((await stats()).lit>1000,'Raw multipart JPEG paints pixels');
        for(const[width,height]of[[800,600],[319,199],[1920,1080],[320,200]]){
            await page.setViewportSize({width,height});
            await page.waitForFunction(({width,height})=>{
                const rect=document.getElementById('frame').getBoundingClientRect();
                return Math.abs(rect.width-width)<.01&&Math.abs(rect.height-height)<.01;
            },{width,height},{timeout:5000});
            const size=await page.evaluate(()=>{
                const r=document.getElementById('frame').getBoundingClientRect();
                return{text:document.body.innerText,x:r.x,y:r.y,width:r.width,height:r.height,
                    scrollWidth:document.documentElement.scrollWidth,scrollHeight:document.documentElement.scrollHeight};
            });
            assert.deepEqual(size,{text:'',x:0,y:0,width,height,scrollWidth:width,scrollHeight:height});
        }
        const solid=await page.evaluate(()=>{
            const c=document.createElement('canvas');c.width=800;c.height=600;const ctx=c.getContext('2d');
            return['#20e040','#d02050'].map(color=>{ctx.fillStyle=color;ctx.fillRect(0,0,800,600);return c.toDataURL('image/jpeg',.92).split(',')[1];});
        });
        await change(makeState(fixtures[0],{enabled:true,pictureMode:'Mono',ambilightStyle:'Contours'},800,600),[{bytes:Buffer.from(solid[0],'base64')}]);
        assert.deepEqual(await page.evaluate(()=>[document.getElementById('frame').width,document.getElementById('frame').height]),[800,600]);
        assert.ok((await stats(await page.screenshot())).colored>60000,'SignalRGB control alone never filters raw web output');
        await change(makeState(fixtures[0]),[{bytes:Buffer.from(fixtures[0].composite,'base64')}]);
        assert.equal(connections,1,'Raw/HQ switches preserve one stream');
        await reference.setContent(fs.readFileSync(path.join(root,'Better-SignalRGB-Screen-Capture-Effect.html'),'utf8'));
        for(const fixture of fixtures){
            const effect={...defaults,webEnabled:true,ambilight:false};
            await change(makeState(fixture,effect),fixture.sources.map(source=>({id:source.id,bytes:Buffer.from(source.jpeg,'base64')})));
            await setReference(fixture,effect,true);await compare(fixture.name);
        }
        const fixture=fixtures[5],effect={...defaults,webEnabled:true,ambilight:false};
        await change(makeState(fixture,effect),fixture.sources.map(source=>({id:source.id,bytes:Buffer.from(source.jpeg,'base64')})));
        await setReference(fixture,effect,true);
        const appearances=[
            {pictureMode:'Standard',hue:57,brightness:17,saturation:29},
            {pictureMode:'Cinema'},{pictureMode:'Mono',blur:true},{pictureMode:'Vivid'},
            {pictureMode:'Dominant'},{pictureMode:'HD',interpolation:'pixelated'},
            {ambilight:true,ambilightSpread:8,ambilightBlur:9,ambilightSaturation:2,ambilightIntensity:160},
            {ambilight:true,ambilightFullscreen:true,ambilightBlur:9,ambilightSpread:8},
            {ambilight:true,ambilightFullscreen:true,hideSources:true,pictureMode:'Mono',blur:true,ambilightBlur:9},
            {ambilight:true,ambilightFullscreen:true,hideSources:true,ambilightIntensity:0},
            {ambilight:false,ambilightFullscreen:true,hideSources:true},
            {ambilight:true,ambilightStyle:'Soft',ambilightSpread:14,ambilightBlur:9},
            {ambilight:true,ambilightStyle:'Soft',ambilightFullscreen:true,hideSources:true,ambilightCutoff:30},
            {ambilight:true,ambilightStyle:'Classic',ambilightFullscreen:true,hideSources:true,ambilightCutoff:45},
            {ambilight:true,ambilightStyle:'Soft',ambilightFullscreen:true,hideSources:true,ambilightCutoff:100},
            {screenX:48,screenY:30,screenWidth:224,screenHeight:140,ambilight:true,ambilightBlur:8},
            {screenX:40,screenY:15,screenWidth:140,screenHeight:160,ambilight:true,ambilightFullscreen:true,ambilightStyle:'Soft'},
            {ambilight:true,ambilightStyle:'Contours'},
            {ambilight:true,ambilightStyle:'Contours',ambilightEdgeDepth:20,ambilightEdgeMix:30},
            {ambilight:true,ambilightStyle:'Contours',ambilightEdgeReach:18,ambilightEdgeFade:0},
            {ambilight:true,ambilightStyle:'Contours',ambilightEdgeReach:200,ambilightEdgeFade:100},
            {ambilight:true,ambilightStyle:'Contours',ambilightFullscreen:true,hideSources:true},
            {ambilight:true,ambilightStyle:'Contours',ambilightFullscreen:true,hideSources:true,pictureMode:'Mono'},
            {ambilight:true,ambilightStyle:'Contours',ambilightFullscreen:true,hideSources:true,ambilightIntensity:0},
            {ambilight:true,ambilightStyle:'Contours',ambilightFullscreen:true,hideSources:true,ambilightCutoff:100},
            {screenX:48,screenY:30,screenWidth:224,screenHeight:140,ambilight:true,ambilightStyle:'Contours'},
            {ambilight:false,ambilightStyle:'Contours',ambilightFullscreen:true,hideSources:true}
        ];
        for(const appearance of appearances){
            const settings={...effect,...appearance};
            await change(makeState(fixture,settings)); // Sliders must also redraw paused sources without a JPEG.
            await setReference(fixture,settings);await compare(JSON.stringify(appearance),3);
            if(appearance.pictureMode==='Mono')assert.equal((await stats(await page.screenshot())).colored,0,'Mono also affects glow');
            if(appearance.ambilightIntensity===0)assert.equal((await stats(await page.screenshot())).lit,0,'Zero intensity with hidden picture is black');
            if(appearance.ambilightCutoff===100)assert.equal((await stats(await page.screenshot())).lit,0,'Full cutoff removes glow without a residual halo');
        }
        const hq={...effect,ambilight:true,ambilightFullscreen:true,hideSources:true,ambilightBlur:9,ambilightSpread:8};
        await setReference(fixture,hq);await change(makeState(fixture,hq,800,600));
        assert.deepEqual(await page.evaluate(()=>[document.getElementById('frame').width,document.getElementById('frame').height]),[800,600]);
        await compare('HQ preserves logical glow radii and geometry',5);
        await page.evaluate(()=>{window.decodeDelay=120;});
        const started=await page.evaluate(()=>window.startedDecodes);
        update(makeState(fixtures[0],effect),[{id:fixtures[0].sources[0].id,bytes:Buffer.from(solid[0],'base64')}]);
        await page.waitForFunction(before=>window.startedDecodes>before,started);
        await change({...makeState(fixtures[0],effect),sources:[]},[]);
        await page.waitForTimeout(160);assert.equal((await stats()).lit,0,'Late decode cannot resurrect a removed source');
        await page.evaluate(()=>{window.decodeDelay=0;});
        await change(makeState(fixtures[0],effect),[{id:fixtures[0].sources[0].id,bytes:Buffer.from(fixtures[0].sources[0].jpeg,'base64')}]);
        assert.ok((await stats()).lit>1000,'Recovered source renders again');
        await page.evaluate(()=>{window.decodeDelay=100;});
        const beforeDecodes=await page.evaluate(()=>window.startedDecodes);
        for(const response of streams)for(let i=0;i<30;i++)part(response,'image/jpeg',Buffer.from(solid[i===29?1:0],'base64'),fixtures[0].sources[0].id);
        await page.waitForTimeout(500);
        const bounded=await page.evaluate(()=>({...window.webOutputDiagnostics,startedDecodes:window.startedDecodes}));
        assert.ok(bounded.activeDecoders<=2&&bounded.bufferedBytes<=32*1024*1024&&bounded.decodedPixels<=16*1024*1024,'Decoder and memory budgets stay bounded');
        assert.ok(bounded.startedDecodes-beforeDecodes<15,'Superseded frames are dropped, not queued');
        await page.evaluate(()=>{window.decodeDelay=0;});
        await change(makeState(fixtures[0]),[{bytes:Buffer.from(solid[0],'base64')}]);
        assert.equal(connections,1,'All settings/source/HQ edits share one stream');
        for(const response of streams)response.destroy();
        const deadline=Date.now()+5000;while(connections<2&&Date.now()<deadline)await new Promise(resolve=>setTimeout(resolve,30));
        assert.equal(connections,2,'Disconnected output reconnects');await waitForState(stateVersion);
        await page.waitForFunction(()=>document.getElementById('frame').getContext('2d').getImageData(0,0,1,1).data[1]>100);
        assert.deepEqual(errors,[],'No browser runtime errors');
        console.log(`PASS: web output: raw/HQ, ${fixtures.length} transforms, all appearance controls and glow pixel parity, paused-source edits, removal/recovery, bounded decoding and reconnect.`);
    }finally{
        await page.close();await reference.close();for(const response of streams)response.destroy();
        server.closeAllConnections();await new Promise(resolve=>server.close(resolve));
    }
};
