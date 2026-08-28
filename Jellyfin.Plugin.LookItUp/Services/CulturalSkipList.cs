namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Closed-class terms that must never become popups (shared by the index compiler and runtime).
/// </summary>
public static class CulturalSkipList
{
    /// <summary>Globally obvious geography, calendar, and religious words.</summary>
    public static readonly HashSet<string> ObviousTerms = new(StringComparer.OrdinalIgnoreCase)
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
        "september", "october", "november", "december",
        "thanksgiving", "thanksgiving day", "thanksgiving eve",
        "american", "british", "french", "german", "italian", "japanese", "chinese",
        "mexican", "canadian", "irish", "scottish", "russian", "spanish", "korean",
        "indian", "australian",
        "street", "avenue", "road", "boulevard"
    };

    /// <summary>
    /// One-word dialogue / function words that must not be index phrases
    /// (Wait, Sure, Boys, Coffee, …).
    /// </summary>
    public static readonly HashSet<string> CommonOneWord = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "this", "that", "these", "those", "there", "here",
        "i", "you", "he", "she", "we", "they", "it", "me", "him", "her", "us", "them",
        "my", "your", "his", "our", "their", "and", "or", "but", "if", "then", "so",
        "of", "in", "on", "at", "to", "for", "from", "with", "about", "into", "over",
        "yes", "no", "ok", "okay", "hey", "hi", "hello", "please", "sorry", "well",
        "oh", "ah", "uh", "um", "what", "when", "where", "who", "why", "how",
        "is", "are", "was", "were", "be", "been", "have", "has", "had", "do", "does", "did",
        "will", "would", "could", "should", "may", "might", "must", "can", "not",
        "all", "any", "some", "just", "also", "only", "even", "still", "very", "too",
        "up", "down", "out", "off", "back", "away", "now", "once", "get", "got", "go",
        "come", "see", "look", "let", "make", "know", "think", "want", "like", "take",
        "give", "tell", "say", "said", "ask", "wait", "sure", "boys", "coffee", "either",
        "half", "good", "morning", "next", "stop", "friend", "boys", "man", "guy",
        "right", "left", "big", "little", "old", "new", "first", "last", "best",
        "love", "life", "time", "day", "night", "home", "house", "car", "show",
        "movie", "song", "book", "game", "team", "city", "town", "place", "thing",
        "stuff", "kind", "sort", "way", "lot", "bit", "part", "end", "start",
        "wait", "hold", "come", "gonna", "wanna", "yeah", "yep", "nope", "gee", "ooh"
    };

    /// <summary>Wikidata types that are given names / family names, never phrases.</summary>
    public static readonly HashSet<string> GivenNameTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Q202444", "Q101352", "Q12308941", "Q11879590", "Q3409032"
    };

    /// <summary>Wikidata types treated as titled works (1-word titles are too noisy).</summary>
    public static readonly HashSet<string> WorkTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Q11424", "Q24862", "Q202866", "Q5398426", "Q15416", "Q1261214",
        "Q7366", "Q482994", "Q2188189", "Q134556", "Q7725634", "Q571",
        "Q25379", "Q47461344", "Q838948", "Q3305213"
    };

    /// <summary>Wikidata types treated as humans.</summary>
    public static readonly HashSet<string> HumanTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Q5", "Q215627"
    };

    /// <summary>True when the phrase is globally obvious.</summary>
    public static bool IsObvious(string phrase)
        => !string.IsNullOrWhiteSpace(phrase) && ObviousTerms.Contains(phrase.Trim());
}
