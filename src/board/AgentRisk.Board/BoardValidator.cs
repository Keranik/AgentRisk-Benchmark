namespace AgentRisk.Board;

/// <summary>
/// Structural validation of raw <see cref="BoardData"/>. Returns every problem
/// found (an empty list means the data is valid); it never throws, so callers
/// can report all problems at once. The checks are the ones the engine
/// relies on: referential integrity, adjacency symmetry, and
/// a connected board graph.
/// </summary>
public static class BoardValidator
{
    public static IReadOnlyList<string> Validate(BoardData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var problems = new List<string>();

        if (data.WildCards != BoardConstants.WildCardCount)
        {
            problems.Add($"wildCards must be {BoardConstants.WildCardCount}, got {data.WildCards}");
        }

        ValidateTerritoryFields(data, problems);
        var membership = ValidateContinents(data, problems);
        ValidateTerritoryContinents(data, membership, problems);
        ValidateAdjacency(data, problems);
        ValidateCards(data, problems);

        // Connectivity is only meaningful once the references above check out.
        if (problems.Count == 0)
        {
            ValidateConnectivity(data, problems);
        }

        return problems;
    }

    /// <summary>Unique territory codes and basic field sanity.</summary>
    private static void ValidateTerritoryFields(BoardData data, List<string> problems)
    {
        var codes = new HashSet<string>();
        foreach (var territory in data.Territories)
        {
            if (!codes.Add(territory.Code))
            {
                problems.Add($"duplicate territory code {territory.Code}");
            }

            if (string.IsNullOrEmpty(territory.Name))
            {
                problems.Add($"{territory.Code}: missing name");
            }
        }
    }

    /// <summary>
    /// Unique continent codes, positive bonuses, members that exist, appear
    /// once, and belong to no other continent. Returns the membership map the
    /// member lists describe (territory code -> continent code).
    /// </summary>
    private static Dictionary<string, string> ValidateContinents(BoardData data, List<string> problems)
    {
        var territoryCodes = data.Territories.Select(t => t.Code).ToHashSet();
        var continentCodes = new HashSet<string>();
        var membership = new Dictionary<string, string>();

        foreach (var continent in data.Continents)
        {
            if (!continentCodes.Add(continent.Code))
            {
                problems.Add($"duplicate continent code {continent.Code}");
            }

            if (continent.Bonus <= 0)
            {
                problems.Add($"{continent.Code}: bonus must be positive, got {continent.Bonus}");
            }

            var seen = new HashSet<string>();
            foreach (var member in continent.Members)
            {
                if (!territoryCodes.Contains(member))
                {
                    problems.Add($"{continent.Code}: member {member} is not a territory");
                }

                if (!seen.Add(member))
                {
                    problems.Add($"{continent.Code}: member {member} listed twice");
                }

                if (membership.TryGetValue(member, out var other))
                {
                    problems.Add($"{member}: member of two continents ({other} and {continent.Code})");
                }

                membership[member] = continent.Code;
            }
        }

        return membership;
    }

    /// <summary>
    /// Each territory's continent field must name a real continent and agree
    /// with the continent member lists, in both directions.
    /// </summary>
    private static void ValidateTerritoryContinents(
        BoardData data, Dictionary<string, string> membership, List<string> problems)
    {
        var continentCodes = data.Continents.Select(c => c.Code).ToHashSet();

        foreach (var territory in data.Territories)
        {
            if (!continentCodes.Contains(territory.Continent))
            {
                problems.Add($"{territory.Code}: unknown continent {territory.Continent}");
            }
            else if (!membership.TryGetValue(territory.Code, out var listed) || listed != territory.Continent)
            {
                problems.Add(
                    $"{territory.Code}: continent field {territory.Continent} disagrees with members lists ({listed ?? "none"})");
            }
        }

        foreach (var territory in data.Territories)
        {
            if (!membership.ContainsKey(territory.Code))
            {
                problems.Add($"{territory.Code}: not a member of any continent");
            }
        }
    }

    /// <summary>Every neighbor exists; no self-loops, no duplicates, and every border is listed from both sides.</summary>
    private static void ValidateAdjacency(BoardData data, List<string> problems)
    {
        var codes = data.Territories.Select(t => t.Code).ToHashSet();
        var byCode = data.Territories.ToDictionary(t => t.Code);

        foreach (var territory in data.Territories)
        {
            var seen = new HashSet<string>();
            foreach (var neighbor in territory.Neighbors)
            {
                if (!codes.Contains(neighbor))
                {
                    problems.Add($"{territory.Code}: neighbor {neighbor} does not exist");
                }

                if (neighbor == territory.Code)
                {
                    problems.Add($"{territory.Code}: neighbor of itself");
                }

                if (!seen.Add(neighbor))
                {
                    problems.Add($"{territory.Code}: neighbor {neighbor} listed twice");
                }
            }

            foreach (var neighbor in territory.Neighbors)
            {
                if (byCode.TryGetValue(neighbor, out var back) && !back.Neighbors.Contains(territory.Code))
                {
                    problems.Add($"asymmetric border: {territory.Code} lists {neighbor} but not vice versa");
                }
            }
        }
    }

    /// <summary>Exactly one valid card per territory, and no cards for territories that do not exist.</summary>
    private static void ValidateCards(BoardData data, List<string> problems)
    {
        var extraCards = data.Cards.Keys.ToHashSet();
        foreach (var territory in data.Territories)
        {
            if (!data.Cards.TryGetValue(territory.Code, out var card) || !CardTypeCode.IsValidCode(card))
            {
                problems.Add($"{territory.Code}: missing or invalid card type {card ?? "null"}");
            }

            extraCards.Remove(territory.Code);
        }

        foreach (var extra in extraCards)
        {
            problems.Add($"cards: entry for unknown territory {extra}");
        }
    }

    /// <summary>The board graph must be a single connected component (breadth-first search over the borders).</summary>
    private static void ValidateConnectivity(BoardData data, List<string> problems)
    {
        if (data.Territories.Count == 0)
        {
            return;
        }

        var byCode = data.Territories.ToDictionary(t => t.Code);
        var seen = new HashSet<string> { data.Territories[0].Code };
        var queue = new Queue<string>();
        queue.Enqueue(data.Territories[0].Code);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var neighbor in byCode[current].Neighbors)
            {
                if (seen.Add(neighbor))
                {
                    queue.Enqueue(neighbor);
                }
            }
        }

        if (seen.Count != data.Territories.Count)
        {
            problems.Add($"board is not connected: reached {seen.Count} of {data.Territories.Count} territories");
        }
    }
}
