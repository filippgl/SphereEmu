using System.Collections.Generic;
using System.Globalization;
using Godot;
using SphServer.Godot.Scripts.Navigation;
using SphServer.Godot.Scripts.Objects.HelperGizmos;
using SphServer.Sphere.Game;
using SphServer.Sphere.Game.WorldObject;

namespace SphServer.Server.Debug.Parser;

// Local diagnostic, not for upstream: samples random paths inside each nearby spawner leash and
// counts how many raw navmesh paths are absurd versus what the guarded FindPath lets through
public partial class ConsoleCommandParser
{
    private const float NavCheckBadLeashMarginMeters = 10f;
    private const float NavCheckBadVerticalSpanMeters = 20f;

    private void NavCheck (string args)
    {
        var tokens = (args ?? string.Empty).Split (' ', StringSplitOptions.RemoveEmptyEntries);
        var radius = tokens.Length > 0 && float.TryParse (tokens[0], NumberStyles.Float,
            CultureInfo.InvariantCulture, out var r) ? r : 200f;
        var samples = tokens.Length > 1 && int.TryParse (tokens[1], out var s) ? s : 40;
        var diag = tokens.Length > 2 && tokens[2] == "diag";

        Vector3 center;
        if (sphereClient is null || !ClientWorldPosition.TryGetGodotWorldPosition (sphereClient, out center))
        {
            SendFeedback ("/navcheck needs a connected client with a position.");
            return;
        }

        Callable.From (() => RunNavCheck (center, radius, samples, diag)).CallDeferred ();
    }

    private async void RunNavCheck (Vector3 center, float radius, int samples, bool diag)
    {
        var spawners = new List<MonsterSpawner> ();
        CollectSpawners (SphereServer.ServerNode, spawners);
        spawners.RemoveAll (x => Horizontal (x.LeashCenterWorld, center) > radius);

        // New regions only answer queries after the navigation server syncs at frame end
        foreach (var spawner in spawners)
        {
            TerrainNavMeshRuntime.EnsureTilesLoaded (spawner, spawner.LeashCenterWorld, spawner.LeashRadiusMeters + 8f);
        }

        var tree = SphereServer.ServerNode.GetTree ();
        await TerrainNavMeshRuntime.SyncAsync (tree);
        for (var i = 0; i < 3; i++)
        {
            await SphereServer.ServerNode.ToSignal (tree, SceneTree.SignalName.PhysicsFrame);
        }

        if (diag)
        {
            DiagnoseNearestSpawner (center, spawners);
        }

        var rng = new RandomNumberGenerator ();
        var checkedSpawners = 0;
        var reasons = new Dictionary<NavPathFailReason, int> ();
        int rawPaths = 0, rawBad = 0, acceptedBad = 0, accepted = 0, unsampled = 0;

        foreach (var spawner in spawners)
        {
            var leashCenter = spawner.LeashCenterWorld;
            checkedSpawners++;
            var leash = spawner.LeashRadiusMeters;
            for (var i = 0; i < samples; i++)
            {
                if (!TrySampleNavPoint (rng, leashCenter, leash, out var start) ||
                    !TrySampleNavPoint (rng, leashCenter, leash, out var goal))
                {
                    unsampled++;
                    continue;
                }

                var result = NavPathQuery.FindPath (spawner, new NavPathRequest (start, goal, leashCenter, leash));
                reasons[result.Reason] = reasons.GetValueOrDefault (result.Reason) + 1;

                if (TerrainNavMeshRuntime.IsPointOnNavMesh (start, out var rawStart) &&
                    TerrainNavMeshRuntime.IsPointOnNavMesh (goal, out var rawGoal))
                {
                    var raw = TerrainNavMeshRuntime.FindPath (rawStart, rawGoal);
                    if (raw.Length > 0)
                    {
                        rawPaths++;
                        if (IsObviouslyBadPath (raw, leashCenter, leash))
                        {
                            rawBad++;
                        }
                    }
                }

                if (result.Success)
                {
                    accepted++;
                    if (IsObviouslyBadPath (result.Waypoints.ToArray (), leashCenter, leash))
                    {
                        acceptedBad++;
                    }
                }
            }
        }

        var reasonText = string.Join (", ", reasons.Select (x => $"{x.Key}={x.Value}"));
        SendFeedback ($"/navcheck: {checkedSpawners} spawner(s) within {radius:F0}m; raw paths {rawPaths}, " +
                      $"raw absurd {rawBad}; unsampled {unsampled}; accepted {accepted}, accepted absurd {acceptedBad}; {reasonText}");
    }

