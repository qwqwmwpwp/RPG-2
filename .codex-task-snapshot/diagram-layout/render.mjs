import fs from 'node:fs/promises';
import puppeteer from 'puppeteer';
const browser = await puppeteer.launch({executablePath:'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',headless:true});
try {
const page = await browser.newPage();
await page.setViewport({width:1200,height:1200});
await page.setContent('<html><head><meta charset="utf-8"></head><body style="margin:0;background:white"></body></html>');
await page.addScriptTag({path:'node_modules/mermaid/dist/mermaid.js'});
const source = await fs.readFile('framework.mmd','utf8');
const result = await page.evaluate(async (source) => {mermaid.initialize({startOnLoad:false,securityLevel:'loose'});const {svg} = await mermaid.render('framework',source);document.body.innerHTML=svg;const element=document.querySelector('svg');return {svg,viewBox:element.getAttribute('viewBox'),width:element.getBoundingClientRect().width,height:element.getBoundingClientRect().height};},source);
await fs.writeFile('framework.svg',result.svg);
console.log(JSON.stringify({viewBox:result.viewBox,width:result.width,height:result.height,ratio:result.width/result.height}));
await (await page.$('svg')).screenshot({path:'framework.png'});
} finally {await browser.close();}