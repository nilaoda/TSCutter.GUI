using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Utils;

public static class VersionChecker
{
    private const string VersionUrl = "https://api.github.com/repos/nilaoda/TSCutter.GUI/releases/latest";
    private const string ReleaseUrl = "https://github.com/nilaoda/TSCutter.GUI/releases/latest";
    private const string ReleaseUrlPrefix = "https://github.com/nilaoda/TSCutter.GUI/releases/tag/";

    public static async Task<string> GetLatestTagAsync()
    {
        try
        {
            var redirctUrl = await Get302Async(ReleaseUrl);
            var latestTag = redirctUrl.Replace(ReleaseUrlPrefix, "");
            return latestTag;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get latest tag: {ex.Message}");
            return "";
        }
    }

    public static async Task<GitHubVersionInfo?> CheckUpdateAsync()
    {
        try
        {
            var jsonStr = await GetContentAsync(VersionUrl);
            return JsonSerializer.Deserialize<GitHubVersionInfo>(jsonStr, AppJsonContext.Default.GitHubVersionInfo);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get GitHub version info: {ex.Message}");
            return null;
        }
    }
    
    // 获取网页源码
    private static async Task<string> GetContentAsync(string url)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "TSCutter.GUI");
        using var response = await client.GetAsync(url);
        using var content = response.Content;
        return await content.ReadAsStringAsync();
    }
    
    // 重定向
    private static async Task<string> Get302Async(string url)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };
        var redirectedUrl = "";
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync(url);
        using var content = response.Content;
        if (response.StatusCode != HttpStatusCode.Found) return redirectedUrl;
        
        var headers = response.Headers;
        if (headers.Location != null)
        {
            redirectedUrl = headers.Location.AbsoluteUri;
        }

        return redirectedUrl;
    }
}