namespace SphServer.Server.GameplayLogic.Cult;

/// <summary>
/// One row per character wearing a turnskin; Id is the character id
/// </summary>
public class CultSkinDbEntry
{
    public int Id { get; set; }

    /// <summary>
    /// turnskin.cfg SK number, 1 = crater dwarf
    /// </summary>
    public byte Skin { get; set; }
}
