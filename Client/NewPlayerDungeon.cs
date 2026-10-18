using System;
using System.Collections.Generic;
using Godot;
using SphServer.Client.Networking.GameplayLogic.Stats;
using SphServer.Helpers;
using SphServer.Packets;
using SphServer.Server.Config;
using SphServer.Shared.Db;
using SphServer.Shared.Db.DataModels;
using SphServer.Shared.GameData.Enums;
using SphServer.Shared.Logger;
using SphServer.Shared.Networking;
using SphServer.Shared.Networking.DataModel.Serializers;
using SphServer.Shared.WorldState;
using SphServer.Sphere.Game;
using SphServer.Sphere.Game.WorldObject;
using SphereServer = SphServer.Server.SphereServer;

namespace SphServer.Client;

/// <summary>
/// First world entry of a new character: placed in the captured dungeon instance, teleported there
/// after the client settles, then the dungeon geometry (labyr) and the captured dungeon objects
/// (guide, sword, token, chest, tutorial tablet) are sent
/// </summary>
public static class NewPlayerDungeon
{
    /// <summary>
    /// World coords of the captured instance spawn; the serializer flips y itself
    /// </summary>
    private static readonly WorldCoords TeleportCoords =
        new (-1098.69506835937500, -4501.61474609375000, 1900.05493164062500, 1.57079637050629);

    /// <summary>
    /// No sst file has geometry above y 2100: the client builds the dungeon from
    /// params\mazes\x000.mbd ('a' + 23), whose guide, sword, token and spawn markers match the
    /// captured objects when placed at this room center (GetRoomCoord grid, 200 units)
    /// </summary>
    private const ushort MazeId = 23000;

    private const float LabyrinthX = -1100f;
    private const float LabyrinthY = 4500f;
    private const float LabyrinthZ = 1900f;

    private const int StartingMoney = 10;
    private const double TeleportDelaySeconds = 1;
    private const double WorldLoadPollSeconds = 0.25;
    private const double FirstTeleportMaxWaitSeconds = 10;
    private const double TeleportRetrySeconds = 5;
    private const int TeleportRetryCount = 24;
    private const double LoadDelaySeconds = 0.5;

    /// <summary>
    /// x000 npc47 marker (maze -3948.41 1101.51 -3880.92); the live capture has a Psoglavets
    /// monster standing exactly there that hits the newcomer
    /// </summary>
    private static readonly Vector3 DungeonMobWorld = new (-1148.41f, 4501.51f, 1919.08f);

    private const int DungeonMobTypeId = 1010;

    /// <summary>
    /// x000 portal1 marker (maze -3899.59 1101.43 -3852.13), the dungeon exit
    /// </summary>
    private static readonly Vector3 ExitPortalWorld = new (-1099.59f, 4501.43f, 1947.87f);

    private const float ExitPortalRadius = 2.5f;

    /// <summary>
    /// Torweal city center; the dungeon token is Token_Tutorial_Torweal
    /// </summary>
    private static readonly WorldCoords ExitCoords = new (2292, 155.3, -2386);

    /// <summary>
    /// Live wp_sword1st (weapon_starting_sword) on the pedestal, next to the x000 wp_sword1 marker
    /// (maze -3930.07 1100.82 -3899.86)
    /// </summary>
    private static readonly Vector3 StarterSwordWorld = new (-1130.093f, 4500.912f, 1900.147f);

    /// <summary>
    /// Кривой меч: wp_sword1st has no game object, and this is the weakest catalog sword
    /// </summary>
    private const int StarterSwordGameId = 1;

    /// <summary>
    /// Not a process id, so the client keeps the sword on the ground
    /// </summary>
    private const ushort GroundContainerId = 0xFF00;

