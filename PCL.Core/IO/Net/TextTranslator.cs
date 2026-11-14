using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.IO.Net;

/// <summary>
///     通用机器翻译：先走必应翻译中国站（cn.bing.com，国内可直连、不需要密钥），失败时走 Google 翻译。<br/>
///     下载模组页的“翻译简介”查的是 MCIM 预先翻好的模组简介（按模组 ID），不能翻译任意文本，
///     所以更新日志这类内容用这里的通用翻译。长文本按行分段（每段不超过约 4500 字符，必应单次上限约 5000），并发翻译后按原顺序拼回。
/// </summary>
public sealed partial class TextTranslator(Func<HttpClient> client)
{
    public static TextTranslator Default { get; } = new(() => NetworkService.GetClient());

    /// <summary>简体中文的目标语言代码。</summary>
    public const string SimplifiedChinese = "zh-Hans";

    /// <summary>繁体中文的目标语言代码。</summary>
    public const string TraditionalChinese = "zh-Hant";

    private const int ChunkChars = 4500;
    private const int Concurrency = 4;
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";

    private readonly SemaphoreSlim _bingLock = new(1, 1);
    private BingSession? _bing;

    /// <summary>必应翻译网页给出的会话参数，有效期由页面给出（通常一小时）。</summary>
    public sealed record BingSession(string Ig, string Iid, string Key, string Token, DateTimeOffset Expires);

    /// <summary>
    ///     翻译文本，保留换行（Markdown 的 <c>###</c>、<c>-</c> 等行首标记也会保留）。
    /// </summary>
    /// <param name="text">原文。</param>
    /// <param name="target">目标语言：<see cref="SimplifiedChinese" /> 或 <see cref="TraditionalChinese" /> 等必应语言代码。</param>
    /// <param name="progress">已完成段数与总段数。</param>
    /// <param name="token">取消令牌。</param>
    public async Task<string> TranslateAsync(string text, string target, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken token = default)
    {
        var chunks = Split(text, ChunkChars);
        var results = new string[chunks.Count];
        var done = 0;
        using var gate = new SemaphoreSlim(Concurrency);
        var tasks = chunks.Select(async (chunk, index) =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                results[index] = chunk.Trim().Length == 0
                    ? chunk
                    : await _TranslateChunkAsync(chunk, target, token).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }

            progress?.Report((Interlocked.Increment(ref done), chunks.Count));
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return string.Join("\n", results);
    }

    /// <summary>
    ///     按行把文本切成不超过 <paramref name="maxChars" /> 个字符的段；超长的单行再按字符切开。
    /// </summary>
    public static List<string> Split(string text, int maxChars = ChunkChars)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length > maxChars)
            {
                if (current.Length > 0)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                }

                for (var start = 0; start < line.Length; start += maxChars)
                    chunks.Add(line.Substring(start, Math.Min(maxChars, line.Length - start)));
                continue;
            }

            if (current.Length > 0 && current.Length + 1 + line.Length > maxChars)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0) current.Append('\n');
            current.Append(line);
        }

        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    private async Task<string> _TranslateChunkAsync(string chunk, string target, CancellationToken token)
    {
        Exception? bingError = null;
        // 必应的会话参数可能过期，失败一次后刷新再试
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await _BingAsync(chunk, target, attempt > 0, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                bingError = ex;
            }
        }

        try
        {
            return await _GoogleAsync(chunk, target, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            throw new InvalidOperationException($"必应翻译：{bingError?.Message}；Google 翻译：{ex.Message}", ex);
        }
    }

    #region 必应翻译

    [GeneratedRegex("IG:\"([A-F0-9]+)\"")]
    private static partial Regex BingIg();

    [GeneratedRegex("data-iid=\"(translator\\.\\d+)\"")]
    private static partial Regex BingIid();

    [GeneratedRegex("params_AbusePreventionHelper\\s*=\\s*\\[(\\d+),\"([^\"]+)\",(\\d+)")]
    private static partial Regex BingHelper();

    /// <summary>从 <c>https://cn.bing.com/translator</c> 页面取会话参数；页面格式不符时返回 null。</summary>
    public static BingSession? ParseBingPage(string html, DateTimeOffset now)
    {
        var ig = BingIg().Match(html);
        var helper = BingHelper().Match(html);
        if (!ig.Success || !helper.Success) return null;
        var iid = BingIid().Match(html);
        var lifetime = long.TryParse(helper.Groups[3].Value, out var ms) && ms > 0
            ? TimeSpan.FromMilliseconds(ms)
            : TimeSpan.FromMinutes(30);
        // 提前几分钟换新，避免翻译到一半过期
        var margin = lifetime > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(5) : TimeSpan.Zero;
        return new BingSession(ig.Groups[1].Value, iid.Success ? iid.Groups[1].Value : "translator.5028",
            helper.Groups[1].Value, helper.Groups[2].Value, now + lifetime - margin);
    }

    private async Task<BingSession> _GetBingSessionAsync(bool refresh, CancellationToken token)
    {
        await _bingLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!refresh && _bing is { } cached && cached.Expires > DateTimeOffset.UtcNow) return cached;
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://cn.bing.com/translator");
            request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await client().SendAsync(request, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            _bing = ParseBingPage(html, DateTimeOffset.UtcNow) ?? throw new InvalidDataException("必应翻译页面的格式已变化");
            return _bing;
        }
        finally
        {
            _bingLock.Release();
        }
    }

    private async Task<string> _BingAsync(string text, string target, bool refresh, CancellationToken token)
    {
        var session = await _GetBingSessionAsync(refresh, token).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://cn.bing.com/ttranslatev3?isVertical=1&IG={session.Ig}&IID={session.Iid}");
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["fromLang"] = "auto-detect",
            ["to"] = target,
            ["text"] = text,
            ["token"] = session.Token,
            ["key"] = session.Key
        });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await client().SendAsync(request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ParseBingResult(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
    }

    /// <summary>解析必应的翻译结果；出错时抛出异常。</summary>
    public static string ParseBingResult(string json)
    {
        var node = JsonNode.Parse(json);
        if (node is JsonArray array && array[0]?["translations"]?[0]?["text"]?.GetValue<string>() is { } text)
            return text;
        var status = node?["statusCode"]?.ToString();
        throw new InvalidDataException(status is null ? "必应翻译返回了无法识别的结果" : $"必应翻译返回 {status}");
    }

    #endregion

    #region Google 翻译

    private async Task<string> _GoogleAsync(string text, string target, CancellationToken token)
    {
        var language = target switch
        {
            SimplifiedChinese => "zh-CN",
            TraditionalChinese => "zh-TW",
            _ => target
        };
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={Uri.EscapeDataString(language)}&dt=t");
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["q"] = text });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await client().SendAsync(request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ParseGoogleResult(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
    }

    /// <summary>解析 Google 翻译的结果：第一项是按句切开的译文片段。</summary>
    public static string ParseGoogleResult(string json)
    {
        if (JsonNode.Parse(json) is not JsonArray { Count: > 0 } array || array[0] is not JsonArray parts)
            throw new InvalidDataException("Google 翻译返回了无法识别的结果");
        var builder = new StringBuilder();
        foreach (var part in parts.OfType<JsonArray>())
            if (part.Count > 0 && part[0] is JsonValue value && value.TryGetValue<string>(out var piece))
                builder.Append(piece);
        return builder.ToString();
    }

    #endregion
}
