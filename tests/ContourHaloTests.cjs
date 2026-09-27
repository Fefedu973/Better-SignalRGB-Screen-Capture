const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const enginePath = path.join(__dirname, '..', 'Better-SignalRGB-Screen-Capture', 'Services', 'WebOutput', 'ContourHalo.js');

module.exports = async function checkContourHalo(browser, options = {}) {
    const page = await browser.newPage({ viewport: { width:800, height:600 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    try {
        await page.evaluate(() => {
            window.contourContexts = [];
            window.contourResources = [];
            const observed = new WeakSet();
            const original = HTMLCanvasElement.prototype.getContext;
            HTMLCanvasElement.prototype.getContext = function(type, ...args) {
                const context = original.call(this, type, ...args);
                if (context && /webgl/.test(type)) {
                    window.contourContexts.push(context);
                    if(!observed.has(context)){
                        observed.add(context);
                        for(const kind of ['Texture','Program','Buffer','Shader']){
                            const create=context['create'+kind].bind(context);
                            context['create'+kind]=(...args)=>{const resource=create(...args);window.contourResources.push({context,kind,resource});return resource;};
                        }
                    }
                }
                return context;
            };
        });
        await page.addScriptTag({ content:fs.readFileSync(enginePath, 'utf8') });
        await page.evaluate(() => {
            const W = 320, H = 200;
            const defaults = { ambilightStyle:'Contours', ambilightEdgeDepth:3, ambilightEdgeMix:0,
                ambilightEdgeReach:60, ambilightEdgeFade:50, ambilightSaturation:1,
                ambilightIntensity:100, ambilightCutoff:0, ambilightFullscreen:false,
                screenX:0, screenY:0, screenWidth:320, screenHeight:200 };
            function canvas(width=W,height=H) { const c=document.createElement('canvas');c.width=width;c.height=height;return c; }
            function pixels(c) { return c.getContext('2d',{willReadFrequently:true}).getImageData(0,0,c.width,c.height).data; }
            function pixel(data,x,y) { const i=(y*W+x)*4;return Array.from(data.slice(i,i+4)); }
            function alphaSum(data) { let sum=0;for(let i=3;i<data.length;i+=4)sum+=data[i];return sum; }
            function difference(a,b) { let sum=0,max=0;for(let i=0;i<a.length;i++){const d=Math.abs(a[i]-b[i]);sum+=d;max=Math.max(max,d);}return{mean:sum/a.length,max}; }
            function diagnostics(engine) { const d=engine.diagnostics;return{...(typeof d==='function'?d():d)}; }
            function fixture(name='split',width=W,height=H) {
                const frame=canvas(width,height),mask=canvas(),fc=frame.getContext('2d'),mc=mask.getContext('2d');
                if(name!=='transparent') {fc.fillStyle='#000';fc.fillRect(0,0,width,height);}
                fc.save();fc.scale(width/W,height/H);
                fc.fillStyle='#f00';fc.fillRect(90,60,70,80);fc.fillStyle='#00f';fc.fillRect(160,60,70,80);
                if(name==='center-green'||name==='center-magenta') {fc.fillStyle=name==='center-green'?'#0f0':'#f0f';fc.fillRect(115,80,90,40);}
                if(name==='mirror') {fc.fillStyle='#00f';fc.fillRect(90,60,70,80);fc.fillStyle='#f00';fc.fillRect(160,60,70,80);}
                fc.restore();mc.fillStyle='#fff';mc.fillRect(90,60,140,80);
                if(name==='thin-gap'){
                    fc.clearRect(0,0,width,height);fc.save();fc.scale(width/W,height/H);
                    fc.fillStyle='#f00';fc.fillRect(90,60,3,80);fc.fillStyle='#00f';fc.fillRect(94,60,136,80);fc.restore();
                    mc.clearRect(0,0,W,H);mc.fillStyle='#fff';mc.fillRect(90,60,3,80);mc.fillRect(94,60,136,80);
                }
                if(name==='left'){
                    fc.clearRect(0,0,width,height);fc.save();fc.scale(width/W,height/H);fc.fillStyle='#f00';fc.fillRect(0,60,40,80);fc.restore();
                    mc.clearRect(0,0,W,H);mc.fillStyle='#fff';mc.fillRect(0,60,40,80);
                }
                return{frame,mask,draw:ctx=>ctx.drawImage(mask,0,0,W,H)};
            }
            // Independent inverse-transform oracle, without CSS, Path2D or production
            // geometry helpers. Crop is a rotated fixed mask; mirrors affect pixels only.
            function transformedFixture() {
                const frame=canvas(),mask=canvas(),fc=frame.getContext('2d'),mc=mask.getContext('2d');
                const image=fc.createImageData(W,H),m=mc.createImageData(W,H);
                const sources=[{x:35,y:45,w:135,h:90,rotation:28,cropRotation:-31,left:.12,right:.19,top:.15,bottom:.06,mirror:true},
                    {x:181,y:70,w:91,h:71,rotation:-24,cropRotation:21,left:.04,right:.12,top:.18,bottom:.07,mirror:false}];
                for(let y=0;y<H;y++)for(let x=0;x<W;x++){
                    const i=(y*W+x)*4;image.data[i+3]=255;
                    for(let k=0;k<sources.length;k++){
                        const s=sources[k],a=-s.rotation*Math.PI/180,dx=x+.5-s.x-s.w/2,dy=y+.5-s.y-s.h/2;
                        const lx=dx*Math.cos(a)-dy*Math.sin(a)+s.w/2,ly=dx*Math.sin(a)+dy*Math.cos(a)+s.h/2;
                        const cw=s.w*(1-s.left-s.right),ch=s.h*(1-s.top-s.bottom),cx=lx-s.w*s.left-cw/2,cy=ly-s.h*s.top-ch/2;
                        const ca=-s.cropRotation*Math.PI/180,px=cx*Math.cos(ca)-cy*Math.sin(ca),py=cx*Math.sin(ca)+cy*Math.cos(ca);
                        if(lx<0||ly<0||lx>=s.w||ly>=s.h||Math.abs(px)>cw/2||Math.abs(py)>ch/2)continue;
                        const left=(s.mirror?s.w-lx:lx)<s.w/2;
                        const color=k===0?(left?[255,0,0]:[0,0,255]):(left?[0,255,0]:[255,255,0]);
                        image.data.set([...color,255],i);m.data.set([255,255,255,255],i);
                    }
                }
                fc.putImageData(image,0,0);mc.putImageData(m,0,0);
                return{frame,mask,draw:ctx=>ctx.drawImage(mask,0,0,W,H)};
            }
            function geometryStats(f,actual) {
                const mask=pixels(f.mask),image=pixels(f.frame),boundary=[];
                const inside=(x,y)=>x>=0&&y>=0&&x<W&&y<H&&mask[(y*W+x)*4+3]>250;
                let interiorSamples=0,interiorLeaks=0;
                for(let y=1;y<H-1;y++)for(let x=1;x<W-1;x++)if(inside(x,y)){
                    if(!inside(x-1,y)||!inside(x+1,y)||!inside(x,y-1)||!inside(x,y+1))boundary.push({x,y,c:pixel(image,x,y)});
                    if(inside(x-2,y)&&inside(x+2,y)&&inside(x,y-2)&&inside(x,y+2)){
                        interiorSamples++;if(actual[(y*W+x)*4+3]>3)interiorLeaks++;
                    }
                }
                let outsideSamples=0,colorErrors=0;
                for(let y=6;y<H-6;y+=6)for(let x=6;x<W-6;x+=6){
                    if(inside(x,y))continue;
                    let best=Infinity,nearest;
                    for(const p of boundary){const d=(p.x-x)**2+(p.y-y)**2;if(d<best){best=d;nearest=p;}}
                    const distance=Math.sqrt(best);if(distance<8||distance>30)continue;
                    // Skip ties between differently colored boundaries and content edges.
                    if(boundary.some(p=>Math.hypot(p.x-x,p.y-y)<distance+5&&p.c.some((v,i)=>i<3&&Math.abs(v-nearest.c[i])>80)))continue;
                    const value=pixel(actual,x,y);outsideSamples++;
                    const expected=nearest.c.slice(0,3),on=expected.map((v,i)=>v>180?i:-1).filter(i=>i>=0),off=expected.map((v,i)=>v<40?i:-1).filter(i=>i>=0);
                    if(value[3]<3||on.some(i=>value[i]<60)||off.some(i=>value[i]>100))colorErrors++;
                }
                return{interiorSamples,interiorLeaks,outsideSamples,colorErrors};
            }
            window.contourTest={canvas,pixels,pixel,alphaSum,difference,diagnostics,fixture,transformedFixture,defaults};
            window.contourTest.run=function(forceCanvas2D){
                const output=canvas(),engine=ContourHalo.create(output,{forceCanvas2D,forceWebGL:!forceCanvas2D});
                const draw=(f,settings={},key='rectangle')=>{engine.render(f.frame,f.draw,key,{...defaults,...settings});return pixels(output);};
                const split=fixture(),base=draw(split),initial=diagnostics(engine);
                const result={backend:initial.backend,left:pixel(base,70,100),right:pixel(base,250,100),corner:pixel(base,80,50),
                    interior:pixel(base,130,100),beyond:pixel(base,20,100)};
                const green=draw(fixture('center-green')),magenta=draw(fixture('center-magenta'));
                result.centerDifference=difference(green,magenta);
                result.alphaBackgroundDifference=difference(draw(fixture('transparent')),base);
                const mirrored=draw(fixture('mirror'));result.mirrorLeft=pixel(mirrored,70,100);result.mirrorRight=pixel(mirrored,250,100);
                const short=draw(split,{ambilightEdgeReach:20}),long=draw(split,{ambilightEdgeReach:70});
                result.reach={short:pixel(short,45,100),long:pixel(long,45,100)};
                const shallow=draw(split,{ambilightEdgeFade:0}),steep=draw(split,{ambilightEdgeFade:100});
                result.fade={shallow:pixel(shallow,50,100),steep:pixel(steep,50,100)};
                const narrowBand=draw(fixture('center-green'),{ambilightEdgeDepth:1}),wideBand=draw(fixture('center-green'),{ambilightEdgeDepth:20});
                result.depth={narrow:pixel(narrowBand,70,100),wide:pixel(wideBand,70,100)};
                const unmixed=draw(split,{ambilightEdgeMix:0}),mixed=draw(split,{ambilightEdgeMix:30});
                result.mix={none:pixel(unmixed,150,40),wide:pixel(mixed,150,40)};
                result.thinGap=pixel(draw(fixture('thin-gap'),{ambilightEdgeDepth:20},'thin-gap'),70,100);
                const offCanvas=draw(fixture('left'),{screenX:300,screenWidth:160},'off-canvas');
                result.offCanvas={edge:pixel(offCanvas,290,100),far:pixel(offCanvas,140,100)};
                result.zeroIntensity=alphaSum(draw(split,{ambilightIntensity:0}));
                result.fullCutoff=alphaSum(draw(split,{ambilightCutoff:100}));
                const complex=transformedFixture();result.transforms=geometryStats(complex,draw(complex,{},'transformed-two-sources'));
                draw(split,{},'cache');const cached=diagnostics(engine);draw(split,{},'cache');result.cached={before:cached,after:diagnostics(engine)};
                const beforeMove=draw(split,{},'paused');const afterMove=draw(split,{screenX:20,screenWidth:260},'paused');
                result.pausedPlacementDifference=difference(beforeMove,afterMove);
                engine.clear();result.cleared=alphaSum(pixels(output));engine.dispose();result.disposed=diagnostics(engine);
                return result;
            };
            window.contourTest.benchmark=function(forceCanvas2D){
                const output=canvas(800,600),engine=ContourHalo.create(output,{forceCanvas2D,forceWebGL:!forceCanvas2D}),f=fixture('split',800,600);
                let gl=null;const dispatch=[],completion=[],finish=[],geometry=[];
                const settings={...defaults,ambilightEdgeMix:2};
                const complete=()=>{if(!forceCanvas2D&&gl&&!gl.isContextLost())gl.finish();};
                for(let i=0;i<8;i++){engine.render(f.frame,f.draw,'warm',settings);if(!forceCanvas2D)gl=window.contourContexts.at(-1);complete();}
                const before=diagnostics(engine);
                for(let i=0;i<40;i++){
                    // Invalidate the source canvas as a fresh decoded frame would. This
                    // pixel is outside the silhouette and does not alter expected light.
                    const producer=f.frame.getContext('2d');producer.fillStyle=i%2?'#010101':'#000';producer.fillRect(0,0,1,1);
                    complete();const start=performance.now();engine.render(f.frame,f.draw,'warm',settings);const submitted=performance.now();complete();const end=performance.now();
                    dispatch.push(submitted-start);finish.push(end-submitted);completion.push(end-start);
                }
                for(let i=0;i<12;i++){
                    complete();const start=performance.now();engine.render(f.frame,f.draw,'geometry-'+i,{...settings,screenX:i%2});complete();geometry.push(performance.now()-start);
                }
                const percentile=(values,p)=>[...values].sort((a,b)=>a-b)[Math.min(values.length-1,Math.ceil(values.length*p)-1)];
                const stats=values=>({p50:percentile(values,.5),p95:percentile(values,.95),max:Math.max(...values)});
                const after=diagnostics(engine);
                const debug=gl?.getExtension('WEBGL_debug_renderer_info');
                const result={backend:after.backend,forceWebGL:!forceCanvas2D,output:'800x600',samples:40,dispatchMs:stats(dispatch),gpuFinishWaitMs:stats(finish),completedMs:stats(completion),
                    geometryRebuildMs:stats(geometry),over33ms:completion.filter(v=>v>1000/30).length,estimatedBytes:after.estimatedBytes,
                    bytesBefore:before.estimatedBytes,geometryBuilds:after.geometryBuilds-before.geometryBuilds,
                    renderer:gl?(debug?gl.getParameter(debug.UNMASKED_RENDERER_WEBGL):gl.getParameter(gl.RENDERER)):'Canvas2D'};
                engine.dispose();result.disposed=diagnostics(engine);return result;
            };
            window.contourTest.parity=function(width,height){
                const f=fixture('split',width,height),outputs=[canvas(width,height),canvas(width,height)];
                const engines=[ContourHalo.create(outputs[0],{forceWebGL:true}),ContourHalo.create(outputs[1],{forceCanvas2D:true})];
                try{
                    for(const engine of engines)engine.render(f.frame,f.draw,'parity',{...defaults,ambilightEdgeMix:2});
                    const a=pixels(outputs[0]),b=pixels(outputs[1]);let outliers=0;
                    for(let i=0;i<a.length;i+=4)if([0,1,2,3].some(c=>Math.abs(a[i+c]-b[i+c])>50))outliers++;
                    return{...difference(a,b),outlierFraction:outliers/(width*height)};
                }finally{for(const engine of engines)engine.dispose();}
            };
        });
        const hqGaps=await page.evaluate(()=>{
            const h=window.contourTest,results=[];
            for(const forceCanvas2D of [false,true]){
                const f=h.fixture('thin-gap',800,600),output=h.canvas(800,600),engine=ContourHalo.create(output,{forceCanvas2D,forceWebGL:!forceCanvas2D});
                try{
                    engine.render(f.frame,f.draw,'hq-thin-gap',{...h.defaults,ambilightEdgeDepth:20});
                    const data=h.pixels(output),samples=[[175,300],[221,299],[222,300],[223,301],[224,300],[224,178],[223,179]];
                    results.push({backend:h.diagnostics(engine).backend,samples:samples.map(([x,y])=>({x,y,rgba:Array.from(data.slice((y*800+x)*4,(y*800+x)*4+4))}))});
                }finally{engine.dispose();}
            }
            return results;
        });
        if(options.hqGapOnly)console.log('HQGAP samples: '+JSON.stringify(hqGaps));
        for(const result of hqGaps){
            for(const p of result.samples)assert.ok(p.rgba[0]>170&&p.rgba[2]<45&&p.rgba[3]>5,
                `${result.backend}: HQ subpixel (${p.x},${p.y}) must not sample a blue source across a one-pixel gap: ${p.rgba}`);
            console.log(`PASS: contour ${result.backend} HQ thin-source/gap regression, ${result.samples.length} subcell and corner samples.`);
        }
        if(options.hqGapOnly){assert.deepEqual(errors,[]);return [];}
        for(const forceCanvas2D of [false,true]){
            const r=await page.evaluate(force=>window.contourTest.run(force),forceCanvas2D);
            assert.ok(['webgl','canvas2d'].includes(r.backend),'A declared rendering backend is used');
            if(forceCanvas2D)assert.equal(r.backend,'canvas2d','The explicit CPU fallback is exercised');
            assert.ok(r.left[0]>180&&r.left[2]<60&&r.left[3]>5,`${r.backend}: left border remains red ${r.left}`);
            assert.ok(r.right[2]>180&&r.right[0]<60&&r.right[3]>5,`${r.backend}: right border remains blue ${r.right}`);
            assert.ok(r.corner[0]>100&&r.corner[3]>5,`${r.backend}: diagonal corners receive the nearby edge color`);
            assert.equal(r.interior[3],0,`${r.backend}: the actual source interior stays transparent`);
            assert.equal(r.beyond[3],0,`${r.backend}: light stops beyond configured reach`);
            assert.ok(r.centerDifference.mean<.05,`${r.backend}: image center must not influence edge color ${JSON.stringify(r.centerDifference)}`);
            assert.ok(r.alphaBackgroundDifference.mean<.05,`${r.backend}: opaque black background and transparent background have the same geometry`);
            assert.ok(r.mirrorLeft[2]>180&&r.mirrorRight[0]>180,`${r.backend}: mirroring pixels swaps local halo colors without moving the mask`);
            assert.equal(r.reach.short[3],0,`${r.backend}: short reach is bounded`);
            assert.ok(r.reach.long[3]>5,`${r.backend}: editing reach redraws the same paused frame`);
            assert.ok(r.fade.shallow[3]>r.fade.steep[3],`${r.backend}: increased fade lowers distant brightness`);
            assert.ok(r.depth.wide[1]>r.depth.narrow[1]+60,`${r.backend}: a deeper edge band samples farther inside the same paused picture`);
            assert.ok(r.mix.wide[2]>r.mix.none[2]+30&&r.mix.wide[0]<r.mix.none[0]-30,`${r.backend}: lateral mixing blends adjacent edge colors ${JSON.stringify(r.mix)}`);
            assert.ok(r.thinGap[0]>180&&r.thinGap[2]<40&&r.thinGap[3]>5,`${r.backend}: depth sampling must stop at a one-pixel gap after a three-pixel source ${r.thinGap}`);
            assert.ok(r.offCanvas.edge[0]>180&&r.offCanvas.edge[3]>5&&r.offCanvas.far[3]===0,`${r.backend}: standalone placement may extend beyond the canvas without being silently recentered`);
            assert.equal(r.zeroIntensity,0,`${r.backend}: zero intensity clears all emitted light`);
            assert.equal(r.fullCutoff,0,`${r.backend}: full cutoff clears all emitted light`);
            assert.ok(r.transforms.interiorSamples>3000&&r.transforms.interiorLeaks===0,`${r.backend}: transformed crop interiors do not acquire halo ${JSON.stringify(r.transforms)}`);
            assert.ok(r.transforms.outsideSamples>60&&r.transforms.colorErrors/r.transforms.outsideSamples<.08,`${r.backend}: independent transformed multi-source edge-color oracle ${JSON.stringify(r.transforms)}`);
            assert.equal(r.cached.before.geometryBuilds,r.cached.after.geometryBuilds,`${r.backend}: unchanged geometry reuses its mask/distance map`);
            assert.ok(r.pausedPlacementDifference.mean>.5,`${r.backend}: placement edits redraw without a new frame or caller geometry-key change`);
            assert.equal(r.cleared,0,`${r.backend}: clear releases visible pixels`);
            assert.equal(r.disposed.disposed,true,`${r.backend}: disposal is recorded`);
            assert.equal(r.disposed.estimatedBytes,0,`${r.backend}: disposal releases owned buffers`);
            console.log(`PASS: contour ${r.backend}; colors, center exclusion, alpha, corners/reach/fade, independent transforms ${r.transforms.colorErrors}/${r.transforms.outsideSamples}, paused edits and disposal.`);
        }
        for(const[width,height]of[[320,200],[800,600]]){
            const result=await page.evaluate(([w,h])=>window.contourTest.parity(w,h),[width,height]);
            assert.ok(result.mean<5&&result.outlierFraction<.025,`GPU/CPU ${width}x${height} parity ${JSON.stringify(result)}`);
            console.log(`PASS: contour GPU/CPU ${width}x${height} parity ${JSON.stringify(result)}.`);
        }
        const automatic=await page.evaluate(()=>{
            const h=window.contourTest,f=h.fixture(),engine=ContourHalo.create(h.canvas());
            engine.render(f.frame,f.draw,'auto',h.defaults);const result=h.diagnostics(engine);engine.dispose();return result;
        });
        if(automatic.fallbackReason==='software-renderer')assert.equal(automatic.backend,'canvas2d','A known software WebGL renderer automatically uses the faster CPU path');
        console.log(`PASS: automatic contour backend ${automatic.backend}; fallback reason ${automatic.fallbackReason||'none'}.`);
        const loss=await page.evaluate(()=>{
            const h=window.contourTest,f=h.fixture(),output=h.canvas();
            const engine=ContourHalo.create(output,{forceWebGL:true});engine.render(f.frame,f.draw,'loss',h.defaults);
            window.contourLoss={engine,output,f,gl:window.contourContexts.at(-1)};
            const before=h.diagnostics(engine),extension=window.contourLoss.gl?.getExtension('WEBGL_lose_context');
            if(before.backend==='webgl'&&extension){extension.loseContext();return{supported:true};}
            return{supported:false,backend:before.backend};
        });
        if(loss.supported){
            await page.waitForFunction(()=>window.contourLoss.gl.isContextLost());
            const recovered=await page.evaluate(()=>{
                const {engine,output,f}=window.contourLoss,h=window.contourTest;
                engine.render(f.frame,f.draw,'loss',h.defaults);
                const result={diagnostics:h.diagnostics(engine),left:h.pixel(h.pixels(output),70,100)};engine.dispose();return result;
            });
            assert.equal(recovered.diagnostics.backend,'canvas2d','A lost WebGL context switches to the CPU renderer');
            assert.ok(recovered.diagnostics.fallbackCount>0,'The runtime fallback is recorded');
            assert.ok(recovered.left[0]>180&&recovered.left[3]>5,'Context loss preserves a visible halo from the existing paused frame');
            console.log('PASS: contour WebGL context loss falls back without a blank frame.');
        }else{
            await page.evaluate(()=>window.contourLoss.engine.dispose());
            console.log(`INFO: WebGL loss test unavailable because Chromium selected ${loss.backend}; forced CPU tests still passed.`);
        }
        const benchmarks=[];
        for(const forceCanvas2D of [false,true]){
            const result=await page.evaluate(force=>window.contourTest.benchmark(force),forceCanvas2D);
            assert.ok(result.completedMs.p95<2000,'Contour smoke performance does not hang the browser');
            assert.ok(result.estimatedBytes<64*1024*1024,'HQ halo working buffers remain bounded below 64 MiB');
            assert.equal(result.estimatedBytes,result.bytesBefore,'Repeated frames and geometry edits do not accumulate buffers');
            assert.equal(result.disposed.estimatedBytes,0,'Benchmark disposal releases its buffers');
            benchmarks.push(result);console.log(`BENCHMARK: contour ${JSON.stringify(result)}`);
        }
        assert.deepEqual(errors,[],'No contour browser runtime errors');
        const retained=await page.evaluate(()=>window.contourResources.filter(({context,kind,resource})=>resource&&!context.isContextLost()&&context['is'+kind](resource)).map(({kind})=>kind));
        assert.deepEqual(retained,[],'Disposal actually deletes GPU textures/programs/buffers/shaders, beyond resetting diagnostics');
        console.log('INFO: benchmark p50/p95 are local Chromium measurements, not a hardware guarantee; dispatch and explicit GPU finish wait are reported separately. Zero readings are below the browser timer resolution, not proof of zero GPU cost. CPU and forced WebGL use the same scene/settings.');
        return benchmarks;
    }finally{await page.close();}
};

if(require.main===module){
    const {chromium}=require('playwright');
    const launch=process.argv.includes('--hardware')?{headless:true,channel:'chromium',args:['--use-angle=d3d11']}:{headless:true};
    (async()=>{const browser=await chromium.launch(launch);try{await module.exports(browser,{hqGapOnly:process.argv.includes('--hq-gap-only')});}finally{await browser.close();}})()
        .catch(error=>{console.error(error);process.exitCode=1;});
}
