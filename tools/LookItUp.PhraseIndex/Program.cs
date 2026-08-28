using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.LookItUp.Models;
using Jellyfin.Plugin.LookItUp.Services;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    var outDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Jellyfin.Plugin.LookItUp", "Data"));
    for (var i = 0; i < args.Length; i++)
    {
        if ((args[i] is "--out" or "-o") && i + 1 < args.Length)
        {
            outDir = Path.GetFullPath(args[++i]);
        }
    }

    Directory.CreateDirectory(outDir);
    using var http = CreateClient();
    var entities = new Dictionary<string, PhraseIndexSourceEntity>(StringComparer.OrdinalIgnoreCase);

    Console.WriteLine("Fetching Wikidata entities…");
    foreach (var query in Queries())
    {
        Console.WriteLine($"  SPARQL: {query.Name}");
        try
        {
            var rows = await SparqlAsync(http, query.Sparql).ConfigureAwait(false);
            Console.WriteLine($"    {rows.Count} rows");
            foreach (var row in rows)
            {
                Merge(entities, row);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"    failed: {ex.Message}");
        }
    }

    Console.WriteLine($"Unique QIDs: {entities.Count}. Fetching English labels/aliases…");
    await FillLabelsAsync(http, entities.Values.ToList()).ConfigureAwait(false);

    var compiled = PhraseIndexCompiler.Compile(entities.Values);
    var file = new PhraseIndexFile
    {
        Version = 1,
        GeneratedAtUtc = DateTime.UtcNow,
        Entries = compiled.ToList()
    };

    var jsonPath = Path.Combine(outDir, "phrase-index.json");
    var gzPath = Path.Combine(outDir, "phrase-index.json.gz");
    var jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    var json = JsonSerializer.Serialize(file, jsonOptions);
    await File.WriteAllTextAsync(jsonPath, json).ConfigureAwait(false);
    await using (var fs = File.Create(gzPath))
    await using (var gz = new GZipStream(fs, CompressionLevel.SmallestSize))
    await using (var writer = new StreamWriter(gz, Encoding.UTF8))
    {
        await writer.WriteAsync(json).ConfigureAwait(false);
    }

    Console.WriteLine($"Wrote {compiled.Count} phrases");
    Console.WriteLine($"  {jsonPath} ({new FileInfo(jsonPath).Length / 1024} KB)");
    Console.WriteLine($"  {gzPath} ({new FileInfo(gzPath).Length / 1024} KB gzip)");

    string[] probes =
    [
        "dockers", "dan quayle", "lloyd bentsen", "i love lucy", "mussolini", "trini lopez",
        "winston churchill", "star trek", "algonquin round table", "starship enterprise",
        "poconos", "hamptons", "lemon tree", "consumer reports", "jon voight", "kmart", "mets"
    ];
    foreach (var probe in probes)
    {
        var hit = compiled.FirstOrDefault(e => e.Phrase.Equals(probe, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine(hit is null ? $"  missing: {probe}" : $"  ok: {probe} → {hit.Title}");
    }

    return compiled.Count == 0 ? 2 : 0;
}

static void Merge(Dictionary<string, PhraseIndexSourceEntity> entities, SparqlRow row)
{
    if (string.IsNullOrWhiteSpace(row.Qid) || string.IsNullOrWhiteSpace(row.Title))
    {
        return;
    }

    if (!entities.TryGetValue(row.Qid, out var entity))
    {
        entity = new PhraseIndexSourceEntity
        {
            Qid = row.Qid,
            Title = row.Title,
            Sitelinks = row.Sitelinks
        };
        entities[row.Qid] = entity;
    }

    if (row.Sitelinks > entity.Sitelinks)
    {
        entity.Sitelinks = row.Sitelinks;
    }

    if (!string.IsNullOrWhiteSpace(row.Type) && !entity.Types.Contains(row.Type, StringComparer.OrdinalIgnoreCase))
    {
        entity.Types.Add(row.Type);
    }
}

static async Task FillLabelsAsync(HttpClient http, List<PhraseIndexSourceEntity> entities)
{
    const int batchSize = 50;
    for (var i = 0; i < entities.Count; i += batchSize)
    {
        var batch = entities.Skip(i).Take(batchSize).ToList();
        var ids = string.Join('|', batch.Select(e => e.Qid));
        var url =
            "https://www.wikidata.org/w/api.php?action=wbgetentities&props=labels|aliases&languages=en&format=json&ids="
            + Uri.EscapeDataString(ids);
        try
        {
            using var response = await http.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("entities", out var bag))
            {
                continue;
            }

            foreach (var entity in batch)
            {
                if (!bag.TryGetProperty(entity.Qid, out var el))
                {
                    continue;
                }

                AddText(entity, el, "labels");
                AddText(entity, el, "aliases");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  wbgetentities @{i}: {ex.Message}");
        }

        if ((i / batchSize) % 20 == 19)
        {
            Console.WriteLine($"  labels {Math.Min(i + batchSize, entities.Count)}/{entities.Count}");
        }
    }
}

static void AddText(PhraseIndexSourceEntity entity, JsonElement el, string property)
{
    if (!el.TryGetProperty(property, out var node) || node.ValueKind != JsonValueKind.Object)
    {
        return;
    }

    if (!node.TryGetProperty("en", out var en))
    {
        return;
    }

    if (en.ValueKind == JsonValueKind.Object && en.TryGetProperty("value", out var one))
    {
        var value = one.GetString();
        if (!string.IsNullOrWhiteSpace(value))
        {
            entity.Phrases.Add(value);
        }

        return;
    }

    if (en.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in en.EnumerateArray())
        {
            if (item.TryGetProperty("value", out var valueEl))
            {
                var value = valueEl.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    entity.Phrases.Add(value);
                }
            }
        }
    }
}

