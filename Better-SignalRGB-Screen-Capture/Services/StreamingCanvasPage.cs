namespace Better_SignalRGB_Screen_Capture.Services;

internal static class StreamingCanvasPage
{
    // A clean output surface for browser capture in OpenRGB and other consumers.
    // Use the production composite so geometry, layers and updates have one owner.
    public const string Html = """
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Better SignalRGB Output</title>
        <style>html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#000}#frame{display:block;width:100%;height:100%;object-fit:fill;border:0;opacity:0}</style>
        </head><body><canvas id="frame" width="320" height="200"></canvas><script>
        const frame=document.getElementById('frame'),ctx=frame.getContext('2d',{alpha:false});
        const maxFrame=8*1024*1024,decoder=new TextDecoder();
        let retry,request,generation=0,active=true,pending,decoding=false;
        function clear(){pending=null;ctx.fillStyle='#000';ctx.fillRect(0,0,frame.width,frame.height);frame.style.opacity='1';}
        async function drawLatest(){
          if(decoding||!pending)return;decoding=true;const item=pending;pending=null;let bitmap;
          try{bitmap=await createImageBitmap(new Blob([item.bytes],{type:'image/jpeg'}));
            if(active&&item.generation===generation&&bitmap.width*bitmap.height<=16*1024*1024){
              if(frame.width!==bitmap.width||frame.height!==bitmap.height){frame.width=bitmap.width;frame.height=bitmap.height;}
              ctx.drawImage(bitmap,0,0);frame.style.opacity='1';}}
          catch{}finally{if(bitmap)bitmap.close();decoding=false;if(pending)drawLatest();}
        }
        async function connect(){
          clearTimeout(retry);if(!active)return;const version=++generation;
          request=new AbortController();let reader;
          try{
            const response=await fetch('/stream',{cache:'no-store',signal:request.signal});
            if(!response.ok||!response.body)throw new Error('Unavailable');
            const mime=response.headers.get('content-type')||'';
            const match=/boundary="?([^;"\s]+)/i.exec(mime);
            if(!mime.toLowerCase().startsWith('multipart/x-mixed-replace')||!match)throw new Error('Invalid stream');
            reader=response.body.getReader();let buffer=new Uint8Array(0),length=null;
            while(active&&version===generation){
              const part=await reader.read();if(part.done)break;
              if(buffer.length+part.value.length>maxFrame+65536)throw new Error('Oversized frame');
              const joined=new Uint8Array(buffer.length+part.value.length);joined.set(buffer);joined.set(part.value,buffer.length);buffer=joined;
              while(true){
                if(length===null){
                  let end=-1;for(let i=0;i+3<buffer.length&&i<1024;i++)if(buffer[i]===13&&buffer[i+1]===10&&buffer[i+2]===13&&buffer[i+3]===10){end=i;break;}
                  if(end<0){if(buffer.length>1024)throw new Error('Invalid header');break;}
                  const header=decoder.decode(buffer.subarray(0,end)),size=/Content-Length:\s*(\d+)/i.exec(header);
                  if(!header.trimStart().startsWith('--'+match[1]+'\r\n')||!size||!/Content-Type:\s*image\/jpeg/i.test(header))throw new Error('Invalid frame');
                  length=Number(size[1]);if(!Number.isSafeInteger(length)||length<1||length>maxFrame)throw new Error('Invalid size');
                  buffer=buffer.subarray(end+4);
                }
                if(buffer.length<length)break;
                pending={bytes:buffer.slice(0,length),generation:version};drawLatest();buffer=buffer.subarray(length);length=null;
              }
            }
          }catch{}finally{
            if(reader)try{await reader.cancel();}catch{}
            if(version===generation){generation++;clear();if(active)retry=setTimeout(connect,1000);}
          }
        }
        addEventListener('pagehide',()=>{active=false;generation++;pending=null;clearTimeout(retry);request?.abort();});
        addEventListener('pageshow',event=>{if(event.persisted){active=true;connect();}});
        connect();</script></body></html>
        """;
}