    /// <summary>
    /// tutomsg triggers: the client opens language	utoNN.hts while the player stands near one.
    /// Numbers follow the tuto texts (greeting, sword, Псоглавец, merchant, powder, stats, exit);
    /// positions are the x000 "empty" markers along the route
    /// </summary>
    private static readonly (ushort MessageNumber, Vector3 World)[] TutorialPoints =
    [
        (1, new (-1099.4f, 4501.5f, 1899.9f)),
        (2, new (-1126.5f, 4501.5f, 1900.0f)),
        (3, new (-1144.9f, 4501.5f, 1904.8f)),
        (4, new (-1133.2f, 4501.5f, 1930.6f)),
        (5, new (-1117.5f, 4501.5f, 1930.1f)),
        (6, new (-1099.8f, 4501.5f, 1933.0f)),
        (7, new (-1099.6f, 4501.5f, 1945.2f))
    ];

    private static readonly ushort[] tutorialEntityIds = new ushort[TutorialPoints.Length];

    /// <summary>
    /// x000 token marker on its stone; the captured replay brings its own copy as 0B16, which is
    /// removed so only the server's token, whose pickup is answered, stays
    /// </summary>
    private static readonly Vector3 TokenWorld = new (-1099.616f, 4501.088f, 1943.19f);

    private const ushort ReplayTokenEntityId = 0x0B16;

    /// <summary>
    /// tokenst setup (ContMan 15, u24 LOCKID + u8 NUM): teleport point 1803 «В Торвил», one charge
    /// </summary>
    private static readonly byte[] TokenSetup = [0x0B, 0x07, 0x00, 1];

    private static ushort tokenEntityId;
    private static readonly HashSet<ushort> tokenShownTo = [];

    /// <summary>
    /// Clients holding the token leave the original way, by using it on the portal
    /// </summary>
    private static readonly HashSet<ushort> tokenTakenBy = [];
    private static ushort exitTeleportEntityId;

    /// <summary>
    /// When each client last left through the portal: its next position update can still be on
    /// the portal, and the entry teleport retries must not pull it back in
    /// </summary>
    private static readonly Dictionary<ushort, DateTime> lastExitAt = [];

    private const double ExitCooldownSeconds = 10;

    private const double TokenHolderPortalGraceSeconds = 10;

    private static readonly Dictionary<ushort, DateTime> onPortalSince = [];

    /// <summary>
    /// When the client's own position update last put it inside the maze; once it has, the
    /// teleport has landed and the retries stop, so a later /tp or exit is left alone
    /// </summary>
    private static readonly Dictionary<ushort, DateTime> lastInsideReportAt = [];
    private static ushort labyrinthEntityId;
    private static ushort starterSwordEntityId;
    private static Monster? dungeonMob;

    /// <summary>
    /// Client indexes that still see the pedestal sword; each one may take it once per entry
    /// </summary>
    private static readonly HashSet<ushort> starterSwordShownTo = [];

    /// <summary>
    /// The client hangs on the loading screen when the session starts inside the dungeon, so the
    /// character enters at the regular spawn and only the teleport moves it
    /// </summary>
    public static void PlaceCharacter (CharacterDbEntry character)
    {
        PlaceAtSpawn (character);
        character.Money = StartingMoney;
    }

    public static void PlaceAtSpawn (CharacterDbEntry character)
    {
        character.X = ServerConfig.AppConfig.Spawn_X;
        character.Y = -ServerConfig.AppConfig.Spawn_Y;
        character.Z = -ServerConfig.AppConfig.Spawn_Z;
        character.Angle = ServerConfig.AppConfig.Spawn_Angle;
    }

    private static void MoveToDungeon (CharacterDbEntry character, WorldCoords target)
    {
        // DB keeps Y and Z negated relative to world coords; the target's y is already flipped
        character.X = target.x;
        character.Y = target.y;
        character.Z = -target.z;
        character.Angle = target.turn;
    }

    /// <summary>
    /// One id for every viewer: each client owns its own labyr process
    /// </summary>
    private static ushort GetLabyrinthEntityId ()
    {
        if (labyrinthEntityId == 0)
        {
            labyrinthEntityId = WorldObjectIndex.New ();
        }

        return labyrinthEntityId;
    }