static async Task<List<SparqlRow>> SparqlAsync(HttpClient http, string sparql)
{
    var url = "https://query.wikidata.org/sparql?format=json&query=" + Uri.EscapeDataString(sparql);
    using var response = await http.GetAsync(url).ConfigureAwait(false);
    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {Trim(body, 180)}");
    }

    using var doc = JsonDocument.Parse(body);
    var rows = new List<SparqlRow>();
    if (!doc.RootElement.TryGetProperty("results", out var results)
        || !results.TryGetProperty("bindings", out var bindings))
    {
        return rows;
    }

    foreach (var binding in bindings.EnumerateArray())
    {
        var qid = QidFromUri(Read(binding, "item"));
        var title = Read(binding, "title");
        var type = QidFromUri(Read(binding, "type"));
        var sitelinks = 0;
        if (int.TryParse(Read(binding, "sitelinks"), out var parsed))
        {
            sitelinks = parsed;
        }

        if (!string.IsNullOrWhiteSpace(qid) && !string.IsNullOrWhiteSpace(title))
        {
            rows.Add(new SparqlRow(qid, title, type, sitelinks));
        }
    }

    return rows;
}

static string Read(JsonElement binding, string name)
{
    if (!binding.TryGetProperty(name, out var cell))
    {
        return string.Empty;
    }

    return cell.TryGetProperty("value", out var value) ? value.GetString() ?? string.Empty : string.Empty;
}

static string QidFromUri(string uri)
{
    var idx = uri.LastIndexOf('/');
    return idx >= 0 ? uri[(idx + 1)..] : uri;
}

static string Trim(string text, int max)
    => text.Length <= max ? text : text[..max];

static HttpClient CreateClient()
{
    var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LookItUpPhraseIndex", "1.0"));
    http.DefaultRequestHeaders.UserAgent.Add(
        new ProductInfoHeaderValue("(+https://github.com/mohamed-sadek/jellyfin-plugin-look-it-up)"));
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));
    return http;
}