    private void DiagnoseNearestSpawner (Vector3 center, List<MonsterSpawner> spawners)
    {
        MonsterSpawner? nearest = null;
        var best = float.MaxValue;
        foreach (var spawner in spawners)
        {
            var d = spawner.LeashCenterWorld.DistanceSquaredTo (center);
            if (d < best)
            {
                best = d;
                nearest = spawner;
            }
        }

        if (nearest is null)
        {
            SendFeedback ($"/navcheck diag: no spawners at all, player {center}");
            return;
        }

        var c = nearest.LeashCenterWorld;
        var loaded = TerrainNavMeshRuntime.EnsureTilesLoaded (nearest, c, nearest.LeashRadiusMeters + 8f);
        var synced = TerrainNavMeshRuntime.TrySyncImmediate ();
        var closest = TerrainNavMeshRuntime.TryClosestPoint (c, out var cp);
        var onNav = TerrainNavMeshRuntime.IsPointOnNavMesh (c, out var snap);
        var below = TerrainNavMeshRuntime.TryFindNavMeshBelow (c + new Vector3 (0f, 30f, 0f), 60f, out var ground);
        var path = NavPathQuery.FindPath (nearest, new NavPathRequest (c, c + new Vector3 (5f, 0f, 0f), c,
            nearest.LeashRadiusMeters));
        SendFeedback ($"/navcheck diag: player {center}, spawner '{nearest.Name}' at {c} r={nearest.LeashRadiusMeters}; " +
                      $"files={TerrainNavMeshRuntime.HasAnyTileFiles ()} ensure={loaded} sync={synced} " +
                      $"ready={TerrainNavMeshRuntime.IsReadyForQueries} regions={TerrainNavMeshRuntime.LoadedRegionCount}; " +
                      $"closest={closest} {cp}; onNav={onNav} {snap}; below={below} {ground}; " +
                      $"path={path.Reason} n={path.Waypoints.Count}");
    }

    private static float Horizontal (Vector3 a, Vector3 b) => new Vector2 (a.X - b.X, a.Z - b.Z).Length ();

    private static void CollectSpawners (Node node, List<MonsterSpawner> spawners)
    {
        if (node is MonsterSpawner spawner)
        {
            spawners.Add (spawner);
        }

        foreach (var child in node.GetChildren ())
        {
            CollectSpawners (child, spawners);
        }
    }

    // A mob stands on the ground under its spot, so probe down from above the leash centre height
    private static bool TrySampleNavPoint (RandomNumberGenerator rng, Vector3 leashCenter, float leash,
        out Vector3 point)
    {
        var angle = rng.RandfRange (0f, Mathf.Tau);
        var distance = leash * Mathf.Sqrt (rng.Randf ());
        var probe = new Vector3 (leashCenter.X + Mathf.Cos (angle) * distance, leashCenter.Y + 30f,
            leashCenter.Z + Mathf.Sin (angle) * distance);
        if (TerrainNavMeshRuntime.TryFindNavMeshBelow (probe, 60f, out point))
        {
            return true;
        }

        // Drop-to-ground is outdoor only; dungeon spawners sample at the leash centre height
        probe.Y = leashCenter.Y;
        return TerrainNavMeshRuntime.IsPointOnNavMesh (probe, out point);
    }

    // Far outside the leash or spanning many storeys: what knelse saw as walls, floors and continents
    private static bool IsObviouslyBadPath (Vector3[] path, Vector3 leashCenter, float leash)
    {
        var minY = float.MaxValue;
        var maxY = float.MinValue;
        var limit = leash + NavCheckBadLeashMarginMeters;
        foreach (var point in path)
        {
            var dx = point.X - leashCenter.X;
            var dz = point.Z - leashCenter.Z;
            if (dx * dx + dz * dz > limit * limit)
            {
                return true;
            }

            minY = Mathf.Min (minY, point.Y);
            maxY = Mathf.Max (maxY, point.Y);
        }

        return maxY - minY > NavCheckBadVerticalSpanMeters;
    }
}
