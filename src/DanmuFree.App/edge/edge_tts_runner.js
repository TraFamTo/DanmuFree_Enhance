// Edge TTS（Read Aloud）合成 runner：由 EdgeNodeTtsClient 以 node 子进程调用。
// 背景（2026-10）：MS 边缘层按 TLS 指纹拦客户端——.NET SChannel 连 voice list 可以、
// 合成路径一律 403；OpenSSL（node）+ 新域名 + 旧版 v1 token 实测出音频。故合成走本脚本。
//
// 协议：wss://api.msedgeservices.com/tts/cognitiveservices/websocket/v1
//       ?Ocp-Apim-Subscription-Key=<TrustedClientToken>   ← 老常量 token 换个参数名
//       &Sec-MS-GEC=<v1 token：SHA256("{300s取整的ticks}{token}")大写hex>
//       &Sec-MS-GEC-Version=1-144.0.3719.82&ConnectionId=<32hex>
//   headers: User-Agent(Mac/Chrome144) + origin(chrome-extension)。UA 必须带（实测缺 UA 被拒）。
//   两文本帧 speech.config + ssml；二进制帧剥 2 字节大端头长 + 头，把 MP3 原样写 stdout；
//   文本帧 Path:turn.end 结束 exit 0。任何失败 stderr + exit 1（.NET 侧转异常落 log.txt）。
//
// 用法：node edge_tts_runner.js --voice zh-CN-XiaoxiaoNeural --rate "+0%" [--timeout 30]
//       stdin：要合成的文本（utf8）。stdout：MP3 裸流。
// 依赖：ws（edge/node_modules/ws，纯 JS 零原生依赖，随包分发；node 内置 WebSocket 不能自定义头）。

'use strict'
const { createHash, randomUUID } = require('node:crypto')
const WebSocket = require('ws')

const TRUSTED_TOKEN = '6A5AA1D4EAFF4E9FB37E23D68491D6F4'
const WS_BASE = 'wss://api.msedgeservices.com/tts/cognitiveservices/websocket/v1'
const GEC_VERSION = '1-144.0.3719.82'
const UA = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/144.0.0.0 Safari/537.36 Edg/144.0.0.0'
const ORIGIN = 'chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold'

function arg(name, fallback) {
    const i = process.argv.indexOf('--' + name)
    return i > 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback
}

// Sec-MS-GEC v1：unix 秒 + Windows 纪元，向下取整 300 秒，×1e7 ticks，拼 token，SHA-256 大写 hex
function secMsGec() {
    const ticks = BigInt(Math.floor(Date.now() / 1000 + 11644473600)) * 10000000n
    const rounded = ticks - (ticks % 3000000000n)
    return createHash('sha256').update(`${rounded}${TRUSTED_TOKEN}`, 'ascii').digest('hex').toUpperCase()
}

function escapeXml(s) {
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
        .replace(/'/g, '&apos;').replace(/"/g, '&quot;')
}

function fail(msg) {
    process.stderr.write(String(msg) + '\n')
    process.exit(1)
}

function readStdin() {
    return new Promise((resolve, reject) => {
        const chunks = []
        process.stdin.on('data', (c) => chunks.push(c))
        process.stdin.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')))
        process.stdin.on('error', reject)
    })
}

async function main() {
    const voice = arg('voice', 'zh-CN-XiaoxiaoNeural')
    const rate = arg('rate', '+0%')
    const timeoutMs = parseInt(arg('timeout', '30000'), 10)
    const text = (await readStdin()).trim()
    if (!text) fail('empty text')

    const cid = randomUUID().replaceAll('-', '')
    const url = `${WS_BASE}?Ocp-Apim-Subscription-Key=${TRUSTED_TOKEN}` +
        `&Sec-MS-GEC=${secMsGec()}&Sec-MS-GEC-Version=${GEC_VERSION}&ConnectionId=${cid}`

    const ws = new WebSocket(url, { headers: { 'User-Agent': UA }, origin: ORIGIN })
    const killer = setTimeout(() => { try { ws.terminate() } catch {} fail(`timeout after ${timeoutMs}ms`) }, timeoutMs)

    ws.on('error', (e) => { clearTimeout(killer); fail(`ws error: ${e.message}`) })
    ws.on('close', (code) => { clearTimeout(killer); fail(`ws closed before turn.end (code ${code})`) })
    ws.on('message', (data, isBinary) => {
        if (isBinary) {
            if (data.length < 2) return
            const headerLen = (data[0] << 8) | data[1]
            if (2 + headerLen > data.length) return
            const header = data.slice(2, 2 + headerLen).toString('utf8')
            if (header.includes('Path:audio') && !header.includes('Path:audio.metadata'))
                process.stdout.write(data.slice(2 + headerLen)) // MP3 原样出 stdout
            return
        }
        if (data.toString('utf8').includes('Path:turn.end')) {
            clearTimeout(killer)
            try { ws.close() } catch { }
            process.exit(0) // 丢 frame end 事件直接退，防止 ws 'close' 处理器误报
        }
    })

    ws.on('open', () => {
        const ts = new Date().toUTCString()
        ws.send(`X-RequestId:${cid}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n` +
            '{"context":{"synthesis":{"audio":{"metadataoptions":{"sentenceBoundaryEnabled":"false","wordBoundaryEnabled":"false"},"outputFormat":"audio-24khz-48kbitrate-mono-mp3"}}}}')
        const ssml = `<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='zh-CN'>` +
            `<voice name='${escapeXml(voice)}'><prosody rate='${escapeXml(rate)}' pitch='+0Hz' volume='+0%'>${escapeXml(text)}</prosody></voice></speak>`
        ws.send(`X-RequestId:${cid}\r\nContent-Type:application/ssml+xml\r\nX-Timestamp:${ts}\r\nPath:ssml\r\n\r\n${ssml}`)
    })
}

main().catch((e) => fail(e && e.message ? e.message : e))
