using System;
using Godot;
using SphServer.Helpers;
using SphServer.Server.Config;
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
    private const double TeleportDelaySeconds = 3;
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

    private static ushort labyrinthEntityId;
    private static Monster? dungeonMob;

    /// <summary>
    /// The client hangs on the loading screen when the session starts inside the dungeon, so the
    /// character enters at the regular spawn and only the teleport moves it
    /// </summary>
    public static void PlaceCharacter (CharacterDbEntry character)
    {
        character.X = ServerConfig.AppConfig.Spawn_X;
        character.Y = -ServerConfig.AppConfig.Spawn_Y;
        character.Z = -ServerConfig.AppConfig.Spawn_Z;
        character.Angle = ServerConfig.AppConfig.Spawn_Angle;
        character.Money = StartingMoney;
    }

    private static void MoveToDungeon (CharacterDbEntry character)
    {
        // DB keeps Y and Z negated relative to world coords; TeleportCoords.y is already flipped
        character.X = TeleportCoords.x;
        character.Y = TeleportCoords.y;
        character.Z = -TeleportCoords.z;
        character.Angle = TeleportCoords.turn;
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
            await client.ToSignal (client.GetTree ().CreateTimer (TeleportDelaySeconds), SceneTreeTimer.SignalName.Timeout);
            if (!GodotObject.IsInstanceValid (client))
            {
                return;
            }

            Enter (client, replayCapturedObjects: true);
        }
        catch (Exception ex)
        {
            SphLogger.Error ("New player dungeon start failed", ex);
        }
    }

    /// <summary>
    /// GM /npd entry; without the captured replay only the teleport and the geometry go out
    /// </summary>
    public static async void Enter (SphereClient client, bool replayCapturedObjects)
    {
        try
        {
            if (client.CurrentCharacter is not { } character)
            {
                return;
            }

            client.MaybeQueueNetworkPacketSend (
                new CharacterDbEntrySerializer (character).GetNewPlayerDungeonTeleportAndUpdateStatsByteArray (TeleportCoords));
            MoveToDungeon (character);
            client.SaveCharacter ();

            await client.ToSignal (client.GetTree ().CreateTimer (LoadDelaySeconds), SceneTreeTimer.SignalName.Timeout);
            if (!GodotObject.IsInstanceValid (client))
            {
                return;
            }

            client.MaybeQueueNetworkPacketSend (CommonPackets.BuildLabyrinthSpawn (
                client.GetLocalObjectId (GetLabyrinthEntityId ()), LabyrinthX, LabyrinthY, LabyrinthZ, MazeId));
            if (replayCapturedObjects)
            {
                client.MaybeQueueNetworkPacketSend (CommonPackets.LoadNewPlayerDungeon);
                client.SendMoney ();
            }

            EnsureDungeonMob ();

            SphLogger.Info ($"New player dungeon loaded for {character.Name}. Client ID: {character.ClientIndex:X4}");
        }
        catch (Exception ex)
        {
            SphLogger.Error ("New player dungeon enter failed", ex);
        }
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
    /// Called on every position update; the portal is client-side scenery, so the exit is a
    /// proximity check
    /// </summary>
    public static void CheckExit (SphereClient client)
    {
        if (client.CurrentCharacter is not { } character)
        {
            return;
        }

        var world = new Vector3 ((float) character.X, (float) -character.Y, (float) -character.Z);
        if (world.DistanceTo (ExitPortalWorld) > ExitPortalRadius)
        {
            return;
        }

        client.MaybeQueueNetworkPacketSend (new CharacterDbEntrySerializer (character).GetTeleportByteArray (ExitCoords));
        character.X = ExitCoords.x;
        character.Y = -ExitCoords.y;
        character.Z = -ExitCoords.z;
        client.SaveCharacter ();
        SphLogger.Info ($"New player dungeon: {character.Name} left through the portal to Torweal");
    }
}
