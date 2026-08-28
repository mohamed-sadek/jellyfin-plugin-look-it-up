using Jellyfin.Plugin.LookItUp.Models;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Local + Wikidata keep/drop rules for non-US-native cultural popups.
/// </summary>
public sealed class ReferenceGate : IReferenceGate
{
    /// <summary>Wikidata P31 types that are worth a popup.</summary>
    internal static readonly HashSet<string> AllowInstanceOf = new(StringComparer.OrdinalIgnoreCase)
    {
        "Q5",          // human
        "Q215627",     // person
        "Q11424",      // film
        "Q24862",      // short film
        "Q202866",     // animated film
        "Q5398426",    // television series
        "Q15416",      // television program
        "Q431289",     // brand
        "Q167270",     // trademark
        "Q3231690",    // automobile model
        "Q1420",       // motor car
        "Q20799151",   // motor vehicle model
        "Q18524218",   // vehicle model
        "Q59773381",   // car model series
        "Q29048319",   // model series
        "Q1368740",    // automobile manufacturer? (use company too)
        "Q7868205",    // car brand
        "Q6881511",    // enterprise
        "Q4830453",    // business
        "Q43229",      // organization
        "Q783794",     // company
        "Q891723",     // public company
        "Q618779",     // award
        "Q12973014",   // sports team
        "Q847017",     // sports club
        "Q476028",     // association football club
        "Q449897",     // sports competition
        "Q13406554",   // sports competition
        "Q15275719",   // recurring sporting event
        "Q4438121",    // sports organization
        "Q623109",     // sports league
        "Q41176",      // building
        "Q811979",     // architectural structure
        "Q839954",     // archaeological site
        "Q16917",      // hospital
        "Q3918",       // university
        "Q38723",      // higher education institution
        "Q2385804",    // educational institution
        "Q875538",     // public university
        "Q187456",     // bar
        "Q12280",      // restaurant
        "Q11707",      // restaurant (alias used on some items)
        "Q41253",      // movie theater
        "Q3305213",    // painting
        "Q838948",     // work of art
        "Q7725634",    // literary work
        "Q571",        // book
        "Q25379",      // play
        "Q47461344",   // written work
        "Q215380",     // musical group
        "Q5741069",    // rock band
        "Q2088357",    // musical ensemble
        "Q35127",      // website (brands like eBay)
        "Q1616075",    // television channel? skip if too noisy — omit
        "Q11033",      // newspaper? allow as institution
        "Q11032",      // newspaper
        "Q41298",      // magazine
        "Q22687",      // bank
        "Q161726",     // supermarket / retail chain-ish
        "Q507619",     // retail chain
        "Q11315",      // shopping mall? skip
        "Q131734",     // brewery
        "Q131527",     // distillery
    };

    /// <summary>Wikidata P31 types that are never popups.</summary>
    internal static readonly HashSet<string> DenyInstanceOf = new(StringComparer.OrdinalIgnoreCase)
    {
        "Q202444",     // given name
        "Q101352",     // family name
        "Q12308941",   // male given name
        "Q11879590",   // female given name
        "Q3409032",    // unisex given name
        "Q4167410",    // Wikimedia disambiguation page
        "Q13406463",   // Wikimedia list article
        "Q4167836",    // Wikimedia category
        "Q11266439",   // Wikimedia template
        "Q95074",      // fictional character
        "Q15632617",   // fictional human
        "Q15773317",   // film character
        "Q15711870",   // animated character
        "Q337573",     // anime character
        "Q14897293",   // fictional entity
        "Q15773347",   // television character
        "Q6256",       // country
        "Q3624078",    // sovereign state
        "Q5107",       // continent
        "Q82794",      // geographic region
        "Q515",        // city
        "Q1549591",    // big city
        "Q7930989",    // city/town
        "Q3957",       // town
        "Q486972",     // human settlement
        "Q573",        // calendar day
        "Q41825",      // day of the week
        "Q47018901",   // Monday
        "Q3186692",    // calendar month
        "Q178885",     // deity
        "Q9174",       // religion
        "Q3375733",    // religious concept
        "Q17633526",   // Wikinews article
        "Q13442814",   // scholarly article
        "Q35120",      // entity / too generic
        "Q2424752",    // product (generic SKU noise)
        "Q7366",       // song
        "Q482994",     // album
        "Q2188189",    // musical work
        "Q134556",     // single
    };

