using LiteDB;
using SphServer.Client;
using SphServer.Client.Networking.GameplayLogic.Stats;
using SphServer.Helpers;
using SphServer.Server.Config;
using SphServer.Shared.Db;
using SphServer.Shared.Db.DataModels;
using SphServer.Shared.GameData.Enums;
using SphServer.Shared.Logger;
using SphServer.Sphere.Game.WorldObject;

namespace SphServer.Server.GameplayLogic.Cult;

/// <summary>
/// Культ шестерых: an admin event clan whose members see karma work the other way round
/// </summary>
public static class CultRules
{
    private static CultBalance? Config => BalanceConfig.Get<CultBalance> ("cult");

    public static bool IsMember (CharacterDbEntry? character) =>
        character?.Clan is { } clan && Config is { } cfg && clan.Id == cfg.ClanId;

    /// <summary>
    /// Very bad reads as benign and bad as good, so evil deeds pay off for members
    /// </summary>
    public static KarmaTypes Mirror (KarmaTypes karma) =>
        karma is >= KarmaTypes.Очень_Плохая and <= KarmaTypes.Благая
            ? (KarmaTypes) ((int) KarmaTypes.Очень_Плохая + (int) KarmaTypes.Благая - (int) karma)
            : karma;

    /// <summary>
    /// The tier karma effects read; the shown karma stays the real one
    /// </summary>
    public static KarmaTypes EffectiveKarma (CharacterDbEntry character) =>
        IsMember (character) ? Mirror (character.Karma) : character.Karma;

    /// <summary>
    /// Created on first use so a fresh DB needs no seeding step
    /// </summary>
    public static ClanDbEntry? GetOrCreateClan ()
    {
        if (Config is not { } cfg)
        {
            return null;
        }

        var clan = DbConnection.Clans.FindById (cfg.ClanId);
        if (clan is not null && clan.Name == cfg.ClanName)
        {
            return clan;
        }

        clan = new ClanDbEntry { Id = cfg.ClanId, Name = cfg.ClanName };
        DbConnection.Clans.Upsert (clan.Id, clan);
        return clan;
    }

    public static bool Join (SphereClient client, ClanRank? rank = null)
    {
        var character = client.CurrentCharacter;
        if (character is null || Config is not { } cfg || GetOrCreateClan () is not { } clan)
        {
            return false;
        }

        var oldClan = character.Clan?.Name;
        character.Clan = clan;
        character.ClanRank = rank ?? cfg.MemberRank;
        NetworkedStatsUpdater.Update (character, refreshPeers: true);
        client.BroadcastClanRefreshToVisibleClients ();
        client.SaveCharacter ();
        SphLogger.Info ($"Cult: {character.Name} joined [{clan.Name}] as {character.ClanRank} (was [{oldClan}])");
        return true;
    }

    /// <summary>
    /// The client keeps the old nameplate until the next spawn: the clan refresh has no "no clan" form
    /// </summary>
    public static bool Leave (SphereClient client)
    {
        var character = client.CurrentCharacter;
        if (character is null || !IsMember (character))
        {
            return false;
        }

        character.Clan = ClanDbEntry.DefaultClanDbEntry;
        character.ClanRank = ClanRank.Neophyte;
        NetworkedStatsUpdater.Update (character, refreshPeers: true);
        client.SaveCharacter ();
        SphLogger.Info ($"Cult: {character.Name} left the cult");
        return true;
    }

    private const int StateId = 1;

    private static ILiteCollection<CultStateDbEntry> State => DbConnection.Db.GetCollection<CultStateDbEntry> ("Cult");

    /// <summary>
    /// The point set in game wins over cult.json, so a GM can place it without a redeploy
    /// </summary>
    public static WorldCoords? RespawnPoint
    {
        get
        {
            var r = State.FindById (StateId)?.Respawn ?? Config?.Respawn;
            return r is { Length: >= 3 } ? new WorldCoords (r[0], r[1], r[2], r.Length > 3 ? r[3] : 0) : null;
        }
    }

    public static void SetRespawnPoint (WorldCoords point)
    {
        State.Upsert (new CultStateDbEntry { Id = StateId, Respawn = [point.x, point.y, point.z, point.turn] });
        SphLogger.Info ($"Cult: respawn point set to [{point}]");
    }

    /// <summary>
    /// Members revive at the cult's own point; everyone else keeps the nearest city
    /// </summary>
    public static (Cities City, WorldCoords Coords) MaybeOverrideRespawn (CharacterDbEntry character,
        (Cities City, WorldCoords Coords) nearest)
    {
        return IsMember (character) && RespawnPoint is { } point ? (nearest.City, point) : nearest;
    }

    /// <summary>
    /// Count 0 cancels the drop; the chance is not clamped so a multiplier can make it certain
    /// </summary>
    public static void AdjustDrop (Monster monster, CharacterDbEntry killer, ref double chance, ref int count,
        ref LootRatity ratity)
    {
        if (Config is not { } cfg)
        {
            return;
        }

        var member = IsMember (killer);
        if (!member && !cfg.KarmaDropsForNonMembers)
        {
            return;
        }

        var karma = EffectiveKarma (killer);
        if (cfg.DropChanceMultiplierPerKarma.TryGetValue (karma, out var multiplier))
        {
            chance *= multiplier;
        }

        if (cfg.ExtraDropItemsPerKarma.TryGetValue (karma, out var extra))
        {
            count = Math.Max (0, count + extra);
        }
    }
}
