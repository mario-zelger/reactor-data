namespace ReactorData.Tests.Models;

[Model]
partial class Game
{
    public int Id { set; get; }

    public ICollection<Player> Players { get; set; } = new List<Player>();
}