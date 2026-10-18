using System.Text.Json.Serialization;
using SphServer.Server.Config;

namespace SphServer.Server.GameplayLogic.Cult;

/// <summary>
/// cult.json: the Культ шестерых event clan, its respawn point and how karma bends its drops
/// </summary>
public class CultBalance : IValidatableBalanceConfig
{
    /// <summary>
    /// Fixed so a renamed clan keeps its members
    /// </summary>
    [JsonPropertyName ("clan_id")]
    public int ClanId { get; init; } = 6;

    /// <summary>
    /// The nameplate cuts clan names at 15 cp1251 bytes
    /// </summary>
    [JsonPropertyName ("clan_name")]
    public string ClanName { get; init; } = "КУЛЬТ";

    [JsonPropertyName ("member_rank")]
    public ClanRank MemberRank { get; init; } = ClanRank.Neophyte;

    /// <summary>
    /// World coords (x, y, z, turn) where members revive; null keeps the nearest city
    /// </summary>
    [JsonPropertyName ("respawn")]
    public double[]? Respawn { get; init; }

    /// <summary>
    /// turnskin SK number given on joining (1 = crater dwarf); 0 gives none
    /// </summary>
    [JsonPropertyName ("skin_number")]
    public byte SkinNumber { get; init; } = 1;

    /// <summary>
    /// Loot chance multiplier per karma tier, read through the mirrored tier for members
    /// </summary>
    [JsonPropertyName ("drop_chance_multiplier_per_karma")]
    public Dictionary<KarmaTypes, double> DropChanceMultiplierPerKarma { get; init; } = new ();

    /// <summary>
    /// Extra loot items per karma tier, read the same way
    /// </summary>
    [JsonPropertyName ("extra_drop_items_per_karma")]
    public Dictionary<KarmaTypes, int> ExtraDropItemsPerKarma { get; init; } = new ();

    /// <summary>
    /// Chance a member drops a carried item on death, per karma tier as an ordinary player would
    /// have it; members read it through the mirrored tier, so benign karma drops the most
    /// </summary>
    [JsonPropertyName ("death_drop_chance_per_karma")]
    public Dictionary<KarmaTypes, double> DeathDropChancePerKarma { get; init; } = new ();

    [JsonPropertyName ("death_drop_max_items")]
    public int DeathDropMaxItems { get; init; } = 1;

    /// <summary>
    /// Off keeps karma out of drops for everyone outside the clan
    /// </summary>
    [JsonPropertyName ("karma_drops_for_non_members")]
    public bool KarmaDropsForNonMembers { get; init; }

    public void Validate (string configPath)
    {
        if (string.IsNullOrWhiteSpace (ClanName))
        {
            throw new InvalidDataException ($"{configPath}: clan_name is empty");
        }

        if (Respawn is not null && Respawn.Length is not (3 or 4))
        {
            throw new InvalidDataException ($"{configPath}: respawn needs x, y, z and an optional turn");
        }

        if (DropChanceMultiplierPerKarma.Values.Any (m => m < 0))
        {
            throw new InvalidDataException ($"{configPath}: drop_chance_multiplier_per_karma has a negative value");
        }
    }
}
