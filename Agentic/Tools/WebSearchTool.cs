using System.Net;
using System.Text.Json;
using System.Web;
using Agentic.Core;
using HtmlAgilityPack;

namespace Agentic.Tools;

public class WebSearchTool : ITool
{
    public string Name => "web_search";
    public string Description => "Search the web using DuckDuckGo and return a list of results with titles, URLs, and snippets.";

    public BinaryData ParameterSchema => BinaryData.FromString("""
    {
        "type": "object",
        "properties": {
            "query": {
                "type": "string",
                "description": "The search query."
            },
            "max_results": {
                "type": "integer",
                "description": "Maximum number of results to return. Defaults to 5."
            }
        },
        "required": ["query"],
        "additionalProperties": false
    }
    """);

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        var query = arguments.GetProperty("query").GetString()!;
        var maxResults = arguments.TryGetProperty("max_results", out var mr) ? mr.GetInt32() : 5;

        try
        {
            var url = $"https://html.duckduckgo.com/html/?q={HttpUtility.UrlEncode(query)}";
            var html = await HttpClient.GetStringAsync(url, ct);

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Detect CAPTCHA / bot challenge
            if (html.Contains("challenge", StringComparison.OrdinalIgnoreCase)
                && doc.DocumentNode.SelectSingleNode("//input[@name='vqd']") is null)
                return new ToolResult("Search blocked by CAPTCHA. Try again later.", IsError: true);

            var resultNodes = doc.DocumentNode.SelectNodes(
                "//div[contains(@class,'result results_links')]");

            if (resultNodes is null || resultNodes.Count == 0)
                return new ToolResult("No results found.");

            var results = new List<string>();
            foreach (var node in resultNodes.Take(maxResults))
            {
                var titleNode = node.SelectSingleNode(".//a[contains(@class,'result__a')]");
                var snippetNode = node.SelectSingleNode(
                    ".//*[contains(@class,'result__snippet')]");

                var title = Clean(titleNode?.InnerText);
                var href = titleNode?.GetAttributeValue("href", "") ?? "";
                var snippet = Clean(snippetNode?.InnerText);

                if (string.IsNullOrEmpty(title)) continue;

                results.Add($"Title: {title}\nURL: {ExtractUrl(href)}\nSnippet: {snippet}");
            }

            return results.Count > 0
                ? new ToolResult(string.Join("\n\n---\n\n", results))
                : new ToolResult("No results found.");
        }
        catch (HttpRequestException ex)
        {
            return new ToolResult($"Search request failed: {ex.Message}", IsError: true);
        }
    }

    /// <summary>
    /// DuckDuckGo wraps links through a redirect: //duckduckgo.com/l/?uddg=ENCODED_URL&amp;...
    /// Extract the actual destination URL.
    /// </summary>
    private static string ExtractUrl(string href)
    {
        if (string.IsNullOrEmpty(href)) return href;

        if (href.Contains("uddg="))
        {
            try
            {
                var full = href.StartsWith("//") ? "https:" + href : href;
                var qs = HttpUtility.ParseQueryString(new Uri(full).Query);
                var actual = qs["uddg"];
                if (!string.IsNullOrEmpty(actual)) return actual;
            }
            catch { /* fall through to raw href */ }
        }

        return href;
    }

    private static string Clean(string? text) =>
        WebUtility.HtmlDecode(text?.Trim() ?? "");
}
