using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace RedCompute.App.Api;

public static class StreamingProxy
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    public static async Task ForwardAsync(HttpContext ctx, string baseUrl, Dictionary<string, object?> body, Action<string> log)
    {
        var targetUrl = baseUrl.TrimEnd('/') + ctx.Request.Path;
        var json = JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, targetUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        CopyHeaders(ctx.Request, request);

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

        ctx.Response.StatusCode = (int)response.StatusCode;
        ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

        if (response.Content.Headers.ContentLength.HasValue)
            ctx.Response.ContentLength = response.Content.Headers.ContentLength.Value;

        await using var stream = await response.Content.ReadAsStreamAsync(ctx.RequestAborted);
        await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    public static async Task ForwardToPathAsync(HttpContext ctx, string baseUrl, string path, Dictionary<string, object?> body, Action<string> log)
    {
        var targetUrl = baseUrl.TrimEnd('/') + path;
        var json = JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, targetUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        CopyHeaders(ctx.Request, request);

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

        if (!response.IsSuccessStatusCode)
        {
            ctx.Response.StatusCode = (int)response.StatusCode;
            ctx.Response.ContentType = "application/json";
            var errorBody = await response.Content.ReadAsStringAsync(ctx.RequestAborted);
            await ctx.Response.WriteAsync(errorBody, ctx.RequestAborted);
            return;
        }

        ctx.Response.StatusCode = (int)response.StatusCode;
        ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "audio/wav";

        if (response.Content.Headers.ContentLength.HasValue)
            ctx.Response.ContentLength = response.Content.Headers.ContentLength.Value;

        await using var stream = await response.Content.ReadAsStreamAsync(ctx.RequestAborted);
        await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    public static async Task ForwardRawAsync(HttpContext ctx, string baseUrl, string? path, Action<string> log)
    {
        var targetUrl = baseUrl.TrimEnd('/') + "/" + (path ?? "");
        if (ctx.Request.QueryString.HasValue)
            targetUrl += ctx.Request.QueryString.Value;

        using var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), targetUrl);

        if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            request.Content = new StreamContent(ctx.Request.Body);
            if (ctx.Request.ContentType != null)
                request.Content.Headers.TryAddWithoutValidation("Content-Type", ctx.Request.ContentType);
        }

        CopyHeaders(ctx.Request, request);

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

        ctx.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Content.Headers)
        {
            ctx.Response.Headers.TryAdd(header.Key, header.Value.ToArray());
        }
        ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

        await using var stream = await response.Content.ReadAsStreamAsync(ctx.RequestAborted);
        await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    private static void CopyHeaders(HttpRequest source, HttpRequestMessage target)
    {
        foreach (var header in source.Headers)
        {
            if (header.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
                target.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
    }
}