static IEnumerable<(string Name, string Sparql)> Queries()
{
    yield return ("brands", Typed("wd:Q431289 wd:Q167270 wd:Q507619 wd:Q161726 wd:Q7868205", 4, 12000));
    yield return ("companies", Typed("wd:Q4830453 wd:Q783794 wd:Q891723 wd:Q6881511", 8, 12000));
    yield return ("cars", Typed("wd:Q3231690 wd:Q20799151 wd:Q18524218 wd:Q59773381 wd:Q1420", 3, 12000));
    yield return ("teams", Typed("wd:Q12973014 wd:Q847017 wd:Q449897 wd:Q623109 wd:Q476028", 5, 8000));
    yield return ("tv", Typed("wd:Q5398426 wd:Q15416 wd:Q1261214", 8, 8000));
    yield return ("films", Typed("wd:Q11424 wd:Q202866", 20, 8000));
    yield return ("orgs", Typed("wd:Q43229 wd:Q163740 wd:Q1329436 wd:Q431603 wd:Q11032 wd:Q41298", 6, 8000));
    yield return ("books", Typed("wd:Q7725634 wd:Q571 wd:Q25379 wd:Q47461344", 10, 6000));
    yield return ("music-groups", Typed("wd:Q215380 wd:Q5741069 wd:Q2088357", 8, 6000));
    yield return ("songs", Typed("wd:Q7366 wd:Q134556", 12, 6000));
    yield return ("buildings", Typed("wd:Q41176 wd:Q811979 wd:Q187456 wd:Q12280 wd:Q3918", 8, 6000));
    yield return ("events", Typed("wd:Q657449 wd:Q132241 wd:Q182660 wd:Q166808", 4, 4000));
    yield return ("spacecraft", Typed("wd:Q40218 wd:Q15831585 wd:Q1145276 wd:Q3001412", 4, 3000));
    yield return ("us-places", UsPlaces());
    yield return ("us-humans", UsHumans(20, 15000));
    yield return ("famous-humans", FamousHumans(80, 12000));
}

static string Typed(string values, int minSitelinks, int limit) =>
    "SELECT ?item ?title ?sitelinks ?type WHERE {\n" +
    "  VALUES ?type { " + values + " }\n" +
    "  ?item wdt:P31 ?type .\n" +
    "  ?item wikibase:sitelinks ?sitelinks .\n" +
    "  FILTER(?sitelinks >= " + minSitelinks + ")\n" +
    "  ?article schema:about ?item ;\n" +
    "           schema:isPartOf <https://en.wikipedia.org/> ;\n" +
    "           schema:name ?title .\n" +
    "}\nLIMIT " + limit;

static string UsPlaces() =>
    "SELECT ?item ?title ?sitelinks ?type WHERE {\n" +
    "  VALUES ?type { wd:Q46831 wd:Q34763 wd:Q23442 wd:Q123705 wd:Q17343829 wd:Q82794 }\n" +
    "  ?item wdt:P31 ?type .\n" +
    "  ?item wdt:P17 wd:Q30 .\n" +
    "  ?item wikibase:sitelinks ?sitelinks .\n" +
    "  FILTER(?sitelinks >= 6)\n" +
    "  ?article schema:about ?item ;\n" +
    "           schema:isPartOf <https://en.wikipedia.org/> ;\n" +
    "           schema:name ?title .\n" +
    "}\nLIMIT 6000";

static string UsHumans(int minSitelinks, int limit) =>
    "SELECT ?item ?title ?sitelinks ?type WHERE {\n" +
    "  BIND(wd:Q5 AS ?type)\n" +
    "  ?item wdt:P31 wd:Q5 .\n" +
    "  ?item wdt:P27 wd:Q30 .\n" +
    "  ?item wikibase:sitelinks ?sitelinks .\n" +
    "  FILTER(?sitelinks >= " + minSitelinks + ")\n" +
    "  ?article schema:about ?item ;\n" +
    "           schema:isPartOf <https://en.wikipedia.org/> ;\n" +
    "           schema:name ?title .\n" +
    "}\nLIMIT " + limit;

static string FamousHumans(int minSitelinks, int limit) =>
    "SELECT ?item ?title ?sitelinks ?type WHERE {\n" +
    "  BIND(wd:Q5 AS ?type)\n" +
    "  ?item wdt:P31 wd:Q5 .\n" +
    "  ?item wikibase:sitelinks ?sitelinks .\n" +
    "  FILTER(?sitelinks >= " + minSitelinks + ")\n" +
    "  ?article schema:about ?item ;\n" +
    "           schema:isPartOf <https://en.wikipedia.org/> ;\n" +
    "           schema:name ?title .\n" +
    "}\nLIMIT " + limit;

file sealed record SparqlRow(string Qid, string Title, string Type, int Sitelinks);
