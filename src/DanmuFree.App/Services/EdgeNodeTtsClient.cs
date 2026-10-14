using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using DanmuFree.Core.Tts;

namespace DanmuFree.App.Services;

/// <summary>
/// Edge 在线 TTS 的 **node sidecar 实现**（实现 Core 的 <see cref="ITtsClient"/>，替代直连的
/// Core.EdgeTtsClient）。背景（2026-10 排查）：MS 边缘层（Azure Front Door / ceto）开始按
/// **TLS 指纹**拦合成请求——.NET SChannel 连 voice list 正常、连新旧两个合成域名一律 403
/// （应用层参数对齐 Node 成功实现也 403，实锤传输层）；node(OpenSSL) + 新域名
/// api.msedgeservices.com + 旧版 v1 token 实测出音频。故合成走 node 子进程
/// （edge/edge_tts_runner.js + edge/node_modules/ws，随包分发，模式同抖音签名 sidecar）。
/// runner 把 MP3 写 stdout，本类读入后按 ITtsClient 契约返回流（TtsSpeaker 嗅探解码）。
/// </summary>
public sealed class EdgeNodeTtsClient : ITtsClient
{
    // 单文件 publish 下 AppContext.BaseDirectory 是解压临时目录，用 Environment.ProcessPath 定位真实 exe 目录
    //（同 DouyinSigner）。node 优先分发的 node/node.exe，回落 PATH（开发机）。
    private static readonly string AppDir =
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!;
    private static readonly string EdgeDir = Path.Combine(AppDir, "edge");
    private static readonly string RunnerPath = Path.Combine(EdgeDir, "edge_tts_runner.js");
    private static readonly string BundledNode = Path.Combine(AppDir, "node", "node.exe");
    private static string NodeExe => File.Exists(BundledNode) ? BundledNode : "node";

    private readonly string _voice;

    public EdgeNodeTtsClient(string? voice = null) =>
        _voice = string.IsNullOrWhiteSpace(voice) ? EdgeTtsClient.DefaultVoice : voice!;

    public async Task<Stream> SynthesizeAsync(string text, TtsOptions opts, CancellationToken ct)
    {
        if (!File.Exists(RunnerPath))
            throw new HttpRequestException($"找不到 Edge 合成脚本：{RunnerPath}（确认 edge/ 已随程序分发）");

        var psi = new ProcessStartInfo
        {
            FileName = NodeExe,
            WorkingDirectory = EdgeDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(RunnerPath);
        psi.ArgumentList.Add("--voice");
        psi.ArgumentList.Add(_voice);
        psi.ArgumentList.Add("--rate");
        psi.ArgumentList.Add(EdgeTtsClient.SpeedToRate(opts.Speed));

        using var p = Process.Start(psi)
            ?? throw new HttpRequestException("node 启动失败（分发自带 node/node.exe，开发机需 PATH 有 node）");

        // 整体超时：朗读泵串行播放，一条卡死=后续全不念（对齐 Core.EdgeTtsClient 的 20s + 余量）。
        try
        {
            await p.StandardInput.WriteAsync(text.AsMemory(), ct);
            p.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            var audio = new MemoryStream();
            var stdoutTask = p.StandardOutput.BaseStream.CopyToAsync(audio, timeoutCts.Token);
            var stderrTask = p.StandardError.ReadToEndAsync(timeoutCts.Token);
            await p.WaitForExitAsync(timeoutCts.Token);
            await stdoutTask;
            var err = await stderrTask;

            if (p.ExitCode != 0 || audio.Length == 0)
                throw new HttpRequestException($"Edge TTS 合成失败（node exit={p.ExitCode}）：{err.Trim()}");
            return new MemoryStream(audio.ToArray(), writable: false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p.Kill(); } catch { }
            throw new HttpRequestException("Edge TTS 超时（30s 未完成，可能网络不通或端点再变更）。");
        }
        catch
        {
            if (!p.HasExited) { try { p.Kill(); } catch { } }
            throw;
        }
    }
}
