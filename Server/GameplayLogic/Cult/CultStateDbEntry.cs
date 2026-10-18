namespace SphServer.Server.GameplayLogic.Cult;

/// <summary>
/// The one cult row GMs edit in game; cult.json holds the defaults
/// </summary>
public class CultStateDbEntry
{
    public int Id { get; set; }

    /// <summary>
    /// World x, y, z, turn
    /// </summary>
    public double[]? Respawn { get; set; }
}