    public static async void Start (SphereClient client)
    {
        try
        {
            // Client indexes are reused, and the token is not a DB item, so it does not survive a
            // relog: every new session starts without it and sees it on the stone again
            if (client.CurrentCharacter is { } entering)
            {
                tokenTakenBy.Remove (entering.ClientIndex);
                tokenShownTo.Remove (entering.ClientIndex);
            }

            // A teleport that lands during the loading screen is dropped, and how long that screen
            // lasts varies (seconds on a warm client, half a minute in a VM; the world-loaded frame
            // does not always come). So the teleport is sent early, re-sent once the client reports
            // that frame, and re-sent while the client's own position updates still put it outside
            for (var waited = 0.0; !client.WorldLoaded && waited < FirstTeleportMaxWaitSeconds; waited += WorldLoadPollSeconds)
            {
                if (!await Delay (client, WorldLoadPollSeconds))
                {
                    return;
                }
            }

            if (!await Delay (client, TeleportDelaySeconds))
            {
                return;
            }

            var startedAt = DateTime.UtcNow;
            Enter (client, replayCapturedObjects: true);
            var sawWorldLoaded = client.WorldLoaded;
            for (var attempt = 1; attempt <= TeleportRetryCount; attempt++)
            {
                if (!await Delay (client, TeleportRetrySeconds) || client.CurrentCharacter is not { } character)
                {
                    return;
                }

                if (lastExitAt.TryGetValue (character.ClientIndex, out var exitedAt) && exitedAt > startedAt
                    || lastInsideReportAt.TryGetValue (character.ClientIndex, out var insideAt) && insideAt > startedAt)
                {
                    return;
                }

                var worldJustLoaded = client.WorldLoaded && !sawWorldLoaded;
                sawWorldLoaded = client.WorldLoaded;
                if (IsInside (character) && !worldJustLoaded)
                {
                    continue;
                }

                SphLogger.Info ($"New player dungeon: re-sending the teleport (attempt {attempt}) for {character.Name}. " +
                                $"Client ID: {character.ClientIndex:X4}");
                Enter (client, replayCapturedObjects: true);
            }
        }
        catch (Exception ex)
        {
            SphLogger.Error ("New player dungeon start failed", ex);
        }
    }

    /// <summary>
    /// False once the client is gone
    /// </summary>
    private static async global::System.Threading.Tasks.Task<bool> Delay (SphereClient client, double seconds)
    {
        await client.ToSignal (client.GetTree ().CreateTimer (seconds), SceneTreeTimer.SignalName.Timeout);
        return GodotObject.IsInstanceValid (client);
    }

    /// <summary>
    /// GM /npd entry; without the captured replay only the teleport, the geometry, the mob and the
    /// pedestal sword go out
    /// </summary>
    public static void Enter (SphereClient client, bool replayCapturedObjects) =>
        EnterAt (client, TeleportCoords, replayCapturedObjects);

    /// <summary>
    /// GM /npd x y z: a plain /tp inside the maze drops the client-built labyrinth, so the jump goes
    /// through the dungeon entry, which sends the geometry again
    /// </summary>
    public static void EnterAt (SphereClient client, double worldX, double worldY, double worldZ) =>
        EnterAt (client, new WorldCoords (worldX, -worldY, worldZ, TeleportCoords.turn), false);

    private static async void EnterAt (SphereClient client, WorldCoords target, bool replayCapturedObjects)
    {
        try
        {
            if (client.CurrentCharacter is not { } character)
            {
                return;
            }

            client.MaybeQueueNetworkPacketSend (
                new CharacterDbEntrySerializer (character).GetNewPlayerDungeonTeleportAndUpdateStatsByteArray (target));
            MoveToDungeon (character, target);
            client.SaveCharacter ();

            await client.ToSignal (client.GetTree ().CreateTimer (LoadDelaySeconds), SceneTreeTimer.SignalName.Timeout);
            if (!GodotObject.IsInstanceValid (client))
            {
                return;
            }

            client.MaybeQueueNetworkPacketSend (CommonPackets.BuildLabyrinthSpawn (
                client.GetLocalObjectId (GetLabyrinthEntityId ()), LabyrinthX, LabyrinthY, LabyrinthZ, MazeId));
            SendTutorialObjects (client);
            if (replayCapturedObjects)
            {
                client.MaybeQueueNetworkPacketSend (CommonPackets.LoadNewPlayerDungeon);
                // The replay carries the captured newcomer's stats (level 1, zeros) and money
                client.SendMoney ();
                NetworkedStatsUpdater.Update (character, client.MaybeQueueNetworkPacketSend, full: true);
            }

            SendStarterSword (client);
            EnsureDungeonMob ();

            SphLogger.Info ($"New player dungeon loaded for {character.Name}. Client ID: {character.ClientIndex:X4}");
        }
        catch (Exception ex)
        {
            SphLogger.Error ("New player dungeon enter failed", ex);
        }
    }

