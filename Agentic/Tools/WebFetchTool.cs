using System.Net;
using System.Text.Json;
using Agentic.Core;
using HtmlAgilityPack;

namespace Agentic.Tools;

public class WebFetchTool : ITool
{
    public string Name => "web_fetch";
    public string Description =>
        "Fetch a web page at the given URL and extract its readable text content, " +
        "along with links found on the page. Call this again on any returned link to " +
        "explore the website further.";

    public BinaryData ParameterSchema => BinaryData.FromString("""
    {
        "type": "object",
        "properties": {
            "url": {
                "type": "string",
                "description": "The URL of the web page to fetch."
            },
            "max_length": {
                "type": "integer",
                "description": "Maximum character length of returned content. Defaults to 8000."
            }
        },
        "required": ["url"],
        "additionalProperties": false
    }
    """);

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
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
        var url = arguments.GetProperty("url").GetString()!;
        var maxLength = arguments.TryGetProperty("max_length", out var ml) ? ml.GetInt32() : 8000;

        try
        {
            var html = await HttpClient.GetStringAsync(url, ct);

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var baseUri = new Uri(url);

            // Extract links before stripping elements
            var links = ExtractLinks(doc, baseUri);

            // Remove elements that don't contribute readable content
            var removals = doc.DocumentNode.SelectNodes(
                "//script|//style|//nav|//footer|//iframe|//noscript|//svg|//img|//video|//audio");
            if (removals is not null)
                foreach (var node in removals)
                    node.Remove();

            var body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
            var rawText = WebUtility.HtmlDecode(body.InnerText);

            // Collapse whitespace: trim each line, drop blanks
            var lines = rawText
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0);

            var content = string.Join("\n", lines);

            if (content.Length > maxLength)
                content = content[..maxLength] + "\n\n[Content truncated]";

            // Append links section
            if (links.Count > 0)
            {
                content += "\n\n--- Links found on this page ---\n";
                foreach (var (text, href) in links)
                    content += $"- [{text}] {href}\n";
            }

            if (string.IsNullOrWhiteSpace(content))
                return new ToolResult("Page returned no readable text content.");

            return new ToolResult(content);
        }
        catch (HttpRequestException ex)
        {
            return new ToolResult($"Failed to fetch URL: {ex.Message}", IsError: true);
        }
    }

    private static List<(string Text, string Href)> ExtractLinks(HtmlDocument doc, Uri baseUri)
    {
        var anchors = doc.DocumentNode.SelectNodes("//a[@href]");
        if (anchors is null) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<(string Text, string Href)>();

        foreach (var a in anchors)
        {
            var href = a.GetAttributeValue("href", "").Trim();
            if (string.IsNullOrEmpty(href)) continue;

            // Skip non-navigable links
            if (href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                                     || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                continue;

            // Resolve relative URLs
            if (Uri.TryCreate(baseUri, href, out var absolute))
                href = absolute.ToString();

            if (!seen.Add(href)) continue;

            var text = WebUtility.HtmlDecode(a.InnerText).Trim();
            if (text.Length == 0) text = href;

            // Keep the label concise
            if (text.Length > 80) text = text[..77] + "...";

            links.Add((text, href));
        }

        // Cap at 50 links to keep output manageable
        return links.Take(50).ToList();
    }
}
