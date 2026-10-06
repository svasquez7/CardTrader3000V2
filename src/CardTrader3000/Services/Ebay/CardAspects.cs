using System.Text.Json;
using System.Text.RegularExpressions;
using CardTrader3000.Data.Entities;

namespace CardTrader3000.Services.Ebay;

/// <summary>
/// Builds eBay item specifics (aspects) for category 261328 from an inventory card. These are
/// best-effort defaults; every value is editable on the listing screen before publishing.
/// </summary>
public static partial class CardAspects
{
    public const int MaxNameLength = 40;
    public const int MaxValueLength = 50;

    private static readonly (string Word, string Sport, string League)[] Sports =
    [
        ("football", "Football", "National Football League (NFL)"),
        ("baseball", "Baseball", "Major League (MLB)"),
        ("basketball", "Basketball", "National Basketball Association (NBA)"),
        ("hockey", "Ice Hockey", "National Hockey League (NHL)"),
        ("soccer", "Soccer", ""),
    ];

    /// <summary>Values offered for the Sport item specific on the listing screen.</summary>
    public static readonly string[] SportValues = ["Football", "Baseball", "Basketball", "Ice Hockey", "Soccer"];

    // Full team names (current plus common historical names on older cards) → sport.
    // Matched against the card's team as a substring, so "New York Giants" ≠ "San Francisco Giants".
    private static readonly Dictionary<string, string[]> TeamsBySport = new()
    {
        ["football"] =
        [
            "Arizona Cardinals", "Atlanta Falcons", "Baltimore Ravens", "Buffalo Bills", "Carolina Panthers", "Chicago Bears",
            "Cincinnati Bengals", "Cleveland Browns", "Dallas Cowboys", "Denver Broncos", "Detroit Lions", "Green Bay Packers",
            "Houston Texans", "Indianapolis Colts", "Jacksonville Jaguars", "Kansas City Chiefs", "Las Vegas Raiders",
            "Los Angeles Chargers", "Los Angeles Rams", "Miami Dolphins", "Minnesota Vikings", "New England Patriots",
            "New Orleans Saints", "New York Giants", "New York Jets", "Philadelphia Eagles", "Pittsburgh Steelers",
            "San Francisco 49ers", "Seattle Seahawks", "Tampa Bay Buccaneers", "Tennessee Titans", "Washington Commanders",
            "Oakland Raiders", "Los Angeles Raiders", "San Diego Chargers", "St. Louis Rams", "Houston Oilers",
            "Tennessee Oilers", "Baltimore Colts", "Washington Redskins", "Washington Football Team", "Phoenix Cardinals"
        ],
        ["baseball"] =
        [
            "Arizona Diamondbacks", "Atlanta Braves", "Baltimore Orioles", "Boston Red Sox", "Chicago Cubs", "Chicago White Sox",
            "Cincinnati Reds", "Cleveland Guardians", "Colorado Rockies", "Detroit Tigers", "Houston Astros", "Kansas City Royals",
            "Los Angeles Angels", "Los Angeles Dodgers", "Miami Marlins", "Milwaukee Brewers", "Minnesota Twins",
            "New York Mets", "New York Yankees", "Athletics", "Philadelphia Phillies", "Pittsburgh Pirates",
            "San Diego Padres", "San Francisco Giants", "Seattle Mariners", "St. Louis Cardinals", "Tampa Bay Rays",
            "Texas Rangers", "Toronto Blue Jays", "Washington Nationals",
            "Cleveland Indians", "Oakland Athletics", "Anaheim Angels", "California Angels", "Florida Marlins",
            "Tampa Bay Devil Rays", "Montreal Expos", "Brooklyn Dodgers"
        ],
        ["basketball"] =
        [
            "Atlanta Hawks", "Boston Celtics", "Brooklyn Nets", "Charlotte Hornets", "Chicago Bulls", "Cleveland Cavaliers",
            "Dallas Mavericks", "Denver Nuggets", "Detroit Pistons", "Golden State Warriors", "Houston Rockets",
            "Indiana Pacers", "Los Angeles Clippers", "Los Angeles Lakers", "Memphis Grizzlies", "Miami Heat",
            "Milwaukee Bucks", "Minnesota Timberwolves", "New Orleans Pelicans", "New York Knicks", "Oklahoma City Thunder",
            "Orlando Magic", "Philadelphia 76ers", "Phoenix Suns", "Portland Trail Blazers", "Sacramento Kings",
            "San Antonio Spurs", "Toronto Raptors", "Utah Jazz", "Washington Wizards",
            "Seattle SuperSonics", "New Jersey Nets", "Vancouver Grizzlies", "Charlotte Bobcats", "New Orleans Hornets",
            "Washington Bullets", "San Diego Clippers"
        ],
        ["hockey"] =
        [
            "Anaheim Ducks", "Boston Bruins", "Buffalo Sabres", "Calgary Flames", "Carolina Hurricanes", "Chicago Blackhawks",
            "Colorado Avalanche", "Columbus Blue Jackets", "Dallas Stars", "Detroit Red Wings", "Edmonton Oilers",
            "Florida Panthers", "Los Angeles Kings", "Minnesota Wild", "Montreal Canadiens", "Nashville Predators",
            "New Jersey Devils", "New York Islanders", "New York Rangers", "Ottawa Senators", "Philadelphia Flyers",
            "Pittsburgh Penguins", "San Jose Sharks", "Seattle Kraken", "St. Louis Blues", "Tampa Bay Lightning",
            "Toronto Maple Leafs", "Utah Hockey Club", "Utah Mammoth", "Vancouver Canucks", "Vegas Golden Knights",
            "Washington Capitals", "Winnipeg Jets",
            "Mighty Ducks of Anaheim", "Atlanta Thrashers", "Phoenix Coyotes", "Arizona Coyotes", "Quebec Nordiques",
            "Hartford Whalers", "Minnesota North Stars"
        ]
    };

