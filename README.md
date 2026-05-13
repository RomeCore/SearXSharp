
<h1 align="center">🦈 SearXSharp</h1>

<p align="center">
  <b>The first and only C#-adapted meta-search engine, yeah!</b>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4?logo=dotnet&style=flat-square">
  <img src="https://img.shields.io/badge/License-MIT-green?style=flat-square">
  <img src="https://img.shields.io/badge/status-vibe--coded-ff69b4?style=flat-square">
  <img src="https://img.shields.io/badge/engines-21%20and%20counting-blue?style=flat-square">
</p>

---

## 🤔 What the hell is this?

**SearXSharp** is a C# port of the legendary **[SearXNG](https://github.com/searxng/searxng)** — the privacy-respecting, self-hosted meta-search engine. 

It queries **multiple search engines simultaneously**, merges, deduplicates, and returns clean structured results. **No API keys. No paid subscriptions. Just raw scraping power.**

**Why does this exist?** Because every AI project needs to search the web without paying $200/month to Google/Bing/Whoever, and the Python ecosystem had SearXNG while .NET devs were left jerking off in the corner. Not anymore!

---

## ⚠️ Important Legal & Ethical Notes

```
┌─────────────────────────────────────────────────────────────────────┐
│  THIS PROJECT IS ENTIRELY VIBE-CODED                                │
│                                                                     │
│  I'm not sure if it works 100% like original SearXNG,               │
│  but I hope that this thing can be useful for your AI applications  │
│  to avoid paid APIs!                                                │
│                                                                     │
│  Respect robots.txt and terms of service of the websites            │
│  you're scraping.                                                   │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 🚀 Features

### 🔍 Multi-Engine Madness
SearXSharp fires **all engines simultaneously** (like SearXNG's threading model) and merges results faster than your boss can say "why is this not done yet?"

### 🧠 Smart Deduplication
Results are deduplicated by URL. When conflicts happen, the result with more content wins. Survival of the fittest!

### 🛡️ Anti-Bot Countermeasures
- Random user-agent rotation (Chrome, Firefox, Edge, and even GSA mobile agents)
- Cookie consent bypass for Google
- CAPTCHA detection and logging
- Configurable timeouts per engine

### 📦 Zero External API Dependencies
**No API keys needed.** Just pure HTML scraping, like nature intended.

---

## 🔧 Quick Start

```csharp
using SearXSharp;

// Create the manager (you can optionally pass your own ILogger)
var manager = new SearchEngineManager();

// Configure your search
var query = new SearchQuery
{
    Query = "C# meta search engine",
    Language = "en",
    SafeSearch = SafeSearchLevel.Moderate,
    TimeRange = TimeRange.Week,
    MaxResults = 20
};

// Search ALL the things!
var results = await manager.SearchAllAsync(query);

// Results are already merged and deduplicated!
Console.WriteLine($"Found {results.Results.Count} results in {results.TotalTimeMs}ms");

foreach (var result in results.Results)
{
    Console.WriteLine($"- {result.Title}: {result.Url}");
}
```

---

## 🤝 Contributing

**Want to add a new engine?** Here's the template:

```csharp
public class MyCoolSearchEngine : SearchEngineBase
{
    public override string Name => "mycoolengine";
    public override string DisplayName => "My Cool Engine";
    
    public MyCoolSearchEngine(ILogger logger) : base(logger)
    {
        // Configure your engine here
    }
    
    public override async Task<SearchResultList> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        // 1. Build your request URL
        // 2. Scrape the HTML/parse JSON
        // 3. Return SearchResultList
        // 4. Profit (or not, this is open source)
    }
}
```

---

## 📜 License

**MIT** — Do whatever the you want with it, just don't blame me if something breaks. 
This project is vibe-coded, remember? 

---

## 🙏 Credits

- **[SearXNG](https://github.com/searxng/searxng)** — The original Python-based meta-search engine that inspired this beautiful disaster
- **You, the user** — For being too cheap to pay for search APIs
- **Me, the vibe-coder** — For writing this at 2 AM while questioning my life choices

---

<p align="center">
  <b>Made with ❤️, ☕, and an unhealthy amount of profanity</b><br>
  <i>If this project saved you money, buy me a coffee. Or don't. I'm not your mom, blyat.</i>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/vibe--coded-🦈-brightgreen?style=for-the-badge">
</p>
