using System;
using System.Collections.Generic;
using System.Linq;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using Xunit;

namespace Imperial2030.Tests;

public class GameDetailDtoBuilderTests
{
    /// <summary>
    /// The roster comes back in seating order (Player.Id) whatever order the collection holds. A replay
    /// session's in-memory copy returned it in a different order from one poll to the next, and the
    /// Assets panel's dropdown showed one player's name over another player's holdings.
    /// </summary>
    [Fact]
    public void PlayersAreInSeatingOrderRegardlessOfCollectionOrder()
    {
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        var game = new Game { Id = Guid.NewGuid(), Name = "Order", Status = GameStatus.InProgress };
        foreach (var id in ids.OrderByDescending(x => x))
        {
            game.Players.Add(new Player { Id = id, GameId = game.Id, IsBot = true, BotName = id.ToString()[..4] });
        }

        var dto = GameDetailDtoBuilder.Build(game, null, null, null);

        Assert.Equal(ids.OrderBy(x => x), dto.Players.Select(p => p.Id));
    }
}