    /// <summary>
    /// The tutorial triggers and the telep1 exit portal; labyr builds only walls, so the original
    /// server spawned these. The token on the floor comes with the captured dungeon objects
    /// </summary>
    private static void SendTutorialObjects (SphereClient client)
    {
        for (var i = 0; i < TutorialPoints.Length; i++)
        {
            if (tutorialEntityIds[i] == 0)
            {
                tutorialEntityIds[i] = WorldObjectIndex.New ();
            }

            var (number, world) = TutorialPoints[i];
            client.MaybeQueueNetworkPacketSend (CommonPackets.BuildContManSpawn (
                client.GetLocalObjectId (tutorialEntityIds[i]), ObjectType.Tutorial_Message, world.X, world.Y, world.Z,
                15, [(byte) number, (byte) (number >> 8)]));
        }

        if (exitTeleportEntityId == 0)
        {
            exitTeleportEntityId = WorldObjectIndex.New ();
        }

        if (tokenEntityId == 0)
        {
            tokenEntityId = WorldObjectIndex.New ();
        }

        client.MaybeQueueNetworkPacketSend (CommonPackets.DespawnEntity (ReplayTokenEntityId));
        // A token already in this client's bag would jump back onto the stone if spawned again
        if (client.CurrentCharacter is { } viewer && !tokenTakenBy.Contains (viewer.ClientIndex))
        {
            client.MaybeQueueNetworkPacketSend (CommonPackets.BuildContManSpawn (
                client.GetLocalObjectId (tokenEntityId), ObjectType.Token_Tutorial_Torweal,
                TokenWorld.X, TokenWorld.Y, TokenWorld.Z, 15, TokenSetup));
            tokenShownTo.Add (viewer.ClientIndex);
        }

        client.MaybeQueueNetworkPacketSend (CommonPackets.BuildContManSpawn (
            client.GetLocalObjectId (exitTeleportEntityId), ObjectType.Teleport,
            ExitPortalWorld.X, ExitPortalWorld.Y, ExitPortalWorld.Z));
    }

    /// <summary>
    /// One mob for the shared instance, respawned on the next entry once killed
    /// </summary>
    private static void EnsureDungeonMob ()
    {
        if (dungeonMob is not null && GodotObject.IsInstanceValid (dungeonMob) && !dungeonMob.IsDead)
        {
            return;
        }

        if (!GameObjectDb.Db.TryGetValue (DungeonMobTypeId, out var gameObject) || gameObject is null
            || !MonsterTypeMapping.MonsterTypeToMonsterNameMapping.TryGetValue (DungeonMobTypeId, out var monsterType))
        {
            SphLogger.Warning ($"New player dungeon: monster type {DungeonMobTypeId} is not spawnable");
            return;
        }

        var scene = (PackedScene) ResourceLoader.Load ("res://Godot/Scenes/Monster.tscn");
        var monster = scene.Instantiate<Monster> ();
        monster.MonsterType = monsterType;
        monster.MonsterInstance = new SphMonsterInstance (new SphMonsterData (gameObject), 1, NamedBossRank.None);
        monster.Angle = WorldObject.CreateRandomSpawnAngle ();
        monster.Name = $"NewPlayerDungeon_{monsterType}";
        monster.ID = WorldObjectIndex.New ();
        SphereServer.ServerNode.CallDeferred ("add_child", monster);
        // Godot space keeps Y and Z negated, like CharacterDbEntry.Origin
        monster.Transform = new Transform3D (Basis.Identity,
            new Vector3 (DungeonMobWorld.X, -DungeonMobWorld.Y, -DungeonMobWorld.Z));
        dungeonMob = monster;
        SphLogger.Info ($"New player dungeon: spawned {monsterType} [{monster.ID:X4}]");
    }

