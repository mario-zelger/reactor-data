namespace ReactorData.Tests.Models;

[Model]
partial class Player
{
    public int Id { set; get; }

    public required string Name { get; set; }

    public ICollection<Game> Games { get; set; } = new List<Game>();
}