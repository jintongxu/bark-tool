const fs = require('node:fs');
const path = require('node:path');
const sizes = [16, 32, 48, 256];
function distance(x, y, ax, ay, bx, by) {
  const t = Math.max(0, Math.min(1, ((x-ax)*(bx-ax)+(y-ay)*(by-ay))/((bx-ax)**2+(by-ay)**2)));
  return Math.hypot(x-ax-t*(bx-ax), y-ay-t*(by-ay));
}
const images = sizes.map(n => {
  const maskStride = Math.ceil(n/32)*4;
  const data = Buffer.alloc(40+n*n*4+maskStride*n);
  data.writeUInt32LE(40,0); data.writeInt32LE(n,4); data.writeInt32LE(n*2,8);
  data.writeUInt16LE(1,12); data.writeUInt16LE(32,14); data.writeUInt32LE(n*n*4,20);
  for(let y=0;y<n;y++)for(let x=0;x<n;x++) {
    const px=(x+.5)/n, py=(y+.5)/n;
    const circle = Math.hypot(px-.5,py-.5)<.47;
    const arrow = Math.min(distance(px,py,.29,.71,.70,.30),distance(px,py,.39,.30,.70,.30),distance(px,py,.70,.30,.70,.61)) < .045;
    const color = arrow ? [101,219,196] : [38,54,80];
    const i=40+((n-1-y)*n+x)*4;
    data[i]=color[2];data[i+1]=color[1];data[i+2]=color[0];data[i+3]=circle?255:0;
    if(!circle)data[40+n*n*4+(n-1-y)*maskStride+(x>>3)] |= 128>>(x&7);
  }
  return data;
});
const header=Buffer.alloc(6+16*sizes.length); header.writeUInt16LE(1,2);header.writeUInt16LE(sizes.length,4);
let offset=header.length;
images.forEach((image,i)=>{const o=6+16*i;header[o]=sizes[i]===256?0:sizes[i];header[o+1]=header[o];header.writeUInt16LE(1,o+4);header.writeUInt16LE(32,o+6);header.writeUInt32LE(image.length,o+8);header.writeUInt32LE(offset,o+12);offset+=image.length;});
const target=path.join(__dirname,'../src/BarkTool.Desktop/Resources/bark.ico');
fs.mkdirSync(path.dirname(target),{recursive:true});fs.writeFileSync(target,Buffer.concat([header,...images]));
console.log('Generated bark.ico (16/32/48/256 px)');