    /// <summary>
    /// The captured replay only moves the sword (TransformUpdate 0B09), its spawn frame is cut off,
    /// so the spawn goes in the weapon_starting_sword.spdp shape: no game id, ground container
    /// </summary>
    private static void SendStarterSword (SphereClient client)
    {
        if (client.CurrentCharacter is not { } character)
        {
            return;
        }

        if (starterSwordEntityId == 0)
        {
            starterSwordEntityId = WorldObjectIndex.New ();
        }

        client.MaybeQueueNetworkPacketSend (ItemRecordEncoder.EncodeWithoutGameId (
            client.GetLocalObjectId (starterSwordEntityId), (int) ObjectType.Weapon_Starting_Sword, GroundContainerId,
            StarterSwordWorld.X, StarterSwordWorld.Y, StarterSwordWorld.Z));
        starterSwordShownTo.Add (character.ClientIndex);
    }

    /// <summary>
    /// Null once the client took the sword or never got it
    /// </summary>
    public static ushort? StarterSwordIdFor (SphereClient client)
    {
        if (starterSwordEntityId == 0 || client.CurrentCharacter is not { } character
                                      || !starterSwordShownTo.Contains (character.ClientIndex))
        {
            return null;
        }

        return client.GetLocalObjectId (starterSwordEntityId);
    }

    /// <summary>
    /// The pedestal object goes away for this client only, and a regular Кривой меч takes its place
    /// in the inventory, since the client cannot carry a weapon without a game id
    /// </summary>
    public static ushort? TokenIdFor (SphereClient client) =>
        tokenEntityId != 0 && client.CurrentCharacter is { } character && tokenShownTo.Contains (character.ClientIndex)
            ? client.GetLocalObjectId (tokenEntityId)
            : null;

    /// <summary>
    /// The token is a client object with no DB item, so the same object is moved into the bag
    /// with the live pickup reply and keeps the target and charges set at spawn
    /// </summary>
    public static void TakeToken (SphereClient client)
    {
        if (TokenIdFor (client) is not { } localTokenId || client.CurrentCharacter is not { } character)
        {
            return;
        }

        if (character.FindEmptyInventorySlot () is not { } slot)
        {
            SphLogger.Info ($"New player dungeon: {character.Name} cannot take the token, inventory full");
            return;
        }

        tokenShownTo.Remove (character.ClientIndex);
        tokenTakenBy.Add (character.ClientIndex);
        var playerId = character.ClientIndex;
        client.MaybeQueueNetworkPacketSend (ItemRecordEncoder.EncodePutInto (localTokenId, ObjectType.Token_Tutorial_Torweal,
            SphServer.Shared.BitStream.SphBitStream.ByteSwap (playerId)));
        // Item scripts keep their amount at 0 until this arrives, and TestIt then divides by it
        client.MaybeQueueNetworkPacketSend (ItemRecordEncoder.EncodeCount (localTokenId, ObjectType.Token_Tutorial_Torweal, 1));
        if (ItemSlotReserve.Build (playerId, slot, localTokenId) is { } reserve)
        {
            client.MaybeQueueNetworkPacketSend (reserve);
        }

        SphLogger.Info ($"New player dungeon: {character.Name} took the token [{localTokenId:X4}] into {slot}");
    }

