using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using EntityTagHeaderValue = Microsoft.Net.Http.Headers.EntityTagHeaderValue;

namespace TSCutter.GUI.Web;

internal static class WebUiHttp
{
    private static readonly ConcurrentDictionary<string, EntityTagHeaderValue> AssetTags = new();
    internal static bool UsesExplicitMedia(HttpRequest request) => request.Query["media"] == "explicit";
    internal const WebSocketCloseStatus EditorAlreadyConnected = (WebSocketCloseStatus)4009;
    internal static bool IsSupportedLocale(string? locale) => locale is "en-US" or "zh-CN" or "zh-TW";
    internal static Uri Origin(HttpRequest request) => new(request.Scheme + "://" + request.Host.ToUriComponent());
    internal static bool HasSameOrigin(HttpRequest request) =>
        Uri.TryCreate(request.Headers.Origin, UriKind.Absolute, out var origin) && origin.UserInfo.Length == 0 &&
        Uri.TryCreate(request.Scheme + "://" + request.Host.ToUriComponent(), UriKind.Absolute, out var current) &&
        string.Equals(origin.GetLeftPart(UriPartial.Authority), current.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    internal static string CookieName(HttpRequest request) =>
        (request.IsHttps ? "__Host-TSCutterWeb-" : "TSCutterWeb-") + Origin(request).Port;

    internal static CookieOptions BrowserCookie(HttpRequest request) => new()
    {
        Path = "/",
        HttpOnly = true,
        Secure = request.IsHttps,
        SameSite = SameSiteMode.Strict,
        MaxAge = TimeSpan.FromDays(365)
    };

    internal static void ConfigureListener(KestrelServerOptions options, bool allowLan, int port, bool enableIpv6 = false)
    {
        // IPv6 由宿主显式启用；内部工作进程保留 IPv4 回环地址及随机端口协议。
        if (allowLan && enableIpv6)
            options.ListenAnyIP(port);
        else
            options.Listen(allowLan ? IPAddress.Any : IPAddress.Loopback, port);
    }

    internal static bool IsShareableAddress(IPAddress address) =>
        !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) &&
        (address.AddressFamily == AddressFamily.InterNetwork ||
         address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6LinkLocal &&
         !address.IsIPv6Multicast && !address.IsIPv4MappedToIPv6);

    internal static IPAddress[] GetAddresses(bool allowLan, bool enableIpv6 = false) => allowLan
        ? new[] { IPAddress.Loopback }.Concat(NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses).Select(entry => entry.Address)
            // 链路本地 IPv6 需要设备相关的作用域，浏览器通常无法直接使用，不生成此类链接。
            .Where(address => IsShareableAddress(address) && (enableIpv6 || address.AddressFamily == AddressFamily.InterNetwork)).OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)).Distinct().ToArray()
        : [IPAddress.Loopback];

    internal static IResult ServeAsset(HttpContext context, string asset, string contentType)
    {
        context.Response.Headers.CacheControl = "private, no-cache";
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'none'; script-src 'self'; style-src 'self'; img-src blob:; media-src blob:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream($"TSCutter.GUI.Web.Client.{asset}");
        if (resource is null)
            return Results.NotFound();
        var tag = AssetTags.GetOrAdd(asset, static (_, stream) =>
            new EntityTagHeaderValue($"\"{Convert.ToHexString(SHA256.HashData(stream))}\""), resource);
        resource.Position = 0;
        return Results.Stream(resource, contentType, entityTag: tag);
    }
}