    /// <summary>
    /// The card's sport and league, from the set name ("2013 Topps Football") or, failing that,
    /// the team ("Denver Broncos"). Null when neither gives it away.
    /// </summary>
    public static (string Sport, string League)? InferSport(InventoryCard card)
    {
        var set = card.CardSet.ToLowerInvariant();
        foreach (var s in Sports)
            if (set.Contains(s.Word)) return (s.Sport, s.League);

        if (!string.IsNullOrWhiteSpace(card.Team))
        {
            var team = card.Team.Trim();
            foreach (var (word, teams) in TeamsBySport)
            {
                if (teams.Any(t => team.Contains(t, StringComparison.OrdinalIgnoreCase)))
                {
                    var s = Sports.First(x => x.Word == word);
                    return (s.Sport, s.League);
                }
            }
        }
        return null;
    }

    // Product line → manufacturer. Checked in order; first match wins.
    private static readonly (string Word, string Manufacturer)[] Brands =
    [
        ("bowman", "Topps"), ("stadium club", "Topps"), ("topps", "Topps"),
        ("prizm", "Panini"), ("select", "Panini"), ("mosaic", "Panini"), ("optic", "Panini"),
        ("contenders", "Panini"), ("prestige", "Panini"), ("absolute", "Panini"), ("chronicles", "Panini"),
        ("certified", "Panini"), ("panini", "Panini"),
        ("upper deck", "Upper Deck"), ("fleer", "Fleer"), ("skybox", "SkyBox"), ("score", "Score"),
        ("leaf", "Leaf"), ("pacific", "Pacific"), ("pinnacle", "Pinnacle"), ("donruss", "Donruss"),
    ];

    public static Dictionary<string, List<string>> FromCard(InventoryCard card)
    {
        var set = card.CardSet;
        var lower = set.ToLowerInvariant();
        var year = YearRegex().Match(set) is { Success: true } m ? int.Parse(m.Value) : (int?)null;

        var sport = InferSport(card);
        var manufacturer = Brands.FirstOrDefault(b => lower.Contains(b.Word)).Manufacturer;
        // Donruss has been made by Panini since 2014.
        if (manufacturer == "Donruss" && year >= 2014) manufacturer = "Panini";

        var aspects = new Dictionary<string, List<string>>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) aspects[name] = [Trim(value)];
        }

        Add("Type", "Sports Trading Card");
        Add("Sport", sport?.Sport);
        Add("League", sport?.League);
        Add("Player/Athlete", card.PlayerName);
        Add("Team", card.Team);
        Add("Manufacturer", manufacturer);
        Add("Set", set);
        Add("Season", year?.ToString());
        Add("Year Manufactured", year?.ToString());
        Add("Card Number", card.CardNumber);
        if (!card.Parallel.Equals("Base", StringComparison.OrdinalIgnoreCase)) Add("Parallel/Variety", card.Parallel);
        if (card.IsRookie) Add("Features", "Rookie");
        Add("Autographed", "No");
        Add("Original/Licensed Reprint", "Original");
        Add("Card Size", "Standard");
        Add("Vintage", year is < 1980 ? "Yes" : "No");
        Add("Language", "English");

        return aspects;
    }

    public static string Serialize(Dictionary<string, List<string>> aspects) => JsonSerializer.Serialize(aspects);

    public static Dictionary<string, List<string>> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private static string Trim(string value)
    {
        value = value.Trim();
        return value.Length > MaxValueLength ? value[..MaxValueLength].TrimEnd() : value;
    }

    [GeneratedRegex(@"\b(19|20)\d{2}\b")]
    private static partial Regex YearRegex();
}