    public static void TakeStarterSword (SphereClient client)
    {
        if (StarterSwordIdFor (client) is not { } localSwordId || client.CurrentCharacter is not { } character)
        {
            return;
        }

        if (character.FindEmptyInventorySlot () is null
            || !SphObjectDb.GameObjectDataDb.TryGetValue (StarterSwordGameId, out var catalog))
        {
            // A drag already lifted the sword off the pedestal, so it is put back
            SendStarterSword (client);
            SphLogger.Info ($"New player dungeon: {character.Name} cannot take the starter sword, inventory full");
            return;
        }

        starterSwordShownTo.Remove (character.ClientIndex);
        client.MaybeQueueNetworkPacketSend (CommonPackets.DespawnEntity (localSwordId));

        var item = ItemDbEntry.CreateFromGameObject (SphGameObject.CreateFromGameObject (catalog));
        item.ItemCount = 1;
        item.Id = WorldObjectIndex.NewItem ();
        DbConnection.SaveItem (item);
        var slot = client.PutItemInInventoryAndDeclare (item);
        SphLogger.Info ($"New player dungeon: {character.Name} took the starter sword [{item.Id:X4}] into {slot}");
    }

    /// <summary>
    /// Over the x000 maze footprint (about 60 units square around the labyr center)
    /// </summary>
    public static bool IsInside (CharacterDbEntry? character)
    {
        if (character is null)
        {
            return false;
        }

        // Any height above 2100: no static geometry exists up there, and a character lost in the
        // void drifts far above the maze
        return Math.Abs (character.X - LabyrinthX) < 100 && -character.Y > 2100
                                                         && Math.Abs (-character.Z - LabyrinthZ) < 100;
    }

    /// <summary>
    /// Called on every position update; the portal is client-side scenery, so the exit is a
    /// proximity check
    /// </summary>
    public static void CheckExit (SphereClient client)
    {
        if (client.CurrentCharacter is not { } character)
        {
            return;
        }

        // Called on the client's position updates, so this is where the client confirms the teleport
        if (IsInside (character))
        {
            lastInsideReportAt[character.ClientIndex] = DateTime.UtcNow;
        }

        var world = new Vector3 ((float) character.X, (float) -character.Y, (float) -character.Z);
        var onPortal = world.DistanceTo (ExitPortalWorld) <= ExitPortalRadius;
        if (!onPortal)
        {
            onPortalSince.Remove (character.ClientIndex);
        }

        if (!onPortal
            || lastExitAt.TryGetValue (character.ClientIndex, out var exitedAt)
            && (DateTime.UtcNow - exitedAt).TotalSeconds < ExitCooldownSeconds)
        {
            return;
        }

        // A token holder leaves the original way, by using the token while standing here; if that
        // does not happen within a few seconds the portal still lets them out, so nobody is stuck
        if (tokenTakenBy.Contains (character.ClientIndex))
        {
            var since = onPortalSince.TryGetValue (character.ClientIndex, out var t) ? t : DateTime.UtcNow;
            onPortalSince[character.ClientIndex] = since;
            if ((DateTime.UtcNow - since).TotalSeconds < TokenHolderPortalGraceSeconds)
            {
                return;
            }
        }

        onPortalSince.Remove (character.ClientIndex);

        lastExitAt[character.ClientIndex] = DateTime.UtcNow;
        // Removing a tutomsg closes its window, which otherwise stays open after the exit
        foreach (var id in tutorialEntityIds)
        {
            if (id != 0)
            {
                client.MaybeQueueNetworkPacketSend (CommonPackets.DespawnEntity (client.GetLocalObjectId (id)));
            }
        }

        if (exitTeleportEntityId != 0)
        {
            client.MaybeQueueNetworkPacketSend (CommonPackets.DespawnEntity (client.GetLocalObjectId (exitTeleportEntityId)));
        }

        client.MaybeQueueNetworkPacketSend (new CharacterDbEntrySerializer (character).GetTeleportByteArray (ExitCoords));
        character.X = ExitCoords.x;
        character.Y = -ExitCoords.y;
        character.Z = -ExitCoords.z;
        client.SaveCharacter ();
        SphLogger.Info ($"New player dungeon: {character.Name} left through the portal to Torweal");
    }
}
