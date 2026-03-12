using System.ComponentModel.DataAnnotations;

namespace AoiDemo.Web.Models;

public sealed class WorldOptions
{
    public const string SectionName = "World";

    [Range(400, 10000)]
    public int Width { get; set; } = 2400;

    [Range(300, 10000)]
    public int Height { get; set; } = 1800;

    [Range(5, 60)]
    public int TickRate { get; set; } = 20;

    [Range(typeof(float), "40", "1000")]
    public float AoiRadius { get; set; } = 260f;

    [Range(0, 500)]
    public int NpcCount { get; set; } = 96;

    public int Seed { get; set; } = 424242;

    [Range(typeof(float), "20", "1000")]
    public float PlayerSpeed { get; set; } = 260f;

    [Range(typeof(float), "10", "1000")]
    public float NpcSpeed { get; set; } = 90f;

    [Range(1, 12)]
    public int QuadtreeMaxDepth { get; set; } = 6;

    [Range(1, 64)]
    public int QuadtreeCapacity { get; set; } = 6;

    public float GridCellSize => AoiRadius;
}