    private static readonly HashSet<string> MegaGeography = new(StringComparer.OrdinalIgnoreCase)
    {
        "united states", "usa", "u.s.", "u.s.a.", "america", "the states",
        "new york", "new york city", "nyc", "los angeles", "la", "chicago",
        "london", "paris", "tokyo", "beijing", "moscow", "rome", "berlin",
        "madrid", "sydney", "toronto", "mexico city", "cairo", "dubai",
        "china", "india", "russia", "england", "france", "germany", "italy",
        "spain", "canada", "mexico", "australia", "japan", "brazil", "africa",
        "europe", "asia", "antarctica", "earth", "the world", "world",
        "california", "texas", "florida", "dallas",
        "god", "jesus", "jesus christ", "christ", "hell", "heaven",
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
        "january", "february", "march", "april", "june", "july", "august",
        "september", "october", "november", "december"
    };

    /// <inheritdoc />
    public ReferenceDecision? TryRejectLocal(
        NameCandidate candidate,
        string? showName,
        IReadOnlySet<string> excludeCast)
    {
        var term = (candidate.Term ?? string.Empty).Trim();
        if (term.Length < 2)
        {
            return Drop(candidate, "too-common", "Term is too short.");
        }

        if (MegaGeography.Contains(term))
        {
            return Drop(candidate, "too-common", "Globally obvious geography, calendar, or religious term.");
        }

        if (IsExcludedCast(term, excludeCast))
        {
            return Drop(candidate, "in-show", "Excluded cast or speaker from this title.");
        }

        if (!string.IsNullOrWhiteSpace(showName)
            && term.Equals(showName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Drop(candidate, "in-show", "The current show title.");
        }

        return null;
    }

    /// <inheritdoc />
    public ReferenceDecision Decide(
        NameCandidate candidate,
        WikimediaReferenceHit hit,
        string? showName,
        IReadOnlySet<string> excludeCast)
    {
        var local = TryRejectLocal(candidate, showName, excludeCast);
        if (local is not null)
        {
            return local;
        }

        if (!hit.Found || string.IsNullOrWhiteSpace(hit.Title))
        {
            return Drop(candidate, "not-found", "No Wikipedia article.", uncertain: true);
        }

        if (hit.Title.Contains("list of", StringComparison.OrdinalIgnoreCase)
            || hit.Title.Contains("disambiguation", StringComparison.OrdinalIgnoreCase))
        {
            return Drop(candidate, "wikidata-type", "Disambiguation or list article.");
        }

        if (MegaGeography.Contains(hit.Title) || MegaGeography.Contains(hit.Term))
        {
            return Drop(candidate, "too-common", "Globally obvious geography or calendar.");
        }

        if (IsExcludedCast(hit.Title, excludeCast))
        {
            return Drop(candidate, "in-show", "Wikipedia title matches excluded cast.");
        }

        var show = showName?.Trim();
        if (!string.IsNullOrWhiteSpace(show)
            && LooksLikeThisShow(show, hit.Title, hit.Summary, hit.WikidataDescription))
        {
            return Drop(candidate, "in-show", $"Tied to the fictional world of {show}.");
        }

        var types = hit.InstanceOfIds ?? [];
        var denied = types.FirstOrDefault(DenyInstanceOf.Contains);
        if (denied is not null)
        {
            var category = denied is "Q95074" or "Q15632617" or "Q15773317" or "Q15711870" or "Q337573" or "Q14897293" or "Q15773347"
                ? "in-show"
                : denied is "Q515" or "Q1549591" or "Q6256" or "Q3624078" or "Q5107" or "Q486972" or "Q7930989" or "Q3957"
                    ? "too-common"
                    : "wikidata-type";
            return Drop(
                candidate,
                category,
                $"Wikidata type {denied} is not a popup.",
                uncertain: category == "wikidata-type");
        }

        var allowed = types.FirstOrDefault(AllowInstanceOf.Contains);
        if (allowed is null && types.Count > 0)
        {
            return Drop(candidate, "wikidata-type", "Wikidata type is not a cultural reference worth explaining.", uncertain: true);
        }

        if (allowed is null)
        {
            return Drop(candidate, "wikidata-type", "No Wikidata instance-of type.", uncertain: true);
        }

        if (allowed is "Q5398426" or "Q15416"
            && !string.IsNullOrWhiteSpace(show)
            && hit.Title.Equals(show, StringComparison.OrdinalIgnoreCase))
        {
            return Drop(candidate, "in-show", "The current television series.");
        }

        var kind = KindFromTypes(types);
        var title = hit.Title.Trim();
        var summary = ClampSummary(hit.Summary, title);
        var wordCount = candidate.Term.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var companyOnly = IsCompanyType(allowed) && !types.Any(IsVehicleType);
        return new ReferenceDecision
        {
            Candidate = candidate,
            Kept = true,
            Category = kind,
            Kind = kind,
            Reason = string.IsNullOrWhiteSpace(hit.WikidataDescription)
                ? $"Wikidata {allowed}"
                : hit.WikidataDescription,
            Title = title,
            Summary = summary.StartsWith(title, StringComparison.OrdinalIgnoreCase)
                ? summary
                : $"{title}: {summary}",
            Url = hit.Url,
            ImageUrl = hit.ImageUrl,
            WikidataId = hit.WikidataId,
            Uncertain = hit.Ambiguous || (companyOnly && wordCount == 1),
            AlternateTitles = hit.AlternateTitles ?? []
        };
    }

    private static bool IsCompanyType(string qid)
        => qid is "Q783794" or "Q6881511" or "Q4830453" or "Q43229" or "Q891723" or "Q1368740";

    private static bool IsVehicleType(string qid)
        => qid is "Q3231690" or "Q1420" or "Q20799151" or "Q18524218" or "Q59773381" or "Q29048319" or "Q7868205";

    private static bool IsExcludedCast(string term, IReadOnlySet<string> excludeCast)
    {
        if (excludeCast.Count == 0 || string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        if (excludeCast.Contains(term))
        {
            return true;
        }

        foreach (var name in excludeCast)
        {
            if (name.Length < 3)
            {
                continue;
            }

            if (term.Equals(name, StringComparison.OrdinalIgnoreCase)
                || term.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)
                || term.StartsWith(name + "'s", StringComparison.OrdinalIgnoreCase)
                || term.StartsWith(name + "’s", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeThisShow(string show, string title, string? extract, string? description)
    {
        var blob = $"{title}\n{extract}\n{description}";
        if (blob.IndexOf(show, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        return blob.Contains("fictional", StringComparison.OrdinalIgnoreCase)
               || blob.Contains("character", StringComparison.OrdinalIgnoreCase)
               || blob.Contains("sitcom", StringComparison.OrdinalIgnoreCase)
               || blob.Contains("episode of", StringComparison.OrdinalIgnoreCase)
               || blob.Contains("setting of", StringComparison.OrdinalIgnoreCase)
               || title.Contains("(" + show, StringComparison.OrdinalIgnoreCase);
    }

    private static string KindFromTypes(IReadOnlyList<string> types)
    {
        if (types.Any(t => t is "Q5" or "Q215627"))
        {
            return "person";
        }

        if (types.Any(t => t is "Q11424" or "Q24862" or "Q202866" or "Q5398426" or "Q15416"))
        {
            return "film";
        }

        if (types.Any(t => t is "Q3231690" or "Q1420" or "Q431289" or "Q167270" or "Q7868205" or "Q4830453" or "Q783794" or "Q6881511" or "Q59773381" or "Q20799151" or "Q18524218"))
        {
            return "brand";
        }

        if (types.Any(t => t is "Q41176" or "Q811979" or "Q187456" or "Q12280" or "Q3918"))
        {
            return "place";
        }

        return "other";
    }

    private static string ClampSummary(string? extract, string title)
    {
        var summary = (extract ?? string.Empty).Trim();
        if (summary.Length == 0)
        {
            return title;
        }

        var sentenceEnd = summary.IndexOf(". ", StringComparison.Ordinal);
        if (sentenceEnd > 40 && sentenceEnd < 280)
        {
            summary = summary[..(sentenceEnd + 1)];
        }
        else if (summary.Length > 280)
        {
            summary = summary[..277].TrimEnd() + "...";
        }

        return summary;
    }

    private static ReferenceDecision Drop(NameCandidate candidate, string category, string reason, bool uncertain = false)
    {
        return new ReferenceDecision
        {
            Candidate = candidate,
            Kept = false,
            Category = category,
            Reason = reason,
            Title = candidate.Term,
            Uncertain = uncertain
        };
    }
}
